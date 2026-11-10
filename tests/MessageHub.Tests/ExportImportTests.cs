using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MessageHub.Connectors.Exports;
using MessageHub.Core;
using MessageHub.Query;
using MessageHub.Reporting;
using MessageHub.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MessageHub.Tests;

public sealed class ExportImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pmh-export-test-" + Guid.NewGuid().ToString("N"));
    private SqliteMessageStore Store => new(Path.Combine(_root, "test.db"));
    private static readonly DateTimeOffset Sent = DateTimeOffset.Parse("2026-11-08T13:00:00Z");
    private static ExportReaderOptions LinkedInOptions => new("synthetic-linkedin", ["https://www.linkedin.com/in/owner-example"]);
    private static ExportReaderOptions MessengerOptions => new("synthetic-messenger", ["Owner Example"]);
    private const string Header = "CONVERSATION ID,CONVERSATION TITLE,FROM,SENDER PROFILE URL,TO,RECIPIENT PROFILE URLS,DATE,SUBJECT,CONTENT,FOLDER";
    public ExportImportTests() => Directory.CreateDirectory(_root);
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(_root, true); }
    private string File(string name, string text)
    { var path = Path.Combine(_root, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); System.IO.File.WriteAllText(path, text, new UTF8Encoding(false)); return path; }
    private static string Csv(string date = "2026-11-08 13:00:00 UTC", string content = "Please confirm your availability.") => Header+"\nthread-one,Planning,Friend Example,https://www.linkedin.com/in/friend-example,Owner Example,https://www.linkedin.com/in/owner-example,"+date+",Meeting,"+content+",INBOX\n";
    private static object Message(string sender = "Friend Example", string? content = "Payment is due Friday.", long? timestamp = null, string? id = null) => new
    { sender_name=sender,content,timestamp_ms=timestamp??Sent.ToUnixTimeMilliseconds(),type="Generic",message_id=id };
    private static string Json(IEnumerable<object> messages, string thread = "inbox/friend_example_12345", string[]? participants = null) => JsonSerializer.Serialize(new
    { participants=(participants??["Owner Example","Friend Example"]).Select(x=>new{name=x}),messages,title="Synthetic conversation",thread_path=thread });
    private static async Task<List<UnifiedMessage>> Read(IMessageExportReader reader)
    { var messages=new List<UnifiedMessage>();await foreach(var message in reader.ReadAsync())messages.Add(message);return messages; }
    private string Zip(string name, params (string Name,string Content)[] files)
    {
        var path=Path.Combine(_root,name);using var archive=ZipFile.Open(path,ZipArchiveMode.Create);
        foreach(var file in files){var entry=archive.CreateEntry(file.Name);using var stream=new StreamWriter(entry.Open(),new UTF8Encoding(false));stream.Write(file.Content);}return path;
    }

    [Fact]
    public async Task LinkedInQuotedCsvNormalizesDirectionsDatesAndMultilineText()
    {
        var csv=Header+"\r\nthread-one,Planning,Friend Example,https://www.linkedin.com/in/friend-example,Owner Example,https://www.linkedin.com/in/owner-example,2026-11-08 13:00:00 UTC,Meeting,\"Please confirm, \"\"Tuesday\"\".\r\nSecond line.\",INBOX\r\n"
            +"thread-one,Planning,Owner Example,https://www.linkedin.com/in/owner-example/,Friend Example,https://www.linkedin.com/in/friend-example,2026-11-08T09:00:00-05:00,,Confirmed.,SENT\r\n";
        var reader=new LinkedInExportReader(File("messages.csv",csv),LinkedInOptions);
        var messages=await Read(reader);Assert.Equal(2,messages.Count);Assert.False(messages[0].IsOutgoing);Assert.True(messages[1].IsOutgoing);
        Assert.Equal("Please confirm, \"Tuesday\".\nSecond line.",messages[0].Body);Assert.Equal(Sent,messages[0].SentAt);
        Assert.Equal(Sent.AddHours(1),messages[1].SentAt);Assert.Equal("Planning",messages[1].Subject);
        Assert.True(reader.Summary.Completed);Assert.Equal(2,reader.Summary.ContentDerivedIds);
    }

    [Fact]
    public async Task ReorderedCsvAndRepeatedIdenticalRowsKeepStableIdsAndMultiplicity()
    {
        var row=Csv().Split('\n')[1];var other=row.Replace("Please confirm your availability.","A different request.");
        var first=new LinkedInExportReader(File("first.csv",Header+"\n"+row+"\n"+other+"\n"+row+"\n"),LinkedInOptions);
        var second=new LinkedInExportReader(File("second.csv",Header+"\n"+row+"\n"+row+"\n"+other+"\n"),LinkedInOptions);
        Assert.Equal((await Read(first)).Select(x=>x.SourceMessageId).Order(),(await Read(second)).Select(x=>x.SourceMessageId).Order());
        await Store.InitializeAsync();var path=Path.Combine(_root,"first.csv");
        await Store.ImportExportAsync(new LinkedInExportReader(path,LinkedInOptions));await Store.ImportExportAsync(new LinkedInExportReader(path,LinkedInOptions));
        Assert.Equal(3,(await Store.BetweenAsync(Sent.AddDays(-1),Sent.AddDays(1))).Count);
        var status=Assert.Single(await Store.GetStatusesAsync());Assert.Null(status.LastSuccessfulSync);Assert.NotNull(status.LastExportImport);
    }

    [Fact]
    public async Task MessengerPagedZipPreservesUnicodeGroupsAndStableThreadIdsAcrossFolders()
    {
        var messages=new[]{Message(content:"Caf\u00e9 \ud83d\ude0a"),Message("Owner Example","Confirmed.",Sent.AddMinutes(1).ToUnixTimeMilliseconds())};
        var zip=Zip("messenger.zip",("your_facebook_activity/messages/inbox/friend_12345/message_1.json",Json([messages[0]])),
            ("your_facebook_activity/messages/inbox/friend_12345/message_2.json",Json([messages[1]])),("photos/unused.txt","Never opened as media."));
        var reader=new MessengerExportReader(zip,MessengerOptions);var imported=await Read(reader);
        Assert.Equal(2,imported.Count);Assert.Equal("Caf\u00e9 \ud83d\ude0a",imported[0].Body);Assert.True(imported[1].IsOutgoing);
        Assert.Equal("12345",imported[0].ConversationId);Assert.Single(imported[0].Recipients);Assert.Equal(2,reader.Summary.FileCount);
        var renamed=await Read(new MessengerExportReader(File("renamed.json",Json(messages,"archived_threads/renamed_12345")),MessengerOptions));
        Assert.Equal(imported.Select(x=>x.SourceMessageId).Order(),renamed.Select(x=>x.SourceMessageId).Order());
    }

    [Fact]
    public async Task MessengerSkipsUnsentAndSystemRecordsAndLabelsMediaWithoutOpeningPaths()
    {
        var json=Json([Message(),new{is_unsent=true,content="synthetic-private-unsent-marker"},new{type="Subscribe",content="synthetic-private-system-marker"},
            new{sender_name="Friend Example",timestamp_ms=Sent.ToUnixTimeMilliseconds(),type="Generic",photos=new[]{new{uri="../../outside-secret.jpg"}}},
            Message(content:null)]);
        await Store.InitializeAsync();var result=await Store.ImportExportAsync(new MessengerExportReader(File("media.json",json),MessengerOptions));
        Assert.Equal(2,result.Summary.MessageCount);Assert.Equal(1,result.Summary.SkippedUnsent);Assert.Equal(1,result.Summary.SkippedSystem);Assert.Equal(1,result.Summary.SkippedEmpty);
        Assert.Equal(1,result.Summary.AttachmentOnlyMessages);var messages=await Store.BetweenAsync(Sent.AddDays(-1),Sent.AddDays(1));
        Assert.Contains(messages,x=>x.Body.Contains("Attachment-only message"));Assert.DoesNotContain(messages,x=>x.Body.Contains("outside-secret")||x.Body.Contains("private-unsent")||x.Body.Contains("private-system"));
        using var query=new ArchiveQueryService(Store);var answer=await query.SearchAsync("payment",365);
        Assert.Contains(answer.Warnings,x=>x.Contains("export snapshot imported"));Assert.Contains(answer.Warnings,x=>x.Contains("placeholders"));
        Assert.DoesNotContain(answer.Warnings,x=>x.Contains("not implemented"));
        var report=await new ReportService(Store).BuildAsync(Sent.AddDays(-7),Sent.AddDays(1));Assert.Contains("Import time is not provider freshness",report.Markdown);
    }

    [Fact]
    public async Task NativeMessageIdsUpsertEditsAndAccountsRemainIsolated()
    {
        await Store.InitializeAsync();var path=File("native.json",Json([Message(id:"native-one")]));
        await Store.ImportExportAsync(new MessengerExportReader(path,MessengerOptions));File("native.json",Json([Message(content:"Revised deadline.",id:"native-one")]));
        await Store.ImportExportAsync(new MessengerExportReader(path,MessengerOptions));
        Assert.Single(await Store.SearchAsync("Revised"));Assert.Empty(await Store.SearchAsync("Payment"));
        await Store.ImportExportAsync(new MessengerExportReader(path,MessengerOptions with{Account="another-owner-archive"}));
        Assert.Equal(2,(await Store.BetweenAsync(Sent.AddDays(-1),Sent.AddDays(1))).Count);
    }

    [Fact]
    public async Task DryRunDoesNotOpenOrCreateTheDatabase()
    {
        var result=await Store.ImportExportAsync(new LinkedInExportReader(File("messages.csv",Csv()),LinkedInOptions),dryRun:true);
        Assert.True(result.DryRun);Assert.Equal(1,result.Summary.MessageCount);Assert.False(System.IO.File.Exists(Path.Combine(_root,"test.db")));
    }

    [Fact]
    public async Task MalformedLaterPageAndWrongOwnerRollBackAllMessagesAndProvenance()
    {
        await Store.InitializeAsync();var zip=Zip("bad.zip",("thread/message_1.json",Json([Message()])),("thread/message_2.json","{\"content\":\"synthetic-private-error-marker\"}"));
        await Assert.ThrowsAsync<InvalidDataException>(()=>Store.ImportExportAsync(new MessengerExportReader(zip,MessengerOptions)));
        Assert.Empty(await Store.SearchAsync("payment"));Assert.Empty(await Store.GetLatestExportImportsAsync());
        await Assert.ThrowsAsync<InvalidDataException>(()=>Store.ImportExportAsync(new LinkedInExportReader(File("owner.csv",Csv()),LinkedInOptions with{Self=["Wrong Owner"]})));
        Assert.Empty(await Store.GetStatusesAsync());
    }

    [Fact]
    public async Task OffsetFreeDatesRequireTimezoneAndDstAmbiguitiesAreRejected()
    {
        var path=File("date.csv",Csv("2026-11-08 08:00:00"));
        await Assert.ThrowsAsync<InvalidDataException>(()=>Read(new LinkedInExportReader(path,LinkedInOptions)));
        var zone=TimeZoneInfo.FindSystemTimeZoneById("America/Toronto");
        Assert.Equal(Sent,Assert.Single(await Read(new LinkedInExportReader(path,LinkedInOptions with{TimeZone=zone}))).SentAt);
        File("date.csv",Csv("2026-11-01 01:30:00"));await Assert.ThrowsAsync<InvalidDataException>(()=>Read(new LinkedInExportReader(path,LinkedInOptions with{TimeZone=zone})));
    }

    [Fact]
    public async Task EncodingRepairIsExplicitAndDoesNotCorruptOrdinaryUnicode()
    {
        var text=Encoding.Latin1.GetString(Encoding.UTF8.GetBytes("Caf\u00e9 \ud83d\ude0a"));var path=File("encoded.json",Json([Message(content:text)]));
        Assert.Equal(text,Assert.Single(await Read(new MessengerExportReader(path,MessengerOptions))).Body);
        Assert.Equal("Caf\u00e9 \ud83d\ude0a",Assert.Single(await Read(new MessengerExportReader(path,MessengerOptions with{RepairEncoding=true}))).Body);
        Assert.Equal("Already correct \u00e9",ExportText.Repair("Already correct \u00e9"));
    }

    [Theory]
    [InlineData("../thread/message_1.json")]
    [InlineData("/thread/message_1.json")]
    [InlineData("C:/thread/message_1.json")]
    public async Task UnsafeZipPathsAreRejectedWithoutExtraction(string entry)
    {
        await Assert.ThrowsAsync<InvalidDataException>(()=>Read(new MessengerExportReader(Zip("unsafe.zip",(entry,Json([Message()]))),MessengerOptions)));
        Assert.False(Directory.Exists(Path.Combine(_root,"thread")));
    }

    [Fact]
    public async Task AmbiguousCsvSelectionAndDuplicateZipNamesAreRejected()
    {
        var linked=Zip("multiple.zip",("a/messages.csv",Csv()),("b/messages.csv",Csv()));
        await Assert.ThrowsAsync<InvalidDataException>(()=>Read(new LinkedInExportReader(linked,LinkedInOptions)));
        var duplicate=Zip("duplicate.zip",("thread/message_1.json",Json([Message()])),("thread/message_1.json",Json([Message()])));
        await Assert.ThrowsAsync<InvalidDataException>(()=>Read(new MessengerExportReader(duplicate,MessengerOptions)));
    }

    [Fact]
    public async Task DirectoryAndBomJsonReadTheSameMessagesAndNoMediaFiles()
    {
        var json=Json([Message(),Message("Owner Example","Confirmed.",Sent.AddMinutes(1).ToUnixTimeMilliseconds())]);
        var file=File("directory/messages/inbox/thread/message_1.json",json);
        System.IO.File.WriteAllText(file,json,new UTF8Encoding(encoderShouldEmitUTF8Identifier:true));
        File("directory/messages/inbox/thread/photos/ignored.json","not a message JSON file");
        var direct=await Read(new MessengerExportReader(file,MessengerOptions));
        var directory=await Read(new MessengerExportReader(Path.Combine(_root,"directory"),MessengerOptions));
        Assert.Equal(JsonSerializer.Serialize(direct),JsonSerializer.Serialize(directory));
    }

    [Fact]
    public async Task UnknownTypesDuplicatePropertiesAndAmbiguousNamesAreRejected()
    {
        var unknown=Json([new{sender_name="Friend Example",timestamp_ms=Sent.ToUnixTimeMilliseconds(),content="private-marker",type="NewUnsupportedType"}]);
        await Assert.ThrowsAsync<InvalidDataException>(()=>Read(new MessengerExportReader(File("unknown.json",unknown),MessengerOptions)));
        var duplicate=Json([Message()]).Replace("\"type\":\"Generic\"","\"type\":\"Generic\",\"type\":\"Share\"");
        await Assert.ThrowsAsync<InvalidDataException>(()=>Read(new MessengerExportReader(File("duplicate.json",duplicate),MessengerOptions)));
        await Assert.ThrowsAsync<InvalidDataException>(()=>Read(new MessengerExportReader(File("names.json",Json([Message()],participants:["Owner Example","owner example"])),MessengerOptions)));
    }

    [Fact]
    public async Task ArchiveLinksAndOversizedMetadataAreRejectedBeforeReadingFiles()
    {
        var link=Path.Combine(_root,"link.zip");
        using(var archive=ZipFile.Open(link,ZipArchiveMode.Create))
        { var entry=archive.CreateEntry("thread/message_1.json");entry.ExternalAttributes=unchecked((int)(0xA1FFu<<16));using var writer=new StreamWriter(entry.Open());writer.Write("/outside/path"); }
        await Assert.ThrowsAsync<InvalidDataException>(()=>Read(new MessengerExportReader(link,MessengerOptions)));
        var large=Path.Combine(_root,"large.zip");
        using(var archive=ZipFile.Open(large,ZipArchiveMode.Create))
        { using var stream=archive.CreateEntry("thread/message_1.json").Open();var buffer=new byte[8192];for(long count=0;count<=32L*1024*1024;count+=buffer.Length)stream.Write(buffer); }
        var reader=new MessengerExportReader(large,MessengerOptions);
        await Assert.ThrowsAsync<InvalidDataException>(()=>Read(reader));Assert.Equal(0,reader.Summary.FileCount);
    }

    [Fact]
    public async Task ProvenanceWriteFailureRollsBackFtsAndMessageWrites()
    {
        await Store.InitializeAsync();
        using(var connection=new SqliteConnection($"Data Source={Path.Combine(_root,"test.db")}"))
        {await connection.OpenAsync();using var command=connection.CreateCommand();command.CommandText="CREATE TRIGGER reject_export BEFORE INSERT ON export_imports BEGIN SELECT RAISE(ABORT,'synthetic failure'); END;";await command.ExecuteNonQueryAsync();}
        await Assert.ThrowsAsync<SqliteException>(()=>Store.ImportExportAsync(new LinkedInExportReader(File("messages.csv",Csv()),LinkedInOptions)));
        Assert.Empty(await Store.SearchAsync("availability"));Assert.Empty(await Store.GetLatestExportImportsAsync());
    }

    private sealed class CancellingReader(IMessageExportReader inner,CancellationTokenSource cancel) : IMessageExportReader
    {
        public string Source=>inner.Source;public string Provider=>inner.Provider;public ExportReadSummary Summary=>inner.Summary;
        public async IAsyncEnumerable<UnifiedMessage> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken=default)
        {await foreach(var message in inner.ReadAsync(cancellationToken)){yield return message;cancel.Cancel();cancellationToken.ThrowIfCancellationRequested();}}
    }
    [Fact]
    public async Task CancellationAfterAnUpsertLeavesNoMessagesOrProvenance()
    {
        await Store.InitializeAsync();using var cancel=new CancellationTokenSource();
        var reader=new CancellingReader(new MessengerExportReader(File("cancel.json",Json([Message(),Message("Owner Example","Reply.")])),MessengerOptions),cancel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Store.ImportExportAsync(reader,ct:cancel.Token));
        Assert.Empty(await Store.SearchAsync("payment"));Assert.Empty(await Store.GetStatusesAsync());
    }
}
