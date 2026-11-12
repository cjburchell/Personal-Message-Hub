using System.Security.Cryptography;
using System.Text;
using System.Runtime.CompilerServices;
using MessageHub.Connectors.Thunderbird;
using MessageHub.Core;
using MessageHub.Query;
using MessageHub.Storage;
using MimeKit;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MessageHub.Tests;

public sealed class ThunderbirdTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pmh-thunderbird-" + Guid.NewGuid().ToString("N"));
    private string Input => Path.Combine(_root, "input");
    private string Database => Path.Combine(_root, "db", "test.db");
    private SqliteMessageStore Store => new(Database);
    public ThunderbirdTests() => Directory.CreateDirectory(Input);
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private ThunderbirdArchive Archive(string account = "synthetic", string[]? folders = null) => new(Input, account, ["owner@example.test"], folders);
    internal static byte[] Message(string id = "m1", string body = "Synthetic archive evidence", string subject = "Archive topic", string? reply = null)
    {
        using var mime = new MimeMessage(); mime.From.Add(new MailboxAddress("Synthetic Sender", "sender@example.test"));
        mime.To.Add(new MailboxAddress("Owner", "owner@example.test"));
        mime.Cc.Add(new MailboxAddress("Colleague", "colleague@example.test"));
        mime.Subject = subject; mime.MessageId = id + "@example.test";
        mime.Date = new DateTimeOffset(2001, 4, 5, 10, 30, 0, TimeSpan.FromHours(-4));
        if (reply is not null) { mime.InReplyTo = reply + "@example.test"; mime.References.Add(reply + "@example.test"); }
        mime.Body = new TextPart("plain") { Text = body }; using var memory = new MemoryStream(); mime.WriteTo(memory); return memory.ToArray();
    }
    private string Write(string path, byte[] bytes)
    {
        var full = Path.Combine(Input, path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllBytes(full, bytes); return full;
    }
    private static byte[] Mbox(params byte[][] emails)
    {
        using var memory = new MemoryStream();
        foreach (var email in emails) { memory.Write("From - Thu Apr 05 14:30:00 2001\r\n"u8); memory.Write(email); memory.Write("\r\n"u8); }
        return memory.ToArray();
    }
    private async Task<long> Scalar(string sql)
    {
        await using var db = new SqliteConnection("Data Source=" + Database); await db.OpenAsync(); using var cmd = db.CreateCommand(); cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ExtensionlessMboxAndNestedMaildirImportWithoutChangingSources()
    {
        var mbox = Mbox(Message("a"), Message("b", reply: "a")); var eml = Message("c");
        var path = Write("Inbox", mbox); var token = Write("Archive.sbd/Old/cur/token-without-extension", eml);
        Write("Inbox.msf", "not an email"u8.ToArray()); Write("prefs.js", "synthetic-not-an-email"u8.ToArray());
        var archive = Archive(); var inventory = archive.Discover(); Assert.Equal(2, inventory.Count);
        Assert.Contains(inventory, x => x.Format == "eml" && x.Folder == "Archive/Old");
        var status = await new LocalEmailImportService(Store).ImportAsync(archive);
        Assert.True(status.Complete); Assert.Equal(3, status.Processed); Assert.Equal(0, status.Issues);
        Assert.Equal(mbox, await File.ReadAllBytesAsync(path)); Assert.Equal(eml, await File.ReadAllBytesAsync(token));
        Assert.Equal(3, await Scalar("SELECT COUNT(*) FROM messages"));
        var page = await new EmailArchiveQueries(Store).SearchAsync("archive"); Assert.Equal(3, page.Messages.Count);
        var references = new List<EmailSourceReference>();
        foreach (var hit in page.Messages) references.AddRange((await Store.GetEmailMetadataAsync(hit.Id))!.Sources.Where(x => x.Folder == "Inbox"));
        using var preserved = new MemoryStream();
        foreach (var reference in references.OrderBy(x => x.Offset)) preserved.Write((await Store.GetEmailOriginalAsync(reference.Id))!);
        Assert.Equal(mbox, preserved.ToArray());
        var source = Assert.Single(await Store.GetStatusesAsync()); Assert.Null(source.LastSuccessfulSync); Assert.NotNull(source.LastLocalImport);
    }

    [Fact]
    public async Task ReplaysAppendsAndCompactionPreserveLogicalIdentityAndOriginals()
    {
        var first = Message("first"); var second = Message("second", "Newly appended message");
        var path = Write("Inbox", Mbox(first)); var service = new LocalEmailImportService(Store);
        Assert.True((await service.ImportAsync(Archive())).Complete);
        Assert.Equal(0, (await service.ImportAsync(Archive())).Processed);
        await using (var append = new FileStream(path, FileMode.Append)) await append.WriteAsync(Mbox(second));
        var appended = await service.ImportAsync(Archive()); Assert.True(appended.Complete); Assert.Equal(1, appended.Processed);
        File.WriteAllBytes(path, Mbox(second, first));
        Assert.True((await service.ImportAsync(Archive())).Complete);
        Assert.Equal(2, await Scalar("SELECT COUNT(*) FROM messages"));
        Assert.True(await Scalar("SELECT COUNT(*) FROM email_occurrences") >= 3);
        Assert.Single(await Store.GetStatusesAsync(), x => x.LastLocalImport is not null);
    }

    [Fact]
    public async Task FolderCopiesDeduplicateButReusedMessageIdsAndAccountsDoNotOverwrite()
    {
        var bytes = Message(); Write("Inbox/cur/one", bytes); Write("Sent/cur/copy", bytes);
        Write("Conflict.eml", Message(body: "Different private synthetic content"));
        var service = new LocalEmailImportService(Store); Assert.True((await service.ImportAsync(Archive())).Complete);
        Assert.Equal(2, await Scalar("SELECT COUNT(*) FROM messages")); Assert.Equal(3, await Scalar("SELECT COUNT(*) FROM email_occurrences"));
        var page = await new EmailArchiveQueries(Store).SearchAsync("synthetic archive"); var item = Assert.Single(page.Messages);
        var meta = await Store.GetEmailMetadataAsync(item.Id); Assert.Equal(2, meta!.Sources.Count);
        Assert.Contains("reused_message_id_conflicting_content_thread_may_be_ambiguous", meta.Warnings);
        Assert.Equal(bytes, await Store.GetEmailOriginalAsync(meta.Sources[0].Id));
        Assert.True((await service.ImportAsync(Archive("another-account"))).Complete);
        Assert.Equal(4, await Scalar("SELECT COUNT(*) FROM messages"));
    }

    [Fact]
    public async Task DryRunsAndDiscoveryNeverCreateDatabaseOrReadCredentials()
    {
        Write("Mail/Local Folders/Test", Mbox(Message()));
        Write("logins.json", "synthetic-private-invalid-json"u8.ToArray());
        var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order().ToArray();
        var result = await new LocalEmailImportService(Store).ImportAsync(Archive(), true);
        Assert.True(result.Complete); Assert.Equal(1, result.Processed); Assert.False(Directory.Exists(Path.GetDirectoryName(Database)));
        Assert.Equal(before, Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order());
    }

    [Fact]
    public async Task MalformedEmlIsPreservedWithIssueAndDoesNotPretendCompleteCoverage()
    {
        Write("broken.eml", "synthetic-private-marker without email headers"u8.ToArray()); Write("valid.eml", Message());
        var status = await new LocalEmailImportService(Store).ImportAsync(Archive());
        Assert.False(status.Complete); Assert.Equal(1, status.Issues); Assert.Equal(1, status.Processed);
        Assert.Equal(1, await Scalar("SELECT COUNT(*) FROM email_occurrences WHERE issue IS NOT NULL"));
        var replay = await new LocalEmailImportService(Store).ImportAsync(Archive()); Assert.False(replay.Complete); Assert.Equal(1, replay.Issues);
    }

    [Fact]
    public async Task OriginalDateOffsetParticipantsAttachmentsAndEncodingAreRetained()
    {
        using var mime = MimeMessage.Load(new MemoryStream(Message()));
        var builder = new BodyBuilder { TextBody = "Café résumé — synthetic 😀", HtmlBody = "<script>bad()</script><p>Café</p>" };
        builder.Attachments.Add("../../untrusted.bin", [1, 2, 3, 4], ContentType.Parse("application/octet-stream")); mime.Body = builder.ToMessageBody();
        using var memory = new MemoryStream(); mime.WriteTo(memory); Write("test.eml", memory.ToArray());
        Assert.True((await new LocalEmailImportService(Store).ImportAsync(Archive())).Complete);
        var hit = Assert.Single((await new EmailArchiveQueries(Store).SearchAsync("Café")).Messages);
        var message = await new ArchiveQueryService(Store).GetMessageAsync(hit.Id);
        Assert.Contains("😀", message!.Body); Assert.Equal(TimeSpan.Zero, message.SentAt.Offset); Assert.Contains("-0400", message.Email!.OriginalDate);
        Assert.Contains(message.Email.Participants, x => x.Role == "cc" && x.Address == "colleague@example.test");
        var attachment = Assert.Single(message.Email.Attachments); Assert.Equal(4, attachment.DecodedBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3, 4 })).ToLowerInvariant(), attachment.Sha256);
        Assert.Single(Directory.GetFiles(Input, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ThreadsHandleOutOfOrderParentsAndBridgesWithoutSubjectMerging()
    {
        Write("a.eml", Message("child", reply: "parent")); Write("b.eml", Message("other", subject: "Archive topic"));
        var service = new LocalEmailImportService(Store); await service.ImportAsync(Archive());
        var first = await new EmailArchiveQueries(Store).SearchAsync(); var child = first.Messages[0];
        Write("c.eml", Message("parent")); await service.ImportAsync(Archive());
        var all = (await new EmailArchiveQueries(Store).SearchAsync()).Messages;
        Assert.Equal(2, all.Select(x => x.ThreadId).Distinct().Count());
        var grouped = all.GroupBy(x => x.ThreadId).Single(x => x.Count() == 2);
        Assert.Equal(2, (await new EmailArchiveQueries(Store).ThreadAsync(grouped.First().Id)).Messages.Count);
        using var bridge = MimeMessage.Load(new MemoryStream(Message("bridge", reply: "child"))); bridge.References.Add("other@example.test");
        using var output = new MemoryStream(); bridge.WriteTo(output); Write("d.eml", output.ToArray()); await service.ImportAsync(Archive());
        Assert.Equal(4, (await new EmailArchiveQueries(Store).ThreadAsync(child.Id)).Messages.Count);
    }

    [Fact]
    public async Task ForwardCopiesRemainSeparateAndEmbeddedOriginalsLinkInEitherImportOrder()
    {
        var original = Message("original", body: "Original forwarded text");
        using var forward = MimeMessage.Load(new MemoryStream(Message("forward", "My new commentary", "Fwd: Archive topic")));
        forward.Body = new Multipart("mixed") { new TextPart("plain") { Text = "My new commentary" }, new MessagePart { Message = MimeMessage.Load(new MemoryStream(original)) } };
        using var output = new MemoryStream(); forward.WriteTo(output); Write("forward.eml", output.ToArray());
        await new LocalEmailImportService(Store).ImportAsync(Archive()); Write("original.eml", original); await new LocalEmailImportService(Store).ImportAsync(Archive());
        Assert.Equal(2, await Scalar("SELECT COUNT(*) FROM messages"));
        var hit = Assert.Single((await new EmailArchiveQueries(Store).SearchAsync("commentary")).Messages);
        Assert.Contains((await Store.GetEmailMetadataAsync(hit.Id))!.Relations, x => x.Kind == "embedded_forward");
        Write("inline.eml", Message("inline", "More commentary\n---------- Forwarded message ---------\nFrom: sender@example.test\nSubject: Archive topic\n\nOriginal forwarded text", "Fwd: Archive topic"));
        await new LocalEmailImportService(Store).ImportAsync(Archive());
        var inline = Assert.Single((await new EmailArchiveQueries(Store).SearchAsync("More commentary")).Messages);
        Assert.Contains((await Store.GetEmailMetadataAsync(inline.Id))!.Relations, x => x.Kind == "possible_inline_forward");
        Assert.Equal(3, await Scalar("SELECT COUNT(*) FROM messages"));
    }

    [Fact]
    public async Task HistoricalSearchContactFiltersLiteralFtsAndPaginationWorkWithoutAi()
    {
        Write("one.eml", Message("one")); Write("two.eml", Message("two")); Write("three.eml", Message("three"));
        await new LocalEmailImportService(Store).ImportAsync(Archive()); var queries = new EmailArchiveQueries(Store);
        var page = await queries.ContactAsync("COLLEAGUE@example.test", limit: 1); Assert.Single(page.Messages); Assert.Equal(1, page.NextOffset);
        Assert.Single((await queries.ContactAsync("colleague@example.test", offset: page.NextOffset!.Value, limit: 1)).Messages);
        Assert.Empty((await queries.SearchAsync("archive OR nonexistent")).Messages);
        Assert.Empty((await queries.SearchAsync(start: DateTimeOffset.Parse("2020-01-01T00:00:00Z"))).Messages);
        Assert.Equal(3, (await queries.SearchAsync(folder: ".")).Messages.Count);
        await Assert.ThrowsAsync<ArgumentException>(() => queries.SearchAsync(offset: -1));
        await Assert.ThrowsAsync<ArgumentException>(() => queries.ThreadAsync("missing-id", offset: -1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedImportResumesCommittedBatchesWithoutLosingOrDuplicatingMessages(bool cancel)
    {
        Write("Inbox", Mbox(Enumerable.Range(0, 30).Select(i => Message("resume-" + i)).ToArray()));
        using var cancellation = new CancellationTokenSource();
        var interrupted = new InterruptArchive(Archive(), cancellation, cancel);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LocalEmailImportService(Store).ImportAsync(interrupted, ct: cancellation.Token));
        else Assert.False((await new LocalEmailImportService(Store).ImportAsync(interrupted)).Complete);
        Assert.Equal(25, await Scalar("SELECT COUNT(*) FROM messages"));
        var incomplete = Assert.Single(await Store.GetEmailImportStatusesAsync()); Assert.False(incomplete.Complete);
        var resumed = await new LocalEmailImportService(Store).ImportAsync(Archive());
        Assert.True(resumed.Complete); Assert.Equal(5, resumed.Processed); Assert.Equal(30, await Scalar("SELECT COUNT(*) FROM messages"));
    }

    [Fact]
    public async Task CheckpointFailureRollsBackOriginalsMetadataMessagesAndFtsTogether()
    {
        Write("one.eml", Message()); await Store.InitializeAsync();
        await using (var db = new SqliteConnection("Data Source=" + Database))
        {
            await db.OpenAsync(); using var cmd = db.CreateCommand();
            cmd.CommandText = "CREATE TRIGGER reject_checkpoint BEFORE INSERT ON email_checkpoints BEGIN SELECT RAISE(ABORT,'synthetic failure'); END;";
            await cmd.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<SqliteException>(() => new LocalEmailImportService(Store).ImportAsync(Archive()));
        foreach (var table in new[] { "messages", "email_originals", "email_data", "email_participants", "email_occurrences", "email_checkpoints", "email_threads" })
            Assert.Equal(0, await Scalar("SELECT COUNT(*) FROM " + table));
        Assert.Empty(await Store.SearchAsync("archive"));
    }

    [Fact]
    public async Task VersionedMigrationIsRepeatableAndPreservesExistingArchivesAndReports()
    {
        await Store.InitializeAsync();
        await Store.UpsertAsync(new("legacy", "existing", "thread", "sender@example.test", [], "Legacy", "Historical evidence", DateTimeOffset.UnixEpoch, false));
        await Store.SaveReportAsync(new("legacy-report", DateTimeOffset.UtcNow, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(7), "Saved synthetic report"), []);
        // Recreate the pre-extension schema by removing only the extension tables/migration marker.
        await using (var db = new SqliteConnection("Data Source=" + Database))
        {
            await db.OpenAsync(); using var find = db.CreateCommand(); find.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND (name LIKE 'email_%' OR name='pmh_schema_migrations')";
            var tables = new List<string>(); await using (var row = await find.ExecuteReaderAsync()) while (await row.ReadAsync()) tables.Add(row.GetString(0));
            foreach (var table in tables) { using var drop = db.CreateCommand(); drop.CommandText = "DROP TABLE \"" + table + "\""; await drop.ExecuteNonQueryAsync(); }
        }
        await Store.InitializeAsync(); await Store.InitializeAsync();
        Assert.Single(await Store.SearchAsync("Historical")); Assert.NotNull(await Store.GetReportAsync("legacy-report"));
        Assert.Equal(1, await Scalar("SELECT COUNT(*) FROM pmh_schema_migrations WHERE version=1"));
    }

    [Fact]
    public async Task InvalidDatesAreExplicitAndOversizedMessagesAreRejected()
    {
        Write("missing-date.eml", "From: sender@example.test\r\nTo: owner@example.test\r\nSubject: Undated\r\n\r\nSynthetic text\r\n"u8.ToArray());
        await new LocalEmailImportService(Store).ImportAsync(Archive());
        var hit = Assert.Single((await new EmailArchiveQueries(Store).SearchAsync()).Messages);
        Assert.Equal(DateTimeOffset.UnixEpoch, hit.SentAt); Assert.Equal("unknown", (await Store.GetEmailMetadataAsync(hit.Id))!.DateQuality);
        var large = Write("large.eml", []); using (var stream = new FileStream(large, FileMode.Open)) stream.SetLength(ThunderbirdArchive.MaximumMessageBytes + 1L);
        var result = await new LocalEmailImportService(Store).ImportAsync(Archive()); Assert.False(result.Complete); Assert.True(result.Issues > 0);
        // ZIP exports cannot be mistaken for Thunderbird messages.
        Write("not-mail.zip", "From fake archive"u8.ToArray()); Assert.DoesNotContain(Archive().Discover(), x => x.RelativePath.EndsWith(".zip", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => Archive().Open(new("../outside.eml", ".", "eml", 0)));
    }

    [Fact]
    public async Task TimezoneFreeHeaderDoesNotInventAnOffsetOrUseFileTime()
    {
        Write("floating.eml", "From: sender@example.test\r\nDate: Thu, 5 Apr 2001 10:30:00\r\nSubject: Floating date\r\n\r\nSynthetic text\r\n"u8.ToArray());
        await new LocalEmailImportService(Store).ImportAsync(Archive());
        var hit = Assert.Single((await new EmailArchiveQueries(Store).SearchAsync()).Messages);
        var metadata = (await Store.GetEmailMetadataAsync(hit.Id))!;
        Assert.Equal(DateTimeOffset.UnixEpoch, hit.SentAt); Assert.Equal("unknown", metadata.DateQuality);
        Assert.Contains("2001", metadata.OriginalDate); Assert.Contains("missing_or_invalid_date_uses_epoch", metadata.Warnings);
    }

    [Fact]
    public async Task EmptyEmlAndTruncatedMboxStayIncompleteAndCanBeRepaired()
    {
        var empty = Write("empty.eml", []); var truncated = Mbox(Message("partial"));
        var path = Write("Inbox", truncated[..^2].Concat("unfinished"u8.ToArray()).ToArray());
        var service = new LocalEmailImportService(Store); var failed = await service.ImportAsync(Archive());
        Assert.False(failed.Complete); Assert.Equal(2, failed.Issues);
        Assert.False((await service.ImportAsync(Archive())).Complete);
        File.WriteAllBytes(empty, Message("fixed-empty")); File.WriteAllBytes(path, Mbox(Message("partial")));
        Assert.True((await service.ImportAsync(Archive())).Complete); Assert.Equal(2, await Scalar("SELECT COUNT(*) FROM messages"));
    }

    [Fact]
    public async Task TransferEncodingHtmlFallbackAndMutableThunderbirdHeadersDoNotBreakDeduplication()
    {
        var encoded = "From: =?iso-8859-1?Q?Andr=E9?= <sender@example.test>\r\nTo: owner@example.test\r\nDate: Thu, 5 Apr 2001 10:30:00 -0400\r\nMessage-ID: <encoding@example.test>\r\nSubject: =?iso-8859-1?Q?Caf=E9?=\r\nMIME-Version: 1.0\r\nContent-Type: text/html; charset=iso-8859-1\r\nContent-Transfer-Encoding: quoted-printable\r\n\r\n<p>Caf=E9</p><script>synthetic-bad-script</script><img src=3D\"https://example.test/private\">\r\n";
        Write("one.eml", Encoding.ASCII.GetBytes(encoded)); Write("two.eml", Encoding.ASCII.GetBytes("X-Mozilla-Status: 0001\r\n" + encoded));
        var result = await new LocalEmailImportService(Store).ImportAsync(Archive()); Assert.True(result.Complete);
        Assert.Equal(1, await Scalar("SELECT COUNT(*) FROM messages"));
        var hit = Assert.Single((await new EmailArchiveQueries(Store).SearchAsync("café")).Messages);
        var message = (await new ArchiveQueryService(Store).GetMessageAsync(hit.Id))!;
        Assert.DoesNotContain("script", message.Body); Assert.DoesNotContain("https://", message.Body);
        Assert.Contains(message.Email!.Participants, x => x.Name == "André"); Assert.Equal(2, message.Email.Sources.Count);
    }

    [Fact]
    public async Task ChangedSameSizeMailboxInvalidatesCheckpointAndSourceLockFailsClosed()
    {
        var a = Mbox(Message("aa", "Old content")); var b = Mbox(Message("bb", "New content")); Assert.Equal(a.Length, b.Length);
        var path = Write("Inbox", a); var stamp = File.GetLastWriteTimeUtc(path); var service = new LocalEmailImportService(Store);
        await service.ImportAsync(Archive()); File.WriteAllBytes(path, b); File.SetLastWriteTimeUtc(path, stamp);
        Assert.True((await service.ImportAsync(Archive())).Complete); Assert.Equal(2, await Scalar("SELECT COUNT(*) FROM messages"));
        using (Store.AcquireSyncLock()) await Assert.ThrowsAsync<IOException>(() => service.ImportAsync(Archive()));
        using var locked = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        await Assert.ThrowsAsync<IOException>(() => service.ImportAsync(Archive()));
        Assert.False(Assert.Single(await Store.GetEmailImportStatusesAsync()).Complete);
    }

    [Fact]
    public async Task ThreadMergeKeepsReviewedActionEvidenceConsistentAndSavedReportImmutable()
    {
        Write("a.eml", Message("action-a")); Write("b.eml", Message("action-b"));
        var service = new LocalEmailImportService(Store); await service.ImportAsync(Archive());
        var selected = (await new EmailArchiveQueries(Store).SearchAsync()).Messages.OrderByDescending(x => x.ThreadId, StringComparer.Ordinal).First();
        var report = new StoredReport("reviewed-report", DateTimeOffset.UtcNow, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(1), "Immutable synthetic report");
        await Store.SaveReportAsync(report, [new("reviewed-action", selected.Source, selected.ThreadId, selected.Id, "Review", null)]);
        await using (var db = new SqliteConnection("Data Source=" + Database))
        {
            await db.OpenAsync(); using var cmd = db.CreateCommand(); cmd.CommandText = "UPDATE action_items SET status='reviewed'"; await cmd.ExecuteNonQueryAsync();
        }
        using var bridge = MimeMessage.Load(new MemoryStream(Message("action-bridge", reply: "action-a"))); bridge.References.Add("action-b@example.test");
        using var output = new MemoryStream(); bridge.WriteTo(output); Write("bridge.eml", output.ToArray()); await service.ImportAsync(Archive());
        Assert.Equal(1, await Scalar("SELECT COUNT(*) FROM action_items a JOIN messages m ON m.id=a.evidence_message_id WHERE a.conversation_id=m.conversation_id AND a.status='reviewed'"));
        Assert.Equal(report.Markdown, (await Store.GetReportAsync(report.Id))!.Markdown);
    }

    private sealed class InterruptArchive(ILocalEmailArchive inner, CancellationTokenSource cancellation, bool cancel) : ILocalEmailArchive
    {
        public string Source => inner.Source; public string RootId => inner.RootId; public string RootPath => inner.RootPath;
        public IReadOnlyList<EmailInput> Discover(CancellationToken ct = default) => inner.Discover(ct);
        public ILocalEmailFile Open(EmailInput input) => new InterruptFile(inner.Open(input), cancellation, cancel);
    }
    private sealed class InterruptFile(ILocalEmailFile inner, CancellationTokenSource cancellation, bool cancel) : ILocalEmailFile
    {
        public long Length => inner.Length;
        public Task<string> HashPrefixAsync(long length, CancellationToken ct = default) => inner.HashPrefixAsync(length, ct);
        public Task VerifyAsync(string hash, CancellationToken ct = default) => inner.VerifyAsync(hash, ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
        public async IAsyncEnumerable<LocalEmailRecord> ReadAsync(long offset, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var count = 0;
            await foreach (var record in inner.ReadAsync(offset, ct))
            {
                yield return record;
                if (++count != 26) continue;
                if (cancel) { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); }
                throw new IOException("synthetic-private-error");
            }
        }
    }
}
