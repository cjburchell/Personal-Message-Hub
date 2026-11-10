using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace MessageHub.Web;

public sealed class WebPassword(string path)
{
    private static readonly object User = new();
    private static PasswordHasher<object> Hasher() => new(Options.Create(new PasswordHasherOptions { IterationCount = 210000 }));

    public async Task EnsureConfiguredAsync()
    {
        if (!File.Exists(path)) throw new InvalidOperationException("Set a local web password first.");
        var hash = await File.ReadAllTextAsync(path);
        if (hash.Length is < 20 or > 1024 || Convert.FromBase64String(hash).Length < 32) throw new InvalidDataException("Invalid password hash.");
    }
    public async Task<bool> VerifyAsync(string password)
    {
        if (password.Length is < 1 or > 256) return false;
        try { return Hasher().VerifyHashedPassword(User, await File.ReadAllTextAsync(path), password) != PasswordVerificationResult.Failed; }
        catch (Exception ex) when (ex is FormatException or IOException or ArgumentException) { return false; }
    }
    public async Task SetAsync(string password)
    {
        if (password.Length is < 12 or > 256) throw new ArgumentException("Use 12 through 256 password characters.");
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsWindows())
        {
            var permissions = new DirectorySecurity();
            permissions.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            permissions.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(permissions);
        }
        else File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, Hasher().HashPassword(User, password));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static async Task<string> ReadHiddenAsync(string prompt)
    {
        if (Console.IsInputRedirected) throw new InvalidOperationException("Use an interactive terminal for password setup.");
        Console.Write(prompt);
        var text = new System.Text.StringBuilder();
        while (true)
        {
            if (!Console.KeyAvailable) { await Task.Delay(50); continue; }
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return text.ToString(); }
            if (key.Key == ConsoleKey.Escape) throw new OperationCanceledException();
            if (key.Key == ConsoleKey.Backspace) { if (text.Length > 0) text.Length--; }
            else if (!char.IsControl(key.KeyChar) && text.Length < 256) text.Append(key.KeyChar);
        }
    }
}
