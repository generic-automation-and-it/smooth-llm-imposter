using Microsoft.Extensions.Logging;
using Xunit.v3;

namespace Project.TestFramework.Logging;

public sealed class XUnitLoggerProvider : ILoggerProvider
{
    private readonly List<XUnitLoggerCategoryMinValue> _categoryMinValues;
    private ITestOutputHelper? _output;

    public XUnitLoggerProvider(
        ITestOutputHelper? output = null,
        List<XUnitLoggerCategoryMinValue>? categoryMinValues = null)
    {
        _output = output;
        _categoryMinValues = categoryMinValues ?? [];
    }

    public void SetOutput(ITestOutputHelper? output) => _output = output;

    public ILogger CreateLogger(string categoryName) => new XUnitLogger(categoryName, this);

    public LogLevel GetMinLevel(string categoryName)
    {
        LogLevel result = LogLevel.Trace;

        foreach (XUnitLoggerCategoryMinValue entry in _categoryMinValues)
        {
            if (categoryName.StartsWith(entry.CategoryPrefix, StringComparison.Ordinal))
            {
                result = entry.MinLevel;
            }
        }

        return result;
    }

    public void WriteLineSafe(string message)
    {
        try
        {
            _output?.WriteLine(message);
        }
        catch (InvalidOperationException)
        {
        }
    }

    public void Dispose()
    {
        _output = null;
    }
}
