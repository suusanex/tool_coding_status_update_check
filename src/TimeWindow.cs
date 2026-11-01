/// <summary>
/// 日付境界は指定タイムゾーンのローカル日。保存用の端点は UTC。
/// to の日付は含む。内部表現は翌日 0:00 の直前まで。
/// </summary>
internal readonly record struct TimeWindow(DateTimeOffset FromUtc, DateTimeOffset ToUtcExclusive, string TimeZoneId, DateOnly FromDate, DateOnly ToDate)
{
    public bool Contains(DateTimeOffset timestamp) => timestamp >= FromUtc && timestamp < ToUtcExclusive;

    public static TimeWindow LastDays(int days, TimeZoneInfo zone, DateTimeOffset now)
    {
        if (days < 1)
        {
            throw new StatusCheckException("--last-days must be at least 1.");
        }

        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var endDate = DateOnly.FromDateTime(localNow.DateTime);
        var startDate = endDate.AddDays(-(days - 1));
        return FromDates(startDate, endDate, zone);
    }

    public static TimeWindow FromDates(DateOnly from, DateOnly to, TimeZoneInfo zone)
    {
        if (to < from)
        {
            throw new StatusCheckException("The end date is before the start date.");
        }

        var start = new DateTime(from.Year, from.Month, from.Day, 0, 0, 0, DateTimeKind.Unspecified);
        var endExclusive = new DateTime(to.Year, to.Month, to.Day, 0, 0, 0, DateTimeKind.Unspecified).AddDays(1);
        var fromUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(start, zone), TimeSpan.Zero);
        var toUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(endExclusive, zone), TimeSpan.Zero);
        return new TimeWindow(fromUtc, toUtc, zone.Id, from, to);
    }
}
