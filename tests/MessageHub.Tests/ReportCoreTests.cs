using MessageHub.Core;
using Xunit;

namespace MessageHub.Tests;

public sealed class ReportCoreTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-10T13:00:00Z");
    private static UnifiedMessage Message(string source, string id, string body, bool outgoing = false, int minutes = 0) =>
        new(source, id, "thread", "sender@example.test", [], "Subject", body, Now.AddMinutes(minutes), outgoing);

    [Fact]
    public void RepliedConversationIsNotOutstandingAndSourcesDoNotShareThreads()
    {
        var candidates = ReportCandidateSelector.Select([
            Message("gmail", "question", "Could you confirm?"),
            Message("gmail", "answer", "Confirmed.", true, 1),
            Message("yahoo", "question", "Could you confirm?")
        ]);
        var candidate = Assert.Single(candidates);
        Assert.Equal("yahoo:question", candidate.Id);
        Assert.Contains("reply", candidate.Signals);
        Assert.Contains("action", candidate.Signals);
    }

    [Fact]
    public void CandidateCountAndEvidenceAreBoundedAndDeadlineIsAnExplicitValidDate()
    {
        var messages = Enumerable.Range(0, 100).Select(i => Message("example", "id" + i,
            "Deadline 2026-99-99; due 2026-11-14. " + new string('x', 1000)) with { Subject = new string('s', 300), ConversationId = "thread" + i }).ToArray();
        var candidates = ReportCandidateSelector.Select(messages);
        Assert.Equal(ReportCandidateSelector.MaximumCandidates, candidates.Count);
        Assert.All(candidates, x =>
        {
            Assert.Equal("2026-11-14", x.DueDate);
            Assert.Equal(ReportCandidateSelector.MaximumExcerptCharacters, x.Excerpt.Length);
            Assert.Equal(ReportCandidateSelector.MaximumSubjectCharacters, x.Subject.Length);
        });
    }

    [Fact]
    public void RelativeDeadlineDoesNotInventDate()
    {
        var candidate = Assert.Single(ReportCandidateSelector.Select([Message("example", "id", "Your book is due Friday.")]));
        Assert.Contains("deadline", candidate.Signals);
        Assert.Null(candidate.DueDate);
    }

    [Fact]
    public void DeadlineDateMustFollowDeadlineLanguageRatherThanAnUnrelatedEarlierDate()
    {
        var candidate = Assert.Single(ReportCandidateSelector.Select([
            Message("example", "id", "Invoice issued 2026-11-01. Please reply by 2026-11-14.")
        ]));
        Assert.Contains("deadline", candidate.Signals);
        Assert.Equal("2026-11-14", candidate.DueDate);
    }

    [Theory]
    [InlineData("2026-11-09T07:59:59Z", "2026-11-02T08:00:00Z")]
    [InlineData("2026-11-09T08:00:00Z", "2026-11-09T08:00:00Z")]
    [InlineData("2026-11-10T08:00:00Z", "2026-11-09T08:00:00Z")]
    public void WeeklyScheduleIncludesDueInstantAndCatchesUpOnlyLatestWindow(string now, string expected)
    {
        var schedule = new WeeklySchedule(TimeZoneInfo.Utc);
        Assert.Equal(DateTimeOffset.Parse(expected), schedule.LatestDue(DateTimeOffset.Parse(now)));
    }

    [Fact]
    public void WeeklyScheduleUsesCalendarWeekAcrossDst()
    {
        var schedule = new WeeklySchedule(TimeZoneInfo.FindSystemTimeZoneById("America/Toronto"));
        var due = schedule.LatestDue(DateTimeOffset.Parse("2026-11-02T14:00:00Z"));
        Assert.Equal(DateTimeOffset.Parse("2026-11-02T13:00:00Z"), due);
        Assert.Equal(TimeSpan.FromHours(169), due - schedule.PreviousBoundary(due));
    }

    [Fact]
    public void WeeklyScheduleHandlesSkippedAndRepeatedHours()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Toronto");
        var spring = new WeeklySchedule(zone, DayOfWeek.Sunday, 2);
        var due = spring.LatestDue(DateTimeOffset.Parse("2026-03-08T08:00:00Z"));
        Assert.Equal(DateTimeOffset.Parse("2026-03-08T07:00:00Z"), due);
        Assert.Equal(DateTimeOffset.Parse("2026-03-01T07:00:00Z"), spring.PreviousBoundary(due));
        var fall = new WeeklySchedule(zone, DayOfWeek.Sunday, 1);
        Assert.Equal(DateTimeOffset.Parse("2026-10-25T05:00:00Z"), fall.LatestDue(DateTimeOffset.Parse("2026-11-01T05:30:00Z")));
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T06:00:00Z"), fall.LatestDue(DateTimeOffset.Parse("2026-11-01T06:00:00Z")));
    }
}
