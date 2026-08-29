namespace Vela.Analytics;

/// <summary>
/// Represents the parsed CLI arguments for a report run.
/// </summary>
public class AnalyticsOptions
{
    public ReportType Report { get; init; } = ReportType.Weekly;
    public DateTimeOffset From { get; init; }
    public DateTimeOffset To { get; init; }
    public string OutputDirectory { get; init; } = "reports";

    /// <summary>
    /// Parses CLI args into an AnalyticsOptions instance.
    /// Usage:
    ///   --report weekly
    ///   --report monthly
    ///   --report custom --from 2026-05-01 --to 2026-05-31
    /// The now parameter overrides DateTimeOffset.UtcNow for the weekly/monthly/custom-default
    /// window calculation, so callers can pin the clock for testing.
    /// </summary>
    public static AnalyticsOptions Parse(string[] args, DateTimeOffset? now = null)
    {
        var reportType = ReportType.Weekly;
        DateTimeOffset? from = null;
        DateTimeOffset? to = null;
        var outputDir = "reports";

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--report" when i + 1 < args.Length:
                    reportType = args[++i].ToLowerInvariant() switch
                    {
                        "weekly"  => ReportType.Weekly,
                        "monthly" => ReportType.Monthly,
                        "custom"  => ReportType.Custom,
                        _         => ReportType.Weekly
                    };
                    break;

                case "--from" when i + 1 < args.Length:
                    if (DateTimeOffset.TryParse(args[++i], out var parsedFrom))
                        from = parsedFrom;
                    break;

                case "--to" when i + 1 < args.Length:
                    if (DateTimeOffset.TryParse(args[++i], out var parsedTo))
                        to = parsedTo;
                    break;

                case "--output" when i + 1 < args.Length:
                    outputDir = args[++i];
                    break;
            }
        }

        // Calculate date range in ET, all market activity is measured in ET
        var et = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var nowEt = TimeZoneInfo.ConvertTime(now ?? DateTimeOffset.UtcNow, et);

        // Upper bound is today, not tomorrow, so consecutive scheduled runs never overlap.
        // The report-run day itself belongs to the following window, not this one, since
        // that's the boundary convention already confirmed against the manual reconciliation.
        var (resolvedFrom, resolvedTo) = reportType switch
        {
            ReportType.Weekly => (
                nowEt.AddDays(-7).Date.ToDateTimeOffset(et),
                nowEt.Date.ToDateTimeOffset(et)),

            ReportType.Monthly => (
                nowEt.AddDays(-30).Date.ToDateTimeOffset(et),
                nowEt.Date.ToDateTimeOffset(et)),

            ReportType.Custom => (
                from ?? nowEt.AddDays(-7).Date.ToDateTimeOffset(et),
                to?.AddDays(1) ?? nowEt.Date.ToDateTimeOffset(et)),

            _ => (
                nowEt.AddDays(-7).Date.ToDateTimeOffset(et),
                nowEt.Date.ToDateTimeOffset(et))
        };

        return new AnalyticsOptions
        {
            Report          = reportType,
            From            = resolvedFrom,
            To              = resolvedTo,
            OutputDirectory = outputDir,
        };
    }
}

public enum ReportType
{
    Weekly,
    Monthly,
    Custom
}

/// <summary>
/// Extension to convert a local date to a DateTimeOffset in a specific timezone.
/// </summary>
internal static class DateExtensions
{
    public static DateTimeOffset ToDateTimeOffset(this DateTime localDate, TimeZoneInfo tz)
    {
        var offset = tz.GetUtcOffset(localDate);
        return new DateTimeOffset(localDate, offset);
    }
}