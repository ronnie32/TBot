extern alias shutdownharness;

using System.Diagnostics;

namespace TBot.Tests;

public class ShutdownTests {
    [Fact]
    public async Task Repeated_cancel_signals_allow_the_entrypoint_to_dispose_once() {
        var harnessPath = typeof(shutdownharness::ShutdownHarness.Marker).Assembly.Location;
        var hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
            ?? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location), "..", "..", "..",
                OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var start = new ProcessStartInfo(hostPath) {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(harnessPath);
        using var process = Process.Start(start);
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try {
            await process.WaitForExitAsync(timeout.Token);
        } catch {
            process.Kill(entireProcessTree: true);
            throw;
        }

        Assert.True(process.ExitCode == 0, await errors);
        Assert.Equal("Two signals handled; instance manager disposed once.", (await output).Trim());
    }
}
