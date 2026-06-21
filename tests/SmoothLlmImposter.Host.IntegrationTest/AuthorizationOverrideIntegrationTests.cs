using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SmoothLlmImposter.Application.Features.AuthorizationOverride;
using SmoothLlmImposter.Application.Features.Credentials;

namespace SmoothLlmImposter.Host.IntegrationTest;

/// <summary>
/// L2 coverage for the passthrough authorization override (HLD 003). A fresh <see cref="CredentialAppFixture"/>
/// per test gives an isolated DI container — the in-memory switch and credential store start empty, which also
/// exercises the fail-safe "OFF on (re)start" default (NFR-003).
/// </summary>
public sealed class AuthorizationOverrideIntegrationTests
{
    private const string OverridePath = "/routing/openai/override-authorization";
    private const string ProviderOverridePath = "/routing/openai/openai-official/override-authorization";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Toggle_endpoints_require_admin_authorization()
    {
        using var fixture = new CredentialAppFixture();
        HttpClient client = fixture.CreateClient();

        using HttpResponseMessage anonymous = await client.PutAsync(OverridePath, content: null, Ct);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var operatorRequest = new HttpRequestMessage(HttpMethod.Put, OverridePath);
        operatorRequest.Headers.Add("X-Admin-Api-Key", CredentialAppFixture.OperatorKey);
        using HttpResponseMessage forbidden = await client.SendAsync(operatorRequest, Ct);
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("gemini")]
    [InlineData("unknown")]
    public async Task Unknown_dialect_is_rejected_with_400(string dialect)
    {
        using var fixture = new CredentialAppFixture();
        HttpClient client = AdminClient(fixture);

        using HttpResponseMessage put = await client.PutAsync($"/routing/{dialect}/override-authorization", content: null, Ct);
        put.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Arming_without_an_active_credential_returns_403_and_leaves_switch_off()
    {
        using var fixture = new CredentialAppFixture();
        HttpClient client = AdminClient(fixture);

        using HttpResponseMessage put = await client.PutAsync(OverridePath, content: null, Ct);
        put.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await GetStateAsync(client)).Enabled.ShouldBeFalse();
    }

    [Fact]
    public async Task Armed_passthrough_forces_bearer_and_drops_x_api_key_even_for_apikey_scheme()
    {
        using var fixture = new CredentialAppFixture();
        HttpClient client = AdminClient(fixture);
        await CreateAndActivateAsync(client, "stored-secret", "ApiKey");

        using HttpResponseMessage put = await client.PutAsync(OverridePath, content: null, Ct);
        AuthorizationOverrideState armed = await ReadStateAsync(put);
        armed.ShouldBe(new AuthorizationOverrideState("openai", "openai-official", true));

        using HttpResponseMessage passthrough = await client.PostAsync("/v1/chat/completions", Json("""{"model":"gpt5.5"}"""), Ct);
        passthrough.StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.Upstream.LastAuthorization.ShouldBe("Bearer stored-secret");
        fixture.Upstream.LastApiKey.ShouldBeNull();
    }

