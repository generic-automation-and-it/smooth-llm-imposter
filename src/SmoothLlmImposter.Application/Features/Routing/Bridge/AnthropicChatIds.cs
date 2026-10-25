using System.Security.Cryptography;

namespace SmoothLlmImposter.Application.Features.Routing.Bridge;

/// <summary>Synthesized Messages ids (HLD 016 D3/D5): cryptographically random base62, never upstream-derived.</summary>
internal static class AnthropicChatIds
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    internal const int IdChars = 24;

    public static string NewMessageId() => "msg_" + RandomNumberGenerator.GetString(Alphabet, IdChars);

    public static string NewToolId(HashSet<string> usedIds)
    {
        while (true)
        {
            string id = "toolu_" + RandomNumberGenerator.GetString(Alphabet, IdChars);
            if (usedIds.Add(id))
            {
                return id;
            }
        }
    }
}
