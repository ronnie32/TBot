using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Serilog.Events;
using TBot.Common.Logging;
using Tbot.Services;

namespace TBot.Tests;

[CollectionDefinition("Settings reload", DisableParallelization = true)]
public class SettingsReloadCollection { }

[Collection("Settings reload")]
public partial class SettingsReloadTests {
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Failed_settings_reload_releases_the_gate_and_allows_a_valid_reload(bool malformedJson) {
        var directory = Directory.CreateTempSubdirectory("tbot-settings-reload-");
        var path = Path.Combine(directory.FullName, "settings.json");
        var managerType = typeof(SettingsFileWatcher).Assembly.GetType("Tbot.Services.InstanceManager", true);
        var gateField = managerType.GetField("instancesSem", BindingFlags.Static | BindingFlags.NonPublic);
        var watcherField = managerType.GetField("settingsWatcher", BindingFlags.Static | BindingFlags.NonPublic);
        var originalGate = gateField.GetValue(null);
        var originalWatcher = watcherField.GetValue(null);
        using var gate = new SemaphoreSlim(1, 1);
        var messages = new List<(LogLevel Level, string Message)>();
        bool failWatcherCreation = !malformedJson;
        Action onStats = () => { };
        var logger = Activator.CreateInstance(typeof(ReloadLogger<>).MakeGenericType(managerType), messages, (Action)(() => onStats()));
        var manager = Activator.CreateInstance(managerType, logger, null, null, null, null);
        onStats = () => {
            if (failWatcherCreation)
                managerType.GetProperty("SettingsAbsoluteFilepath").SetValue(manager, Path.Combine(directory.FullName, "missing", "settings.json"));
        };
        managerType.GetProperty("SettingsAbsoluteFilepath").SetValue(manager, path);
        var reload = managerType.GetMethod("OnSettingsChanged");
        gateField.SetValue(null, gate);
        watcherField.SetValue(null, null);
        try {
            File.WriteAllText(path, malformedJson ? "{\"Instances\": [" : "{\"Instances\": []}");
            var malformed = new AsyncVoidContext();
            malformed.Run(() => reload.Invoke(manager, null));

            Assert.Equal(1, gate.CurrentCount);

            failWatcherCreation = false;
            managerType.GetProperty("SettingsAbsoluteFilepath").SetValue(manager, path);
            File.WriteAllText(path, "{\"Instances\": []}");
            var valid = new AsyncVoidContext();
            valid.Run(() => reload.Invoke(manager, null));

            Assert.Empty(malformed.Exceptions);
            Assert.Empty(valid.Exceptions);
            Assert.Contains(messages, message => message.Level == LogLevel.Error);
            Assert.Contains(messages, message => message.Message == "Instances stats: Initialized 0 - Deinitialized 0");
            Assert.Equal(1, gate.CurrentCount);
        } finally {
            if (watcherField.GetValue(null) is SettingsFileWatcher watcher) {
                watcher.deinitWatch();
                (typeof(SettingsFileWatcher).GetField("p", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(watcher) as IDisposable)?.Dispose();
            }
            gateField.SetValue(null, originalGate);
            watcherField.SetValue(null, originalWatcher);
            directory.Delete(true);
        }
    }

    private sealed class AsyncVoidContext : SynchronizationContext {
        private readonly BlockingCollection<Action> _callbacks = new();
        private int _operations;
        public List<Exception> Exceptions { get; } = new();

        public override void OperationStarted() => Interlocked.Increment(ref _operations);
        public override void OperationCompleted() => Interlocked.Decrement(ref _operations);
        public override void Post(SendOrPostCallback callback, object state) => _callbacks.Add(() => callback(state));

        public void Run(Action action) {
            var previous = Current;
            SetSynchronizationContext(this);
            try {
                action();
                var timer = Stopwatch.StartNew();
                while (_operations > 0 || _callbacks.Count > 0) {
                    Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), "Settings reload did not complete.");
                    if (!_callbacks.TryTake(out var callback, 50))
                        continue;
                    try {
                        callback();
                    } catch (Exception exception) {
                        Exceptions.Add(exception);
                    }
                }
            } finally {
                SetSynchronizationContext(previous);
            }
        }
    }

    public sealed class ReloadLogger<T> : ILoggerService<T> {
        private readonly List<(LogLevel Level, string Message)> _messages;
        private readonly Action _onStats;
        public ReloadLogger(List<(LogLevel Level, string Message)> messages, Action onStats) {
            _messages = messages;
            _onStats = onStats;
        }
        public void WriteLog(LogLevel level, LogSender source, string message) {
            _messages.Add((level, message));
            if (message.StartsWith("Instances stats:"))
                _onStats();
        }
        public void ConfigureLogging(string logPath) => throw new NotSupportedException();
        public void AddTelegramLogger(string botToken, string chatId) => throw new NotSupportedException();
        public void RemoveTelegramLogger() => throw new NotSupportedException();
        public bool IsTelegramLoggerEnabled() => throw new NotSupportedException();
        public void SetTelegramLoggerLogLevel(LogEventLevel logLevel) => throw new NotSupportedException();
        public LogEventLevel GetTelegramLoggerLevel() => throw new NotSupportedException();
    }
}
