using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SmoothLlmImposter.Application.Features.Credentials;

namespace SmoothLlmImposter.Host.IntegrationTest;

public sealed class CredentialAdminIntegrationTests(CredentialAppFixture fixture) : IClassFixture<CredentialAppFixture>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Admin_credentials_require_authentication_and_authorization()
    {
        HttpClient client = fixture.CreateClient();

        using HttpResponseMessage anonymous = await client.GetAsync("/admin/credentials", Ct);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/credentials");
        request.Headers.Add("X-Admin-Api-Key", CredentialAppFixture.OperatorKey);
        using HttpResponseMessage forbidden = await client.SendAsync(request, Ct);
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Admin_crud_never_returns_secret_and_activate_enforces_single_active_per_dialect()
    {
        HttpClient client = AuthenticatedClient();

        CredentialResponse first = await CreateAsync(client, "work", "first-secret");
        CredentialResponse second = await CreateAsync(client, "private", "second-secret");

        using HttpResponseMessage activate = await client.PutAsync($"/admin/credentials/{second.Id}/activate", null, Ct);
        string activateBody = await activate.Content.ReadAsStringAsync(Ct);
        activate.StatusCode.ShouldBe(HttpStatusCode.OK, activateBody);

        CredentialResponse[] list = (await client.GetFromJsonAsync<CredentialResponse[]>("/admin/credentials", JsonOptions, Ct))!;
        list.Single(x => x.Id == second.Id).IsActive.ShouldBeTrue();
        list.Single(x => x.Id == first.Id).IsActive.ShouldBeFalse();

        string json = await client.GetStringAsync($"/admin/credentials/{second.Id}", Ct);
        json.ShouldNotContain("second-secret");
        json.ShouldNotContain("secretCiphertext", Case.Insensitive);
    }

    [Fact]
    public async Task Invalid_payload_returns_validation_problem_not_server_error()
    {
        HttpClient client = AuthenticatedClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/admin/credentials",
            new
            {
                providerDialect = "not-a-dialect",
                name = "",
                secret = "",
                authScheme = "Nonsense"
            },
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Active_passthrough_credential_overrides_auth_scheme_and_base_url_without_touching_imposter_route()
    {
        HttpClient client = AuthenticatedClient();
        CredentialResponse credential = await CreateAsync(client, "routing", "stored-secret", "ApiKey", "https://override.openai.test");
        using HttpResponseMessage activate = await client.PutAsync($"/admin/credentials/{credential.Id}/activate", null, Ct);
        string activateBody = await activate.Content.ReadAsStringAsync(Ct);
        activate.StatusCode.ShouldBe(HttpStatusCode.OK, activateBody);

        using HttpResponseMessage passthrough = await client.PostAsync(
            "/v1/chat/completions",
            Json("""{"model":"gpt5.5"}"""),
            Ct);

        passthrough.StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.Upstream.LastRequestUri!.ToString().ShouldBe("https://override.openai.test/v1/chat/completions");
        fixture.Upstream.LastAuthorization.ShouldBeNull();
        fixture.Upstream.LastApiKey.ShouldBe("stored-secret");

        using HttpResponseMessage imposter = await client.PostAsync(
            "/v1/chat/completions",
            Json("""{"model":"gpt5.4"}"""),
            Ct);

        imposter.StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.Upstream.LastRequestUri!.ToString().ShouldBe("https://opencode.test/v1/chat/completions");
        fixture.Upstream.LastAuthorization.ShouldBe("Bearer opencode-key");
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private HttpClient AuthenticatedClient()
    {
        HttpClient client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Api-Key", CredentialAppFixture.AdminKey);
        return client;
    }

    private async Task<CredentialResponse> CreateAsync(
        HttpClient client,
        string name,
        string secret,
        string authScheme = "Bearer",
        string? baseUrlOverride = null)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/admin/credentials",
            new
            {
                providerDialect = "openai",
                name,
                secret,
                authScheme,
                baseUrlOverride
            },
            Ct);

        string json = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, json);
        json.ShouldNotContain(secret);
        json.ShouldNotContain("secretCiphertext", Case.Insensitive);
        return JsonNode.Parse(json)!.Deserialize<CredentialResponse>(JsonOptions)!;
    }
}