    [Fact]
    public async Task Disarming_reverts_to_hld_002_behaviour()
    {
        using var fixture = new CredentialAppFixture();
        HttpClient client = AdminClient(fixture);
        await CreateAndActivateAsync(client, "stored-secret", "ApiKey");

        using (HttpResponseMessage put = await client.PutAsync(OverridePath, content: null, Ct))
        {
            put.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using HttpResponseMessage delete = await client.DeleteAsync(OverridePath, Ct);
        (await ReadStateAsync(delete)).ShouldBe(new AuthorizationOverrideState("openai", "openai-official", false));

        using HttpResponseMessage passthrough = await client.PostAsync("/v1/chat/completions", Json("""{"model":"gpt5.5"}"""), Ct);
        passthrough.StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.Upstream.LastApiKey.ShouldBe("stored-secret");
        fixture.Upstream.LastAuthorization.ShouldBeNull();
    }

    [Fact]
    public async Task Get_reflects_state_across_put_and_delete()
    {
        using var fixture = new CredentialAppFixture();
        HttpClient client = AdminClient(fixture);
        await CreateAndActivateAsync(client, "stored-secret", "Bearer");

        (await GetStateAsync(client)).Enabled.ShouldBeFalse();

        using (await client.PutAsync(OverridePath, content: null, Ct)) { }
        (await GetStateAsync(client)).Enabled.ShouldBeTrue();

        using (await client.DeleteAsync(OverridePath, Ct)) { }
        (await GetStateAsync(client)).Enabled.ShouldBeFalse();
    }

    [Fact]
    public async Task Provider_addressable_route_controls_that_provider()
    {
        using var fixture = new CredentialAppFixture();
        HttpClient client = AdminClient(fixture);
        await CreateAndActivateAsync(client, "stored-secret", "Bearer", providerName: "openai-official");

        using HttpResponseMessage put = await client.PutAsync(ProviderOverridePath, content: null, Ct);
        (await ReadStateAsync(put)).ShouldBe(new AuthorizationOverrideState("openai", "openai-official", true));

        AuthorizationOverrideState defaultState = await GetStateAsync(client);
        defaultState.ShouldBe(new AuthorizationOverrideState("openai", "openai-official", true));
    }

    [Fact]
    public async Task Armed_then_credential_removed_fails_closed_with_dialect_shaped_403()
    {
        using var fixture = new CredentialAppFixture();
        HttpClient client = AdminClient(fixture);
        Guid credentialId = await CreateAndActivateAsync(client, "stored-secret", "ApiKey");

        using (HttpResponseMessage put = await client.PutAsync(OverridePath, content: null, Ct))
        {
            put.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using HttpResponseMessage deleteCredential = await client.DeleteAsync($"/admin/credentials/{credentialId}", Ct);
        deleteCredential.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using HttpResponseMessage passthrough = await client.PostAsync("/v1/chat/completions", Json("""{"model":"gpt5.5"}"""), Ct);
        passthrough.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        string body = await passthrough.Content.ReadAsStringAsync(Ct);
        JsonNode error = JsonNode.Parse(body)!["error"]!;
        error["type"]!.GetValue<string>().ShouldBe("permission_error");
        body.ShouldNotContain("stored-secret");
    }

    [Fact]
    public async Task Matched_imposter_route_is_identical_with_override_on_or_off()
    {
        using var fixture = new CredentialAppFixture();
        HttpClient client = AdminClient(fixture);
        await CreateAndActivateAsync(client, "stored-secret", "ApiKey");

        using (HttpResponseMessage put = await client.PutAsync(OverridePath, content: null, Ct))
        {
            put.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (HttpResponseMessage onImposter = await client.PostAsync("/v1/chat/completions", Json("""{"model":"gpt5.4"}"""), Ct))
        {
            onImposter.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        fixture.Upstream.LastRequestUri!.ToString().ShouldBe("https://opencode.test/v1/chat/completions");
        fixture.Upstream.LastAuthorization.ShouldBe("Bearer opencode-key");
        fixture.Upstream.LastApiKey.ShouldBeNull();

        using (await client.DeleteAsync(OverridePath, Ct)) { }

        using (HttpResponseMessage offImposter = await client.PostAsync("/v1/chat/completions", Json("""{"model":"gpt5.4"}"""), Ct))
        {
            offImposter.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        fixture.Upstream.LastRequestUri!.ToString().ShouldBe("https://opencode.test/v1/chat/completions");
        fixture.Upstream.LastAuthorization.ShouldBe("Bearer opencode-key");
        fixture.Upstream.LastApiKey.ShouldBeNull();
    }

    private static HttpClient AdminClient(CredentialAppFixture fixture)
    {
        HttpClient client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Api-Key", CredentialAppFixture.AdminKey);
        return client;
    }

    private async Task<AuthorizationOverrideState> GetStateAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<AuthorizationOverrideState>(OverridePath, JsonOptions, Ct))!;

    private async Task<AuthorizationOverrideState> ReadStateAsync(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        return JsonNode.Parse(body)!.Deserialize<AuthorizationOverrideState>(JsonOptions)!;
    }

    private async Task<Guid> CreateAndActivateAsync(HttpClient client, string secret, string authScheme, string? providerName = null)
    {
        using HttpResponseMessage created = await client.PostAsJsonAsync(
            "/admin/credentials",
            new { providerDialect = "openai", providerName, name = "routing", secret, authScheme, baseUrlOverride = (string?)null },
            Ct);
        string json = await created.Content.ReadAsStringAsync(Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, json);
        CredentialResponse credential = JsonNode.Parse(json)!.Deserialize<CredentialResponse>(JsonOptions)!;

        using HttpResponseMessage activate = await client.PutAsync($"/admin/credentials/{credential.Id}/activate", content: null, Ct);
        activate.StatusCode.ShouldBe(HttpStatusCode.OK, await activate.Content.ReadAsStringAsync(Ct));
        return credential.Id;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
}
