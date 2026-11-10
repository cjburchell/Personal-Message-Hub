namespace MessageHub.Core;

// Evaluate in wall-clock time, then convert each boundary independently for DST-safe seven-calendar-day windows.
public sealed record WeeklySchedule(TimeZoneInfo TimeZone, DayOfWeek Day = DayOfWeek.Monday, int Hour = 8)
{
    public DateTimeOffset LatestDue(DateTimeOffset now)
    {
        if (Hour is < 0 or > 23) throw new ArgumentOutOfRangeException(nameof(Hour));
        if (!Enum.IsDefined(Day)) throw new ArgumentOutOfRangeException(nameof(Day));
        var local = TimeZoneInfo.ConvertTime(now, TimeZone);
        var date = local.Date.AddDays(-((7 + (int)local.DayOfWeek - (int)Day) % 7)).AddHours(Hour);
        var due = ToUtc(date);
        return due > now ? ToUtc(date.AddDays(-7)) : due;
    }

    public DateTimeOffset PreviousBoundary(DateTimeOffset due) => ToUtc(TimeZoneInfo.ConvertTime(due, TimeZone).Date.AddDays(-7).AddHours(Hour));

    private DateTimeOffset ToUtc(DateTime local)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        // If a configured hour is skipped at DST start, use the first valid minute afterwards.
        while (TimeZone.IsInvalidTime(local)) local = local.AddMinutes(1);
        // At a repeated hour choose its later occurrence, consistently across restarts.
        var offset = TimeZone.IsAmbiguousTime(local) ? TimeZone.GetAmbiguousTimeOffsets(local).Min() : TimeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
