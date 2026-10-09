namespace Receivify.Tests;

internal static class MoneyTests
{
    public static async Task RunAsync()
    {
        Test.Section("Exact money helpers");

        await Test.RunAsync("base units render as a display decimal", () =>
        {
            Test.AreEqual("1.50000000", Money.Format("150000000", 8), "1.5 BTC");
            Test.AreEqual("0.00000001", Money.Format("1", 8), "one satoshi");
            Test.AreEqual("0.00000000", Money.Format("0", 8), "zero");
            Test.AreEqual("-0.00000001", Money.Format("-1", 8), "a negative");
            Test.AreEqual("1234567890", Money.Format("1234567890", 0), "a zero-decimal asset");
            Test.AreEqual(
                "1.234567890123456789",
                Money.Format("1234567890123456789", 18),
                "a full-precision wei value, which no double could hold");
        }).ConfigureAwait(false);

        await Test.RunAsync("a display decimal parses back to exact base units", () =>
        {
            Test.AreEqual("150000000", Money.Parse("1.5", 8), "1.5 BTC");
            Test.AreEqual("1", Money.Parse("0.00000001", 8), "one satoshi");
            Test.AreEqual("0", Money.Parse("0", 8), "zero");
            Test.AreEqual("0", Money.Parse("0.00000000", 8), "padded zero");
            Test.AreEqual("-150000000", Money.Parse("-1.5", 8), "a negative");
            Test.AreEqual(
                "1234567890123456789",
                Money.Parse("1.234567890123456789", 18),
                "and a full-precision wei value round-trips");
        }).ConfigureAwait(false);

        await Test.RunAsync("Format and Parse round-trip each other", () =>
        {
            string[] amounts = { "0", "1", "999", "100000000", "1234567890123456789" };
            foreach (string amount in amounts)
            {
                Test.AreEqual(amount, Money.Parse(Money.Format(amount, 18), 18), "round trip of " + amount);
            }
        }).ConfigureAwait(false);

        await Test.RunAsync("Parse REFUSES to silently truncate money", () =>
        {
            // Rounding an amount away without saying so is exactly the class of bug this SDK's
            // string-money rule exists to prevent, so this throws rather than helping.
            Test.Throws<FormatException>(
                () => Money.Parse("0.000000001", 8),
                "nine decimals on an eight-decimal asset");

            Test.AreEqual(
                "1",
                Money.Parse("0.000000010", 8),
                "but a trailing zero carries no value and is fine");
        }).ConfigureAwait(false);

        await Test.RunAsync("garbage is rejected rather than coerced to zero", () =>
        {
            Test.Throws<FormatException>(() => Money.ToBigInteger("1.5"), "a decimal is not base units");
            Test.Throws<FormatException>(() => Money.ToBigInteger("abc"), "letters");
            Test.Throws<FormatException>(() => Money.ToBigInteger(""), "empty");
            Test.Throws<FormatException>(() => Money.Parse("1.2.3", 8), "two dots");
            Test.Throws<FormatException>(() => Money.Parse("1e18", 8), "scientific notation");
        }).ConfigureAwait(false);

        await Test.RunAsync("comparison and arithmetic stay exact", () =>
        {
            const string a = "1234567890123456789";
            const string b = "1234567890123456788";

            Test.IsTrue(Money.Compare(a, b) > 0, "one wei apart, and the difference survives");
            Test.AreEqual(0, Money.Compare(a, a), "equal");
            Test.AreEqual("1", Money.Subtract(a, b), "subtraction is exact");
            Test.AreEqual("2469135780246913577", Money.Add(a, b), "addition is exact");
        }).ConfigureAwait(false);

        await Test.RunAsync("IsIntegerString recognises base-unit strings", () =>
        {
            Test.IsTrue(Money.IsIntegerString("0"), "zero");
            Test.IsTrue(Money.IsIntegerString("-42"), "negative");
            Test.IsFalse(Money.IsIntegerString("1.0"), "a decimal is not one");
            Test.IsFalse(Money.IsIntegerString("-"), "a lone sign is not one");
            Test.IsFalse(Money.IsIntegerString(null), "null is not one");
        }).ConfigureAwait(false);

        Test.Section("Error-code registry");

        await Test.RunAsync("the frozen registry matches the spec", () =>
        {
            Test.AreEqual(23, ErrorCodes.All.Count, "twenty-three codes");
            Test.IsTrue(ErrorCodes.IsKnown("bad_ticker"), "a known code");
            Test.IsFalse(ErrorCodes.IsKnown("teapot_unavailable"), "a future code is not known yet");
            Test.IsTrue(ErrorCodes.IsRetryable("upstream_error"), "upstream_error is retryable");
            Test.IsTrue(ErrorCodes.IsRetryable("rate_unavailable"), "so is rate_unavailable");
            Test.IsFalse(ErrorCodes.IsRetryable("bad_request"), "a caller error is not");
        }).ConfigureAwait(false);
    }
}
