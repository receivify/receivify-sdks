using System.Globalization;
using System.Numerics;

namespace Receivify;

/// <summary>
/// Opt-in exact arithmetic and formatting for the API's money strings.
/// </summary>
/// <remarks>
/// <para>
/// <b>Money on the wire is always a <see cref="string"/>, and this SDK never changes that.</b>
/// Amounts are exact-integer base units — satoshis for BTC, wei for ETH, the coin's smallest unit
/// otherwise. A <see cref="double"/> cannot represent a 256-bit wei value at all, and a
/// <see cref="decimal"/> tops out at 28-29 significant digits while implying an arithmetic model
/// the API does not have. So the resource records expose the raw string, and everything here is
/// something you reach for deliberately.
/// </para>
/// <para>
/// All conversions route through <see cref="BigInteger"/>, which is part of the base class
/// library. Nothing here parses a float.
/// </para>
/// <para>
/// Get the decimals argument from <see cref="Ticker.Decimals"/> (via <c>ListTickersAsync</c>) —
/// never hard-code 8.
/// </para>
/// </remarks>
public static class Money
{
    /// <summary>Parse an exact-integer base-unit string into a <see cref="BigInteger"/>.</summary>
    /// <param name="baseUnits">An integer string such as <c>"1000000000000000000"</c>.</param>
    /// <exception cref="FormatException">The string is not an optionally-signed run of digits.</exception>
    public static BigInteger ToBigInteger(string baseUnits)
    {
        ArgumentNullException.ThrowIfNull(baseUnits);

        string trimmed = baseUnits.Trim();
        if (!IsIntegerString(trimmed))
        {
            throw new FormatException(
                "Receivify: \"" + baseUnits + "\" is not an exact-integer base-unit amount.");
        }

        return BigInteger.Parse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Render base units as a display-unit decimal string, e.g. <c>Format("150000000", 8)</c> is
    /// <c>"1.50000000"</c>. For display only — never feed the result back into an amount field.
    /// </summary>
    /// <param name="baseUnits">An exact-integer base-unit string.</param>
    /// <param name="decimals">Base units per whole coin as a power of ten, from <see cref="Ticker.Decimals"/>.</param>
    public static string Format(string baseUnits, int decimals)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(decimals);

        BigInteger value = ToBigInteger(baseUnits);
        bool negative = value.Sign < 0;
        string digits = BigInteger.Abs(value).ToString(CultureInfo.InvariantCulture);

        if (decimals == 0)
        {
            return negative ? "-" + digits : digits;
        }

        if (digits.Length <= decimals)
        {
            digits = digits.PadLeft(decimals + 1, '0');
        }

        string whole = digits[..^decimals];
        string fraction = digits[^decimals..];
        string rendered = whole + "." + fraction;

        return negative ? "-" + rendered : rendered;
    }

    /// <summary>
    /// Convert a display-unit decimal string into exact base units, e.g.
    /// <c>Parse("1.5", 8)</c> is <c>"150000000"</c>.
    /// </summary>
    /// <remarks>
    /// Refuses to silently truncate: a value carrying more precision than the asset supports
    /// throws rather than quietly rounding money away.
    /// </remarks>
    /// <param name="displayAmount">A decimal string such as <c>"0.00120000"</c>.</param>
    /// <param name="decimals">Base units per whole coin as a power of ten.</param>
    /// <exception cref="FormatException">The string is not a plain decimal, or it is too precise.</exception>
    public static string Parse(string displayAmount, int decimals)
    {
        ArgumentNullException.ThrowIfNull(displayAmount);
        ArgumentOutOfRangeException.ThrowIfNegative(decimals);

        string text = displayAmount.Trim();
        bool negative = text.StartsWith('-');
        if (negative || text.StartsWith('+'))
        {
            text = text[1..];
        }

        int dot = text.IndexOf('.');
        string whole = dot < 0 ? text : text[..dot];
        string fraction = dot < 0 ? string.Empty : text[(dot + 1)..];

        if (whole.Length == 0)
        {
            whole = "0";
        }

        if (!IsDigits(whole) || (fraction.Length > 0 && !IsDigits(fraction)))
        {
            throw new FormatException(
                "Receivify: \"" + displayAmount + "\" is not a plain decimal amount.");
        }

        if (fraction.Length > decimals)
        {
            string excess = fraction[decimals..];
            if (excess.TrimEnd('0').Length > 0)
            {
                throw new FormatException(
                    "Receivify: \"" + displayAmount + "\" carries more precision than an asset with "
                    + decimals.ToString(CultureInfo.InvariantCulture)
                    + " decimals can hold. Rounding money silently is not something this SDK will do for you.");
            }

            fraction = fraction[..decimals];
        }

        fraction = fraction.PadRight(decimals, '0');

        string combined = (whole + fraction).TrimStart('0');
        if (combined.Length == 0)
        {
            return "0";
        }

        return negative ? "-" + combined : combined;
    }

    /// <summary>Compare two base-unit strings exactly. Same contract as <see cref="IComparable{T}.CompareTo"/>.</summary>
    /// <param name="left">An exact-integer base-unit string.</param>
    /// <param name="right">An exact-integer base-unit string.</param>
    public static int Compare(string left, string right) =>
        BigInteger.Compare(ToBigInteger(left), ToBigInteger(right));

    /// <summary>Exact addition of two base-unit strings; the result is a base-unit string.</summary>
    /// <param name="left">An exact-integer base-unit string.</param>
    /// <param name="right">An exact-integer base-unit string.</param>
    public static string Add(string left, string right) =>
        (ToBigInteger(left) + ToBigInteger(right)).ToString(CultureInfo.InvariantCulture);

    /// <summary>Exact subtraction of two base-unit strings; the result is a base-unit string.</summary>
    /// <param name="left">An exact-integer base-unit string.</param>
    /// <param name="right">An exact-integer base-unit string.</param>
    public static string Subtract(string left, string right) =>
        (ToBigInteger(left) - ToBigInteger(right)).ToString(CultureInfo.InvariantCulture);

    /// <summary>True when the string is an optionally-signed run of decimal digits and nothing else.</summary>
    /// <param name="value">Any string.</param>
    public static bool IsIntegerString(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        int start = value[0] == '-' ? 1 : 0;
        return value.Length > start && IsDigits(value.AsSpan(start));
    }

    private static bool IsDigits(ReadOnlySpan<char> value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }
}
