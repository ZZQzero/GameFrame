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
            service = new AccountService(repository);
            await repository.ValidateAsync(CancellationToken.None);
        }

        [TearDown]
        public async Task Stop()
        {
            service.Dispose();
            await database.DisposeAsync();
        }

        [Test]
        public async Task RegistrationPersistsSaltedHashAndSurvivesServiceRecreation()
        {
            Assert.That(await service.RegisterAsync("player", "player-password", CancellationToken.None), Is.EqualTo(ResultCode.Success));
            using StoredAccount stored = (await repository.FindAsync("player", CancellationToken.None))!;
            Assert.That(stored.Salt.Length, Is.EqualTo(16));
            Assert.That(stored.PasswordHash.Length, Is.EqualTo(32));
            Assert.That(stored.Iterations, Is.EqualTo(600000));
            using var second = new AccountService(new PostgresAccountRepository(database.Database));
            AuthenticatedAccount? login = await second.LoginAsync("player", "player-password", CancellationToken.None);
            Assert.That(login!.Value.Id, Is.EqualTo((ulong)stored.Id));
            Assert.That(await second.LoginAsync("player", "wrong-password", CancellationToken.None), Is.Null);
        }

        [Test]
        public async Task ConcurrentDuplicateRegistrationCreatesExactlyOneAccount()
        {
            ResultCode[] results = await Task.WhenAll(
                service.RegisterAsync("same", "first-password", CancellationToken.None),
                service.RegisterAsync("same", "second-password", CancellationToken.None));
            Assert.That(results.Count(result => result == ResultCode.Success), Is.EqualTo(1));
            Assert.That(results.Count(result => result == ResultCode.AccountAlreadyExists), Is.EqualTo(1));
            AuthenticatedAccount? first = await service.LoginAsync("same", "first-password", CancellationToken.None);
            AuthenticatedAccount? second = await service.LoginAsync("same", "second-password", CancellationToken.None);
            Assert.That(first.HasValue ^ second.HasValue, Is.True);
        }

        [Test]
        public async Task DistinctAccountsRemainIndependentAndOutOfOrderLoginCannotReplaceNewerEpoch()
        {
            await service.RegisterAsync("alice", "alice-password", CancellationToken.None);
            await service.RegisterAsync("bob", "bob-password", CancellationToken.None);
            AuthenticatedAccount alice = (await service.LoginAsync("alice", "alice-password", CancellationToken.None))!.Value;
            AuthenticatedAccount bob = (await service.LoginAsync("bob", "bob-password", CancellationToken.None))!.Value;
            AuthenticatedAccount newerAlice = (await service.LoginAsync("alice", "alice-password", CancellationToken.None))!.Value;
            using var sessions = new AccountSessions(new LoginOptions());
            var newer = sessions.Login(newerAlice, () => { }, CancellationToken.None);
            var independent = sessions.Login(bob, () => { }, CancellationToken.None);
            Assert.That(sessions.Login(alice, () => { }, CancellationToken.None).Response.ResultCode, Is.EqualTo(ResultCode.InvalidCredentials));
            Assert.That(sessions.IsCurrent(newer.Binding), Is.True);
            Assert.That(sessions.Logout(independent.Binding), Is.True);
            Assert.That(sessions.IsCurrent(newer.Binding), Is.True);
        }

        [Test]
        public async Task SqlParametersPreserveLiteralAccountName()
        {
            string account = "player'; DROP TABLE accounts; --";
            Assert.That(await service.RegisterAsync(account, "player-password", CancellationToken.None), Is.EqualTo(ResultCode.Success));
            Assert.That(await service.LoginAsync(account, "player-password", CancellationToken.None), Is.Not.Null);
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
            Assert.ThrowsAsync<PostgresException>(async () => await service.LoginAsync("player", "player-password", CancellationToken.None));
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
