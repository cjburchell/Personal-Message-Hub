namespace MessageHub.Core;

public static class ReportSummaryBuilder
{
    public static ReportSummary Build(IEnumerable<UnifiedMessage> messages)
    {
        var items = messages.ToArray();
        return new ReportSummary(
            items.Length,
            items.Count(x => !x.IsOutgoing),
            items.Count(x => x.IsOutgoing),
            items.GroupBy(x => x.Source, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase));
    }
}
