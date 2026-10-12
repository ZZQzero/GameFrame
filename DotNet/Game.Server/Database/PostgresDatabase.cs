using Npgsql;

namespace Game.Server.Database
{
    internal sealed class PostgresDatabase : IAsyncDisposable
    {
        // Repositories borrow this pool; only the application owner disposes it.
        internal NpgsqlDataSource DataSource { get; }

        internal PostgresDatabase(string connectionString, string searchPath = "public")
        {
            var options = new NpgsqlConnectionStringBuilder(connectionString)
            {
                MaxPoolSize = 16,
                Timeout = 5,
                CommandTimeout = 5,
                IncludeErrorDetail = false,
                SearchPath = searchPath
            };
            DataSource = NpgsqlDataSource.Create(options.ConnectionString);
        }

        internal async Task ValidateAsync(CancellationToken cancellation)
        {
            await using NpgsqlCommand command = DataSource.CreateCommand("SELECT 1");
            await command.ExecuteScalarAsync(cancellation);
        }

        public ValueTask DisposeAsync() => DataSource.DisposeAsync();
    }
}
