using System.Net;
using Game.Protocol;
using Game.Server;
using Game.Server.Login;
using Game.Server.Sessions;
using GameFrame.Network;
using NUnit.Framework;

namespace GameFrame.Tests
{
    [TestFixture]
    public sealed class AccountLifecycleTests
    {
        [Test]
        public void LocalKcpApplicationExplicitlyHandlesEveryInboundProductionRoute()
        {
            Assert.DoesNotThrow(() => LocalKcpService.CreateApplication().Freeze());
        }

        private static TaskCompletionSource<ulong> PendingEpoch() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static Task<AuthenticationResult> Login(AccountSessions sessions, ulong epoch,
            Action<AccountSessionEndReason>? close = null, ulong account = 1) =>
            sessions.LoginAsync(account, _ => Task.FromResult(epoch), close ?? (_ => { }), CancellationToken.None);

        [Test]
        public async Task LogoutInvalidatesAnOlderEpochStillWaitingForItsDatabaseResult()
        {
            using var sessions = new AccountSessions(new LoginOptions());
            var epoch = PendingEpoch();
            Task<AuthenticationResult> old =
                sessions.LoginAsync(1, _ => epoch.Task, _ => { }, CancellationToken.None);
            var newer = await Login(sessions, 2);
            Assert.That(sessions.Logout(newer.Binding!.Id), Is.True);
            Assert.That(newer.Binding.CloseReason, Is.EqualTo(AccountSessionEndReason.Revoked));
            Assert.That(sessions.TrackedAccountCount, Is.EqualTo(1), "The old operation still owns the comparison state.");
            epoch.SetResult(1);
            var rejected = await old;
            Assert.That(rejected.Status, Is.EqualTo(AuthenticationStatus.InvalidCredentials));
            Assert.That(rejected.Binding, Is.Null);
            Assert.That(sessions.TrackedAccountCount, Is.Zero);
            var fresh = await Login(sessions, 3);
            Assert.That(fresh.Status, Is.EqualTo(AuthenticationStatus.Success));
            newer.Dispose();
            fresh.Dispose();
        }

        [Test]
        public async Task ExpiredAndPurgedSessionCannotBeRevivedByAnOldResult()
        {
            using var sessions = new AccountSessions(new LoginOptions { AccountLifetime = TimeSpan.FromMilliseconds(40) });
            var epoch = PendingEpoch();
            var old = sessions.LoginAsync(1, _ => epoch.Task, _ => { }, CancellationToken.None);
            var newer = await Login(sessions, 2);
            await Task.Delay(80);
            var unrelated = await Login(sessions, 1, account: 2);
            Assert.That(newer.Binding!.CloseReason, Is.EqualTo(AccountSessionEndReason.Expired));
            epoch.SetResult(1);
            Assert.That((await old).Binding, Is.Null);
            Assert.That(sessions.TrackedAccountCount, Is.EqualTo(1));
            newer.Dispose();
            unrelated.Dispose();
        }

