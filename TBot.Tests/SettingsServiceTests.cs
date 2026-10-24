using System.Reflection;
using Microsoft.VisualStudio.Threading;
using Tbot.Common.Settings;

namespace TBot.Tests;

public class SettingsServiceTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Equivalent_paths_share_the_lock_while_a_writer_is_active(bool relativePath) {
        // Keep the fixture on the working directory's drive so GetRelativePath
        // produces a relative path on Windows, where %TEMP% may be on another drive.
        var directory = Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, "tbot-settings-lock-" + Guid.NewGuid().ToString("N")));
        var path = Path.Combine(directory.FullName, "settings.json");
        var equivalentPath = relativePath ? Path.GetRelativePath(Environment.CurrentDirectory, path)
            : Path.Combine(directory.FullName, ".", "settings.json");
        if (relativePath)
            Assert.False(Path.IsPathRooted(equivalentPath));
        try {
            await File.WriteAllTextAsync(path, "original");
            var semaphore = GetSettingsLock(path);
            Task write;
            using (await semaphore.WriteLockAsync()) {
                using (ExecutionContext.SuppressFlow())
                    write = Task.Run(() => SettingsService.WriteSettings(equivalentPath, "updated"));
                await Assert.ThrowsAsync<TimeoutException>(() => write.WaitAsync(TimeSpan.FromMilliseconds(250)));
                Assert.Equal("original", await File.ReadAllTextAsync(path));
            }
            await write.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("updated", await SettingsService.GetSettingsFileContents(path));
        } finally {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Competing_reads_and_writes_keep_each_settings_file_complete() {
        var directory = Directory.CreateTempSubdirectory("tbot-settings-parallel-");
        try {
            var paths = Enumerable.Range(0, 32).Select(i => Path.Combine(directory.FullName, $"settings-{i}.json")).ToArray();
            foreach (var path in paths)
                await File.WriteAllTextAsync(path, "{\"Generation\":0}");

            await Task.WhenAll(paths.SelectMany(path => Enumerable.Range(0, 6).Select(generation => Task.Run(async () => {
                await SettingsService.WriteSettings(path, $"{{\"Generation\":{generation}}}");
                dynamic settings = await SettingsService.GetSettings(path);
                Assert.InRange((int)settings.Generation, 0, 5);
            }))));

            foreach (var path in paths) {
                dynamic settings = await SettingsService.GetSettings(path);
                Assert.InRange((int)settings.Generation, 0, 5);
            }
        } finally {
            directory.Delete(true);
        }
    }

    private static AsyncReaderWriterLock GetSettingsLock(string path) {
        var manager = typeof(SettingsService).GetField("_settingsSemaphoreManager", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        return (AsyncReaderWriterLock)manager.GetType().GetMethod("GetSemaphore").Invoke(manager, new object[] { path });
    }
}
