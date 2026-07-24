using SmoothLlmImposter.Application.Features.Routing;

namespace SmoothLlmImposter.Application.UnitTest.Routing;

public class SessionIdentityResolverTests
{
    private static CallerHeaders Headers(params (string Name, string Value)[] headers) =>
        new(headers.Select(h => new KeyValuePair<string, IReadOnlyList<string>>(h.Name, [h.Value])).ToArray());

    [Fact]
    public void Header_session_id_wins_over_body_and_other_headers()
    {
        CallerHeaders headers = Headers(
            ("x-opencode-session", "from-opencode"),
            ("session_id", "from-session-id"),
            ("conversation_id", "from-conversation"));

        SessionIdentity identity = SessionIdentityResolver.Resolve(
            headers,
            """{"model":"gpt","prompt_cache_key":"from-body"}""");

        identity.Source.ShouldBe(SessionIdentitySource.Captured);
        identity.Value.ShouldBe("from-session-id");
        identity.LogToken.ShouldBe("captured");
    }

    [Fact]
    public void Header_lookup_is_case_insensitive_and_follows_precedence()
    {
        SessionIdentity identity = SessionIdentityResolver.Resolve(
            Headers(("X-Session-Id", "from-x-session")),
            """{"model":"gpt","prompt_cache_key":"from-body"}""");

        identity.Value.ShouldBe("from-x-session");
        identity.Source.ShouldBe(SessionIdentitySource.Captured);
    }

    [Fact]
    public void Body_prompt_cache_key_is_used_when_no_header()
    {
        SessionIdentity identity = SessionIdentityResolver.Resolve(
            CallerHeaders.None,
            """{"model":"gpt","prompt_cache_key":"body-cache","metadata":{"user_id":"meta-user"}}""");

        identity.Value.ShouldBe("body-cache");
        identity.Source.ShouldBe(SessionIdentitySource.Captured);
    }

    [Fact]
    public void Body_metadata_user_id_is_used_when_no_prompt_cache_key()
    {
        SessionIdentity identity = SessionIdentityResolver.Resolve(
            CallerHeaders.None,
            """{"model":"gpt","metadata":{"user_id":"meta-user"}}""");

        identity.Value.ShouldBe("meta-user");
        identity.Source.ShouldBe(SessionIdentitySource.Captured);
    }

    [Fact]
    public void Derived_fingerprint_is_stable_for_same_caller_material()
    {
        CallerHeaders headers = Headers(
            ("Authorization", "Bearer abc"),
            ("chatgpt-account-id", "acct-1"));

        SessionIdentity a = SessionIdentityResolver.Resolve(headers, """{"model":"gpt"}""");
        SessionIdentity b = SessionIdentityResolver.Resolve(headers, """{"model":"gpt","messages":[]}""");

        a.Source.ShouldBe(SessionIdentitySource.Derived);
        b.Source.ShouldBe(SessionIdentitySource.Derived);
        a.Value.ShouldBe(b.Value);
        a.Value!.StartsWith("derived-").ShouldBeTrue();
        a.LogToken.ShouldBe("derived");
    }

    [Fact]
    public void Derived_fingerprint_changes_when_stable_identity_changes()
    {
        SessionIdentity a = SessionIdentityResolver.Resolve(Headers(("Authorization", "Bearer a")), "{}");
        SessionIdentity b = SessionIdentityResolver.Resolve(Headers(("Authorization", "Bearer b")), "{}");

        a.Value.ShouldNotBe(b.Value);
    }

    [Fact]
    public void Returns_none_when_no_stable_identity_exists()
    {
        SessionIdentity identity = SessionIdentityResolver.Resolve(
            Headers(("x-stainless-lang", "js")),
            """{"model":"gpt"}""");

        identity.ShouldBe(SessionIdentity.None);
        identity.LogToken.ShouldBe("none");
        identity.HasValue.ShouldBeFalse();
    }
}
