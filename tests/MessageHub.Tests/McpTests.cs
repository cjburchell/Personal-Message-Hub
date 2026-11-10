using MessageHub.Core;
using MessageHub.Storage;
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
            var environment=StdioClientTransportOptions.GetDefaultEnvironmentVariables();environment["MESSAGEHUB_DB"]=database;
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using(var client=await McpClient.CreateAsync(new StdioClientTransport(new()
            {
                Command="dotnet",Arguments=[executable],InheritEnvironmentVariables=false,EnvironmentVariables=environment
            }),cancellationToken:timeout.Token))
            {
                var tools=await client.ListToolsAsync(cancellationToken:timeout.Token);
                Assert.Equal(new[]{"ask_archive","get_message","get_report","list_reports","search_messages","source_status"},tools.Select(x=>x.Name).Order(StringComparer.Ordinal));
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
            }
            Assert.Single(await store.BetweenAsync(DateTimeOffset.UtcNow.AddDays(-7),DateTimeOffset.UtcNow));
            Assert.Empty(await store.GetReportsAsync());Assert.Empty(await store.GetStatusesAsync());
        }
        finally{SqliteConnection.ClearAllPools();Directory.Delete(directory,true);}
    }
}
