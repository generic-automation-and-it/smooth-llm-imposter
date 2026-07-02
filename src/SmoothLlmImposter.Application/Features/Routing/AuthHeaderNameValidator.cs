namespace SmoothLlmImposter.Application.Features.Routing;

internal static class AuthHeaderNameValidator
{
    private static readonly string[] TransportOwnedHeaderNames =
    [
        "Host",
        "Transfer-Encoding",
    ];

    public static bool IsValid(string? headerName) =>
        headerName is null || (
            !string.IsNullOrWhiteSpace(headerName) &&
            IsToken(headerName) &&
            !IsTransportOwned(headerName));

    public static string FailureMessage(string path) =>
        $"{path}:AuthHeader must be omitted or a custom request-header name; transport-owned headers (Content-*, Host, Transfer-Encoding) are not allowed.";

    private static bool IsTransportOwned(string headerName) =>
        headerName.StartsWith("Content-", StringComparison.OrdinalIgnoreCase) ||
        TransportOwnedHeaderNames.Any(transportHeader => string.Equals(headerName, transportHeader, StringComparison.OrdinalIgnoreCase));

    private static bool IsToken(string value) => value.All(static ch =>
        char.IsAsciiLetterOrDigit(ch) ||
        ch is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~');
}
