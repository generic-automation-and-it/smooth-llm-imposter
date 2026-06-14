namespace Project.TestFramework.TestCaseOrdering;

[AttributeUsage(AttributeTargets.Method)]
public sealed class TestPriorityAttribute(int priority) : Attribute
{
    public int Priority { get; } = priority;
}
