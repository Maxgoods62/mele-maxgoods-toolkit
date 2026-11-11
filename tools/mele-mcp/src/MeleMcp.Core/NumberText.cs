using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace MeleMcp.Core;

/// <summary>
/// The one way this code reads a decimal number from text (src/BannedSymbols.txt keeps float.Parse and double.Parse here): the
/// invariant culture, no thousands separator, and a finite result. float.Parse(text, CultureInfo.InvariantCulture) allows
/// thousands separators, so it reads "0,5" as 5 and "1,25" as 125; with or without them it takes "NaN" and "Infinity", and reads
/// "1e39" as Infinity. A caller that catches float.Parse's FormatException keeps working: these throw the same type.
/// </summary>
public static class NumberText
{
    /// <summary>A sign, digits, a decimal point, an exponent, white space around it: no thousands separator.</summary>
    private const NumberStyles Plain = NumberStyles.Float;

    [SuppressMessage("ApiDesign", "RS0030", Justification = "The one place decimal numbers are read from text.")]
    public static bool TryFloat(string? text, out float value) =>
        float.TryParse(text, Plain, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);

    [SuppressMessage("ApiDesign", "RS0030", Justification = "The one place decimal numbers are read from text.")]
    public static bool TryDouble(string? text, out double value) =>
        double.TryParse(text, Plain, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    public static float Float(string? text) => TryFloat(text, out var value) ? value : throw NotANumber(text);

    public static double Double(string? text) => TryDouble(text, out var value) ? value : throw NotANumber(text);

    /// <summary>
    /// Makes every thread of the process format and parse with the invariant culture wherever no culture is given: a French
    /// Windows otherwise prints 2.5 as "2,5" (an interpolated string, a bare ToString()), in this code and in LEC's. A program
    /// calls it as its first statement. It does not replace the methods above: the invariant culture's own thousands separator
    /// is the comma.
    /// </summary>
    public static void UseInvariantCulture()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
    }

    private static FormatException NotANumber(string? text) =>
        new($"'{text}' is not a number: write digits with a decimal point, such as 0.5, -12 or 1e-3 (no comma, no NaN, no Infinity).");
}
