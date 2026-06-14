namespace Project.Application.ComponentTest.Features;

/// <summary>
/// L1 placeholder — demonstrates WireMock stub/call roundtrip in Application component tests.
/// Replace with real feature tests once application use-cases are implemented.
/// </summary>
[Collection("Aspire")]
public sealed class SampleApplicationComponentTests(AspireFixture aspire)
{
    [Fact]
    public void WireMockBaseUrl_IsAvailable()
    {
        aspire.WireMockBaseUrl.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task WireMock_CanStubAndReceiveJsonResponse()
    {
        await using var admin = aspire.CreateWireMockAdminClient();
        await admin.StubJsonResponseAsync("GET", "/api/ping", new { status = "ok" }, cancellationToken: TestContext.Current.CancellationToken);

        using var http = new System.Net.Http.HttpClient
        {
            BaseAddress = new Uri(aspire.WireMockBaseUrl)
        };
        using var response = await http.GetAsync("/api/ping", TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.ShouldBeTrue();
    }
}
