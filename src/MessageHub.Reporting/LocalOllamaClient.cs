using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MessageHub.Reporting;

public sealed class LocalOllamaClient : IDisposable
{
    private readonly HttpClient _client;
    private readonly Uri _endpoint;
    private readonly string _model;
    private readonly bool _ownsClient;
    public const int MaximumOutputTokens = 1200;
    private const int MaximumResponseBytes = 65536;

    public LocalOllamaClient(string model, Uri? endpoint = null, HttpClient? client = null)
    {
        _endpoint = endpoint ?? new Uri("http://127.0.0.1:11434/");
        if (_endpoint.Scheme != "http" || !IPAddress.TryParse(_endpoint.Host.Trim('[', ']'), out var address)
            || !IPAddress.IsLoopback(address) || _endpoint.UserInfo.Length != 0 || _endpoint.AbsolutePath != "/"
            || _endpoint.Query.Length != 0 || _endpoint.Fragment.Length != 0)
            throw new ArgumentException("Ollama must use a literal loopback HTTP address.");
        if (string.IsNullOrWhiteSpace(model) || model.Length > 120 || model.Contains("cloud", StringComparison.OrdinalIgnoreCase)
            || model.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-' or ':' or '/')))
            throw new ArgumentException("Select a downloaded local model.");
        _model = model;
        _ownsClient = client is null;
        _client = client ?? new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
        if (_ownsClient) _client.Timeout = TimeSpan.FromMinutes(2);
    }

    public async Task<string> GetIdentityAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_endpoint, "api/tags"));
        using var tags = await SendAsync(request, ct);
        var local = tags.RootElement.GetProperty("models").EnumerateArray().FirstOrDefault(x =>
            x.GetProperty("name").GetString() is { } name && (name == _model || name == _model + ":latest"));
        if (local.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("Model is not installed locally.");
        RequireLocal(local);
        var digest = local.GetProperty("digest").GetString();
        if (string.IsNullOrWhiteSpace(digest) || local.GetProperty("size").GetInt64() <= 0)
            throw new InvalidDataException("Missing local model digest.");
        return "ollama:" + _model + ":" + digest;
    }

    public async Task<OllamaCompletion> CompleteAsync(object schema, string system, string user, int maximumOutputTokens = 1200, CancellationToken ct = default)
    {
        if (maximumOutputTokens is < 1 or > MaximumOutputTokens || System.Text.Encoding.UTF8.GetByteCount(user) > 16000)
            throw new ArgumentException("Model input exceeds the budget.");
        using (var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_endpoint, "api/show")) { Content = JsonContent.Create(new { model = _model }) })
        using (var show = await SendAsync(request, ct))
        {
            RequireLocal(show.RootElement);
            if (!show.RootElement.TryGetProperty("model_info", out var info) || info.ValueKind != JsonValueKind.Object || !info.EnumerateObject().Any())
                throw new InvalidDataException("Local model metadata is required before sending evidence.");
        }
        var payload = new
        {
            model = _model, stream = false, think = false,
            options = new { temperature = 0, num_predict = maximumOutputTokens, num_ctx = 16384 }, format = schema,
            messages = new[] { new { role = "system", content = system }, new { role = "user", content = user } }
        };
        using var chatRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(_endpoint, "api/chat")) { Content = JsonContent.Create(payload) };
        using var response = await SendAsync(chatRequest, ct);
        RequireLocal(response.RootElement);
        if (!response.RootElement.GetProperty("done").GetBoolean()
            || response.RootElement.TryGetProperty("done_reason", out var reason) && reason.GetString() == "length")
            throw new InvalidDataException("Incomplete analysis.");
        var message = response.RootElement.GetProperty("message");
        if (message.TryGetProperty("tool_calls", out var tools) && tools.GetArrayLength() > 0)
            throw new InvalidDataException("Tool requests are not accepted.");
        var input = response.RootElement.TryGetProperty("prompt_eval_count", out var inputCount) ? inputCount.GetInt32() : 0;
        var output = response.RootElement.TryGetProperty("eval_count", out var outputCount) ? outputCount.GetInt32() : 0;
        if (input < 0 || output < 0 || output > maximumOutputTokens) throw new InvalidDataException("Invalid generation budget.");
        return new(message.GetProperty("content").GetString() ?? throw new InvalidDataException("Missing content."), input, output);
    }
    private static void RequireLocal(JsonElement model)
    {
        foreach (var field in new[] { "remote_host", "remote_model" })
            if (model.TryGetProperty(field, out var remote) && remote.ValueKind != JsonValueKind.Null && !string.IsNullOrEmpty(remote.GetString()))
                throw new InvalidDataException("Remote-backed models are not allowed.");
    }

    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        ct = timeout.Token;
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaximumResponseBytes) throw new InvalidDataException("Analysis response too large.");
            buffer.Write(chunk, 0, read);
        }
        return JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
    }

    public void Dispose() { if (_ownsClient) _client.Dispose(); }
}

public sealed record OllamaCompletion(string Content, int InputTokens, int OutputTokens);
