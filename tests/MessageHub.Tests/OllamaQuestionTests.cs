using System.Net;
using System.Text;
using System.Text.Json;
using MessageHub.Core;
using MessageHub.Query;
using Xunit;

namespace MessageHub.Tests;

public sealed class OllamaQuestionTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public string? Prompt { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            object response;
            if(request.RequestUri!.AbsolutePath=="/api/show")response=new{model_info=new{local=true}};
            else
            {
                Prompt=await request.Content!.ReadAsStringAsync(ct);using var json=JsonDocument.Parse(Prompt);
                Assert.Equal(900,json.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32());
                var messages=json.RootElement.GetProperty("messages");
                Assert.DoesNotContain("synthetic-question-marker",messages[0].GetProperty("content").GetString()!);
                Assert.Contains("synthetic-question-marker",messages[1].GetProperty("content").GetString()!);
                Assert.False(json.RootElement.TryGetProperty("tools",out _));
                response=new{done=true,done_reason="stop",prompt_eval_count=300,eval_count=40,message=new{content=JsonSerializer.Serialize(new{statements=new[]{new{text="Verify the Friday deadline.",evidenceIds=new[]{"example:id"}}}})}};
            }
            return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(response),Encoding.UTF8,"application/json")};
        }
    }
    [Fact]
    public async Task QuestionAndEvidenceRemainInUserPayloadAndOutputRequiresCitations()
    {
        var handler=new Handler();using var client=new HttpClient(handler);using var answerer=new OllamaQuestionAnswerer("synthetic",httpClient:client);
        var end=DateTimeOffset.Parse("2026-11-10T13:00:00Z");
        var answer=await answerer.AnswerAsync("synthetic-question-marker: what is due?",[new("example:id","example","thread",end.AddDays(-1),"Deadline","Due Friday.",false,true,false)],end.AddDays(-7),end);
        Assert.Equal("example:id",Assert.Single(answer.Statements).EvidenceIds[0]);Assert.Equal(40,answer.OutputTokens);Assert.NotNull(handler.Prompt);
    }
}
