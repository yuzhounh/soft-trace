namespace SoftTrace.Core;

public sealed record AcademicCalendarSource(string AcademicYear, string Url);

public static class AcademicCalendar
{
    // Fixed month groups, independent of a school or a specific academic year.
    public static IReadOnlyList<AcademicCalendarSource> Sources { get; } = [];

    public static string Classify(DateOnly date) => date.Month switch
    {
        2 => "winter",
        7 or 8 => "summer",
        >= 3 and <= 6 => "spring",
        _ => "autumn"
    };
}
