using System.Diagnostics;
using Xunit;

namespace MessageHub.Tests;

public sealed class CliTests
{
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
            Assert.Equal(1, (await Run("import-json")).Code);
            var invalid = Path.Combine(directory, "invalid.json");
            await File.WriteAllTextAsync(invalid, "[{\"body\":\"synthetic-private-marker\"}]");
            var result = await Run("import-json", invalid);
            Assert.Equal(1, result.Code);
            Assert.DoesNotContain("synthetic-private-marker", result.Output);
            await File.WriteAllTextAsync(invalid, "null");
            Assert.Equal(1, (await Run("import-json", invalid)).Code);
            Assert.Contains("Total: 0;", (await Run("report", "1")).Output);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
