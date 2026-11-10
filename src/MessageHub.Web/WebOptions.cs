using MessageHub.Query;

namespace MessageHub.Web;

public sealed record WebOptions(ArchiveRuntimeOptions Archive, string PasswordFile, int Port = 5080)
{
    public string Origin => $"http://127.0.0.1:{Port}";
    public static WebOptions FromEnvironment()
    {
        var directory = Environment.GetEnvironmentVariable("MESSAGEHUB_WEB_AUTH_DIR") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PersonalMessageHub", "web-auth");
        var value = Environment.GetEnvironmentVariable("MESSAGEHUB_WEB_PORT");
        var port = value is null ? 5080 : int.TryParse(value, out var number) ? number : throw new ArgumentException("Invalid web port.");
        if (port is < 1 or > 65535) throw new ArgumentException("Invalid web port.");
        return new(ArchiveRuntimeOptions.FromEnvironment(), Path.Combine(directory, "web.password.hash"), port);
    }
}
