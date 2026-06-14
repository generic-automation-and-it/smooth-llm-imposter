using Npgsql;
using Respawn;
using Respawn.Graph;
using System.Data;

namespace Project.TestFramework.Fixtures;

public sealed class DatabaseResetter(string connectionString) : IAsyncDisposable
{
    private Respawner? _respawner;
    private NpgsqlConnection? _connection;

    public async Task ResetAsync()
    {
        _connection ??= new NpgsqlConnection(connectionString);

        if (_connection.State != ConnectionState.Open)
        {
            await _connection.OpenAsync();
        }

        _respawner ??= await Respawner.CreateAsync(_connection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            SchemasToInclude = ["public"],
            TablesToIgnore = [new Table("public", "__EFMigrationsHistory")]
        });

        await _respawner.ResetAsync(_connection);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
    }
}
