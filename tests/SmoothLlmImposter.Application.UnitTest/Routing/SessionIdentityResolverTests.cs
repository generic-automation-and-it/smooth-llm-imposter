using SmoothLlmImposter.Application.Features.Routing;

namespace SmoothLlmImposter.Application.UnitTest.Routing;

public class SessionIdentityResolverTests
{
    private static CallerHeaders Headers(params (string Name, string Value)[] headers) =>
        new(headers.Select(h => new KeyValuePair<string, IReadOnlyList<string>>(h.Name, [h.Value])).ToArray());

    private static CallerHeaders MultiValueHeader(string name, params string[] values) =>
        new([new KeyValuePair<string, IReadOnlyList<string>>(name, values)]);

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
    public void Body_prompt_cache_key_with_non_string_value_is_ignored()
    {
        // Non-string prompt_cache_key values (number / object) must not crash the resolver; the
        // TryReadString helper is type-strict, so we fall through to metadata.user_id.
        SessionIdentity identity = SessionIdentityResolver.Resolve(
            CallerHeaders.None,
            """{"model":"gpt","prompt_cache_key":42,"metadata":{"user_id":"meta-user"}}""");

        identity.Value.ShouldBe("meta-user");
        identity.Source.ShouldBe(SessionIdentitySource.Captured);
    }

    [Fact]
    public void Body_metadata_non_object_is_ignored()
    {
        // metadata is a string here, not an object — the resolver must not crash and must fall through
        // to the fingerprint path. No fingerprint inputs are present either, so result is None.
        SessionIdentity identity = SessionIdentityResolver.Resolve(
            CallerHeaders.None,
            """{"model":"gpt","metadata":"oops"}""");

        identity.ShouldBe(SessionIdentity.None);
    }

    [Fact]
    public void Non_object_top_level_body_returns_none_when_no_stable_identity()
    {
        // A JSON array body is valid JSON but not a top-level object; body capture must not throw and
        // there are no fingerprint inputs to derive from, so the resolver returns None.
        SessionIdentity identity = SessionIdentityResolver.Resolve(CallerHeaders.None, """[]""");

        identity.ShouldBe(SessionIdentity.None);
    }

    [Fact]
    public void Invalid_json_body_swallowed_and_falls_through()
    {
        // The resolver swallows JsonException in the body-capture path; the router/transformer is
        // responsible for surfacing bad-JSON as a routing error downstream.
        SessionIdentity identity = SessionIdentityResolver.Resolve(
            Headers(("Authorization", "Bearer stable")),
            "not-json");

        identity.Source.ShouldBe(SessionIdentitySource.Derived);
        identity.Value!.StartsWith("derived-").ShouldBeTrue();
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
    public void Derived_fingerprint_is_independent_of_header_enumeration_order()
    {
        // CallerHeaders.Items enumeration order is dictated by the Host edge; the resolver must sort
        // fingerprint inputs before hashing so a caller that reorders them cannot fork the hash.
        CallerHeaders a = new(
        [
            new KeyValuePair<string, IReadOnlyList<string>>("x-api-key", ["key-1"]),
            new KeyValuePair<string, IReadOnlyList<string>>("openai-organization", ["org-1"]),
            new KeyValuePair<string, IReadOnlyList<string>>("openai-project", ["proj-1"]),
            new KeyValuePair<string, IReadOnlyList<string>>("chatgpt-account-id", ["acct-1"]),
        ]);
        CallerHeaders b = new(
        [
            new KeyValuePair<string, IReadOnlyList<string>>("chatgpt-account-id", ["acct-1"]),
            new KeyValuePair<string, IReadOnlyList<string>>("openai-project", ["proj-1"]),
            new KeyValuePair<string, IReadOnlyList<string>>("openai-organization", ["org-1"]),
            new KeyValuePair<string, IReadOnlyList<string>>("x-api-key", ["key-1"]),
        ]);

        SessionIdentityResolver.Resolve(a, "{}").Value.ShouldBe(SessionIdentityResolver.Resolve(b, "{}").Value);
    }

    [Theory]
    [InlineData("chatgpt-account-id", "acct-1", "acct-2")]
    [InlineData("x-api-key", "key-1", "key-2")]
    [InlineData("openai-organization", "org-1", "org-2")]
    [InlineData("openai-project", "proj-1", "proj-2")]
    [InlineData("authorization", "Bearer a", "Bearer b")]
    public void Each_fingerprint_header_is_a_derivation_input(string headerName, string valueA, string valueB)
    {
        // Pin that every individual fingerprint header (all five in FingerprintHeaderNames) is an
        // input to the derived hash: it produces a derived identity, and changing only that header's
        // value diverges the hash. Pairwise compare so a refactor that drops one from the hash input
        // (but still resolves "derived-") breaks this test, not just one that drops it from the set.
        SessionIdentity a = SessionIdentityResolver.Resolve(Headers((headerName, valueA)), "{}");
        SessionIdentity b = SessionIdentityResolver.Resolve(Headers((headerName, valueB)), "{}");

        a.Source.ShouldBe(SessionIdentitySource.Derived);
        a.Value!.StartsWith("derived-").ShouldBeTrue();
        b.Value.ShouldNotBe(a.Value);
    }

    [Fact]
    public void Body_user_field_contributes_to_fingerprint()
    {
        // body.user is the sixth fingerprint input (LADR-03); changing it must change the hash.
        SessionIdentity a = SessionIdentityResolver.Resolve(CallerHeaders.None, """{"model":"gpt","user":"alice"}""");
        SessionIdentity b = SessionIdentityResolver.Resolve(CallerHeaders.None, """{"model":"gpt","user":"bob"}""");

        a.Source.ShouldBe(SessionIdentitySource.Derived);
        b.Source.ShouldBe(SessionIdentitySource.Derived);
        a.Value.ShouldNotBe(b.Value);
    }

    [Fact]
    public void Multi_value_header_first_non_blank_value_wins()
    {
        // A caller that sends the same header twice (rare, but possible) gets the first non-blank
        // value; the resolver must not surface an empty entry that hashes into a different bucket.
        SessionIdentity identity = SessionIdentityResolver.Resolve(
            MultiValueHeader("Authorization", "", "   ", "Bearer real"),
            "{}");

        identity.Source.ShouldBe(SessionIdentitySource.Derived);
        identity.Value!.StartsWith("derived-").ShouldBeTrue();
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
