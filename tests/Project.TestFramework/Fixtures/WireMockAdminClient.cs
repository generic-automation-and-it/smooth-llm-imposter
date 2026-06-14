using System.Net.Http;
using System.Net.Http.Json;

namespace Project.TestFramework.Fixtures;

public sealed class WireMockAdminClient(HttpClient httpClient) : IAsyncDisposable
{
    public static WireMockAdminClient Create(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("WireMock base URL is not available.");
        }

        return new WireMockAdminClient(new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/")
        });
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await SendAsync(HttpMethod.Delete, "__admin/mappings", cancellationToken);
        await SendAsync(HttpMethod.Delete, "__admin/requests", cancellationToken);
    }

    public async Task StubJsonResponseAsync(
        string method,
        string path,
        object responseBody,
        int statusCode = 200,
        CancellationToken cancellationToken = default)
    {
        var request = new
        {
            request = new
            {
                method,
                urlPath = path
            },
            response = new
            {
                status = statusCode,
                headers = new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/json"
                },
                jsonBody = responseBody
            }
        };

        using HttpResponseMessage response = await httpClient.PostAsJsonAsync("__admin/mappings", request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public ValueTask DisposeAsync()
    {
        httpClient.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task SendAsync(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
