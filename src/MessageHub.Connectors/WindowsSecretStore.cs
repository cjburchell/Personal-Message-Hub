using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Apis.Util.Store;

namespace MessageHub.Connectors;

// Google token persistence and provider secrets share an encrypted, per-Windows-user store.
[SupportedOSPlatform("windows")]
public sealed class WindowsSecretStore(string directory) : IDataStore
{
    private string FileName<T>(string key) => Path.Combine(directory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(typeof(T).FullName + ":" + key))) + ".secret");

    public async Task StoreAsync<T>(string key, T value)
    {
        Directory.CreateDirectory(directory);
        var clear = JsonSerializer.SerializeToUtf8Bytes(value);
        var path = FileName<T>(key);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var encrypted = ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser);
            await File.WriteAllBytesAsync(temporary, encrypted);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async Task<T> GetAsync<T>(string key)
    {
        var path = FileName<T>(key);
        if (!File.Exists(path)) return default!;
        var clear = ProtectedData.Unprotect(await File.ReadAllBytesAsync(path), null, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<T>(clear)!; }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public Task DeleteAsync<T>(string key)
    {
        File.Delete(FileName<T>(key));
        return Task.CompletedTask;
    }

    public Task ClearAsync() => throw new NotSupportedException("Delete provider credentials explicitly.");
}
