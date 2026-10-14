using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using GameFrame.Sqlite;
using UnityEngine;

namespace Game.Hotfix
{
    public sealed class RememberedAccount
    {
        public string AccountName { get; }
        public ulong AccountId { get; }
        public long ExpiresUtcMilliseconds { get; }
        public bool CanResume { get; internal set; }

        internal RememberedAccount(string name, ulong id, long expires)
        {
            AccountName = name;
            AccountId = id;
            ExpiresUtcMilliseconds = expires;
        }
    }

    public sealed class RememberedLoginCredential : IDisposable
    {
        public RememberedAccount Account { get; }
        public byte[] SessionId { get; } = new byte[16];
        public byte[] Token { get; } = new byte[32];

        internal RememberedLoginCredential(RememberedAccount account, byte[] payload)
        {
            Account = account;
            Buffer.BlockCopy(payload, 16, SessionId, 0, 16);
            Buffer.BlockCopy(payload, 32, Token, 0, 32);
        }

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(SessionId);
            CryptographicOperations.ZeroMemory(Token);
        }
    }

    /// <summary>每个服务端保存最近一个账号。SQLite 只有账号元数据，令牌由系统凭据库拥有。</summary>
    public sealed class RememberedLoginStore
    {
        private const int ApplicationId = 1195789390;
        private readonly string path;
        private readonly string endpoint;
        private readonly string vaultKey;
        private readonly ILoginCredentialVault vault;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);

        public RememberedLoginStore(string absolutePath, Uri loginEndpoint, ILoginCredentialVault credentialVault,
            string credentialNamespace = "remembered-login")
        {
            if (!Path.IsPathRooted(absolutePath))
            {
                throw new ArgumentException("账号数据库必须使用绝对路径。", nameof(absolutePath));
            }

            path = absolutePath;
            if (string.IsNullOrWhiteSpace(credentialNamespace))
            {
                throw new ArgumentException("凭据命名空间不能为空。", nameof(credentialNamespace));
            }

            endpoint = (loginEndpoint ?? throw new ArgumentNullException(nameof(loginEndpoint))).AbsoluteUri;
            vault = credentialVault ?? throw new ArgumentNullException(nameof(credentialVault));
            using var sha = SHA256.Create();
            vaultKey = "GameFrame.Login." + BitConverter.ToString(sha.ComputeHash(
                Encoding.UTF8.GetBytes(Application.identifier + "|" + credentialNamespace + "|" + endpoint))).Replace("-", string.Empty);
        }

        public async UniTask<RememberedAccount> ReadAsync(CancellationToken token = default)
        {
            await gate.WaitAsync(token);
            try
            {
                RememberedAccount account = await ReadMetadataAsync(token);
                if (account == null)
                {
                    return null;
                }

                byte[] payload = vault.Read(vaultKey);
                try
                {
                    account.CanResume = Matches(account, payload);
                    return account;
                }
                finally
                {
                    Clear(payload);
                }
            }
            finally
            {
                gate.Release();
            }
        }

        public async UniTask SaveAsync(string account, ulong id, long expiresUtcMilliseconds,
            byte[] sessionId, byte[] resumeToken, CancellationToken token = default)
        {
            if (!GameLoginSession.IsValidAccount(account) || id == 0 || id > long.MaxValue ||
                expiresUtcMilliseconds <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() ||
                sessionId == null || sessionId.Length != 16 || resumeToken == null || resumeToken.Length != 32)
            {
                throw new ArgumentException("保存的账号或恢复凭据无效。");
            }

            await gate.WaitAsync(token);
            var payload = new byte[64];
            try
            {
                Buffer.BlockCopy(BitConverter.GetBytes(id), 0, payload, 0, 8);
                Buffer.BlockCopy(BitConverter.GetBytes(expiresUtcMilliseconds), 0, payload, 8, 8);
                Buffer.BlockCopy(sessionId, 0, payload, 16, 16);
                Buffer.BlockCopy(resumeToken, 0, payload, 32, 32);
                vault.Write(vaultKey, payload);
                try
                {
                    await WithDatabaseAsync(async db => await db.ExecuteAsync(new SqliteCommand(
                        "INSERT INTO remembered_login(endpoint,account_name,account_id,expires_utc_ms) VALUES(?,?,?,?) " +
                        "ON CONFLICT(endpoint) DO UPDATE SET account_name=excluded.account_name," +
                        "account_id=excluded.account_id,expires_utc_ms=excluded.expires_utc_ms",
                        endpoint, account, (long)id, expiresUtcMilliseconds), token));
                }
                catch
                {
                    // The server may already have rotated credentials. Never retain an ambiguous local pair.
                    try
                    {
                        vault.Delete(vaultKey);
                    }
                    catch (Exception cleanup)
                    {
                        Debug.LogException(cleanup);
                    }

                    throw;
                }
            }
            finally
            {
                Clear(payload);
                gate.Release();
            }
        }

        /// <summary>读取待恢复凭据；建连失败不会消耗系统存储中的令牌。</summary>
        public async UniTask<RememberedLoginCredential> ReadCredentialAsync(CancellationToken token = default)
        {
            await gate.WaitAsync(token);
            byte[] payload = null;
            try
            {
                RememberedAccount account = await ReadMetadataAsync(token);
                if (account == null)
                {
                    return null;
                }

                payload = vault.Read(vaultKey);
                token.ThrowIfCancellationRequested();
                return Matches(account, payload) ? new RememberedLoginCredential(account, payload) : null;
            }
            finally
            {
                Clear(payload);
                gate.Release();
            }
        }

        /// <summary>提交 Resume 前消耗读到的同一凭据。被替换或过期时返回 false，不删除新凭据。</summary>
        public async UniTask<bool> ConsumeAsync(RememberedLoginCredential credential, CancellationToken token = default)
        {
            if (credential == null)
            {
                throw new ArgumentNullException(nameof(credential));
            }

            await gate.WaitAsync(token);
            byte[] payload = null;
            try
            {
                payload = vault.Read(vaultKey);
                if (!Matches(credential.Account, payload) ||
                    !CryptographicOperations.FixedTimeEquals(credential.SessionId, payload.AsSpan(16, 16)) ||
                    !CryptographicOperations.FixedTimeEquals(credential.Token, payload.AsSpan(32, 32)))
                {
                    return false;
                }

                token.ThrowIfCancellationRequested();
                vault.Delete(vaultKey);
                return true;
            }
            finally
            {
                Clear(payload);
                gate.Release();
            }
        }

        /// <summary>仅删除与已失效会话匹配的令牌；迟到的关闭不删除后来保存的凭据。</summary>
        public async UniTask InvalidateCredentialAsync(byte[] expectedTokenHash, CancellationToken token = default)
        {
            if (expectedTokenHash == null || expectedTokenHash.Length != 32)
            {
                throw new ArgumentException("恢复令牌摘要必须为 32 字节。", nameof(expectedTokenHash));
            }

            await gate.WaitAsync(token);
            byte[] payload = null;
            try
            {
                payload = vault.Read(vaultKey);
                if (payload != null && payload.Length == 64)
                {
                    using var sha = SHA256.Create();
                    byte[] currentHash = sha.ComputeHash(payload, 32, 32);
                    if (CryptographicOperations.FixedTimeEquals(expectedTokenHash, currentHash))
                    {
                        vault.Delete(vaultKey);
                    }
                }
            }
            finally
            {
                Clear(payload);
                gate.Release();
            }
        }

        public async UniTask ForgetAsync(CancellationToken token = default)
        {
            await gate.WaitAsync(token);
            try
            {
                vault.Delete(vaultKey);
                await WithDatabaseAsync(async db => await db.ExecuteAsync(new SqliteCommand(
                    "DELETE FROM remembered_login WHERE endpoint=?", endpoint), token));
            }
            finally
            {
                gate.Release();
            }
        }

        private UniTask<RememberedAccount> ReadMetadataAsync(CancellationToken token) => WithDatabaseAsync(async db =>
        {
            var rows = await db.QueryPageAsync(new SqliteCommand(
                "SELECT account_name,account_id,expires_utc_ms FROM remembered_login WHERE endpoint=?", endpoint),
                new SqliteQueryBudget(1, 1024), row => new RememberedAccount(row.GetString(0),
                    checked((ulong)row.GetInt64(1)), row.GetInt64(2)), token);
            return rows.Count == 0 ? null : rows[0];
        });

        private async UniTask<T> WithDatabaseAsync<T>(Func<SqliteDatabase, Task<T>> action)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            bool exists = File.Exists(path);
            SqliteDatabase db = await SqliteDatabase.OpenAsync(new SqliteOpenOptions(path,
                exists ? SqliteOpenMode.OpenExistingReadWrite : SqliteOpenMode.CreateNew));
            Exception failure = null;
            try
            {
                if (!exists)
                {
                    using var result = await db.ExecuteTransactionAsync(new SqliteBatch(new SqliteQueryBudget(1, 128),
                        new SqliteCommand("PRAGMA application_id=" + ApplicationId),
                        new SqliteCommand("PRAGMA user_version=1"),
                        new SqliteCommand("CREATE TABLE remembered_login(endpoint TEXT PRIMARY KEY,account_name TEXT NOT NULL," +
                            "account_id INTEGER NOT NULL CHECK(account_id>0),expires_utc_ms INTEGER NOT NULL) STRICT")));
                }
                else
                {
                    var ids = await db.QueryPageAsync(new SqliteCommand("PRAGMA application_id"),
                        new SqliteQueryBudget(1, 64), row => row.GetInt64(0));
                    var versions = await db.QueryPageAsync(new SqliteCommand("PRAGMA user_version"),
                        new SqliteQueryBudget(1, 64), row => row.GetInt64(0));
                    if (ids[0] != ApplicationId || versions[0] != 1)
                    {
                        throw new InvalidOperationException("账号数据库格式不匹配，不会覆盖既有文件。");
                    }
                }

                return await action(db);
            }
            catch (Exception error)
            {
                failure = error;
                throw;
            }
            finally
            {
                try
                {
                    await db.CloseAsync();
                }
                catch (Exception cleanup) when (failure != null)
                {
                    Debug.LogException(cleanup);
                }
            }
        }

        private static bool Matches(RememberedAccount account, byte[] payload) => payload != null &&
            payload.Length == 64 && account.AccountId == BitConverter.ToUInt64(payload, 0) &&
            account.ExpiresUtcMilliseconds == BitConverter.ToInt64(payload, 8) &&
            account.ExpiresUtcMilliseconds > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private static void Clear(byte[] value)
        {
            if (value != null)
            {
                CryptographicOperations.ZeroMemory(value);
            }
        }
    }
}
