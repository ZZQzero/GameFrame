using System.Net;
using Game.Hotfix.Network.Generated;
using Game.Hotfix.Network.Protocol;
using Game.Server;
using Game.Server.Login;
using GameFrame.Network;
using GameFrame.Network.Protocol;
using GameFrame.Network.Transport;
using GameFrame.Server.Network;
using NUnit.Framework;

namespace GameFrame.Tests
{
    [TestFixture]
    public sealed class AccountLifecycleTests
    {
        private static TaskCompletionSource<ulong> PendingEpoch() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static Task<(LoginResponse Response, AccountSessions.Binding? Binding)> Login(AccountSessions sessions, ulong epoch,
            Action<ushort>? close = null, ulong account = 1) =>
            sessions.LoginAsync(account, _ => Task.FromResult(epoch), close ?? (_ => { }), CancellationToken.None);

        [Test]
        public async Task LogoutInvalidatesAnOlderEpochStillWaitingForItsDatabaseResult()
        {
            using var sessions = new AccountSessions(new LoginOptions());
            var epoch = PendingEpoch();
            Task<(LoginResponse Response, AccountSessions.Binding? Binding)> old =
                sessions.LoginAsync(1, _ => epoch.Task, _ => { }, CancellationToken.None);
            var newer = await Login(sessions, 2);
            Assert.That(sessions.Logout(newer.Binding!.Id), Is.True);
            Assert.That(newer.Binding.CloseReason, Is.EqualTo(7));
            Assert.That(sessions.TrackedAccountCount, Is.EqualTo(1), "The old operation still owns the comparison state.");
            epoch.SetResult(1);
            var rejected = await old;
            Assert.That(rejected.Response.ResultCode, Is.EqualTo(ResultCode.InvalidCredentials));
            Assert.That(rejected.Binding, Is.Null);
            Assert.That(sessions.TrackedAccountCount, Is.Zero);
            var fresh = await Login(sessions, 3);
            Assert.That(fresh.Response.ResultCode, Is.EqualTo(ResultCode.Success));
            AccountSessions.ClearResponse(newer.Response);
            AccountSessions.ClearResponse(fresh.Response);
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
            Assert.That(newer.Binding!.CloseReason, Is.EqualTo(4));
            epoch.SetResult(1);
            Assert.That((await old).Binding, Is.Null);
            Assert.That(sessions.TrackedAccountCount, Is.EqualTo(1));
            AccountSessions.ClearResponse(newer.Response);
            AccountSessions.ClearResponse(unrelated.Response);
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
            ushort kicked = 0;
            var third = await Login(sessions, 3, reason => kicked = reason);
            var fourth = await Login(sessions, 4);
            Assert.That(kicked, Is.EqualTo(2));
            Assert.That(third.Binding!.CloseReason, Is.EqualTo(2));
            Assert.That(sessions.IsCurrent(fourth.Binding!.Id), Is.True);
            AccountSessions.ClearResponse(newer.Response);
            AccountSessions.ClearResponse(third.Response);
            AccountSessions.ClearResponse(fourth.Response);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task FailedOrCanceledEpochReleasesItsReservedCapacity(bool cancel)
        {
            using var sessions = new AccountSessions(new LoginOptions { MaxSessions = 1 });
            using var cancellation = new CancellationTokenSource();
            var epoch = PendingEpoch();
            var pending = sessions.LoginAsync(1, _ => epoch.Task, _ => { }, cancellation.Token);
            ServerRequestException? overloaded = Assert.ThrowsAsync<ServerRequestException>(async () => await Login(sessions, 1, account: 2));
            Assert.That(overloaded!.StatusCode, Is.EqualTo(1));
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
            AccountSessions.ClearResponse(next.Response);
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
            AccountSessions.ClearResponse(result.Response);
            AccountSessions.ClearResponse(second.Response);
        }

        [Test]
        public async Task ResumeRevokesOnlyTheOldBindingAndRotatesTheToken()
        {
            using var sessions = new AccountSessions(new LoginOptions());
            ushort reason = 0;
            var first = await Login(sessions, 1, value => reason = value);
            var resumed = sessions.Resume(new ResumeLoginRequest
            {
                AccountSessionId = first.Response.AccountSessionId,
                ResumeToken = first.Response.ResumeToken
            }, _ => { });
            Assert.That(reason, Is.EqualTo(2));
            Assert.That(first.Binding!.CloseReason, Is.EqualTo(2));
            Assert.That(resumed.Binding!.CloseReason, Is.Null);
            Assert.That(resumed.Response.AuthEpoch, Is.EqualTo(first.Response.AuthEpoch));
            sessions.Dispose();
            Assert.That(resumed.Binding.CloseReason, Is.EqualTo(7));
            AccountSessions.ClearResponse(first.Response);
            AccountSessions.ClearResponse(resumed.Response);
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
            using var service = new AccountService(repository, sessions);
            var application = new LobbyApplication(sessions, service, new LoginOptions()).Application;
            application.Freeze();
            var memory = new FrameMemoryBudget(1024 * 1024);
            using var cancel = new CancellationTokenSource();
            Task[] blocked = Enumerable.Range(0, 4).Select(_ => service.LoginAsync("player", "valid-password", _ => { }, cancel.Token)).ToArray();
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
            using var service = new AccountService(repository, sessions);
            var application = new LobbyApplication(sessions, service, new LoginOptions()).Application;
            var memory = new FrameMemoryBudget(1024 * 1024);
            var context = Context(application, memory);
            await context.Session.ConnectedAsync(context);
            var login = await Login(sessions, 1);
            var session = (LoginSession)context.Session;
            session.Bind(login.Binding);
            sessions.Logout(login.Binding!.Id);
            var header = new ServerMessageHeader(GameNetRoutes.Echo.Opcode, 1, 0, NetDelivery.Reliable);
            Assert.That(session.CheckDeadline(AccountSessions.Now), Is.EqualTo(7));
            Assert.That(session.Admit(header).CloseReason, Is.EqualTo(7));
            Assert.That(session.CheckDispatch(header).CloseReason, Is.EqualTo(7));
            AccountSessions.ClearResponse(login.Response);
        }

        [Test]
        public async Task LateBindingCannotPromoteAnAlreadyRevokedLogin()
        {
            using var sessions = new AccountSessions(new LoginOptions());
            using var repository = new TestAccountRepository();
            using var service = new AccountService(repository, sessions);
            var application = new LobbyApplication(sessions, service, new LoginOptions()).Application;
            var memory = new FrameMemoryBudget(1024 * 1024);
            var context = Context(application, memory);
            await context.Session.ConnectedAsync(context);
            var first = await Login(sessions, 1);
            var second = await Login(sessions, 2);
            ((LoginSession)context.Session).Bind(first.Binding);
            Assert.That(context.IsRestricted, Is.True);
            Assert.That(context.RequestedCloseReason, Is.EqualTo(2));
            AccountSessions.ClearResponse(first.Response);
            AccountSessions.ClearResponse(second.Response);
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
            var session = new LoginSession(context, sessions, new LoginOptions(), _ => admitted);
            await session.ConnectedAsync(context);
            var header = new ServerMessageHeader(GameNetRoutes.Login.Opcode, 1, 100, NetDelivery.Reliable);
            Assert.That(session.Admit(header), Is.EqualTo(ServerAdmission.Deny(1, 1)));
            admitted = true;
            Assert.That(session.Admit(header).Allowed, Is.True);
        }
    }
}
