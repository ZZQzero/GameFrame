using Game.Hotfix.Network.Protocol;
using Game.Server.Login;
using Npgsql;
using NUnit.Framework;

namespace GameFrame.Tests
{
    [TestFixture]
    public sealed class PostgresAccountTests
    {
        private PostgresTestDatabase database = null!;
        private PostgresAccountRepository repository = null!;
        private AccountService service = null!;
        private AccountSessions sessions = null!;

        [OneTimeSetUp]
        public void RequireDatabase()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GF_POSTGRES_TEST_CONNECTION")))
            {
                Assert.Ignore("PostgreSQL account integration requires GF_POSTGRES_TEST_CONNECTION.");
            }
        }

        [SetUp]
        public async Task Start()
        {
            database = await PostgresTestDatabase.CreateAsync();
            repository = new PostgresAccountRepository(database.Database);
            sessions = new AccountSessions(new LoginOptions());
            service = new AccountService(repository, sessions);
            await repository.ValidateAsync(CancellationToken.None);
        }

        [TearDown]
        public async Task Stop()
        {
            service.Dispose();
            sessions.Dispose();
            await database.DisposeAsync();
        }

        [Test]
        public async Task FirstLoginCreatesTheAccountAndLaterLoginsVerifyTheSavedPassword()
        {
            var first = await service.LoginAsync("first-login", "player-password", _ => { }, CancellationToken.None);
            Assert.That(first.Response.ResultCode, Is.EqualTo(ResultCode.Success));
            Assert.That(first.Response.AccountId, Is.GreaterThan(0));
            using StoredAccount stored = (await repository.FindAsync("first-login", CancellationToken.None))!;
            Assert.That(stored.Id, Is.EqualTo((long)first.Response.AccountId));
            Assert.That(stored.Iterations, Is.EqualTo(600000));
            Assert.That(stored.Salt.Length, Is.EqualTo(16));
            Assert.That(stored.PasswordHash.Length, Is.EqualTo(32));
            var rejected = await service.LoginAsync("first-login", "wrong-password", _ => { }, CancellationToken.None);
            Assert.That(rejected.Response.ResultCode, Is.EqualTo(ResultCode.InvalidCredentials));
            Assert.That(sessions.IsCurrent(first.Binding!.Id), Is.True, "A wrong password must not replace the session.");
            using var recreated = new AccountService(new PostgresAccountRepository(database.Database), sessions);
            var again = await recreated.LoginAsync("first-login", "player-password", _ => { }, CancellationToken.None);
            Assert.That(again.Response.AccountId, Is.EqualTo(first.Response.AccountId));
            Assert.That(again.Response.AuthEpoch, Is.GreaterThan(first.Response.AuthEpoch));
            AccountSessions.ClearResponse(first.Response);
            AccountSessions.ClearResponse(rejected.Response);
            AccountSessions.ClearResponse(again.Response);
        }

        private sealed class ConcurrentCreationRepository : IAccountRepository
        {
            private readonly IAccountRepository inner;
            private readonly TaskCompletionSource bothRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            private int initialReads;

            internal ConcurrentCreationRepository(IAccountRepository inner)
            {
                this.inner = inner;
            }

            public async Task<StoredAccount?> FindAsync(string account, CancellationToken cancellation)
            {
                StoredAccount? stored = await inner.FindAsync(account, cancellation);
                if (Interlocked.Increment(ref initialReads) <= 2)
                {
                    if (Volatile.Read(ref initialReads) == 2)
                    {
                        bothRead.TrySetResult();
                    }

                    await bothRead.Task.WaitAsync(cancellation);
                }

                return stored;
            }

            public Task<bool> CreateAsync(string account, byte[] salt, byte[] hash, int iterations, CancellationToken cancellation) =>
                inner.CreateAsync(account, salt, hash, iterations, cancellation);

