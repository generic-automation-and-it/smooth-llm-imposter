extern alias HostApp;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmoothLlmImposter.Application.Features.ProviderConfiguration;

namespace SmoothLlmImposter.Host.IntegrationTest;

public sealed class ProviderConfigAdminIntegrationTests
{
    private const string AdminKey = "admin-secret";
    private const string OperatorKey = "operator-secret";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Admin_providers_require_authentication_and_authorization()
    {
        using var fixture = new ProviderConfigAppFixture();
        HttpClient client = fixture.CreateClient();

        using HttpResponseMessage anonymous = await client.GetAsync("/admin/providers", Ct);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/providers");
        request.Headers.Add("X-Admin-Api-Key", OperatorKey);
        using HttpResponseMessage forbidden = await client.SendAsync(request, Ct);
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Get_and_list_are_secret_free_and_get_round_trips_into_put()
    {
        using var fixture = new ProviderConfigAppFixture();
        HttpClient client = AuthenticatedClient(fixture);

        string listJson = await client.GetStringAsync("/admin/providers", Ct);
        listJson.ShouldNotContain("opencode-key");
        listJson.ShouldNotContain("secret", Case.Insensitive);

        string getJson = await client.GetStringAsync("/admin/providers/opencode-go", Ct);
        getJson.ShouldNotContain("opencode-key");
        getJson.ShouldNotContain("secret", Case.Insensitive);

        // GET round-trips the seeded SessionForwarding so a regression in the From(...) projection
        // (which would silently drop a non-null value) surfaces here. StripEncryptedContent (HLD 011) rides
        // the same projection and additionally proves the registry seed's clone preserved it.
        ProviderConfigurationBody body = JsonNode.Parse(getJson)!.Deserialize<ProviderConfigurationBody>(JsonOptions)!;
        body.SessionForwarding.ShouldBe("opencode-go");
        body.StripEncryptedContent.ShouldBe(true);
        body.TimeoutSeconds.ShouldBe(450);

        using HttpResponseMessage put = await client.PutAsJsonAsync("/admin/providers/opencode-go", body, JsonOptions, Ct);

        string putJson = await put.Content.ReadAsStringAsync(Ct);
        put.StatusCode.ShouldBe(HttpStatusCode.OK, putJson);
        putJson.ShouldNotContain("opencode-key");
        putJson.ShouldNotContain("secret", Case.Insensitive);
    }

    [Fact]
    public async Task Upsert_mutation_changes_the_next_proxied_request_without_restart_and_preserves_secret()
    {
        using var fixture = new ProviderConfigAppFixture();
        HttpClient client = AuthenticatedClient(fixture);

        using HttpResponseMessage before = await client.PostAsync(
            "/v1/chat/completions",
            Json("""{"model":"gpt5.4","messages":[{"role":"user","content":"hi"}]}"""),
            Ct);
        before.StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.Upstream.LastRequestUri!.ToString().ShouldBe("https://opencode.test/v1/chat/completions");

        ProviderConfigurationBody update = Body(
            baseUrl: "https://runtime-opencode.test",
            models: [new ProviderModelMappingBody("gpt5.4", "runtime-code", Caching: false)]);

        using HttpResponseMessage put = await client.PutAsJsonAsync("/admin/providers/opencode-go", update, JsonOptions, Ct);
        string putBody = await put.Content.ReadAsStringAsync(Ct);
        put.StatusCode.ShouldBe(HttpStatusCode.OK, putBody);

        using HttpResponseMessage after = await client.PostAsync(
            "/v1/chat/completions",
            Json("""{"model":"gpt5.4","messages":[{"role":"user","content":"hi"}]}"""),
            Ct);

        after.StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.Upstream.LastRequestUri!.ToString().ShouldBe("https://runtime-opencode.test/v1/chat/completions");
        fixture.Upstream.LastApiKey.ShouldBe("opencode-key");

        JsonNode forwarded = JsonNode.Parse(fixture.Upstream.LastRequestBody!)!;
        forwarded["model"]!.GetValue<string>().ShouldBe("runtime-code");
    }

    [Fact]
    public async Task Disable_and_enable_take_effect_on_next_request_without_losing_config()
    {
        using var fixture = new ProviderConfigAppFixture();
        HttpClient client = AuthenticatedClient(fixture);

        using HttpResponseMessage disable = await client.PutAsync("/admin/providers/opencode-go/disable", null, Ct);
        string disableBody = await disable.Content.ReadAsStringAsync(Ct);
        disable.StatusCode.ShouldBe(HttpStatusCode.OK, disableBody);

        using HttpResponseMessage disabledRoute = await client.PostAsync(
            "/v1/chat/completions",
            Json("""{"model":"gpt5.4"}"""),
            Ct);

        disabledRoute.StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.Upstream.LastRequestUri!.ToString().ShouldBe("https://api.openai.test/v1/chat/completions");
        fixture.Upstream.LastAuthorization.ShouldBe("Bearer openai-key");

        ProviderConfigurationResponse disabledConfig = (await client.GetFromJsonAsync<ProviderConfigurationResponse>("/admin/providers/opencode-go", JsonOptions, Ct))!;
        disabledConfig.Enabled.ShouldBeFalse();
        disabledConfig.Models.Single().To.ShouldBe("grok-code");

        using HttpResponseMessage enable = await client.PutAsync("/admin/providers/opencode-go/enable", null, Ct);
        string enableBody = await enable.Content.ReadAsStringAsync(Ct);
        enable.StatusCode.ShouldBe(HttpStatusCode.OK, enableBody);

        using HttpResponseMessage enabledRoute = await client.PostAsync(
            "/v1/chat/completions",
            Json("""{"model":"gpt5.4"}"""),
            Ct);

        enabledRoute.StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.Upstream.LastRequestUri!.ToString().ShouldBe("https://opencode.test/v1/chat/completions");
    }

    [Fact]
    public async Task Delete_removes_route_and_insert_adds_a_new_runtime_provider()
    {
        using var fixture = new ProviderConfigAppFixture();
        HttpClient client = AuthenticatedClient(fixture);

        using HttpResponseMessage delete = await client.DeleteAsync("/admin/providers/opencode-go", Ct);
        delete.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using HttpResponseMessage afterDelete = await client.PostAsync("/v1/chat/completions", Json("""{"model":"gpt5.4"}"""), Ct);
        afterDelete.StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.Upstream.LastRequestUri!.ToString().ShouldBe("https://api.openai.test/v1/chat/completions");

        ProviderConfigurationBody insert = Body(
            baseUrl: "https://inserted.test",
            authScheme: null,
            models: [new ProviderModelMappingBody("gpt5.4", "inserted-code", Caching: false)]);
        using HttpResponseMessage put = await client.PutAsJsonAsync("/admin/providers/inserted", insert, JsonOptions, Ct);
        string putBody = await put.Content.ReadAsStringAsync(Ct);
        put.StatusCode.ShouldBe(HttpStatusCode.OK, putBody);

        using HttpResponseMessage afterInsert = await client.PostAsync("/v1/chat/completions", Json("""{"model":"gpt5.4"}"""), Ct);
        afterInsert.StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.Upstream.LastRequestUri!.ToString().ShouldBe("https://inserted.test/v1/chat/completions");
        fixture.Upstream.LastAuthorization.ShouldBeNull();
        fixture.Upstream.LastApiKey.ShouldBeNull();
    }

    [Fact]
    public async Task Fresh_host_reseeds_from_config_and_discards_runtime_edits()
    {
        using (var fixture = new ProviderConfigAppFixture())
        {
            HttpClient client = AuthenticatedClient(fixture);
            ProviderConfigurationBody update = Body(baseUrl: "https://runtime-opencode.test");

            using HttpResponseMessage put = await client.PutAsJsonAsync("/admin/providers/opencode-go", update, JsonOptions, Ct);
            put.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var fresh = new ProviderConfigAppFixture();
        HttpClient freshClient = AuthenticatedClient(fresh);

        ProviderConfigurationResponse provider = (await freshClient.GetFromJsonAsync<ProviderConfigurationResponse>("/admin/providers/opencode-go", JsonOptions, Ct))!;
        provider.BaseUrl.ShouldBe("https://opencode.test");
    }

    [Fact]
    public async Task Runtime_upsert_wins_over_environment_override_and_env_does_not_reassert()
    {
        // Conventional env surface (HLD 007) seeds the registry at startup; HLD 008 LADR-04 requires that a
        // later runtime PUT win over it AND that env not re-assert after startup.
        using var fixture = new ProviderConfigAppFixture(new Dictionary<string, string?>
        {
            ["OPENCODE_GO_BASE_URL"] = "https://env-opencode.test"
        });
        HttpClient client = AuthenticatedClient(fixture);

        // Env override won at seed time over the structured BaseUrl (https://opencode.test).
        ProviderConfigurationResponse seeded = (await client.GetFromJsonAsync<ProviderConfigurationResponse>(
            "/admin/providers/opencode-go", JsonOptions, Ct))!;
        seeded.BaseUrl.ShouldBe("https://env-opencode.test");

        // Runtime PUT overrides the env-seeded value.
        ProviderConfigurationBody update = Body(baseUrl: "https://runtime-opencode.test");
        using HttpResponseMessage put = await client.PutAsJsonAsync("/admin/providers/opencode-go", update, JsonOptions, Ct);
        put.StatusCode.ShouldBe(HttpStatusCode.OK, await put.Content.ReadAsStringAsync(Ct));

        // Next proxied request routes to the runtime value — env does not reapply on the fresh snapshot.
        using HttpResponseMessage after = await client.PostAsync(
            "/v1/chat/completions",
            Json("""{"model":"gpt5.4","messages":[{"role":"user","content":"hi"}]}"""),
            Ct);

        after.StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.Upstream.LastRequestUri!.ToString().ShouldBe("https://runtime-opencode.test/v1/chat/completions");
    }

    [Fact]
    public async Task Invalid_upsert_returns_validation_problem_not_server_error()
    {
        using var fixture = new ProviderConfigAppFixture();
        HttpClient client = AuthenticatedClient(fixture);

        ProviderConfigurationBody invalid = Body(baseUrl: "not-a-url") with { Dialect = "bogus" };
        using HttpResponseMessage response = await client.PutAsJsonAsync("/admin/providers/bad", invalid, JsonOptions, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Admin_round_trip_preserves_anthropic_chat_bridge_options()
    {
        // HLD 016 option plumbing: an omission from the admin DTOs or the registry clone silently clears
        // the value on upsert (the HLD 011 failure mode), so GET -> PUT -> GET must preserve every field.
        using var fixture = new ProviderConfigAppFixture(new Dictionary<string, string?>
        {
            ["Imposter:Providers:chat-bridge:Dialect"] = "anthropic",
            ["Imposter:Providers:chat-bridge:BaseUrl"] = "https://chat.test",
            ["Imposter:Providers:chat-bridge:AuthScheme"] = "Bearer",
            ["Imposter:Providers:chat-bridge:AnthropicUpstreamApi"] = "chat_completions",
            ["Imposter:Providers:chat-bridge:CompatProfile"] = "deepseek",
            ["Imposter:Providers:chat-bridge:SendReasoningEffort"] = "true",
            ["Imposter:Providers:chat-bridge:UserIdMode"] = "hash",
            ["Imposter:Providers:chat-bridge:MaxTokensField"] = "max_completion_tokens",
            ["Imposter:Providers:chat-bridge:MaxOutputTokens"] = "8192",
            ["Imposter:Providers:chat-bridge:ForwardCacheControl"] = "true",
            ["Imposter:Providers:chat-bridge:SendResponseFormat"] = "true",
            ["Imposter:Providers:chat-bridge:DocumentMode"] = "image",
            ["Imposter:Providers:chat-bridge:ThinkMarkupExtraction"] = "true",
            ["Imposter:Providers:chat-bridge:MidturnSystem"] = "merge_into_user",
            ["Imposter:Providers:chat-bridge:Models:0:From"] = "claude-*",
            ["Imposter:Providers:chat-bridge:Models:0:To"] = "chat-model"
        });
        HttpClient client = AuthenticatedClient(fixture);

        string getJson = await client.GetStringAsync("/admin/providers/chat-bridge", Ct);
        ProviderConfigurationBody body = JsonNode.Parse(getJson)!.Deserialize<ProviderConfigurationBody>(JsonOptions)!;
        body.AnthropicUpstreamApi.ShouldBe("chat_completions");
        body.CompatProfile.ShouldBe("deepseek");
        body.SendReasoningEffort.ShouldBe(true);
        body.UserIdMode.ShouldBe("hash");
        body.MaxTokensField.ShouldBe("max_completion_tokens");
        body.MaxOutputTokens.ShouldBe(8192);
        body.ForwardCacheControl.ShouldBe(true);
        body.SendResponseFormat.ShouldBe(true);
        body.DocumentMode.ShouldBe("image");
        body.ThinkMarkupExtraction.ShouldBe(true);
        body.MidturnSystem.ShouldBe("merge_into_user");

        using HttpResponseMessage put = await client.PutAsJsonAsync("/admin/providers/chat-bridge", body, JsonOptions, Ct);
        put.StatusCode.ShouldBe(HttpStatusCode.OK, await put.Content.ReadAsStringAsync(Ct));

        string afterJson = await client.GetStringAsync("/admin/providers/chat-bridge", Ct);
        ProviderConfigurationBody after = JsonNode.Parse(afterJson)!.Deserialize<ProviderConfigurationBody>(JsonOptions)!;
        after.AnthropicUpstreamApi.ShouldBe("chat_completions");
        after.CompatProfile.ShouldBe("deepseek");
        after.SendReasoningEffort.ShouldBe(true);
        after.UserIdMode.ShouldBe("hash");
        after.MaxTokensField.ShouldBe("max_completion_tokens");
        after.MaxOutputTokens.ShouldBe(8192);
        after.ForwardCacheControl.ShouldBe(true);
        after.SendResponseFormat.ShouldBe(true);
        after.DocumentMode.ShouldBe("image");
        after.ThinkMarkupExtraction.ShouldBe(true);
        after.MidturnSystem.ShouldBe("merge_into_user");
    }

    [Fact]
    public async Task Admin_round_trip_preserves_the_message_optimizer_opt_out()
    {
        // HLD 017 option plumbing: an omission from the admin DTOs or the registry clone would clear the
        // opt-out on upsert, silently turning message optimization back on for an opted-out route.
        using var fixture = new ProviderConfigAppFixture(new Dictionary<string, string?>
        {
            ["Imposter:Providers:billed:Dialect"] = "anthropic",
            ["Imposter:Providers:billed:BaseUrl"] = "https://billed.test",
            ["Imposter:Providers:billed:OptimizeMessages"] = "false",
            ["Imposter:Providers:billed:Models:0:From"] = "claude-*",
            ["Imposter:Providers:billed:Models:0:To"] = "billed-model"
        });
        HttpClient client = AuthenticatedClient(fixture);

        string getJson = await client.GetStringAsync("/admin/providers/billed", Ct);
        ProviderConfigurationBody body = JsonNode.Parse(getJson)!.Deserialize<ProviderConfigurationBody>(JsonOptions)!;
        body.OptimizeMessages.ShouldBe(false);

        using HttpResponseMessage put = await client.PutAsJsonAsync("/admin/providers/billed", body, JsonOptions, Ct);
        put.StatusCode.ShouldBe(HttpStatusCode.OK, await put.Content.ReadAsStringAsync(Ct));

        string afterJson = await client.GetStringAsync("/admin/providers/billed", Ct);
        ProviderConfigurationBody after = JsonNode.Parse(afterJson)!.Deserialize<ProviderConfigurationBody>(JsonOptions)!;
        after.OptimizeMessages.ShouldBe(false);
    }

    private static HttpClient AuthenticatedClient(ProviderConfigAppFixture fixture)
    {
        HttpClient client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Api-Key", AdminKey);
        return client;
    }

    private static ProviderConfigurationBody Body(
        string baseUrl,
        string? authScheme = "ApiKey",
        IReadOnlyList<ProviderModelMappingBody>? models = null) => new(
        Name: null,
        Dialect: "openai",
        BaseUrl: baseUrl,
        AuthScheme: authScheme,
        AuthHeader: null,
        IsDefault: false,
        Enabled: true,
        AnthropicVersion: null,
        OpenAiUpstreamApi: "chat_completions",
        RequestNormalization: null,
        SessionForwarding: null,
        StripEncryptedContent: null,
        TimeoutSeconds: null,
        Models: models ?? [new ProviderModelMappingBody("gpt5.4", "grok-code", Caching: false)]);

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private sealed class ProviderConfigAppFixture(IReadOnlyDictionary<string, string?>? extraConfig = null)
        : WebApplicationFactory<HostApp::Program>
    {
        public StubUpstreamHandler Upstream { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.Sources.Clear();
                var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["Admin:ApiKey"] = AdminKey,
                    ["Admin:OperatorApiKey"] = OperatorKey,

                    ["Imposter:Providers:openai-official:Dialect"] = "openai",
                    ["Imposter:Providers:openai-official:BaseUrl"] = "https://api.openai.test",
                    ["Imposter:Providers:openai-official:Secret"] = "openai-key",
                    ["Imposter:Providers:openai-official:IsDefault"] = "true",

                    ["Imposter:Providers:opencode-go:Dialect"] = "openai",
                    ["Imposter:Providers:opencode-go:BaseUrl"] = "https://opencode.test",
                    ["Imposter:Providers:opencode-go:Secret"] = "opencode-key",
                    ["Imposter:Providers:opencode-go:AuthScheme"] = "ApiKey",
                    ["Imposter:Providers:opencode-go:OpenAiUpstreamApi"] = "chat_completions",
                    ["Imposter:Providers:opencode-go:SessionForwarding"] = "opencode-go",
                    ["Imposter:Providers:opencode-go:StripEncryptedContent"] = "true",
                    ["Imposter:Providers:opencode-go:TimeoutSeconds"] = "450",
                    ["Imposter:Providers:opencode-go:Models:0:From"] = "gpt5.4",
                    ["Imposter:Providers:opencode-go:Models:0:To"] = "grok-code"
                };

                if (extraConfig is not null)
                {
                    foreach ((string key, string? value) in extraConfig)
                    {
                        settings[key] = value;
                    }
                }

                config.AddInMemoryCollection(settings);
            });

            builder.ConfigureServices(services =>
                services.AddHttpClient("imposter-upstream")
                    .ConfigurePrimaryHttpMessageHandler(() => Upstream));
        }
    }
}
