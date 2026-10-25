using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SmoothLlmImposter.Application.Features.Routing.Bridge;

/// <summary>
/// Per-request, stateless tool-name and tool-id normalisation (HLD 016 D5/D6, spec 01 sections 7.3 and 8.3).
/// Built from the request tools and its history, reversed on the reply.
/// </summary>
internal sealed partial class AnthropicChatToolIdentity
{
    private readonly Dictionary<string, string> _targetByOriginal = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _originalByTarget = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _firstTargetByOriginal = new(StringComparer.Ordinal);
    private readonly List<KeyValuePair<string, string>> _pairs = [];

    // Tool ids: first assigned target per original (for tool_result matching), plus every id used.
    private readonly Dictionary<string, string> _firstIdByOriginal = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _idOccurrencesByOriginal = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedIds = new(StringComparer.Ordinal);

    /// <summary>Ordered (original, target) pairs, identity pairs included; consumed by the response side.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Pairs => _pairs;

    /// <summary>Registers an original tool name, assigning a deterministic target on first sight.</summary>
    public void RegisterName(string original)
    {
        if (_targetByOriginal.ContainsKey(original))
        {
            return;
        }

        string target = AssignTarget(original);
        _targetByOriginal[original] = target;
        _originalByTarget[target] = original;
        _pairs.Add(new(original, target));
    }

    public string TargetFor(string original) =>
        _targetByOriginal.TryGetValue(original, out string? target)
            ? target
            : Sanitize(original);

    /// <summary>Reverses a sanitized upstream name; names unknown to the map pass through unchanged.</summary>
    public string OriginalFor(string target) =>
        _originalByTarget.TryGetValue(target, out string? original) ? original : target;

    private string AssignTarget(string original)
    {
        string candidate = Sanitize(original);

        if (IsAssignedToOther(candidate, original))
        {
            candidate = Truncate(candidate, 55) + "_" + Hash8(original);

            int suffix = 2;
            while (IsAssignedToOther(candidate, original))
            {
                string token = "_" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
                candidate = Truncate(candidate, 64 - token.Length) + token;
                suffix++;
            }
        }

        return candidate;
    }

    private bool IsAssignedToOther(string candidate, string original) =>
        _originalByTarget.TryGetValue(candidate, out string? owner) &&
        !string.Equals(owner, original, StringComparison.Ordinal);

    /// <summary>
    /// Maps one assistant tool_use id occurrence. A legal id maps to itself on first use; anything else
    /// (illegal, too long, repeated) hashes and disambiguates deterministically.
    /// </summary>
    public string MapToolId(string original)
    {
        _idOccurrencesByOriginal.TryGetValue(original, out int occurrence);

        if (occurrence == 0 && IsLegalId(original))
        {
            if (_usedIds.Add(original))
            {
                _firstIdByOriginal[original] = original;
                _idOccurrencesByOriginal[original] = 1;
                return original;
            }
        }

        string baseId = IsLegalId(original) ? original : "toolu_" + Hash24(original);
        string candidate = occurrence == 0 ? baseId : baseId + "_" + (occurrence + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

        int disambiguator = occurrence + 2;
        while (!_usedIds.Add(candidate))
        {
            candidate = Truncate(baseId, 64 - ("_" + disambiguator.ToString(System.Globalization.CultureInfo.InvariantCulture)).Length) +
                "_" + disambiguator.ToString(System.Globalization.CultureInfo.InvariantCulture);
            disambiguator++;
        }

        _idOccurrencesByOriginal[original] = occurrence + 1;
        _firstIdByOriginal.TryAdd(original, candidate);
        return candidate;
    }

    /// <summary>
    /// Maps a history tool_result's <c>tool_use_id</c> through the same map: the first target assigned
    /// to that original, or the deterministic base value when the id was never seen in an assistant block.
    /// </summary>
    public string MapResultToolId(string original) =>
        _firstIdByOriginal.TryGetValue(original, out string? mapped)
            ? mapped
            : IsLegalId(original) ? original : "toolu_" + Hash24(original);

    public static bool IsLegalId(string id) =>
        id.Length is >= 1 and <= 64 && id.All(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    /// <summary>Sanitizes a name by steps 1-2 only (used for names absent from the map).</summary>
    public static string Sanitize(string original)
    {
        string sanitized = IllegalRunRegex().Replace(original, "_");
        return sanitized.Length <= 64 ? sanitized : Truncate(sanitized, 55) + "_" + Hash8(original);
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length];

    private static string Hash8(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..8].ToLowerInvariant();

    private static string Hash24(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..24].ToLowerInvariant();

    [GeneratedRegex("[^A-Za-z0-9_-]+")]
    private static partial Regex IllegalRunRegex();
}
