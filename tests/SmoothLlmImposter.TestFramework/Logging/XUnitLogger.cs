using Microsoft.Extensions.Logging;

namespace SmoothLlmImposter.TestFramework.Logging;

public sealed class XUnitLogger(string categoryName, XUnitLoggerProvider provider) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= provider.GetMinLevel(categoryName);

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        string message = formatter(state, exception);
        string line = $"[{logLevel}] {categoryName}: {message}";

        if (exception is not null)
        {
            line = $"{line}{Environment.NewLine}{exception}";
        }

        provider.WriteLineSafe(line);
    }
}
