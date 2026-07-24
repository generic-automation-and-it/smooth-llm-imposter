using SmoothLlmImposter.Application.Features.Routing;

namespace SmoothLlmImposter.Application.UnitTest.Routing;

public class SensitiveHeaderNamesTests
{
    // Pinned membership for the Host/Infrastructure shared mask set. A new capture or fingerprint
    // header added to SessionIdentityResolver (Domain) must be reflected here, otherwise the Debug
    // inbound/outbound dumps will leak it. NFR-03 (secret-adjacent logging) is the contract this test
    // enforces — a regression that drops or renames an entry would surface here, not at runtime.
    [Fact]
    public void Values_masks_all_capture_and_fingerprint_headers()
    {
        string[] expected =
        [
            // Auth headers — managed-secret seam.
            "Authorization",
            "x-api-key",
            // Session capture headers — read by SessionIdentityResolver at the highest precedence.
            "session_id",
            "x-opencode-session",
            "x-session-id",
            "conversation_id",
            // Fingerprint inputs (LADR-03) — stable caller identity material; never log in the clear.
            "chatgpt-account-id",
            "openai-organization",
            "openai-project",
        ];

        foreach (string header in expected)
        {
            SensitiveHeaderNames.Values.Contains(header).ShouldBeTrue(
                $"{header} must be masked in Debug dumps to satisfy NFR-03");
        }
    }

    [Fact]
    public void Values_compares_case_insensitively()
    {
        // HttpRequestMessage stores headers case-insensitively; the mask set must too, so a caller
        // sending `Authorization` vs `authorization` cannot slip past Debug masking on a different case.
        SensitiveHeaderNames.Values.Contains("AUTHORIZATION").ShouldBeTrue();
        SensitiveHeaderNames.Values.Contains("Session_Id").ShouldBeTrue();
    }
}
