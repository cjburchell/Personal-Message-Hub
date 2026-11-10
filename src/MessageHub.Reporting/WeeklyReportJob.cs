using MessageHub.Core;
using MessageHub.Storage;

namespace MessageHub.Reporting;

public sealed class WeeklyReportJob(SqliteMessageStore store, ReportService reports, WeeklySchedule schedule,
    TimeProvider? timeProvider = null, Func<CancellationToken, Task>? beforeReport = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<StoredReport?> RunDueAsync(CancellationToken ct = default)
    {
        var end = schedule.LatestDue(_time.GetUtcNow());
        var id = "weekly-" + end.UtcDateTime.ToString("yyyyMMddTHHmmssZ", System.Globalization.CultureInfo.InvariantCulture);
        if (await store.GetReportAsync(id, ct) is not null) return null;
        if (beforeReport is not null) await beforeReport(ct);
        return await reports.BuildAsync(schedule.PreviousBoundary(end), end, id, ct);
    }
}