            public Task<ulong> AdvanceEpochAsync(long accountId, CancellationToken cancellation) => inner.AdvanceEpochAsync(accountId, cancellation);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ConcurrentFirstLoginsCreateOneAccountAndAuthenticateAgainstTheWinningPassword(bool samePassword)
        {
            using var concurrent = new AccountService(new ConcurrentCreationRepository(repository), sessions);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            string secondPassword = samePassword ? "first-password" : "second-password";
            var results = await Task.WhenAll(
                concurrent.LoginAsync("concurrent-login", "first-password", _ => { }, timeout.Token),
                concurrent.LoginAsync("concurrent-login", secondPassword, _ => { }, timeout.Token));
            Assert.That(results.Count(result => result.Response.ResultCode == ResultCode.Success), samePassword ? Is.GreaterThanOrEqualTo(1) : Is.EqualTo(1));
            await using NpgsqlCommand count = database.Database.CreateCommand("SELECT COUNT(*) FROM accounts WHERE account_name = 'concurrent-login'");
            Assert.That(await count.ExecuteScalarAsync(timeout.Token), Is.EqualTo(1L));
            using StoredAccount stored = (await repository.FindAsync("concurrent-login", timeout.Token))!;
            for (int i = 0; i < results.Length; i++)
            {
                if (results[i].Response.ResultCode == ResultCode.Success)
                {
                    Assert.That(results[i].Response.AccountId, Is.EqualTo((ulong)stored.Id));
                    string password = i == 0 ? "first-password" : secondPassword;
                    byte[] hash = PasswordHasher.Hash(password, stored.Salt, stored.Iterations);
                    try
                    {
                        Assert.That(System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(hash, stored.PasswordHash), Is.True);
                    }
                    finally
                    {
                        System.Security.Cryptography.CryptographicOperations.ZeroMemory(hash);
                    }
                }

                AccountSessions.ClearResponse(results[i].Response);
            }
        }

        [Test]
        public async Task InvalidOrCanceledFirstLoginDoesNotCreateAnAccount()
        {
            Assert.That((await service.LoginAsync("invalid", "short", _ => { }, CancellationToken.None)).Response.ResultCode,
                Is.EqualTo(ResultCode.InvalidCredentials));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.CatchAsync<OperationCanceledException>(async () => await service.LoginAsync("canceled-login", "player-password", _ => { }, cancellation.Token));
            Assert.That(await repository.FindAsync("invalid", CancellationToken.None), Is.Null);
            Assert.That(await repository.FindAsync("canceled-login", CancellationToken.None), Is.Null);
        }

        [Test]
        public async Task RegistrationPersistsSaltedHashAndSurvivesServiceRecreation()
        {
            Assert.That(await service.RegisterAsync("player", "player-password", CancellationToken.None), Is.EqualTo(ResultCode.Success));
            using StoredAccount stored = (await repository.FindAsync("player", CancellationToken.None))!;
            Assert.That(stored.Salt.Length, Is.EqualTo(16));
            Assert.That(stored.PasswordHash.Length, Is.EqualTo(32));
            Assert.That(stored.Iterations, Is.EqualTo(600000));
            using var second = new AccountService(new PostgresAccountRepository(database.Database), sessions);
            var login = await second.LoginAsync("player", "player-password", _ => { }, CancellationToken.None);
            Assert.That(login.Response.AccountId, Is.EqualTo((ulong)stored.Id));
            Assert.That((await second.LoginAsync("player", "wrong-password", _ => { }, CancellationToken.None)).Response.ResultCode, Is.EqualTo(ResultCode.InvalidCredentials));
            AccountSessions.ClearResponse(login.Response);
        }

        [Test]
        public async Task ConcurrentDuplicateRegistrationCreatesExactlyOneAccount()
        {
            ResultCode[] results = await Task.WhenAll(
                service.RegisterAsync("same", "first-password", CancellationToken.None),
                service.RegisterAsync("same", "second-password", CancellationToken.None));
            Assert.That(results.Count(result => result == ResultCode.Success), Is.EqualTo(1));
            Assert.That(results.Count(result => result == ResultCode.AccountAlreadyExists), Is.EqualTo(1));
            var first = await service.LoginAsync("same", "first-password", _ => { }, CancellationToken.None);
            var second = await service.LoginAsync("same", "second-password", _ => { }, CancellationToken.None);
            Assert.That((first.Binding != null) ^ (second.Binding != null), Is.True);
            AccountSessions.ClearResponse(first.Response);
            AccountSessions.ClearResponse(second.Response);
        }

