namespace SmoothLlmImposter.Application.Common.Clients.Optimizer;

/// <summary>
/// Optimizer LLM client (HLD 017): rewrites a single human message given system instructions.
/// Each call carries only the instructions and that message — no tools, tool_choice or history (NFR-04).
/// </summary>
public interface IOptimizerClient
{
    Task<string> OptimizeAsync(string systemInstructions, string userText, CancellationToken cancellationToken);
}