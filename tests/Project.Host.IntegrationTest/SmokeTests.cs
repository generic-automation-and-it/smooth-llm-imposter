using System.Net;

namespace Project.Host.IntegrationTest;

public sealed class SmokeTests(HostWebAppFixture fixture) : IClassFixture<HostWebAppFixture>
{
    [Fact]
    public async Task HostBootsAndRespondsToHttp()
    {
        using var response = await fixture.HttpClient.GetAsync("/", TestContext.Current.CancellationToken);

        // The template Host registers no endpoints yet, so an un-routed request returns 404 —
        // proving the app booted and the HTTP pipeline is alive.
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
