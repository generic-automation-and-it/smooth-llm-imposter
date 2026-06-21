using Microsoft.Extensions.Options;

namespace SmoothLlmImposter.Application.UnitTest.Routing;

internal sealed class StaticOptionsSnapshot<T>(T value) : IOptionsSnapshot<T>
    where T : class
{
    public T Value { get; } = value;

    public T Get(string? name) => Value;
}
