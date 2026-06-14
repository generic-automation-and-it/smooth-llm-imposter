using Microsoft.Extensions.Logging;

namespace Project.TestFramework.Logging;

public sealed record XUnitLoggerCategoryMinValue(string CategoryPrefix, LogLevel MinLevel);
