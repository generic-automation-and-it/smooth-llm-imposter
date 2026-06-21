namespace SmoothLlmImposter.Application.Features.Routing;

public interface IProviderRegistry
{
    bool IsSeeded { get; }

    IReadOnlyDictionary<string, ProviderOptions> Snapshot();

    bool TryGet(string key, out ProviderOptions provider);

    void Seed(IReadOnlyDictionary<string, ProviderOptions> providers);

    void Upsert(string key, ProviderOptions provider);

    bool Delete(string key);

    bool TrySetEnabled(string key, bool enabled, out ProviderOptions? provider);
}
