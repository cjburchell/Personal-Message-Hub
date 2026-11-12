using MessageHub.Core;
using MessageHub.Connectors.Thunderbird;
using MessageHub.Storage;
using MessageHub.Query;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace MessageHub.Tests;

public sealed class McpTests
{
    [Fact]
    public async Task RealStdioHandshakeListsOnlyReadToolsAndQueriesSyntheticArchive()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);
        while(root is not null&&!File.Exists(Path.Combine(root.FullName,"PersonalMessageHub.sln")))root=root.Parent;
        Assert.NotNull(root);
        var configuration=new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var executable=Path.Combine(root.FullName,"src","MessageHub.Mcp","bin",configuration,"net10.0","MessageHub.Mcp.dll");
        var directory=Path.Combine(Path.GetTempPath(),"pmh-mcp-test-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var database=Path.Combine(directory,"test.db");
        try
        {
            var store=new SqliteMessageStore(database);await store.InitializeAsync();
            await store.UpsertAsync(new("example","id","thread","sender@example.test",[],"Deadline","Synthetic payment due Friday.",DateTimeOffset.UtcNow.AddDays(-1),false));
            var mail = Path.Combine(directory, "synthetic.eml"); await File.WriteAllBytesAsync(mail, ThunderbirdTests.Message("mcp-history"));
            await new LocalEmailImportService(store).ImportAsync(new ThunderbirdArchive(mail, "mcp-synthetic", ["owner@example.test"]));
            var environment=StdioClientTransportOptions.GetDefaultEnvironmentVariables();environment["MESSAGEHUB_DB"]=database;
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using(var client=await McpClient.CreateAsync(new StdioClientTransport(new()
            {
                Command="dotnet",Arguments=[executable],InheritEnvironmentVariables=false,EnvironmentVariables=environment
            }),cancellationToken:timeout.Token))
            {
                var tools=await client.ListToolsAsync(cancellationToken:timeout.Token);
                Assert.Equal(new[]{"ask_archive","get_contact_history","get_message","get_report","get_thread","list_reports","search_email","search_messages","source_status"},tools.Select(x=>x.Name).Order(StringComparer.Ordinal));
                Assert.All(tools,tool=>{Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint);Assert.False(tool.ProtocolTool.Annotations?.DestructiveHint);});
                var result=await client.CallToolAsync("search_messages",new Dictionary<string,object?>{{"query","What deadlines are coming up?"},{"days",7}},cancellationToken:timeout.Token);
                Assert.False(result.IsError??false);
                var text=string.Join("\n",result.Content.OfType<TextContentBlock>().Select(x=>x.Text));
                Assert.Contains("example:id",text);Assert.Contains("Synthetic payment",text);
                var message=await client.CallToolAsync("get_message",new Dictionary<string,object?>{{"id","example:id"}},cancellationToken:timeout.Token);
                Assert.False(message.IsError??false);
                var invalid=await client.CallToolAsync("search_messages",new Dictionary<string,object?>{{"query","deadline"},{"days",999}},cancellationToken:timeout.Token);
                Assert.True(invalid.IsError);
                Assert.DoesNotContain("Synthetic payment",string.Join("\n",invalid.Content.OfType<TextContentBlock>().Select(x=>x.Text)));
                var historical = await client.CallToolAsync("search_email", new Dictionary<string, object?> { ["query"] = "archive", ["limit"] = 1 }, cancellationToken: timeout.Token);
                Assert.False(historical.IsError ?? false);
                var historicalText = string.Join("\n", historical.Content.OfType<TextContentBlock>().Select(x => x.Text));
                Assert.Contains("2001", historicalText); Assert.DoesNotContain(directory, historicalText);
                var emailHit = Assert.Single((await new EmailArchiveQueries(store).SearchAsync()).Messages);
                foreach (var tool in new[] { "get_thread", "get_contact_history", "get_message" })
                {
                    var arguments = tool == "get_contact_history" ? new Dictionary<string, object?> { ["address"] = "owner@example.test" }
                        : new Dictionary<string, object?> { [tool == "get_thread" ? "messageId" : "id"] = emailHit.Id };
                    var read = await client.CallToolAsync(tool, arguments, cancellationToken: timeout.Token);
                    Assert.False(read.IsError ?? false);
                    var readText = string.Join("\n", read.Content.OfType<TextContentBlock>().Select(x => x.Text));
                    Assert.Contains("2001", readText); Assert.DoesNotContain(directory, readText);
                    if (tool == "get_message") Assert.Contains("email-ref-", readText);
                }
            }
            Assert.Single(await store.BetweenAsync(DateTimeOffset.UtcNow.AddDays(-7),DateTimeOffset.UtcNow));
            Assert.Empty(await store.GetReportsAsync());Assert.Null(Assert.Single(await store.GetStatusesAsync()).LastSuccessfulSync);
        }
        finally{SqliteConnection.ClearAllPools();Directory.Delete(directory,true);}
    }
}
