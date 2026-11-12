using MessageHub.Core;

namespace MessageHub.Storage;

public sealed class LocalEmailImportService(SqliteMessageStore store)
{
    public async Task<EmailImportStatus> ImportAsync(ILocalEmailArchive archive, bool dryRun = false, CancellationToken ct = default)
    {
        IReadOnlyList<EmailInput> files = [];
        if (!dryRun) await store.InitializeAsync(ct);
        using var gate = dryRun ? null : store.AcquireSyncLock();
        var processed = 0; var issues = 0;
        EmailImportStatus Status(bool complete) => new(archive.Source, DateTimeOffset.UtcNow, files.Count, processed, issues, complete);
        if (!dryRun) await store.SetEmailImportStatusAsync(Status(false), ct);
        try
        {
            files = archive.Discover(ct);
            if (files.Count == 0) throw new InvalidDataException("No supported email files selected.");
            foreach (var input in files)
            {
                ct.ThrowIfCancellationRequested(); long offset = 0; var fileIssues = 0;
                try
                {
                    await using var file = archive.Open(input);
                    var snapshot = await file.HashPrefixAsync(file.Length, ct);
                    var checkpoint = dryRun ? null : await store.GetEmailCheckpointAsync(archive.RootId, input.RelativePath, ct);
                    if (checkpoint is not null && checkpoint.SnapshotBytes <= file.Length
                        && (checkpoint.SnapshotBytes == file.Length || input.Format == "mbox")
                        && (checkpoint.SnapshotBytes == file.Length ? snapshot : await file.HashPrefixAsync(checkpoint.SnapshotBytes, ct)) == checkpoint.SnapshotHash)
                    {
                        offset = checkpoint.Offset; fileIssues = checkpoint.Issues;
                        if (checkpoint.Complete && offset == file.Length)
                        {
                            await file.VerifyAsync(snapshot, ct);
                            if (!dryRun) await store.SetEmailFileIssueAsync(archive.RootId, input.RelativePath, offset, false, ct);
                            issues += fileIssues;
                            continue;
                        }
                    }
                    var batch = new List<LocalEmailRecord>(); long bytes = 0;
                    async Task Commit(bool complete)
                    {
                        if (!dryRun) await store.SaveEmailBatchAsync(archive.Source, archive.RootId, archive.RootPath, input,
                            new(file.Length, snapshot, offset, complete, fileIssues), batch, ct);
                        batch.Clear(); bytes = 0;
                    }
                    await foreach (var record in file.ReadAsync(offset, ct))
                    {
                        if (batch.Count > 0 && bytes + record.Original.Length > 8 * 1024 * 1024) await Commit(false);
                        batch.Add(record); bytes += record.Original.Length; offset = record.NextOffset;
                        if (record.Issue is not null) fileIssues++; else processed++;
                        if (batch.Count >= 25) await Commit(false);
                    }
                    await file.VerifyAsync(snapshot, ct);
                    await Commit(true);
                    if (!dryRun) await store.SetEmailFileIssueAsync(archive.RootId, input.RelativePath, offset, false, ct);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or FormatException)
                {
                    // No raw parser exception, filename or content is returned to diagnostics.
                    fileIssues++;
                    if (!dryRun) await store.SetEmailFileIssueAsync(archive.RootId, input.RelativePath, offset, true, ct);
                }
                issues += fileIssues;
            }
            var status = Status(issues == 0);
            if (!dryRun) await store.SetEmailImportStatusAsync(status, ct);
            return status;
        }
        catch
        {
            if (!dryRun)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await store.SetEmailImportStatusAsync(Status(false), timeout.Token); }
                catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException or OperationCanceledException) { }
            }
            throw;
        }
    }
}
