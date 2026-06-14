namespace Project.Infrastructure.ComponentTest.Persistence;

/// <summary>
/// L1 placeholder — demonstrates real PostgreSQL connectivity in Infrastructure component tests.
/// Replace with actual repository/store tests once EF Core persistence is implemented.
/// </summary>
[Collection("Aspire")]
public sealed class SamplePersistenceTests(AspireFixture aspire)
{
    [Fact]
    public async Task Database_CanConnect()
    {
        string connStr = aspire.CreateDatabaseConnectionString("infra-component") + ";SSL Mode=Disable";
        await using var connection = new NpgsqlConnection(connStr);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        connection.State.ShouldBe(ConnectionState.Open);
    }

    [Fact]
    public async Task ProjectTestDatabase_CanCreateIsolatedDatabase()
    {
        await using var db = await ProjectTestDatabase.CreateAsync(
            aspire,
            $"infra-component-sample-{Guid.NewGuid():N}",
            TestContext.Current.CancellationToken);

        db.ConnectionString.ShouldNotBeNullOrWhiteSpace();
        db.DatabaseName.ShouldNotBeNullOrWhiteSpace();

        await using var connection = new NpgsqlConnection(db.ConnectionString + ";SSL Mode=Disable");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        connection.State.ShouldBe(ConnectionState.Open);
    }
}
