using System.Globalization;

namespace Tenure;

/// <summary>
/// Formatting for the human-readable reports. Kept separate from the command line so the exact
/// rendering of a delta is unit-tested rather than only observed by eye.
/// </summary>
public static class ReportFormat
{
    /// <summary>
    /// Renders a signed delta with an explicit zero. A composite format such as "+#;0;-#;0" has four
    /// sections, which .NET treats as three plus literal text, so zero renders as "-" and reads like a
    /// decrease in a report that only lists increases.
    /// </summary>
    public static string Signed(long value) => value switch
    {
        > 0 => "+" + value.ToString(CultureInfo.InvariantCulture),
        < 0 => value.ToString(CultureInfo.InvariantCulture),
        _ => "0",
    };

    /// <summary>Renders a change in the number of owning roots, which is meaningful only when non-zero.</summary>
    public static string RootDelta(long value) => value == 0 ? "unchanged" : Signed(value);
}
