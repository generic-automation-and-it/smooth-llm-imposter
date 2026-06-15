using Microsoft.Extensions.Logging;

namespace SmoothLlmImposter.TestFramework.Logging;

public sealed record XUnitLoggerCategoryMinValue(string CategoryPrefix, LogLevel MinLevel);
