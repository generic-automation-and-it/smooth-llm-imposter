using Npgsql;

namespace Project.TestFramework.Fixtures;

public static class PostgreSqlDatabaseManager
{
    public static async Task DropDatabaseIfExistsAsync(string maintenanceConnectionString, string databaseName)
    {
        await using var connection = new NpgsqlConnection(maintenanceConnectionString);
        await connection.OpenAsync();

        string quotedDatabaseName = QuoteIdentifier(databaseName);

        await using (var terminateCommand = new NpgsqlCommand(
            """
            SELECT pg_terminate_backend(pid)
            FROM pg_stat_activity
            WHERE datname = @databaseName
              AND pid <> pg_backend_pid();
            """,
            connection))
        {
            terminateCommand.Parameters.AddWithValue("databaseName", databaseName);
            await terminateCommand.ExecuteNonQueryAsync();
        }

        await using var dropCommand = new NpgsqlCommand(
            $"DROP DATABASE IF EXISTS {quotedDatabaseName};",
            connection);
        await dropCommand.ExecuteNonQueryAsync();
    }

    public static async Task CreateDatabaseAsync(string maintenanceConnectionString, string databaseName)
    {
        await using var connection = new NpgsqlConnection(maintenanceConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            $"CREATE DATABASE {QuoteIdentifier(databaseName)};",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task RecreateDatabaseAsync(string maintenanceConnectionString, string databaseName)
    {
        await DropDatabaseIfExistsAsync(maintenanceConnectionString, databaseName);
        await CreateDatabaseAsync(maintenanceConnectionString, databaseName);
    }

    private static string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("Database name must be provided.", nameof(identifier));
        }

        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
