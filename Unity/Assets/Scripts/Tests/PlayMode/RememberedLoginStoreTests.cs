using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Hotfix;
using GameFrame.Sqlite;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace GameFrame.Tests
{
    public sealed class RememberedLoginStoreTests
    {
        public sealed class MemoryVault : ILoginCredentialVault
        {
            private readonly Dictionary<string, byte[]> entries = new Dictionary<string, byte[]>();
            public bool FailWrite;

            public byte[] Read(string key) => entries.TryGetValue(key, out var value) ? (byte[])value.Clone() : null;

            public void Write(string key, byte[] value)
            {
                if (FailWrite)
                {
                    throw new IOException("test-vault-write-failure");
                }

                entries[key] = (byte[])value.Clone();
            }

            public void Delete(string key)
            {
                if (entries.TryGetValue(key, out var value))
                {
                    Array.Clear(value, 0, value.Length);
                    entries.Remove(key);
                }
            }
        }

        private string directory;
        private string path;
        private MemoryVault vault;
        private RememberedLoginStore store;
        private static readonly Uri Endpoint = new Uri("wss://remembered-login.test/network");

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "gameframe-login-" + Guid.NewGuid().ToString("N"));
            path = Path.Combine(directory, "login.sqlite");
            vault = new MemoryVault();
            store = new RememberedLoginStore(path, Endpoint, vault);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }

        private UniTask SaveAsync(string account = "saved-player", ulong id = 42) => store.SaveAsync(account, id,
            DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(), new byte[16], new byte[32]);

        [UnityTest]
        public IEnumerator ReopenedDatabaseStoresMetadataAndResumeIsConsumedOnce() => UniTask.ToCoroutine(async () =>
        {
            await SaveAsync();
            store = new RememberedLoginStore(path, Endpoint, vault);
            RememberedAccount account = await store.ReadAsync();
            Assert.AreEqual("saved-player", account.AccountName);
            Assert.AreEqual(42UL, account.AccountId);
            Assert.IsTrue(account.CanResume);
            var db = await SqliteDatabase.OpenAsync(new SqliteOpenOptions(path, SqliteOpenMode.OpenExistingReadOnly));
            try
            {
                var rows = await db.QueryPageAsync(new SqliteCommand("SELECT * FROM remembered_login"),
                    new SqliteQueryBudget(), row => new object[]
                    {
                        row.Count, row["endpoint"], row["account_name"], row["account_id"], row["expires_utc_ms"]
                    });
                Assert.AreEqual(1, rows.Count);
                Assert.AreEqual(4, rows[0][0], "Only the four metadata columns belong in SQLite.");
                Assert.AreEqual(Endpoint.AbsoluteUri, rows[0][1]);
                Assert.AreEqual("saved-player", rows[0][2]);
                Assert.AreEqual(42L, rows[0][3]);
            }
            finally
            {
                await db.CloseAsync();
            }

            using (var credential = await store.TakeAsync())
            {
                Assert.AreEqual(42UL, credential.Account.AccountId);
                Assert.AreEqual(16, credential.SessionId.Length);
                Assert.AreEqual(32, credential.Token.Length);
            }

            Assert.IsNull(await store.TakeAsync(), "Unknown RPC outcome must not allow reuse of a single-use token.");
            Assert.IsFalse((await store.ReadAsync()).CanResume);
            Assert.AreEqual("saved-player", (await store.ReadAsync()).AccountName);
        });

        [UnityTest]
        public IEnumerator AccountReplacementAndForgetClearOnlyThisEndpoint() => UniTask.ToCoroutine(async () =>
        {
            await SaveAsync();
            var other = new RememberedLoginStore(path, new Uri("wss://other-login.test/network"), vault);
            Assert.IsNull(await other.ReadAsync());
            await other.SaveAsync("other-player", 77, DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(),
                new byte[16], new byte[32]);
            await SaveAsync("replacement", 43);
            Assert.AreEqual("replacement", (await store.ReadAsync()).AccountName);
            await store.ForgetAsync();
            Assert.IsNull(await store.ReadAsync());
            Assert.IsTrue((await other.ReadAsync()).CanResume);
        });

        [UnityTest]
        public IEnumerator ExpiredOrMissingSecretRequiresPasswordAndKeepsAccountName() => UniTask.ToCoroutine(async () =>
        {
            await SaveAsync();
            var emptyVaultStore = new RememberedLoginStore(path, Endpoint, new MemoryVault());
            Assert.IsFalse((await emptyVaultStore.ReadAsync()).CanResume);
            var db = await SqliteDatabase.OpenAsync(new SqliteOpenOptions(path, SqliteOpenMode.OpenExistingReadWrite));
            try
            {
                await db.ExecuteAsync(new SqliteCommand("UPDATE remembered_login SET expires_utc_ms=1"));
            }
            finally
            {
                await db.CloseAsync();
            }

            Assert.IsFalse((await store.ReadAsync()).CanResume);
            Assert.IsNull(await store.TakeAsync());
            Assert.AreEqual("saved-player", (await store.ReadAsync()).AccountName);
        });

        [UnityTest]
        public IEnumerator VaultFailureAndCanceledAdmissionDoNotPublishRememberedLogin() => UniTask.ToCoroutine(async () =>
        {
            vault.FailWrite = true;
            bool failed = false;
            try
            {
                await SaveAsync();
            }
            catch (IOException)
            {
                failed = true;
            }

            Assert.IsTrue(failed);
            Assert.IsNull(await store.ReadAsync());
            vault.FailWrite = false;
            await SaveAsync();
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            bool canceled = await store.TakeAsync(cancel.Token).SuppressCancellationThrow().ContinueWith(result => result.IsCanceled);
            Assert.IsTrue(canceled);
            Assert.IsTrue((await store.ReadAsync()).CanResume);
        });

#if UNITY_EDITOR_OSX || UNITY_EDITOR_WIN
        [Test]
        public void SystemVaultRoundTripAndDeletion()
        {
            var systemVault = new LoginCredentialVault();
            string key = "GameFrame.Login.Test." + Guid.NewGuid().ToString("N");
            var value = new byte[64];
            new Random().NextBytes(value);
            try
            {
                Assert.IsNull(systemVault.Read(key));
                systemVault.Write(key, value);
                CollectionAssert.AreEqual(value, systemVault.Read(key));
                value[0] ^= 255;
                systemVault.Write(key, value);
                CollectionAssert.AreEqual(value, systemVault.Read(key));
                systemVault.Delete(key);
                Assert.IsNull(systemVault.Read(key));
            }
            finally
            {
                systemVault.Delete(key);
                Array.Clear(value, 0, value.Length);
            }
        }
#endif
    }
}
