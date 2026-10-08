using System;
using System.Globalization;
using System.Numerics;

namespace Beta29.SearchBackend.Compatibility;

internal static class FrameworkNumber
{
    internal static string ConvertToString(object value)
    {
        // Framework's default double format has 15 significant digits, not Core's
        // shortest round-trippable representation. The supplied catalog uses strings.
        if (value is double d) return d == 0 ? "0" : d.ToString("G15", CultureInfo.CurrentCulture);
        return Convert.ToString(value);
    }

    internal static string FormatFixed(double value, int places)
    {
        NumberFormatInfo format = CultureInfo.CurrentCulture.NumberFormat;
        if (!double.IsFinite(value)) return value.ToString(format);

        // Framework first makes a 15-significant-digit number buffer, then its
        // RoundNumber rounds the fixed-point position up when the next digit >= 5.
        // Work on that decimal buffer: rounding the original binary double with
        // Math.Round would not preserve double-rounding in Framework's formatter.
        string g = Math.Abs(value).ToString("G15", CultureInfo.InvariantCulture);
        int e = g.IndexOf('E');
        int exponent = e < 0 ? 0 : int.Parse(g.Substring(e + 1), CultureInfo.InvariantCulture);
        string mantissa = e < 0 ? g : g.Substring(0, e);
        int dot = mantissa.IndexOf('.');
        int fraction = dot < 0 ? 0 : mantissa.Length - dot - 1;
        BigInteger digits = BigInteger.Parse(mantissa.Replace(".", ""), CultureInfo.InvariantCulture);
        int shift = exponent - fraction + places;
        if (shift >= 0) digits *= BigInteger.Pow(10, shift);
        else
        {
            BigInteger divisor = BigInteger.Pow(10, -shift);
            BigInteger quotient = BigInteger.DivRem(digits, divisor, out BigInteger remainder);
            digits = quotient + (remainder * 2 >= divisor ? 1 : 0);
        }
        string text = digits.ToString(CultureInfo.InvariantCulture).PadLeft(places + 1, '0');
        if (places > 0) text = text.Insert(text.Length - places, format.NumberDecimalSeparator);
        if (value < 0 && !digits.IsZero) text = format.NegativeSign + text;
        return text;
    }
}
