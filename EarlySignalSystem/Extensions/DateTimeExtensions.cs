namespace EarlySignalSystem.Extensions;

// Всички timestamp-и се пазят в UTC (DateTime.UtcNow навсякъде в services-ите) — конвертираме към
// локалната часова зона на машината, на която тече приложението, преди показване в UI-то.
public static class DateTimeExtensions
{
    private const string DefaultFormat = "dd MMM yyyy, HH:mm";

    public static string ToLocalDisplay(this DateTime utc, string format = DefaultFormat) =>
        utc.ToLocalTime().ToString(format);

    public static string? ToLocalDisplay(this DateTime? utc, string format = DefaultFormat) =>
        utc?.ToLocalTime().ToString(format);

    public static DateTime ToLocalDate(this DateTime utc) => utc.ToLocalTime().Date;
}
