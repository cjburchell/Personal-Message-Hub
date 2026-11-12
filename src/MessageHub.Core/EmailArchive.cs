namespace MessageHub.Core;

public sealed record EmailParticipant(string Role, string Address, string Name);
public sealed record EmailAttachment(int Part, string? FileName, string MediaType, string? ContentId,
    string Disposition, long? DecodedBytes, string? Sha256);
public sealed record EmailForward(string Kind, string? Fingerprint, string? BodyHash, string? Subject);
public sealed record EmailDocument(UnifiedMessage Message, string Fingerprint, string BodyHash, string SubjectKey,
    string? RfcMessageId, IReadOnlyList<string> References, string? OriginalDate, string DateQuality,
    IReadOnlyList<EmailParticipant> Participants, IReadOnlyList<EmailAttachment> Attachments,
    IReadOnlyList<EmailForward> Forwards, IReadOnlyList<string> Warnings);
public sealed record LocalEmailRecord(long Offset, long NextOffset, byte[] Original, EmailDocument? Document, string? Issue);
public sealed record EmailInput(string RelativePath, string Folder, string Format, long Bytes);
public sealed record EmailCheckpoint(long SnapshotBytes, string SnapshotHash, long Offset, bool Complete, int Issues = 0);
public sealed record EmailImportStatus(string Source, DateTimeOffset ImportedAt, int Files, int Processed, int Issues, bool Complete);
public sealed record EmailSourceReference(string Id, string Folder, string Format, long Offset, long Bytes, string Sha256);
public sealed record EmailRelation(string MessageId, string Kind);
public sealed record EmailMetadata(string? RfcMessageId, string? OriginalDate, string DateQuality,
    IReadOnlyList<EmailParticipant> Participants, IReadOnlyList<EmailAttachment> Attachments,
    IReadOnlyList<EmailSourceReference> Sources, IReadOnlyList<EmailRelation> Relations, IReadOnlyList<string> Warnings, bool Truncated);
public sealed record EmailSearchHit(string Id, string Source, string ThreadId, string Sender, string? Subject,
    string Excerpt, DateTimeOffset SentAt, bool Truncated);
public sealed record EmailPage(IReadOnlyList<EmailSearchHit> Messages, int? NextOffset, IReadOnlyList<string> Warnings);
public interface ILocalEmailArchive
{
    string Source { get; }
    string RootId { get; }
    string RootPath { get; }
    IReadOnlyList<EmailInput> Discover(CancellationToken ct = default);
    ILocalEmailFile Open(EmailInput input);
}
public interface ILocalEmailFile : IAsyncDisposable
{
    long Length { get; }
    Task<string> HashPrefixAsync(long length, CancellationToken ct = default);
    Task VerifyAsync(string hash, CancellationToken ct = default);
    IAsyncEnumerable<LocalEmailRecord> ReadAsync(long offset, CancellationToken ct = default);
}
