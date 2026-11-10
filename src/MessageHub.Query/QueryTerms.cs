using System.Text.RegularExpressions;

namespace MessageHub.Query;

public static partial class QueryTerms
{
    private static readonly HashSet<string> Stop = new("a an and are at be been can could did do does for from had has have how i in is it me my of on or our show that the their there these they this to us was were what when where which who why will with would you your last days day messages message emails email about please find tell".Split(' '), StringComparer.Ordinal);
    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}@._'-]{1,49}", RegexOptions.CultureInvariant)]
    private static partial Regex Words();
    public static IReadOnlyList<string> Extract(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 1000) throw new ArgumentException("Use a question of 1 through 1000 characters.");
        var words = Words().Matches(query.ToLowerInvariant()).Select(x => x.Value.Trim('.', '\'', '-')).Where(x => x.Length > 1 && !Stop.Contains(x)).Distinct().Take(8).ToList();
        if (words.Any(x => x is "deadlines" or "deadline" or "due")) words.InsertRange(0, ["due", "deadline", "expires"]);
        if (words.Any(x => x is "reply" or "replies" or "unanswered" or "outstanding")) words.InsertRange(0, ["reply", "confirm", "could", "please"]);
        return words.Distinct(StringComparer.Ordinal).Take(8).ToArray();
    }
}
