// ==================================================
// Program.cs - ロジックテストの実行 (名前が "Tests" で終わるクラスの、"Test" で始まる public static メソッドを全部呼ぶ)
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Reflection;

namespace LogicTests;

/// <summary>テストの結果を数える</summary>
public static class T
{
    public static int Passed;
    public static int Failed;
    public static readonly List<string> Failures = new();
    public static string Current = "";

    public static void Check(bool ok, string name)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
        if (ok) Passed++;
        else { Failed++; Failures.Add($"{Current}: {name}"); }
    }

    public static void Near(double actual, double expected, double tol, string name) =>
        Check(Math.Abs(actual - expected) <= tol, $"{name} (actual {actual:G6}, expected {expected:G6} ± {tol:G3})");

    public static void Info(string text) => Console.WriteLine("     " + text);
}

public static class Program
{
    public static int Main(string[] args)
    {
        string filter = args.Length > 0 ? args[0] : "";
        var tests = typeof(Program).Assembly.GetTypes()
            .Where(t => t.Name.EndsWith("Tests") && t.IsClass)
            .OrderBy(t => t.Name)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name.StartsWith("Test") && m.GetParameters().Length == 0)
                .OrderBy(m => m.MetadataToken)
                .Select(m => (Type: t, Method: m)))
            .Where(x => filter.Length == 0 || $"{x.Type.Name}.{x.Method.Name}".Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var (type, method) in tests)
        {
            T.Current = $"{type.Name}.{method.Name}";
            Console.WriteLine($"\n===== {T.Current}");
            try { method.Invoke(null, null); }
            catch (TargetInvocationException ex)
            {
                T.Check(false, $"threw {ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}\n{ex.InnerException?.StackTrace}");
            }
        }
        Console.WriteLine($"\n{T.Passed} passed, {T.Failed} failed ({tests.Count} test methods)");
        foreach (var f in T.Failures) Console.WriteLine("  FAILED: " + f);
        Console.WriteLine(T.Failed == 0 ? "ALL PASS" : "SOME FAILED");
        return T.Failed == 0 ? 0 : 1;
    }
}