        [Test]
        public async Task DistinctAccountsRemainIndependentAndNewLoginAdvancesEpoch()
        {
            await service.RegisterAsync("alice", "alice-password", CancellationToken.None);
            await service.RegisterAsync("bob", "bob-password", CancellationToken.None);
            var alice = await service.LoginAsync("alice", "alice-password", _ => { }, CancellationToken.None);
            var bob = await service.LoginAsync("bob", "bob-password", _ => { }, CancellationToken.None);
            var newer = await service.LoginAsync("alice", "alice-password", _ => { }, CancellationToken.None);
            Assert.That(newer.Response.AuthEpoch, Is.GreaterThan(alice.Response.AuthEpoch));
            Assert.That(sessions.IsCurrent(alice.Binding!.Id), Is.False);
            Assert.That(sessions.IsCurrent(newer.Binding!.Id), Is.True);
            Assert.That(sessions.Logout(bob.Binding!.Id), Is.True);
            Assert.That(sessions.IsCurrent(newer.Binding.Id), Is.True);
            AccountSessions.ClearResponse(alice.Response);
            AccountSessions.ClearResponse(bob.Response);
            AccountSessions.ClearResponse(newer.Response);
        }

        [Test]
        public async Task SqlParametersPreserveLiteralAccountName()
        {
            string account = "player'; DROP TABLE accounts; --";
            Assert.That(await service.RegisterAsync(account, "player-password", CancellationToken.None), Is.EqualTo(ResultCode.Success));
            var login = await service.LoginAsync(account, "player-password", _ => { }, CancellationToken.None);
            Assert.That(login.Response.ResultCode, Is.EqualTo(ResultCode.Success));
            AccountSessions.ClearResponse(login.Response);
            await repository.ValidateAsync(CancellationToken.None);
        }

        [Test]
        public async Task CancellationAndInvalidRegistrationDoNotCreateAnAccount()
        {
            Assert.That(await service.RegisterAsync("player", "short", CancellationToken.None), Is.EqualTo(ResultCode.InvalidInput));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.CatchAsync<OperationCanceledException>(async () => await service.RegisterAsync("canceled", "player-password", cancellation.Token));
            Assert.That(await repository.FindAsync("player", CancellationToken.None), Is.Null);
            Assert.That(await repository.FindAsync("canceled", CancellationToken.None), Is.Null);
        }

        [Test]
        public async Task DatabaseFailurePropagatesInsteadOfBecomingInvalidCredentials()
        {
            await using NpgsqlCommand drop = database.Database.CreateCommand("DROP TABLE accounts");
            await drop.ExecuteNonQueryAsync();
            Assert.ThrowsAsync<PostgresException>(async () => await service.LoginAsync("player", "player-password", _ => { }, CancellationToken.None));
        }

        [Test]
        public async Task ConnectionProbeDoesNotDependOnOrCreateAccountTables()
        {
            await using NpgsqlCommand drop = database.Database.CreateCommand("DROP TABLE accounts");
            await drop.ExecuteNonQueryAsync();
            await database.Pool.ValidateAsync(CancellationToken.None);
            Assert.ThrowsAsync<PostgresException>(async () => await repository.ValidateAsync(CancellationToken.None));
        }

        [Test]
        public void ConnectionProbePropagatesCancellation()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.CatchAsync<OperationCanceledException>(async () => await database.Pool.ValidateAsync(cancellation.Token));
        }

        [Test]
        public async Task DisposingDatabaseClosesTheSharedPool()
        {
            await database.Pool.DisposeAsync();
            Assert.ThrowsAsync<ObjectDisposedException>(async () => await repository.FindAsync("player", CancellationToken.None));
        }
    }
}
