namespace MessageHub.Core;

// Source connectors never mutate the user's inbox; cursor semantics are source-specific.
public interface IMessageConnector
{
    string Source { get; }
    IAsyncEnumerable<UnifiedMessage> FetchAsync(
        DateTimeOffset since, CancellationToken cancellationToken = default);
}
