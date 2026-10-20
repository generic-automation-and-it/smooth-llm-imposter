using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using SmoothLlmImposter.Infrastructure.Persistence.GithubCopilot;
using Shouldly;

namespace SmoothLlmImposter.Infrastructure.ComponentTest;

/// <summary>
/// L1 — real temporary on-disk SQLite. Proves the Microsoft.Data.Sqlite store round-trips, transacts and
/// reopens, and that the bootstrap never deletes existing data. File I/O belongs here, not in a UnitTest project.
/// </summary>
public sealed class SqliteGithubCopilotConnectionStoreTests : IDisposable
{
    private readonly string _databasePath;
    private readonly string _connectionString;

    public SqliteGithubCopilotConnectionStoreTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"copilot-spike-{Guid.NewGuid():N}.db");
        _connectionString = SqliteGithubCopilotConnectionStore.BuildConnectionString(_databasePath);
    }

    public void Dispose()
    {
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    private SqliteGithubCopilotConnectionStore NewStore() => new(_connectionString);

    private static GithubCopilotConnectionRow Row(string prefix) =>
        new(
            Prefix: prefix,
            ConnectionId: Guid.NewGuid().ToString("N"),
            GitHubUser: "octocat",
            ClientId: "Ov23liufWdEaafvAXgiH",
            ApiOrigin: "https://api.enterprise.githubcopilot.com",
            AccessTokenCiphertext: "cipher:access:abc",
            RefreshTokenCiphertext: "cipher:refresh:xyz",
            ExpiresAtUtc: "2026-11-19T00:00:00Z",
            Revision: 1);

    [Fact]
    public void EnsureCreated_is_idempotent_and_creates_the_connections_table()
    {
        var store = NewStore();
        EnsureSchema(store);

        // A second run must not throw or reset the version row.
        EnsureSchema(store);

        using var connection = store.Open();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Version FROM github_copilot_schema WHERE Id = 1;";
        long version = (long)command.ExecuteScalar()!;
        version.ShouldBe(1);
    }

    [Fact]
    public void Upsert_then_Get_round_trips_all_fields_by_prefix()
    {
        var store = NewStore();
        EnsureSchema(store);
        store.Upsert(Row("company1"));

        var loaded = store.Get("company1");

        loaded.ShouldNotBeNull();
        loaded!.Prefix.ShouldBe("company1");
        loaded.GitHubUser.ShouldBe("octocat");
        loaded.ClientId.ShouldBe("Ov23liufWdEaafvAXgiH");
        loaded.ApiOrigin.ShouldBe("https://api.enterprise.githubcopilot.com");
        loaded.AccessTokenCiphertext.ShouldBe("cipher:access:abc");
        loaded.RefreshTokenCiphertext.ShouldBe("cipher:refresh:xyz");
        loaded.Revision.ShouldBe(1);
    }

    [Fact]
    public void Upsert_replaces_an_existing_prefix_connection()
    {
        var store = NewStore();
        EnsureSchema(store);
        store.Upsert(Row("company1"));

        var updated = Row("company1") with { Revision = 2, GitHubUser = "monalisa" };
        store.Upsert(updated);

        var loaded = store.Get("company1");
        loaded!.Revision.ShouldBe(2);
        loaded.GitHubUser.ShouldBe("monalisa");
    }

    [Fact]
    public void A_rolled_back_transaction_leaves_no_row()
    {
        var store = NewStore();
        EnsureSchema(store);

        var row = Row("company1");
        using (var connection = store.Open())
        {
            connection.Open();
            using var tx = connection.BeginTransaction();
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                INSERT INTO github_copilot_connections
                    (Prefix, ConnectionId, GitHubUser, ClientId, ApiOrigin,
                     AccessTokenCiphertext, RefreshTokenCiphertext, ExpiresAtUtc, Revision)
                VALUES
                    (@Prefix, @ConnectionId, @GitHubUser, @ClientId, @ApiOrigin,
                     @AccessTokenCiphertext, @RefreshTokenCiphertext, @ExpiresAtUtc, @Revision);
                """;
            cmd.Parameters.AddWithValue("@Prefix", row.Prefix);
            cmd.Parameters.AddWithValue("@ConnectionId", row.ConnectionId);
            cmd.Parameters.AddWithValue("@GitHubUser", row.GitHubUser);
            cmd.Parameters.AddWithValue("@ClientId", row.ClientId);
            cmd.Parameters.AddWithValue("@ApiOrigin", row.ApiOrigin);
            cmd.Parameters.AddWithValue("@AccessTokenCiphertext", row.AccessTokenCiphertext);
            cmd.Parameters.AddWithValue("@RefreshTokenCiphertext", (object?)row.RefreshTokenCiphertext ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ExpiresAtUtc", (object?)row.ExpiresAtUtc ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Revision", row.Revision);
            cmd.ExecuteNonQuery();
            tx.Rollback();
        }

        store.Get("company1").ShouldBeNull();
    }

    [Fact]
    public void Data_survives_reopen_with_a_new_store_on_the_same_file()
    {
        var store = NewStore();
        EnsureSchema(store);
        store.Upsert(Row("company1"));

        // Fresh store instance pointing at the same file — a process-restart analogue.
        var reopenedStore = NewStore();
        EnsureSchema(reopenedStore);

        var loaded = reopenedStore.Get("company1");
        loaded.ShouldNotBeNull();
        loaded!.ConnectionId.ShouldBe(store.Get("company1")!.ConnectionId);
    }

    [Fact]
    public void Delete_removes_the_row_and_Existence_tracks_it()
    {
        var store = NewStore();
        EnsureSchema(store);
        store.Upsert(Row("company1"));
        store.Exists("company1").ShouldBeTrue();

        int deleted = store.Delete("company1");

        deleted.ShouldBe(1);
        store.Exists("company1").ShouldBeFalse();
        store.Get("company1").ShouldBeNull();
    }

    private static void EnsureSchema(SqliteGithubCopilotConnectionStore store) => store.EnsureCreated();

    /// <summary>
    /// RL-001 regression: the bootstrap must never delete an existing Copilot database. A real connection row
    /// written before StartAsync must survive, and the probe must leave no residue.
    /// </summary>
    [Fact]
    public async Task Bootstrap_preserves_existing_connections_on_startup()
    {
        var store = NewStore();
        EnsureSchema(store);
        var existing = Row("company1");
        store.Upsert(existing);

        Environment.SetEnvironmentVariable("ENABLE_GITHUB_COPILOT", "true");
        try
        {
            var bootstrap = new GithubCopilotBootstrap(
                NullLogger<GithubCopilotBootstrap>.Instance,
                new StubConfiguration(new() { ["GithubCopilot:DatabasePath"] = _databasePath }));
            await bootstrap.StartAsync(TestContext.Current.CancellationToken);

            var surviving = store.Get("company1");
            surviving.ShouldNotBeNull();
            surviving!.ConnectionId.ShouldBe(existing.ConnectionId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ENABLE_GITHUB_COPILOT", null);
        }
    }

    private sealed class StubConfiguration(Dictionary<string, string?> values) : IConfiguration
    {
        public string? this[string key] { get => values.GetValueOrDefault(key); set => values[key] = value; }
        public IEnumerable<IConfigurationSection> GetChildren() => [];
        public IChangeToken GetReloadToken() => EmptyChangeToken.Instance;
        public IConfigurationSection GetSection(string key) => new StubSection(key, this);
    }

    private sealed class StubSection(string key, IConfiguration root) : IConfigurationSection
    {
        public string Key { get; } = key;
        public string Path => Key;
        public string? Value { get => root[Key]; set => root[Key] = value; }
        public string? this[string k] { get => root[$"{Key}:{k}"]; set => root[$"{Key}:{k}"] = value; }
        public IEnumerable<IConfigurationSection> GetChildren() => [];
        public IChangeToken GetReloadToken() => EmptyChangeToken.Instance;
        public IConfigurationSection GetSection(string k) => new StubSection($"{Key}:{k}", root);
    }

    private sealed class EmptyChangeToken : IChangeToken
    {
        public static readonly EmptyChangeToken Instance = new();
        public bool HasChanged => false;
        public bool ActiveChangeCallbacks => false;
        public IDisposable RegisterChangeCallback(Action<object?> callback, object? state) =>
            throw new NotSupportedException();
    }
}
