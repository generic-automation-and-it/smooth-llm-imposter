using Microsoft.Data.Sqlite;
using SmoothLlmImposter.Application.Common.Persistence;
using SmoothLlmImposter.Application.Features.GithubCopilot;

namespace SmoothLlmImposter.Infrastructure.Persistence.GithubCopilot;

/// <summary>
/// Feature-scoped SQLite connection store for the opt-in GitHub Copilot integration. Uses
/// Microsoft.Data.Sqlite directly with parameterized ADO.NET commands and explicit row mapping, so it
/// stays NativeAOT-compatible: no reflection, no ref-emit, no ORM on this path. No EF Core here.
/// </summary>
internal sealed class SqliteGithubCopilotConnectionStore(string? connectionString) : IGithubCopilotConnectionStore
{
    private const int SchemaVersion = 1;

    public SqliteConnection Open() => new(connectionString ?? throw new InvalidOperationException(
        $"{GithubCopilotOptions.SectionName}:DatabasePath is not configured; the GitHub Copilot store cannot be opened."));

    public void EnsureCreated()
    {
        EnsureDataDirectoryExists();
        using var connection = Open();
        connection.Open();

        ExecuteNonQuery(
            connection,
            """
            CREATE TABLE IF NOT EXISTS github_copilot_schema (
                Id      INTEGER PRIMARY KEY CHECK (Id = 1),
                Version INTEGER NOT NULL
            );
            """);

        using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "SELECT Version FROM github_copilot_schema WHERE Id = 1;";
        var result = versionCommand.ExecuteScalar();
        if (result is not null and long version && version >= SchemaVersion)
        {
            return; // Current or newer schema already present; never downgrade.
        }

        using var transaction = connection.BeginTransaction();

        ExecuteNonQuery(
            connection,
            """
            CREATE TABLE IF NOT EXISTS github_copilot_connections (
                Prefix                 TEXT PRIMARY KEY,
                ConnectionId           TEXT NOT NULL,
                GitHubUser             TEXT NOT NULL,
                ClientId               TEXT NOT NULL,
                ApiOrigin              TEXT NOT NULL,
                AccessTokenCiphertext  TEXT NOT NULL,
                RefreshTokenCiphertext TEXT NULL,
                ExpiresAtUtc           TEXT NULL,
                Revision               INTEGER NOT NULL
            );
            """,
            transaction);

        using var insertVersion = connection.CreateCommand();
        insertVersion.Transaction = transaction;
        insertVersion.CommandText = "INSERT INTO github_copilot_schema (Id, Version) VALUES (1, @Version);";
        insertVersion.Parameters.AddWithValue("@Version", SchemaVersion);
        insertVersion.ExecuteNonQuery();

        transaction.Commit();
    }

    public void VerifyWritable()
    {
        EnsureDataDirectoryExists();
        using var connection = Open();
        connection.Open();

        var probe = new GithubCopilotConnection(
            Prefix: $"probe-{Guid.NewGuid():N}",
            ConnectionId: Guid.NewGuid().ToString("N"),
            GitHubUser: "native-aot",
            ClientId: "probe",
            ApiOrigin: "https://api.githubcopilot.com",
            AccessTokenCiphertext: "cipher:probe",
            RefreshTokenCiphertext: null,
            ExpiresAtUtc: null,
            Revision: 1);

        // Insert + read inside one transaction, then roll back: the write path is exercised without any
        // durable residue, so a crash mid-probe cannot leave a phantom connection for startup hydration.
        using var transaction = connection.BeginTransaction();

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO github_copilot_connections
                (Prefix, ConnectionId, GitHubUser, ClientId, ApiOrigin,
                 AccessTokenCiphertext, RefreshTokenCiphertext, ExpiresAtUtc, Revision)
            VALUES
                (@Prefix, @ConnectionId, @GitHubUser, @ClientId, @ApiOrigin,
                 @AccessTokenCiphertext, @RefreshTokenCiphertext, @ExpiresAtUtc, @Revision);
            """;
        AddRowParameters(insert, probe);
        insert.ExecuteNonQuery();

        using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT GitHubUser FROM github_copilot_connections WHERE Prefix = @Prefix;";
        read.Parameters.AddWithValue("@Prefix", probe.Prefix);
        if (read.ExecuteScalar() is not string user || user != probe.GitHubUser)
        {
            throw new InvalidOperationException("GitHub Copilot SQLite store write probe failed.");
        }

        transaction.Rollback();
    }

    public void Upsert(GithubCopilotConnection row)
    {
        using var connection = Open();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO github_copilot_connections
                (Prefix, ConnectionId, GitHubUser, ClientId, ApiOrigin,
                 AccessTokenCiphertext, RefreshTokenCiphertext, ExpiresAtUtc, Revision)
            VALUES
                (@Prefix, @ConnectionId, @GitHubUser, @ClientId, @ApiOrigin,
                 @AccessTokenCiphertext, @RefreshTokenCiphertext, @ExpiresAtUtc, @Revision)
            ON CONFLICT(Prefix) DO UPDATE SET
                ConnectionId           = excluded.ConnectionId,
                GitHubUser             = excluded.GitHubUser,
                ClientId               = excluded.ClientId,
                ApiOrigin              = excluded.ApiOrigin,
                AccessTokenCiphertext  = excluded.AccessTokenCiphertext,
                RefreshTokenCiphertext = excluded.RefreshTokenCiphertext,
                ExpiresAtUtc           = excluded.ExpiresAtUtc,
                Revision               = excluded.Revision;
            """;
        AddRowParameters(command, row);
        command.ExecuteNonQuery();
    }

