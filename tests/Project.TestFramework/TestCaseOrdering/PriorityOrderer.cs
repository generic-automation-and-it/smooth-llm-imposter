using System.Reflection;
using Xunit.Sdk;
using Xunit.v3;

namespace Project.TestFramework.TestCaseOrdering;

public sealed class PriorityOrderer : ITestCaseOrderer
{
    public IReadOnlyCollection<TTestCase> OrderTestCases<TTestCase>(IReadOnlyCollection<TTestCase> testCases)
        where TTestCase : ITestCase
    {
        var sortedMethods = new SortedDictionary<int, List<TTestCase>>();

        foreach (TTestCase testCase in testCases)
        {
            int priority = 0;

            if (testCase is IXunitTestCase xunitTestCase)
            {
                MethodInfo methodInfo = xunitTestCase.TestMethod.Method;
                TestPriorityAttribute? attribute = methodInfo.GetCustomAttribute<TestPriorityAttribute>();
                if (attribute is not null)
                {
                    priority = attribute.Priority;
                }
            }

            GetOrCreate(sortedMethods, priority).Add(testCase);
        }

        var ordered = new List<TTestCase>(testCases.Count);
        foreach (List<TTestCase> list in sortedMethods.Keys.Select(priority => sortedMethods[priority]))
        {
            list.Sort((x, y) => StringComparer.OrdinalIgnoreCase.Compare(x.TestCaseDisplayName, y.TestCaseDisplayName));
            ordered.AddRange(list);
        }

        return ordered;
    }

    private static TValue GetOrCreate<TKey, TValue>(IDictionary<TKey, TValue> dictionary, TKey key)
        where TKey : notnull
        where TValue : new()
    {
        if (dictionary.TryGetValue(key, out TValue? result))
        {
            return result;
        }

        result = new TValue();
        dictionary[key] = result;
        return result;
    }
}
