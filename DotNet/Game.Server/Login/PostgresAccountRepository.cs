using Npgsql;

namespace Game.Server.Login
{
    internal sealed class PostgresAccountRepository : IAccountRepository
    {
        private readonly NpgsqlDataSource database;

        internal PostgresAccountRepository(NpgsqlDataSource database)
        {
            this.database = database;
        }

        internal async Task ValidateAsync(CancellationToken cancellation)
        {
            await using NpgsqlCommand command = database.CreateCommand(
                "SELECT id, account_name, password_salt, password_hash, password_iterations, auth_epoch FROM accounts LIMIT 0");
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellation);
        }

        public async Task<bool> CreateAsync(string account, byte[] salt, byte[] hash, int iterations, CancellationToken cancellation)
        {
            await using NpgsqlCommand command = database.CreateCommand(
                "INSERT INTO accounts(account_name, password_salt, password_hash, password_iterations) VALUES ($1, $2, $3, $4) " +
                "ON CONFLICT (account_name) DO NOTHING");
            command.Parameters.AddWithValue(account);
            command.Parameters.AddWithValue(salt);
            command.Parameters.AddWithValue(hash);
            command.Parameters.AddWithValue(iterations);
            return await command.ExecuteNonQueryAsync(cancellation) == 1;
        }

        public async Task<StoredAccount?> FindAsync(string account, CancellationToken cancellation)
        {
            await using NpgsqlCommand command = database.CreateCommand(
                "SELECT id, password_salt, password_hash, password_iterations FROM accounts WHERE account_name = $1");
            command.Parameters.AddWithValue(account);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellation);
            if (!await reader.ReadAsync(cancellation))
            {
                return null;
            }

            return new StoredAccount(reader.GetInt64(0), reader.GetFieldValue<byte[]>(1), reader.GetFieldValue<byte[]>(2), reader.GetInt32(3));
        }

        public async Task<ulong> AdvanceEpochAsync(long accountId, CancellationToken cancellation)
        {
            await using NpgsqlCommand command = database.CreateCommand(
                "UPDATE accounts SET auth_epoch = auth_epoch + 1 WHERE id = $1 AND auth_epoch < 9223372036854775807 RETURNING auth_epoch");
            command.Parameters.AddWithValue(accountId);
            object? epoch = await command.ExecuteScalarAsync(cancellation);
            return epoch is long value ? checked((ulong)value) : throw new InvalidOperationException("Account missing or authentication generation exhausted.");
        }
    }
}
