using System.Diagnostics;
using System.Reflection;

namespace FleetPulse.Api.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute;

public sealed class AssertionException(string message) : Exception(message);

public static class Assert
{
    public static void True(bool cond, string what = "condition")
    {
        if (!cond) throw new AssertionException($"expected true: {what}");
    }

    public static void Equal<T>(T expected, T actual, string what = "value")
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertionException($"{what}: expected <{expected}> but was <{actual}>");
    }

    public static void Near(double expected, double? actual, double tol = 1e-6, string what = "value")
    {
        if (actual is null || Math.Abs(expected - actual.Value) > tol)
            throw new AssertionException($"{what}: expected {expected} ± {tol} but was {actual?.ToString() ?? "null"}");
    }

    public static void Null(object? value, string what = "value")
    {
        if (value is not null) throw new AssertionException($"{what}: expected null but was <{value}>");
    }

    public static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T e) { return e; }
        throw new AssertionException($"expected {typeof(T).Name}");
    }
}

/// <summary>Discovers public static methods marked [Test] in this assembly and runs them.</summary>
public static class TestRunner
{
    public static async Task<int> Main(string[] args)
    {
        var filter = args.FirstOrDefault();
        var tests = typeof(TestRunner).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<TestAttribute>() is not null)
            .Where(m => filter is null || $"{m.DeclaringType!.Name}.{m.Name}".Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.DeclaringType!.Name).ThenBy(m => m.Name)
            .ToList();
        int failed = 0;
        var sw = Stopwatch.StartNew();
        foreach (var m in tests)
        {
            var name = $"{m.DeclaringType!.Name}.{m.Name}";
            try
            {
                var r = m.Invoke(null, null);
                if (r is Task task) await task;
                Console.WriteLine($"  PASS {name}");
            }
            catch (Exception e)
            {
                failed++;
                var inner = e is TargetInvocationException { InnerException: { } ie } ? ie : e;
                Console.WriteLine($"  FAIL {name}: {inner.GetType().Name}: {inner.Message}");
            }
        }
        Console.WriteLine($"{tests.Count - failed}/{tests.Count} tests passed in {sw.Elapsed.TotalSeconds:F1}s");
        return failed == 0 && tests.Count > 0 ? 0 : 1;
    }
}
