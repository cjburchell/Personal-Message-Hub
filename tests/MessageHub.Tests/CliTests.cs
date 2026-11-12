using System.Diagnostics;
using Xunit;

namespace MessageHub.Tests;

public sealed class CliTests
{
    [Fact]
    public async Task ThunderbirdCommandsDiscoverDryRunImportReplayAndExportOriginalPrivately()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "PersonalMessageHub.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var cli = Path.Combine(root.FullName, "src", "MessageHub.Cli", "bin", configuration, "net10.0", "MessageHub.Cli.dll");
        var temporary = Path.Combine(Path.GetTempPath(), "pmh-thunderbird-cli-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temporary);
        var database = Path.Combine(temporary, "db", "archive.db"); var input = Path.Combine(temporary, "synthetic.eml");
        var bytes = ThunderbirdTests.Message(body: "synthetic-private-cli-body"); await File.WriteAllBytesAsync(input, bytes);
        try
        {
            async Task<(int Code, string Output)> Run(params string[] arguments)
            {
                var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                info.ArgumentList.Add(cli); foreach (var argument in arguments) info.ArgumentList.Add(argument);
                info.Environment["MESSAGEHUB_DB"] = database;
                using var process = Process.Start(info)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(entireProcessTree: true); throw; }
                return (process.ExitCode, await stdout + await stderr);
            }
            var discovery = await Run("discover-thunderbird", input); Assert.Equal(0, discovery.Code); Assert.False(Directory.Exists(Path.GetDirectoryName(database)));
            var dry = await Run("import-thunderbird", input, "--account", "synthetic", "--self", "owner@example.test", "--dry-run");
            Assert.Equal(0, dry.Code); Assert.False(Directory.Exists(Path.GetDirectoryName(database))); Assert.DoesNotContain("synthetic-private-cli-body", dry.Output);
            var imported = await Run("import-thunderbird", input, "--account", "synthetic", "--self", "owner@example.test");
            Assert.Equal(0, imported.Code); Assert.DoesNotContain("synthetic-private-cli-body", imported.Output);
            Assert.Equal(0, (await Run("import-thunderbird", input, "--account", "synthetic", "--self", "owner@example.test")).Code);
            var store = new MessageHub.Storage.SqliteMessageStore(database);
            var hit = Assert.Single((await new MessageHub.Query.EmailArchiveQueries(store).SearchAsync()).Messages);
            var reference = Assert.Single((await store.GetEmailMetadataAsync(hit.Id))!.Sources).Id;
            var output = Path.Combine(temporary, "preserved.eml"); Assert.Equal(0, (await Run("email-original", reference, output)).Code);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(output)); Assert.Equal(1, (await Run("email-original", reference, output)).Code);
            await File.WriteAllTextAsync(input, "synthetic-private-invalid-message");
            var failed = await Run("import-thunderbird", input, "--account", "synthetic", "--self", "owner@example.test");
            Assert.Equal(1, failed.Code); Assert.DoesNotContain("synthetic-private-invalid-message", failed.Output);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(temporary, true); }
    }

    [Fact]
    public async Task SyntheticWorkflowAndInvalidInputExitCodes()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "PersonalMessageHub.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var cli = Path.Combine(root.FullName, "src", "MessageHub.Cli", "bin", configuration, "net10.0", "MessageHub.Cli.dll");
        var directory = Path.Combine(Path.GetTempPath(), "pmh-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            async Task<(int Code, string Output)> Run(params string[] arguments)
            {
                var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                info.ArgumentList.Add(cli);
                foreach (var argument in arguments) info.ArgumentList.Add(argument);
                info.Environment["MESSAGEHUB_DB"] = Path.Combine(directory, "test.db");
                info.Environment.Remove("MESSAGEHUB_REPORT_TIMEZONE");
                info.Environment.Remove("MESSAGEHUB_OLLAMA_MODEL");
                info.Environment.Remove("MESSAGEHUB_OLLAMA_ENDPOINT");
                using var process = Process.Start(info)!;
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
                return (process.ExitCode, await output + await error);
            }

            Assert.Equal(0, (await Run("init")).Code);
            Assert.Contains("No sources synchronized", (await Run("status")).Output);
            Assert.Equal(0, (await Run("import-demo")).Code);
            Assert.Equal(0, (await Run("import-demo")).Code);
            Assert.Contains("Total: 3; incoming: 2; outgoing: 1", (await Run("report", "7")).Output);
            Assert.Contains("demo:", (await Run("status")).Output);
            Assert.Contains("Availability", (await Run("search", "availability")).Output);
            var sample = Path.Combine(root.FullName, "samples", "messages.example.json");
            Assert.Equal(0, (await Run("import-json", sample)).Code);
            Assert.Equal(0, (await Run("import-json", sample)).Code);
            Assert.Contains("Coffee", (await Run("search", "Saturday")).Output);
            Assert.Equal(1, (await Run("report", "invalid")).Code);
            Assert.Equal(1, (await Run("report", "0")).Code);
            Assert.Equal(1, (await Run("unknown")).Code);
            Assert.Equal(1, (await Run("sync", "unsupported")).Code);
            Assert.Equal(1, (await Run("sync", "gmail", "unexpected")).Code);
            Assert.Equal(1, (await Run("auth", "gmail")).Code);
            Assert.Equal(1, (await Run("forget", "unsupported")).Code);
            Assert.Equal(1, (await Run("import-json")).Code);
            var invalid = Path.Combine(directory, "invalid.json");
            await File.WriteAllTextAsync(invalid, "[{\"body\":\"synthetic-private-marker\"}]");
            var result = await Run("import-json", invalid);
            Assert.Equal(1, result.Code);
            Assert.DoesNotContain("synthetic-private-marker", result.Output);
            await File.WriteAllTextAsync(invalid, "null");
            Assert.Equal(1, (await Run("import-json", invalid)).Code);
            Assert.Contains("Total: 0;", (await Run("report", "1")).Output);
            var saved = await Run("report", "7", "--end", "2026-11-10T13:00:00Z");
            Assert.Equal(0, saved.Code);
            var reportId = saved.Output.Split('\n').Select(x => x.Trim()).Single(x => x.StartsWith("Saved report: "))["Saved report: ".Length..];
            Assert.Contains(reportId, (await Run("reports")).Output);
            Assert.Contains("Source coverage and freshness", (await Run("report-show", reportId)).Output);
            Assert.Contains("your availability", (await Run("message", "demo:001")).Output);
            var weekly = await Run("worker", "--once", "--timezone", "UTC");
            Assert.Equal(0, weekly.Code);
            Assert.Contains("Saved report: weekly-", weekly.Output);
            Assert.Contains("already exists", (await Run("worker", "--once", "--timezone", "UTC")).Output);
            Assert.Equal(0, (await Run("REPORT", "7", "--end", "2026-11-10T13:00:00Z")).Code);
            Assert.Equal(1, (await Run("report", "--ai")).Code);
            Assert.Equal(1, (await Run("report", "--end", "2026-11-10")).Code);
            Assert.Equal(1, (await Run("worker", "--once", "--hour", "25")).Code);
            Assert.Equal(1, (await Run("worker", "--once", "--timezone", "unknown-zone")).Code);
            Assert.Equal(1, (await Run("report-show", "missing")).Code);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task OfficialExportCommandsDryRunReplayAndExposeSnapshotProvenanceWithoutContentInErrors()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);
        while(root is not null&&!File.Exists(Path.Combine(root.FullName,"PersonalMessageHub.sln")))root=root.Parent;
        Assert.NotNull(root);
        var configuration=new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var cli=Path.Combine(root.FullName,"src","MessageHub.Cli","bin",configuration,"net10.0","MessageHub.Cli.dll");
        var directory=Path.Combine(Path.GetTempPath(),"pmh-export-cli-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        var database=Path.Combine(directory,"nested","test.db");
        try
        {
            async Task<(int Code,string Output)> Run(params string[] arguments)
            {
                var info=new ProcessStartInfo("dotnet"){RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false};
                info.ArgumentList.Add(cli);foreach(var argument in arguments)info.ArgumentList.Add(argument);
                info.Environment["MESSAGEHUB_DB"]=database;
                using var process=Process.Start(info)!;var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try{await process.WaitForExitAsync(timeout.Token);}catch(OperationCanceledException){process.Kill(entireProcessTree:true);throw;}
                return(process.ExitCode,await output+await error);
            }
            var linked=Path.Combine(root.FullName,"samples","linkedin.messages.example.csv");
            var messenger=Path.Combine(root.FullName,"samples","messenger.message.example.json");
            var dry=await Run("import-linkedin",linked,"--account","synthetic-linkedin","--self","https://www.linkedin.com/in/owner-example","--dry-run");
            Assert.Equal(0,dry.Code);Assert.Contains("3 message rows",dry.Output);Assert.False(File.Exists(database));Assert.False(Directory.Exists(Path.GetDirectoryName(database)));
            for(var i=0;i<2;i++)
            {
                Assert.Equal(0,(await Run("import-linkedin",linked,"--account","synthetic-linkedin","--self","https://www.linkedin.com/in/owner-example")).Code);
                Assert.Equal(0,(await Run("import-messenger",messenger,"--account","synthetic-messenger","--self","Owner Example")).Code);
            }
            var store=new MessageHub.Storage.SqliteMessageStore(database);
            Assert.Equal(6,(await store.BetweenAsync(DateTimeOffset.Parse("2026-11-08T00:00:00Z"),DateTimeOffset.Parse("2026-11-10T00:00:00Z"))).Count);
            Assert.Equal(2,(await store.GetLatestExportImportsAsync()).Count);
            Assert.Contains("export snapshot imported",(await Run("status")).Output);
            Assert.Contains("Snapshot coverage unknown",(await Run("imports")).Output);
            var invalid=Path.Combine(directory,"bad.json");await File.WriteAllTextAsync(invalid,"{\"content\":\"synthetic-private-export-marker\"}");
            var failed=await Run("import-messenger",invalid,"--account","synthetic-messenger","--self","Owner Example");
            Assert.Equal(1,failed.Code);Assert.DoesNotContain("synthetic-private-export-marker",failed.Output);Assert.Contains("export-imports.md",failed.Output);
        }
        finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(directory,true);}
    }
}
