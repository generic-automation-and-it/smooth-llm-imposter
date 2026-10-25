using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SmoothLlmImposter.Application.Features.Routing.Bridge;

/// <summary>
/// One Messages-shaped failure: the HTTP status to return, the Anthropic error type and the message text
/// (HLD 016 spec 02 section 11 and spec 04 section 7).
/// </summary>
internal sealed record AnthropicChatFailure(int StatusCode, string Type, string Message);

/// <summary>
/// Maps an upstream failure to the Messages error contract: status-derived type, preserved upstream text,
/// credential-failure wording, and context-overflow rewording into the two forms the client recognises.
/// </summary>
internal static partial class AnthropicChatErrorMapper
{
    private const int MessageMaxChars = 2_000;

    /// <summary>Status-derived error type (spec 02 section 11.2 / spec 04 section 7.1).</summary>
    public static string TypeForStatus(int status) => status switch
    {
        400 => "invalid_request_error",
        401 => "authentication_error",
        403 => "permission_error",
        404 => "not_found_error",
        408 or 504 => "timeout_error",
        413 => "request_too_large",
        429 => "rate_limit_error",
        500 or 502 => "api_error",
        503 or 529 => "overloaded_error",
        >= 400 and < 500 => "invalid_request_error",
        >= 500 => "api_error",
        _ => "api_error"
    };

    public static AnthropicChatFailure Map(int upstreamStatus, string body, string? configuredSecret = null) =>
        MapInternal(upstreamStatus, body, configuredSecret, inBand: false);

    /// <summary>Maps an in-band 2xx error (spec 02 section 11.5) to a failure.</summary>
    public static AnthropicChatFailure MapInBand(JsonObject errorObject, string? configuredSecret = null) =>
        MapInternal(
            NumericStatus(errorObject) ?? 502,
            errorObject.ToJsonString(),
            configuredSecret,
            inBand: true);

    private static AnthropicChatFailure MapInternal(int upstreamStatus, string body, string? configuredSecret, bool inBand)
    {
        int status = upstreamStatus;
        string type = TypeForStatus(status);

        if (status < 400)
        {
            // Unexpected redirects and other non-2xx below 400.
            status = 502;
            type = "api_error";
        }

        JsonObject? parsed = string.IsNullOrWhiteSpace(body) ? null : AnthropicChatJson.TryParseObject(body);
        string message = ExtractMessage(body, parsed, upstreamStatus);

        if (!string.IsNullOrEmpty(configuredSecret))
        {
            message = message.Replace(configuredSecret, "***", StringComparison.Ordinal);
        }

        string? code = ErrorCode(parsed);

        if (status is 401 or 403)
        {
            message = CredentialMessage(status, type, message);
        }

        if (IsOverflow(status, code, message, inBand))
        {
            return new AnthropicChatFailure(400, "invalid_request_error", OverflowText(message, parsed));
        }

        return new AnthropicChatFailure(status, type, Truncate(message, MessageMaxChars));
    }