    public GithubCopilotConnection? Get(string prefix)
    {
        using var connection = Open();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Prefix, ConnectionId, GitHubUser, ClientId, ApiOrigin,
                   AccessTokenCiphertext, RefreshTokenCiphertext, ExpiresAtUtc, Revision
            FROM github_copilot_connections
            WHERE Prefix = @Prefix;
            """;
        command.Parameters.AddWithValue("@Prefix", prefix);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRow(reader) : null;
    }

    // SQLite creates the database file but never its parent directory. The Docker volume provides one; a bare
    // configured path may not, so the feature creates it on first enabled use (never while disabled).
    private void EnsureDataDirectoryExists()
    {
        if (connectionString is null)
        {
            return;
        }

        string dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        string? directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public GithubCopilotConnection[] List()
    {
        using var connection = Open();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Prefix, ConnectionId, GitHubUser, ClientId, ApiOrigin,
                   AccessTokenCiphertext, RefreshTokenCiphertext, ExpiresAtUtc, Revision
            FROM github_copilot_connections
            ORDER BY Prefix;
            """;

        var rows = new List<GithubCopilotConnection>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(ReadRow(reader));
        }

        return rows.ToArray();
    }

    public bool Exists(string prefix) => Get(prefix) is not null;

    public int Delete(string prefix)
    {
        using var connection = Open();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM github_copilot_connections WHERE Prefix = @Prefix;";
        command.Parameters.AddWithValue("@Prefix", prefix);
        return command.ExecuteNonQuery();
    }

    private static void AddRowParameters(SqliteCommand command, GithubCopilotConnection row)
    {
        command.Parameters.AddWithValue("@Prefix", row.Prefix);
        command.Parameters.AddWithValue("@ConnectionId", row.ConnectionId);
        command.Parameters.AddWithValue("@GitHubUser", row.GitHubUser);
        command.Parameters.AddWithValue("@ClientId", row.ClientId);
        command.Parameters.AddWithValue("@ApiOrigin", row.ApiOrigin);
        command.Parameters.AddWithValue("@AccessTokenCiphertext", row.AccessTokenCiphertext);
        command.Parameters.AddWithValue("@RefreshTokenCiphertext", (object?)row.RefreshTokenCiphertext ?? DBNull.Value);
        command.Parameters.AddWithValue("@ExpiresAtUtc", (object?)row.ExpiresAtUtc ?? DBNull.Value);
        command.Parameters.AddWithValue("@Revision", row.Revision);
    }

    private static GithubCopilotConnection ReadRow(SqliteDataReader reader) =>
        new(
            Prefix: reader.GetString(0),
            ConnectionId: reader.GetString(1),
            GitHubUser: reader.GetString(2),
            ClientId: reader.GetString(3),
            ApiOrigin: reader.GetString(4),
            AccessTokenCiphertext: reader.GetString(5),
            RefreshTokenCiphertext: reader.IsDBNull(6) ? null : reader.GetString(6),
            ExpiresAtUtc: reader.IsDBNull(7) ? null : reader.GetString(7),
            Revision: reader.GetInt64(8));

    private static void ExecuteNonQuery(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    public static string BuildConnectionString(string databasePath) =>
        new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
}
