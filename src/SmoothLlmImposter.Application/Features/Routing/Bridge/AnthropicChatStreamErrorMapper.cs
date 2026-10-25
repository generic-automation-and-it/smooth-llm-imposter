using System.Text.Json.Nodes;

namespace SmoothLlmImposter.Application.Features.Routing.Bridge;

/// <summary>
/// Classifies an in-band Chat SSE error object into the Messages error type and message (HLD 016 spec 03
/// section 8.3).
/// </summary>
internal static class AnthropicChatStreamErrorMapper
{
    private const int MessageMaxChars = 2_000;

    private static readonly IReadOnlyDictionary<string, string> CodeTypeMap =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["rate_limit_error"] = "rate_limit_error",
            ["rate_limit_exceeded"] = "rate_limit_error",
            ["insufficient_quota"] = "rate_limit_error",
            ["too_many_requests"] = "rate_limit_error",
            ["overloaded_error"] = "overloaded_error",
            ["overloaded"] = "overloaded_error",
            ["server_overloaded"] = "overloaded_error",
            ["service_unavailable"] = "overloaded_error",
            ["authentication_error"] = "authentication_error",
            ["invalid_api_key"] = "authentication_error",
            ["unauthorized"] = "authentication_error",
            ["permission_error"] = "permission_error",
            ["permission_denied"] = "permission_error",
            ["forbidden"] = "permission_error",
            ["not_found_error"] = "not_found_error",
            ["model_not_found"] = "not_found_error",
            ["not_found"] = "not_found_error",
            ["request_too_large"] = "request_too_large",
            ["timeout"] = "timeout_error",
            ["timeout_error"] = "timeout_error",
            ["invalid_request_error"] = "invalid_request_error"
        };

    public static AnthropicChatFailure Map(JsonObject chunk)
    {
        string message = ExtractMessage(chunk);
        string? code = CodeValue(chunk);
        int? status = StatusValue(chunk);
        string type = status is int numeric
            ? AnthropicChatErrorMapper.TypeForStatus(numeric)
            : TypeForCode(code);

        if (AnthropicChatErrorMapper.LooksLikeOverflow(code, message, status, inBand: true))
        {
            return new AnthropicChatFailure(
                400,
                "invalid_request_error",
                Truncate(AnthropicChatErrorMapper.OverflowText(message, chunk), MessageMaxChars));
        }

        // Spec 04 section 7.3: an in-band authentication/permission error carries the upstream-credential
        // wording; the HTTP status part is omitted when the upstream gave no numeric status.
        if (type is "authentication_error" or "permission_error")
        {
            message = AnthropicChatErrorMapper.CredentialMessage(status, type, message);
        }

        return new AnthropicChatFailure(status ?? 502, type, Truncate(message, MessageMaxChars));
    }

    private static string ExtractMessage(JsonObject chunk)
    {
        if (chunk["error"] is JsonObject error)
        {
            if (AnthropicChatJson.String(error["message"]) is { } errorMessage)
            {
                return errorMessage;
            }

            if (AnthropicChatJson.String(error["detail"]) is { } detail)
            {
                return detail;
            }
        }

        if (AnthropicChatJson.String(chunk["error"]) is { } errorString)
        {
            return errorString;
        }

        return AnthropicChatJson.String(chunk["message"])
            ?? AnthropicChatJson.String(chunk["detail"])
            ?? "Upstream reported an error";
    }

    private static string? CodeValue(JsonObject chunk)
    {
        if (chunk["error"] is JsonObject error)
        {
            string? code = AnthropicChatJson.String(error["type"]) ?? AnthropicChatJson.String(error["code"]);
            if (code is not null)
            {
                return code;
            }
        }

        return AnthropicChatJson.String(chunk["code"]);
    }

    private static int? StatusValue(JsonObject chunk)
    {
        if (chunk["error"] is JsonObject error)
        {
            int? status = FirstStatus(error);
            if (status is not null)
            {
                return status;
            }
        }

        return FirstStatus(chunk);
    }

    private static int? FirstStatus(JsonObject obj)
    {
        foreach (string member in (string[])["status", "http_status", "code"])
        {
            if (AnthropicChatJson.Number(obj[member]) is double number && number is >= 400 and <= 599)
            {
                return (int)number;
            }

            if (AnthropicChatJson.String(obj[member]) is { } text &&
                int.TryParse(text, out int parsed) && parsed is >= 400 and <= 599)
            {
                return parsed;
            }
        }

        return null;
    }

    private static string TypeForCode(string? code)
    {
        if (code is null)
        {
            return "api_error";
        }

        string lowered = code.ToLowerInvariant();
        if (CodeTypeMap.TryGetValue(lowered, out string? type))
        {
            return type;
        }

        return lowered.StartsWith("invalid_", StringComparison.Ordinal) ? "invalid_request_error" : "api_error";
    }

    private static string Truncate(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        int cut = maxChars;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return text[..cut];
    }
}