        [Test]
        public async Task CompetingCommitsUseEpochAndKeepTheNewerBinding()
        {
            using var sessions = new AccountSessions(new LoginOptions());
            var epoch = PendingEpoch();
            var old = sessions.LoginAsync(1, _ => epoch.Task, _ => { }, CancellationToken.None);
            var newer = await Login(sessions, 2);
            epoch.SetResult(1);
            Assert.That((await old).Binding, Is.Null);
            Assert.That(sessions.IsCurrent(newer.Binding!.Id), Is.True);
            AccountSessionEndReason? kicked = null;
            var third = await Login(sessions, 3, reason => kicked = reason);
            var fourth = await Login(sessions, 4);
            Assert.That(kicked, Is.EqualTo(AccountSessionEndReason.Replaced));
            Assert.That(third.Binding!.CloseReason, Is.EqualTo(AccountSessionEndReason.Replaced));
            Assert.That(sessions.IsCurrent(fourth.Binding!.Id), Is.True);
            newer.Dispose();
            third.Dispose();
            fourth.Dispose();
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task FailedOrCanceledEpochReleasesItsReservedCapacity(bool cancel)
        {
            using var sessions = new AccountSessions(new LoginOptions { MaxSessions = 1 });
            using var cancellation = new CancellationTokenSource();
            var epoch = PendingEpoch();
            var pending = sessions.LoginAsync(1, _ => epoch.Task, _ => { }, cancellation.Token);
            using AuthenticationResult overloaded = await Login(sessions, 1, account: 2);
            Assert.That(overloaded.Status, Is.EqualTo(AuthenticationStatus.Overloaded));
            if (cancel)
            {
                cancellation.Cancel();
                epoch.SetResult(1);
                Assert.CatchAsync<OperationCanceledException>(async () => await pending);
            }
            else
            {
                epoch.SetException(new InvalidOperationException("Epoch write failed."));
                Assert.ThrowsAsync<InvalidOperationException>(async () => await pending);
            }

            Assert.That(sessions.TrackedAccountCount, Is.Zero);
            var next = await Login(sessions, 1, account: 2);
            Assert.That(next.Binding, Is.Not.Null);
            next.Dispose();
        }

        [Test]
        public async Task WaitingForOneAccountDoesNotHoldTheGateForAnother()
        {
            using var sessions = new AccountSessions(new LoginOptions());
            var epoch = PendingEpoch();
            var first = sessions.LoginAsync(1, _ => epoch.Task, _ => { }, CancellationToken.None);
            var second = await Login(sessions, 1, account: 2).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.That(second.Binding, Is.Not.Null);
            epoch.SetResult(1);
            var result = await first;
            result.Dispose();
            second.Dispose();
        }

        [Test]
        public async Task ResumeRevokesOnlyTheOldBindingAndRotatesTheToken()
        {
            using var sessions = new AccountSessions(new LoginOptions());
            AccountSessionEndReason? reason = null;
            var first = await Login(sessions, 1, value => reason = value);
            var resumed = sessions.Resume(first.AccountSessionId, first.ResumeToken, _ => { });
            Assert.That(reason, Is.EqualTo(AccountSessionEndReason.Replaced));
            Assert.That(first.Binding!.CloseReason, Is.EqualTo(AccountSessionEndReason.Replaced));
            Assert.That(resumed.Binding!.CloseReason, Is.Null);
            Assert.That(resumed.Binding!.Identity.AuthEpoch, Is.EqualTo(first.Binding!.Identity.AuthEpoch));
            sessions.Dispose();
            Assert.That(resumed.Binding.CloseReason, Is.EqualTo(AccountSessionEndReason.Revoked));
            first.Dispose();
            resumed.Dispose();
        }

        [Test]
        public async Task DisposalInvalidatesPendingCommitAndReclaimsState()
        {
            using var sessions = new AccountSessions(new LoginOptions());
            var epoch = PendingEpoch();
            var pending = sessions.LoginAsync(1, _ => epoch.Task, _ => { }, CancellationToken.None);
            sessions.Dispose();
            epoch.SetResult(1);
            Assert.ThrowsAsync<ObjectDisposedException>(async () => await pending);
            Assert.That(sessions.TrackedAccountCount, Is.Zero);
        }

        internal sealed class BlockedRepository : IAccountRepository
        {
            internal readonly TaskCompletionSource<StoredAccount?> FindResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task<StoredAccount?> FindAsync(string account, CancellationToken cancellation) => FindResult.Task.WaitAsync(cancellation);
            public Task<bool> CreateAsync(string account, byte[] salt, byte[] hash, int iterations, CancellationToken cancellation) => Task.FromResult(true);
            public Task<ulong> AdvanceEpochAsync(long accountId, CancellationToken cancellation) => Task.FromResult(1UL);
        }

        private static ServerConnectionContext Context(ServerApplication application, FrameMemoryBudget memory)
        {
            var context = new ServerConnectionContext(1, 1, 0, NetworkTransportKind.WebSocket,
                new IPEndPoint(IPAddress.Loopback, 1000), application.Catalog, memory, CancellationToken.None,
                _ => throw new InvalidOperationException("No Push expected."), () => { });
            application.Initialize(context);
            return context;
        }

        private static OwnedFrame Request<TRequest, TResponse>(FrameMemoryBudget memory, NetRpc<TRequest, TResponse> route, TRequest dto)
            where TRequest : class where TResponse : class
        {
            OwnedFrame frame = memory.Allocate(32 + route.MaxBodyBytes);
            var writer = new BoundedFrameWriter(frame);
            route.RequestCodec.Encode(writer, dto);
            writer.Complete(FrameKind.Request, route.Opcode, 1);
            return frame;
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task AuthenticationOverloadIsStatusOneAndLeavesTheCorrectConnectionPhase(bool register)
        {
            var repository = new BlockedRepository();
            using var sessions = new AccountSessions(new LoginOptions());
            using var service = new AuthenticationService(repository, sessions, new LoginOptions());
            var application = new LobbyApplication(new LoginModule(sessions, service, new LoginOptions())).Application;
            application.Freeze();
            var memory = new FrameMemoryBudget(1024 * 1024);
            using var cancel = new CancellationTokenSource();
            Task[] blocked = Enumerable.Range(0, 4).Select(index => service.LoginAsync("blocked-" + index, "valid-password", _ => { }, cancel.Token)).ToArray();
            try
            {
                var context = Context(application, memory);
                await context.Session.ConnectedAsync(context);
                using OwnedFrame request = register ? Request(memory, GameNetRoutes.Register,
                    new RegisterRequest { Account = "player", Password = "valid-password" }) :
                    Request(memory, GameNetRoutes.Login, new LoginRequest { Account = "player", Password = "valid-password" });
                Assert.That(context.Session.Admit(ServerApplication.Header(request)).Allowed, Is.True);
                using (OwnedFrame response = (await application.DispatchAsync(context, request, memory))!)
                {
                    Assert.That(response.Header.StatusCode, Is.EqualTo(1));
                    Assert.That(response.Header.BodyLength, Is.Zero);
                }

                Assert.That(context.TakeCloseReason(), Is.EqualTo(register ? 0 : 1));
                ServerAdmission next = context.Session.Admit(ServerApplication.Header(request));
                Assert.That(next.Allowed, Is.EqualTo(register), "Registration may be explicitly retried; failed Login closes.");
                if (register)
                {
                    cancel.Cancel();
                    foreach (Task task in blocked)
                    {
                        Assert.CatchAsync<OperationCanceledException>(async () => await task);
                    }

                    using OwnedFrame retried = (await application.DispatchAsync(context, request, memory))!;
                    Assert.That(retried.Header.StatusCode, Is.Zero);
                    Assert.That(GameNetRoutes.Register.ResponseCodec.Decode(retried.Body).ResultCode, Is.EqualTo(ResultCode.Success));
                }
            }
            finally
            {
                cancel.Cancel();
                foreach (Task task in blocked)
                {
                    try
                    {
                        await task;
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }
            }

            Assert.That(memory.OwnedBytes, Is.Zero);
        }

        [Test]
        public async Task AdmissionAndDeadlinePreserveRevocationReasonAfterTheIndexIsGone()
        {
            using var sessions = new AccountSessions(new LoginOptions());
            using var repository = new TestAccountRepository();
            using var service = new AuthenticationService(repository, sessions, new LoginOptions());
            var application = new LobbyApplication(new LoginModule(sessions, service, new LoginOptions())).Application;
            var memory = new FrameMemoryBudget(1024 * 1024);
            var context = Context(application, memory);
            await context.Session.ConnectedAsync(context);
            var login = await Login(sessions, 1);
            var session = (LobbySession)context.Session;
            session.Bind(login.Binding);
            sessions.Logout(login.Binding!.Id);
            var header = new ServerMessageHeader(GameNetRoutes.Echo.Opcode, 1, 0, NetDelivery.Reliable);
            Assert.That(session.CheckDeadline(AccountSessions.Now), Is.EqualTo(7));
            Assert.That(session.Admit(header).CloseReason, Is.EqualTo(7));
            Assert.That(session.CheckDispatch(header).CloseReason, Is.EqualTo(7));
            login.Dispose();
        }

        [Test]
        public async Task LateBindingCannotPromoteAnAlreadyRevokedLogin()
        {
            using var sessions = new AccountSessions(new LoginOptions());
            using var repository = new TestAccountRepository();
            using var service = new AuthenticationService(repository, sessions, new LoginOptions());
            var application = new LobbyApplication(new LoginModule(sessions, service, new LoginOptions())).Application;
            var memory = new FrameMemoryBudget(1024 * 1024);
            var context = Context(application, memory);
            await context.Session.ConnectedAsync(context);
            var first = await Login(sessions, 1);
            var second = await Login(sessions, 2);
            ((LobbySession)context.Session).Bind(first.Binding);
            Assert.That(context.IsRestricted, Is.True);
            Assert.That(context.RequestedCloseReason, Is.EqualTo(2));
            first.Dispose();
            second.Dispose();
        }

        [Test]
        public async Task IpRateLimitUsesOverloadStatusAndReleasesAuthenticationAdmission()
        {
            using var sessions = new AccountSessions(new LoginOptions());
            var memory = new FrameMemoryBudget(1024 * 1024);
            var context = new ServerConnectionContext(1, 1, 0, NetworkTransportKind.WebSocket,
                new IPEndPoint(IPAddress.Loopback, 1000), GameNetProtocol.Catalog, memory,
                CancellationToken.None, _ => false, () => { });
            bool admitted = false;
            var session = new LobbySession(context, sessions, new LoginOptions(), _ => admitted);
            await session.ConnectedAsync(context);
            var header = new ServerMessageHeader(GameNetRoutes.Login.Opcode, 1, 100, NetDelivery.Reliable);
            Assert.That(session.Admit(header), Is.EqualTo(ServerAdmission.Deny(1, 1)));
            admitted = true;
            Assert.That(session.Admit(header).Allowed, Is.True);
        }

        [Test]
        public async Task ConnectionIdentityRequiresAuthenticationAndDoesNotOwnResponseCredentials()
        {
            var options = new LoginOptions();
            using var sessions = new AccountSessions(options);
            using var repository = new TestAccountRepository();
            using var service = new AuthenticationService(repository, sessions, options);
            var application = new LobbyApplication(new LoginModule(sessions, service, options)).Application;
            var memory = new FrameMemoryBudget(1024 * 1024);
            var context = Context(application, memory);
            await context.Session.ConnectedAsync(context);
            var session = (LobbySession)context.Session;
            Assert.That(Assert.Throws<ServerRequestException>(() => session.RequireIdentity())!.StatusCode, Is.EqualTo(2));

            var request = new LoginRequest { Account = "identity", Password = "valid-password" };
            LoginResponse response = await new LoginHandlers(service).LoginAsync(context, request, CancellationToken.None);
            try
            {
                Assert.That(request.Password, Is.Empty);
                AccountIdentity identity = session.RequireIdentity();
                Assert.That(identity.AccountId, Is.EqualTo(response.AccountId));
                Assert.That(identity.AuthEpoch, Is.EqualTo(response.AuthEpoch));
                Assert.That(identity.BindingId, Is.EqualTo(session.Binding));
                LoginHandlers.ClearResponse(response);
                Assert.That(response.ResumeToken, Is.All.Zero);
                Assert.That(response.AccountSessionId, Is.All.Zero);
                Assert.That(session.RequireIdentity(), Is.EqualTo(identity));
                Assert.That(sessions.IsCurrent(identity.BindingId), Is.True);
            }
            finally
            {
                LoginHandlers.ClearResponse(response);
                await session.ClosedAsync(context, default);
            }

            Assert.That(memory.OwnedBytes, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task DelayedEchoRejectsAnIdentityRevokedDuringItsAwait(bool logout)
        {
            var options = new LoginOptions();
            using var sessions = new AccountSessions(options);
            using var repository = new TestAccountRepository();
            using var service = new AuthenticationService(repository, sessions, options);
            var application = new LobbyApplication(new LoginModule(sessions, service, options)).Application;
            application.Freeze();
            var memory = new FrameMemoryBudget(1024 * 1024);
            var context = Context(application, memory);
            await context.Session.ConnectedAsync(context);
            using AuthenticationResult first = await Login(sessions, 1);
            ((LobbySession)context.Session).Bind(first.Binding);
            using (OwnedFrame request = Request(memory, GameNetRoutes.Echo, new EchoRequest { DelayMs = 100 }))
            {
                Task<OwnedFrame?> pending = application.DispatchAsync(context, request, memory);
                Assert.That(pending.IsCompleted, Is.False);
                if (logout)
                {
                    Assert.That(service.Logout(first.Binding!.Id), Is.EqualTo(AuthenticationStatus.Success));
                }
                else
                {
                    using AuthenticationResult newer = await Login(sessions, 2);
                }

                using OwnedFrame response = (await pending)!;
                Assert.That(response.Header.StatusCode, Is.EqualTo(2));
                Assert.That(response.Header.BodyLength, Is.Zero);
                Assert.That(context.TakeCloseReason(), Is.EqualTo(logout ? 7 : 2));
            }

            await context.Session.ClosedAsync(context, default);
            Assert.That(memory.OwnedBytes, Is.Zero);
        }

        [Test]
        public async Task AuthenticationServiceResumesDetachedSessionsAndLogoutRevokesTheRotatedCredentials()
        {
            var options = new LoginOptions();
            using var sessions = new AccountSessions(options);
            using var repository = new TestAccountRepository();
            using var service = new AuthenticationService(repository, sessions, options);
            var application = new LobbyApplication(new LoginModule(sessions, service, options)).Application;
            var memory = new FrameMemoryBudget(1024 * 1024);
            var context = Context(application, memory);
            await context.Session.ConnectedAsync(context);
            using AuthenticationResult first = await service.LoginAsync("resume", "valid-password", _ => { }, CancellationToken.None);
            ((LobbySession)context.Session).Bind(first.Binding);
            await context.Session.ClosedAsync(context, default);
            Assert.That(sessions.IsCurrent(first.Binding!.Id), Is.True);

            using AuthenticationResult resumed = service.Resume(first.AccountSessionId, first.ResumeToken, _ => { }, CancellationToken.None);
            Assert.That(resumed.Status, Is.EqualTo(AuthenticationStatus.Success));
            Assert.That(resumed.Binding!.Identity.AccountId, Is.EqualTo(first.Binding.Identity.AccountId));
            Assert.That(resumed.Binding.Identity.AuthEpoch, Is.EqualTo(first.Binding.Identity.AuthEpoch));
            Assert.That(resumed.Binding.Id, Is.Not.EqualTo(first.Binding.Id));
            using AuthenticationResult reused = service.Resume(first.AccountSessionId, first.ResumeToken, _ => { }, CancellationToken.None);
            Assert.That(reused.Status, Is.EqualTo(AuthenticationStatus.InvalidCredentials));
            Assert.That(service.Logout(first.Binding.Id), Is.EqualTo(AuthenticationStatus.SessionRevoked));
            Assert.That(service.Logout(resumed.Binding.Id), Is.EqualTo(AuthenticationStatus.Success));
            using AuthenticationResult revoked = service.Resume(resumed.AccountSessionId, resumed.ResumeToken, _ => { }, CancellationToken.None);
            Assert.That(revoked.Status, Is.EqualTo(AuthenticationStatus.InvalidCredentials));
            Assert.That(memory.OwnedBytes, Is.Zero);
        }
    }
}
