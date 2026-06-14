using Microsoft.Extensions.Logging;
using Xunit.v3;

namespace Project.TestFramework.Logging;

public sealed class XUnitLoggerFactory : ILoggerFactory
{
    private readonly XUnitLoggerProvider _provider;

    public XUnitLoggerFactory(
        ITestOutputHelper? output = null,
        List<XUnitLoggerCategoryMinValue>? categoryMinValues = null)
    {
        _provider = new XUnitLoggerProvider(output, categoryMinValues);
    }

    public void SetOutput(ITestOutputHelper? output) => _provider.SetOutput(output);

    public ILogger CreateLogger(string categoryName) => _provider.CreateLogger(categoryName);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose() => _provider.Dispose();
}
