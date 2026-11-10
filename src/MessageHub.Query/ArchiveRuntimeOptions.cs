namespace MessageHub.Query;

public sealed record ArchiveRuntimeOptions(string DatabasePath, string? Model, Uri? OllamaEndpoint)
{
    public static ArchiveRuntimeOptions FromEnvironment() => new(
        Environment.GetEnvironmentVariable("MESSAGEHUB_DB") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PersonalMessageHub", "messages.db"),
        Environment.GetEnvironmentVariable("MESSAGEHUB_OLLAMA_MODEL"),
        Environment.GetEnvironmentVariable("MESSAGEHUB_OLLAMA_ENDPOINT") is { Length: > 0 } endpoint ? new Uri(endpoint) : null);
}