    /// <summary>Extracts the upstream's own message text (spec 02 section 11.3).</summary>
    private static string ExtractMessage(string body, JsonObject? parsed, int status)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return $"Upstream returned HTTP {status} with no error message.";
        }

        string trimmed = body.TrimStart();
        if (trimmed.StartsWith('<'))
        {
            return $"Upstream returned an HTML error page (HTTP {status}).";
        }

        if (parsed is null)
        {
            return body;
        }

        string? message = FirstString(parsed, "error.message", "message", "detail", "errors[0].message");
        if (message is null && parsed["detail"] is JsonArray details)
        {
            var pieces = new List<string>();
            foreach (JsonNode? item in details)
            {
                if (AnthropicChatJson.String(item) is { } text)
                {
                    pieces.Add(text);
                }
                else if (item is JsonObject detail && AnthropicChatJson.String(detail["msg"]) is { } msg)
                {
                    pieces.Add(msg);
                }
            }

            message = pieces.Count > 0 ? string.Join("; ", pieces) : null;
        }

        return string.IsNullOrEmpty(message)
            ? $"Upstream returned HTTP {status} with no error message."
            : message;
    }

    private static string? FirstString(JsonObject root, params string[] paths)
    {
        foreach (string path in paths)
        {
            JsonNode? node = root;
            foreach (string segment in path.Split('.'))
            {
                if (segment.EndsWith("[0]", StringComparison.Ordinal))
                {
                    string name = segment[..^3];
                    node = node is JsonObject obj && obj[name] is JsonArray { Count: > 0 } array ? array[0] : null;
                }
                else
                {
                    node = node is JsonObject obj ? obj[segment] : null;
                }

                if (node is null)
                {
                    break;
                }
            }

            if (AnthropicChatJson.String(node) is { Length: > 0 } value)
            {
                return value;
            }
        }

        if (root["error"] is JsonObject error && error["message"] is null &&
            AnthropicChatJson.String(root["error"]) is { } errorText)
        {
            return errorText;
        }

        if (root["error"] is JsonValue && AnthropicChatJson.String(root["error"]) is { } stringError)
        {
            return stringError;
        }

        return null;
    }

    private static string? ErrorCode(JsonObject? parsed)
    {
        if (parsed?["error"] is JsonObject error)
        {
            return AnthropicChatJson.String(error["code"]) ?? AnthropicChatJson.String(error["type"]);
        }

        return parsed is null ? null : AnthropicChatJson.String(parsed["code"]);
    }

    internal static int? NumericStatus(JsonObject errorObject)
    {
        if (errorObject["error"] is JsonObject error && StatusIn(error) is int nestedStatus)
        {
            return nestedStatus;
        }

        return StatusIn(errorObject);
    }

    private static int? StatusIn(JsonObject obj)
    {
        foreach (string member in (string[])["status", "http_status", "code"])
        {
            if (AnthropicChatJson.Number(obj[member]) is double number &&
                number is >= 400 and <= 599)
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

    internal static string CredentialMessage(int? status, string type, string upstreamMessage)
    {
        string http = status is int numeric ? $" (HTTP {numeric})" : string.Empty;
        string prefix = type switch
        {
            "authentication_error" => $"The upstream provider rejected the API key configured for this route{http}",
            _ => $"The upstream provider denied access for the API key configured for this route{http}"
        };

        return upstreamMessage.StartsWith("Upstream returned HTTP", StringComparison.Ordinal) ||
            string.Equals(upstreamMessage, "Upstream reported an error", StringComparison.Ordinal)
                ? prefix
                : $"{prefix}: {upstreamMessage}";
    }

    // ---------------------------------------------------------------- context overflow

    private static readonly string[] OverflowKeywords =
    [
        "maximum context length", "context length", "context limit", "context window", "context size",
        "prompt is too long", "prompt too long", "input is too long", "too many tokens",
        "token limit", "exceeds the maximum number of tokens", "reduce the length of the messages", "n_ctx"
    ];

    private static readonly string[] OverflowCodes =
    [
        "context_length_exceeded", "context_window_exceeded", "exceed_context_size_error"
    ];

    private static bool IsOverflow(int status, string? code, string message, bool inBand) =>
        LooksLikeOverflow(code, message, status, inBand);

    /// <summary>
    /// Overflow classification shared by HTTP errors and in-band errors (spec 04 section 7.5.1): an HTTP
    /// error qualifies at 400/413/422; an in-band error qualifies at any status except 429.
    /// </summary>
    internal static bool LooksLikeOverflow(string? code, string message, int? status, bool inBand)
    {
        if (status == 429)
        {
            return false;
        }

        if (!inBand && status is not null && status is not (400 or 413 or 422))
        {
            return false;
        }

        if (code is not null && OverflowCodes.Contains(code.ToLowerInvariant()))
        {
            return true;
        }

        string lowered = message.ToLowerInvariant();
        return OverflowKeywords.Any(keyword => lowered.Contains(keyword, StringComparison.Ordinal));
    }

    internal static string OverflowText(string message, JsonObject? parsed)
    {
        var numbers = new OverflowNumbers();

        // P11: explicit numeric members of the error object. A choice-level error object is the error
        // itself, so it may carry the members at its top level.
        JsonObject? error = parsed?["error"] as JsonObject ?? parsed;
        if (error is not null &&
            Count(error["n_prompt_tokens"]) is double inputTokens &&
            Count(error["n_ctx"]) is double contextLimit)
        {
            numbers.SetInputLimit(inputTokens, contextLimit);
        }

        if (!numbers.HasAny)
        {
            TryParsePhrasings(message, numbers);
        }

        string form = numbers.Form(message);
        return Truncate(form, MessageMaxChars);
    }

    private sealed class OverflowNumbers
    {
        public double? Input { get; set; }

        public double? MaxTokens { get; set; }

        public double? Limit { get; set; }

        public double? Total { get; set; }

        public bool HasAny => Input is not null || Limit is not null || Total is not null;

        public void SetInputMaxLimit(double input, double maxTokens, double limit)
        {
            Input = input;
            MaxTokens = maxTokens;
            Limit = limit;
        }

        public void SetInputLimit(double input, double limit)
        {
            Input = input;
            Limit = limit;
        }

        public void SetTotalLimit(double total, double limit)
        {
            Total = total;
            Limit = limit;
        }

        public string Form(string message)
        {
            string fallback = message.StartsWith("prompt is too long", StringComparison.OrdinalIgnoreCase)
                ? message
                : "prompt is too long: " + message;

            if (Input is double i && MaxTokens is double m && Limit is double l &&
                i > 0 && m > 0 && l > 0 && i + m > l)
            {
                if (i <= l && l - i >= 4000)
                {
                    return $"input length and `max_tokens` exceed context limit: {(long)i} + {(long)m} > {(long)l}";
                }

                return $"prompt is too long: {(long)(i <= l ? i + m : i)} tokens > {(long)l} maximum";
            }

            if (Input is double input && Limit is double limit && input > limit && input > 0 && limit > 0)
            {
                return $"prompt is too long: {(long)input} tokens > {(long)limit} maximum";
            }

            if (Total is double total && Limit is double totalLimit && total > totalLimit && total > 0 && totalLimit > 0)
            {
                return $"prompt is too long: {(long)total} tokens > {(long)totalLimit} maximum";
            }

            return fallback;
        }
    }

    // Spec 04 section 7.5.2: the P1-P10 phrasings are a data table, tried in order; each row reports
    // whether it could fill its quantities from this message.
    private sealed record OverflowPhrasing(Regex Pattern, Func<Match, string, OverflowNumbers, bool> Apply);

    private static readonly OverflowPhrasing[] OverflowPhrasings =
    [
        new(P1Regex(), static (match, _, numbers) =>
        {
            numbers.SetInputMaxLimit(
                Num(match.Groups[1].Value), Num(match.Groups[2].Value), Num(match.Groups[3].Value));
            return true;
        }),
        new(P2Regex(), static (match, _, numbers) =>
        {
            numbers.SetInputLimit(Num(match.Groups[1].Value), Num(match.Groups[2].Value));
            return true;
        }),
        new(SplitRegex(), static (match, message, numbers) =>
        {
            if (LimitFromMessage(message) is not double limit)
            {
                return false;
            }

            string inputText = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[3].Value;
            string maxText = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[4].Value;
            numbers.SetInputMaxLimit(Num(inputText), Num(maxText), limit);
            return true;
        }),
        new(P4Regex(), static (match, message, numbers) =>
        {
            if (LimitFromMessage(message) is not double limit)
            {
                return false;
            }

            numbers.SetTotalLimit(Num(match.Groups[1].Value), limit);
            return true;
        }),
        new(P5Regex(), static (match, message, numbers) =>
        {
            if (LimitFromMessage(message) is not double limit)
            {
                return false;
            }

            string inputText = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            numbers.SetInputLimit(Num(inputText), limit);
            return true;
        }),
        new(P6Regex(), static (match, message, numbers) =>
        {
            if (LimitFromMessage(message) is not double limit)
            {
                return false;
            }

            numbers.SetInputMaxLimit(Num(match.Groups[1].Value), Num(match.Groups[2].Value), limit);
            return true;
        }),
        new(P7Regex(), static (match, message, numbers) =>
        {
            if (LimitFromMessage(message) is not double limit)
            {
                return false;
            }

            numbers.SetInputMaxLimit(Num(match.Groups[2].Value), Num(match.Groups[1].Value), limit);
            return true;
        }),
        new(P8Regex(), static (match, _, numbers) =>
        {
            numbers.SetInputLimit(Num(match.Groups[1].Value), Num(match.Groups[2].Value));
            return true;
        }),
        new(P9Regex(), static (match, _, numbers) =>
        {
            numbers.SetTotalLimit(Num(match.Groups[2].Value), Num(match.Groups[1].Value));
            return true;
        }),
        new(P10Regex(), static (match, _, numbers) =>
        {
            numbers.SetInputLimit(Num(match.Groups[1].Value), Num(match.Groups[2].Value));
            return true;
        })
    ];

    private static void TryParsePhrasings(string message, OverflowNumbers numbers)
    {
        foreach (OverflowPhrasing phrasing in OverflowPhrasings)
        {
            Match match = phrasing.Pattern.Match(message);
            if (match.Success && phrasing.Apply(match, message, numbers))
            {
                return;
            }
        }
    }

    private static double? LimitFromMessage(string message)
    {
        Match match = LimitRegex().Match(message);
        if (!match.Success)
        {
            return null;
        }

        string limitText = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        return Num(limitText);
    }

    private static double Num(string text)
    {
        string normalized = text.Replace(",", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);
        return double.TryParse(normalized, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double value)
            ? value
            : 0;
    }

    private static double? Count(JsonNode? node) =>
        AnthropicChatJson.Number(node) is double value && value >= 0 ? Math.Floor(value) : null;

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

    // P1: input length and `max_tokens` exceed context limit: I + M > L
    [GeneratedRegex(@"input length and [`'""]?max_tokens[`'""]? exceed context limit:\s*([\d,_]+)\s*\+\s*([\d,_]+)\s*>\s*([\d,_]+)", RegexOptions.IgnoreCase)]
    private static partial Regex P1Regex();

    // P2: prompt is too long: I tokens > L maximum
    [GeneratedRegex(@"prompt is too long:\s*([\d,_]+)\s*tokens\s*>\s*([\d,_]+)\s*maximum", RegexOptions.IgnoreCase)]
    private static partial Regex P2Regex();

    // L in: maximum context length is/of L tokens, context window is L tokens
    [GeneratedRegex(@"maximum context length (?:is|of)\s*([\d,_]+)\s*tokens|context window is\s*([\d,_]+)\s*tokens", RegexOptions.IgnoreCase)]
    private static partial Regex LimitRegex();

    // P3 split forms.
    [GeneratedRegex(@"\(?\s*([\d,_]+)\s*(?:in the messages,|in your prompt;|of text input,)[^()]*?([\d,_]+)\s*(?:in the completion\)|for the completion\)|in the output\))|([\d,_]+)\s*tokens from the input messages and ([\d,_]+)\s*tokens for the completion", RegexOptions.IgnoreCase)]
    private static partial Regex SplitRegex();

    // P6
    [GeneratedRegex(@"input length is\s*([\d,_]+)\s*tokens and max_tokens is\s*([\d,_]+)", RegexOptions.IgnoreCase)]
    private static partial Regex P6Regex();

    // P7
    [GeneratedRegex(@"['`]?max_(?:tokens|completion_tokens)['`]? is too large:\s*([\d,_]+).*?request has\s*([\d,_]+)\s*input tokens", RegexOptions.IgnoreCase)]
    private static partial Regex P7Regex();

    // P4
    [GeneratedRegex(@"you requested (?:a total of\s*)?([\d,_]+)\s*tokens", RegexOptions.IgnoreCase)]
    private static partial Regex P4Regex();

    // P5
    [GeneratedRegex(@"your messages resulted in\s*([\d,_]+)\s*tokens|your prompt has\s*([\d,_]+)\s*tokens", RegexOptions.IgnoreCase)]
    private static partial Regex P5Regex();

    // P8
    [GeneratedRegex(@"input token count \(([\d,_]+)\) exceeds the maximum number of tokens allowed \(([\d,_]+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex P8Regex();

    // P9
    [GeneratedRegex(@"exceeded model token limit:\s*([\d,_]+)\s*\(requested:\s*([\d,_]+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex P9Regex();

    // P10
    [GeneratedRegex(@"keep the first\s*([\d,_]+)\s*tokens.*?context length of only\s*([\d,_]+)\s*tokens", RegexOptions.IgnoreCase)]
    private static partial Regex P10Regex();
}
