using System.Globalization;

namespace Receivify.Tests;

/// <summary>
/// A ~100-line assertion harness so the test suite needs no NuGet package at all.
/// </summary>
internal static class Test
{
    private static int _passed;
    private static int _failed;
    private static readonly List<string> Failures = new();

    public static async Task RunAsync(string name, Func<Task> body)
    {
        try
        {
            await body().ConfigureAwait(false);
            _passed++;
            Console.WriteLine("  pass  " + name);
        }
        catch (Exception ex)
        {
            _failed++;
            Failures.Add(name + ": " + ex.Message);
            Console.WriteLine("  FAIL  " + name);
            Console.WriteLine("        " + ex.Message);
        }
    }

    public static Task RunAsync(string name, Action body) =>
        RunAsync(name, () =>
        {
            body();
            return Task.CompletedTask;
        });

    public static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine(title);
    }

    public static int Summarise()
    {
        Console.WriteLine();
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{_passed + _failed} tests, {_passed} passed, {_failed} failed"));

        if (_failed > 0)
        {
            Console.WriteLine();
            foreach (string failure in Failures)
            {
                Console.WriteLine("  - " + failure);
            }
        }

        return _failed == 0 ? 0 : 1;
    }

    public static void IsTrue(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("expected true — " + message);
        }
    }

    public static void IsFalse(bool condition, string message) => IsTrue(!condition, message);

    public static void AreEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                message + " — expected <" + Describe(expected) + ">, got <" + Describe(actual) + ">");
        }
    }

    public static void IsNull(object? value, string message)
    {
        if (value is not null)
        {
            throw new InvalidOperationException(message + " — expected null, got <" + Describe(value) + ">");
        }
    }

    public static void IsNotNull(object? value, string message)
    {
        if (value is null)
        {
            throw new InvalidOperationException(message + " — expected a value, got null");
        }
    }

    public static async Task<TException> ThrowsAsync<TException>(Func<Task> body, string message)
        where TException : Exception
    {
        try
        {
            await body().ConfigureAwait(false);
        }
        catch (TException ex)
        {
            return ex;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                message + " — expected " + typeof(TException).Name + ", got " + ex.GetType().Name + ": " + ex.Message);
        }

        throw new InvalidOperationException(message + " — expected " + typeof(TException).Name + ", nothing was thrown");
    }

    public static TException Throws<TException>(Action body, string message)
        where TException : Exception
    {
        try
        {
            body();
        }
        catch (TException ex)
        {
            return ex;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                message + " — expected " + typeof(TException).Name + ", got " + ex.GetType().Name + ": " + ex.Message);
        }

        throw new InvalidOperationException(message + " — expected " + typeof(TException).Name + ", nothing was thrown");
    }

    private static string Describe(object? value) => value?.ToString() ?? "null";
}
