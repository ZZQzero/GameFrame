using Game.Server.Database;
using Npgsql;
using NUnit.Framework;

namespace GameFrame.Tests
{
    internal sealed class PostgresTestDatabase : IAsyncDisposable
    {
        private readonly NpgsqlDataSource admin;
        private readonly string schema;
        internal PostgresDatabase Pool { get; }
        internal NpgsqlDataSource Database => Pool.DataSource;

        private PostgresTestDatabase(NpgsqlDataSource admin, PostgresDatabase database, string schema)
        {
            this.admin = admin;
            Pool = database;
            this.schema = schema;
        }

        internal static async Task<PostgresTestDatabase> CreateAsync()
        {
            string? connection = Environment.GetEnvironmentVariable("GF_POSTGRES_TEST_CONNECTION");
            if (string.IsNullOrWhiteSpace(connection))
            {
                Assert.Ignore("Real PostgreSQL integration requires GF_POSTGRES_TEST_CONNECTION; no database test was run.");
            }

            var options = new NpgsqlConnectionStringBuilder(connection!) { Timeout = 5, CommandTimeout = 5, MaxPoolSize = 8 };
            NpgsqlDataSource admin = NpgsqlDataSource.Create(options.ConnectionString);
            string schema = "gf_test_" + Guid.NewGuid().ToString("N");
            await using (NpgsqlCommand create = admin.CreateCommand($"CREATE SCHEMA {schema}"))
            {
                await create.ExecuteNonQueryAsync();
            }

            var database = new PostgresDatabase(options.ConnectionString, schema);
            var test = new PostgresTestDatabase(admin, database, schema);
            try
            {
                await database.ValidateAsync(CancellationToken.None);
                string sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Database", "001_accounts.sql"));
                await using NpgsqlCommand initialize = database.DataSource.CreateCommand(sql);
                await initialize.ExecuteNonQueryAsync();
                return test;
            }
            catch
            {
                await test.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Pool.DisposeAsync();
            try
            {
                // Only the unique schema created by this fixture is removed.
                await using NpgsqlCommand drop = admin.CreateCommand($"DROP SCHEMA {schema} CASCADE");
                await drop.ExecuteNonQueryAsync();
            }
            finally
            {
                await admin.DisposeAsync();
            }
        }
    }
}
