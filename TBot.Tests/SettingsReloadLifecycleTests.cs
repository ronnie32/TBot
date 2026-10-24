using System.Collections;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tbot.Includes;
using Tbot.Services;
using TBot.Common.Logging;
using TBot.Ogame.Infrastructure;
using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;

namespace TBot.Tests;

public partial class SettingsReloadTests {
    [Fact]
    public async Task Dependency_injection_can_activate_the_production_constructor_without_a_delay_registration() {
        using var fixture = new LifecycleFixture();
        var managerType = typeof(ITBotMain).Assembly.GetType("Tbot.Services.InstanceManager", true);
        var telegramType = typeof(ITBotMain).Assembly.GetType("Tbot.Services.TelegramMessenger", true);
        var services = new ServiceCollection();
        foreach (var type in new[] { managerType, telegramType }) {
            var loggerType = typeof(ILoggerService<>).MakeGenericType(type);
            var logger = Activator.CreateInstance(typeof(ReloadLogger<>).MakeGenericType(type),
                new List<(LogLevel Level, string Message)>(), (Action)(() => { }));
            services.AddSingleton(loggerType, logger);
        }
        services.AddSingleton(StrictInterfaceProxy.Make<IOgameService>((method, args) => throw new NotSupportedException(method.Name)));
        services.AddSingleton(StrictInterfaceProxy.Make<ICalculationService>((method, args) => throw new NotSupportedException(method.Name)));
        services.AddScoped(managerType);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService(managerType));
    }

    [Fact]
    public async Task Failed_initialization_disposes_the_partial_bot_and_its_scope() {
        using var fixture = new LifecycleFixture();
        fixture.Initialize = _ => Task.FromException(new InvalidOperationException("offline initialization failure"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Start("B"));

        var bot = Assert.Single(fixture.Bots);
        Assert.False(bot.Active);
        Assert.Equal(1, bot.Disposals);
        Assert.Equal(1, bot.ScopeDisposals);
        Assert.Equal(1, fixture.AvailableReloadPermits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_start_keeps_successful_starts_tracked_including_later_results(bool failureFirst) {
        using var fixture = new LifecycleFixture();
        var laterStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var laterStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Initialize = alias => alias switch {
            "B" => Task.FromException(new InvalidOperationException("offline B init failure")),
            "C" => Later(),
            _ => Task.CompletedTask
        };
        Task Later() {
            laterStarted.TrySetResult();
            return laterStart.Task;
        }

        var reload = Task.Run(() => fixture.Reload(failureFirst ? new[] { "B", "A", "C" } : new[] { "A", "B", "C" }));
        try {
            await laterStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        } finally {
            laterStart.TrySetResult();
            await reload.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(new[] { "A", "C" }, fixture.TrackedAliases.OrderBy(alias => alias));
        var failed = Assert.Single(fixture.Bots, bot => bot.Alias == "B");
        Assert.False(failed.Active);
        Assert.Equal(1, failed.Disposals);
        Assert.Equal(1, failed.ScopeDisposals);

        fixture.Reload("A", "C");
        Assert.Single(fixture.Bots, bot => bot.Alias == "A");
        Assert.Single(fixture.Bots, bot => bot.Alias == "C");
    }

    [Fact]
    public void A_failed_new_start_does_not_restore_removed_instances_to_the_registry() {
        using var fixture = new LifecycleFixture();
        fixture.Reload("A");
        var removed = Assert.Single(fixture.Bots);
        fixture.Initialize = _ => Task.FromException(new InvalidOperationException("offline replacement failure"));

        fixture.Reload("B");

        Assert.Empty(fixture.TrackedAliases);
        Assert.False(removed.Active);
        Assert.Equal(1, removed.Disposals);
        Assert.Equal(1, removed.ScopeDisposals);
        fixture.Initialize = _ => Task.CompletedTask;
        fixture.Reload("A");
        Assert.Equal(2, fixture.Bots.Count(bot => bot.Alias == "A"));
        Assert.Single(fixture.Bots, bot => bot.Active);
    }

    [Fact]
    public void Failed_cleanup_remains_owned_and_blocks_an_overlapping_retry() {
        using var fixture = new LifecycleFixture();
        fixture.Initialize = _ => Task.FromException(new InvalidOperationException("offline initialization failure"));
        fixture.FailDisposal = true;

        fixture.Reload("B");
        fixture.Initialize = _ => Task.CompletedTask;
        fixture.Reload("B");

        Assert.Equal(new[] { "B" }, fixture.TrackedAliases);
        Assert.Single(fixture.Bots);
        Assert.True(fixture.Bots[0].Active);
        Assert.Equal(1, fixture.Bots[0].Disposals);
    }

    [Fact]
    public void A_stagger_failure_still_accounts_for_the_start_that_already_succeeded() {
        using var fixture = new LifecycleFixture();
        fixture.Delay = () => Task.FromException(new InvalidOperationException("offline stagger failure"));

        fixture.Reload("A", "B");

        Assert.Equal(new[] { "A" }, fixture.TrackedAliases);
        Assert.Single(fixture.Bots);
        fixture.Delay = () => Task.CompletedTask;
        fixture.Reload("A");
        Assert.Single(fixture.Bots);
    }

    [Fact]
    public void An_error_during_the_stagger_waits_until_startup_ownership_is_published() {
        using var fixture = new LifecycleFixture();
        fixture.Delay = () => {
            Assert.Single(fixture.Bots).RaiseError();
            return Task.CompletedTask;
        };

        fixture.Reload("A");

        Assert.Empty(fixture.TrackedAliases);
        var bot = Assert.Single(fixture.Bots);
        Assert.False(bot.Active);
        Assert.Equal(1, bot.Disposals);
        Assert.Equal(1, bot.ScopeDisposals);
    }

    [Fact]
    public async Task Shutdown_disposes_each_scope_owned_bot_once() {
        using var fixture = new LifecycleFixture();
        fixture.Reload("A", "B");

        await fixture.Stop();

        Assert.Empty(fixture.TrackedAliases);
        Assert.All(fixture.Bots, bot => {
            Assert.False(bot.Active);
            Assert.Equal(1, bot.Disposals);
            Assert.Equal(1, bot.ScopeDisposals);
        });
    }

    [Fact]
    public async Task Shutdown_waits_for_inflight_startup_ownership_before_disposing() {
        using var fixture = new LifecycleFixture();
        var staggerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var staggerFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Delay = () => {
            staggerStarted.TrySetResult();
            return staggerFinished.Task;
        };
        var reload = Task.Run(() => fixture.Reload("A"));
        await staggerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var shutdown = fixture.Stop();
        try {
            Assert.False(shutdown.IsCompleted);
        } finally {
            staggerFinished.TrySetResult();
            await Task.WhenAll(reload, shutdown).WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Empty(fixture.TrackedAliases);
        var bot = Assert.Single(fixture.Bots);
        Assert.False(bot.Active);
        Assert.Equal(1, bot.Disposals);
        Assert.Equal(1, bot.ScopeDisposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shutdown_attempts_every_owned_instance_when_one_cleanup_fails(bool failureAlreadyRetained) {
        using var fixture = new LifecycleFixture();
        fixture.FailDisposal = true;
        fixture.Initialize = alias => failureAlreadyRetained && alias == "A"
            ? Task.FromException(new InvalidOperationException("offline A initialization failure"))
            : Task.CompletedTask;
        fixture.Reload("A", "B");
        var failed = Assert.Single(fixture.Bots, bot => bot.Alias == "A");
        var healthy = Assert.Single(fixture.Bots, bot => bot.Alias == "B");
        healthy.FailDisposalOverride = false;

        var error = await Record.ExceptionAsync(fixture.Stop);

        Assert.Equal(new[] { "A" }, fixture.TrackedAliases);
        Assert.False(healthy.Active);
        Assert.Equal(1, healthy.Disposals);
        Assert.Equal(1, healthy.ScopeDisposals);
        Assert.True(failed.Active);
        Assert.Equal(1, failed.Disposals);
        Assert.Equal(1, fixture.AvailableReloadPermits);
        Assert.Single(Assert.IsType<AggregateException>(error).InnerExceptions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Bulk_removal_attempts_every_selected_instance_before_blocking_new_starts(bool failureAlreadyRetained) {
        using var fixture = new LifecycleFixture();
        fixture.FailDisposal = true;
        fixture.Initialize = alias => failureAlreadyRetained && alias == "A"
            ? Task.FromException(new InvalidOperationException("offline A initialization failure"))
            : Task.CompletedTask;
        fixture.Reload("A", "B");
        var failed = Assert.Single(fixture.Bots, bot => bot.Alias == "A");
        var healthy = Assert.Single(fixture.Bots, bot => bot.Alias == "B");
        healthy.FailDisposalOverride = false;
        fixture.Initialize = _ => Task.CompletedTask;

        fixture.Reload("C");

        Assert.Equal(new[] { "A" }, fixture.TrackedAliases);
        Assert.False(healthy.Active);
        Assert.Equal(1, healthy.Disposals);
        Assert.Equal(1, healthy.ScopeDisposals);
        Assert.True(failed.Active);
        Assert.Equal(1, failed.Disposals);
        Assert.Equal(1, fixture.AvailableReloadPermits);
        Assert.DoesNotContain(fixture.Bots, bot => bot.Alias == "C");
        Assert.Contains(fixture.Messages, message => message.Level == LogLevel.Error && message.Message.StartsWith("Error reloading settings:"));
    }

    private sealed class LifecycleFixture : IDisposable {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("tbot-partial-reload-");
        private readonly Type _managerType = typeof(ITBotMain).Assembly.GetType("Tbot.Services.InstanceManager", true);
        private readonly Dictionary<FieldInfo, object> _originalFields = new();
        private readonly object _manager;
        private readonly ServiceProvider _provider;
        public string SettingsPath { get; }
        public List<FakeBot> Bots { get; } = new();
        public List<(LogLevel Level, string Message)> Messages { get; } = new();
        public Func<string, Task> Initialize { get; set; } = _ => Task.CompletedTask;
        public Func<Task> Delay { get; set; } = () => Task.CompletedTask;
        public bool FailDisposal { get; set; }
        public int AvailableReloadPermits => ((SemaphoreSlim)_managerType.GetField("instancesSem", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null)).CurrentCount;
        public string[] TrackedAliases => ((IEnumerable)_managerType.GetField("instances", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null))
            .Cast<object>().Select(entry => (string)entry.GetType().GetField("_alias").GetValue(entry)).ToArray();

        public LifecycleFixture() {
            SettingsPath = Path.Combine(_directory.FullName, "settings.json");
            var logger = Activator.CreateInstance(typeof(ReloadLogger<>).MakeGenericType(_managerType), Messages, (Action)(() => { }));
            _provider = new ServiceCollection()
                .AddScoped<ScopeResource>()
                .AddScoped<ITBotMain>(provider => {
                    var bot = new FakeBot(this, provider.GetRequiredService<ScopeResource>());
                    Bots.Add(bot);
                    return bot;
                }).BuildServiceProvider();
            _manager = Activator.CreateInstance(_managerType, logger, null, _provider.GetRequiredService<IServiceScopeFactory>(), null, null,
                (Func<Task>)(() => Delay()));
            _managerType.GetProperty("SettingsAbsoluteFilepath").SetValue(_manager, SettingsPath);
            foreach (var name in new[] { "instancesSem", "settingsWatcher", "instances", "_mainSettings", "telegramMessenger" }) {
                var field = _managerType.GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
                _originalFields.Add(field, field.GetValue(null));
                field.SetValue(null, name switch {
                    "instancesSem" => new SemaphoreSlim(1, 1),
                    "instances" => Activator.CreateInstance(field.FieldType),
                    _ => null
                });
            }
            // Keep file watcher delivery out of this deterministic lifecycle fixture.
            var dormant = new SettingsFileWatcher(() => { }, Path.Combine(_directory.FullName, "unwatched.json"));
            dormant.deinitWatch();
            _managerType.GetField("settingsWatcher", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, dormant);
        }

        public async Task Start(string alias) {
            var path = Path.Combine(_directory.FullName, alias + ".json");
            File.WriteAllText(path, "{}");
            var task = (Task)_managerType.GetMethod("StartTBotMain", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(_manager, new object[] { path, alias });
            await task;
        }

        public void Reload(params string[] aliases) {
            foreach (var alias in aliases)
                File.WriteAllText(Path.Combine(_directory.FullName, alias + ".json"), "{}");
            File.WriteAllText(SettingsPath, System.Text.Json.JsonSerializer.Serialize(new {
                Instances = aliases.Select(alias => new { Settings = alias + ".json", Alias = alias })
            }));
            var context = new AsyncVoidContext();
            context.Run(() => _managerType.GetMethod("OnSettingsChanged").Invoke(_manager, null));
            Assert.Empty(context.Exceptions);
        }

        public async Task Stop() => await (ValueTask)_managerType.GetMethod("DisposeAsync").Invoke(_manager, null);

        public void Dispose() {
            if (!FailDisposal)
                Stop().GetAwaiter().GetResult();
            var watcherField = _managerType.GetField("settingsWatcher", BindingFlags.Static | BindingFlags.NonPublic);
            if (watcherField.GetValue(null) is SettingsFileWatcher watcher) {
                watcher.deinitWatch();
                (typeof(SettingsFileWatcher).GetField("p", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(watcher) as IDisposable)?.Dispose();
            }
            foreach (var original in _originalFields)
                original.Key.SetValue(null, original.Value);
            _provider.Dispose();
            _directory.Delete(true);
        }
    }

    private sealed class ScopeResource : IDisposable {
        public int Disposals { get; private set; }
        public void Dispose() => Disposals++;
    }

    private sealed class FakeBot : ITBotMain, IAsyncDisposable {
        private readonly LifecycleFixture _fixture;
        private readonly ScopeResource _resource;
        public string Alias { get; private set; }
        public bool Active { get; private set; }
        public int Disposals { get; private set; }
        public int ScopeDisposals => _resource.Disposals;
        public bool? FailDisposalOverride { get; set; }

        public FakeBot(LifecycleFixture fixture, ScopeResource resource) {
            _fixture = fixture;
            _resource = resource;
        }

        public Task Init(string path, string alias, ITelegramMessenger messenger) {
            Alias = alias;
            Active = true;
            return _fixture.Initialize(alias);
        }

        public ValueTask DisposeAsync() {
            Disposals++;
            if (FailDisposalOverride ?? _fixture.FailDisposal)
                return ValueTask.FromException(new InvalidOperationException("offline disposal failure"));
            Active = false;
            return ValueTask.CompletedTask;
        }

        public event EventHandler OnError;
        public void RaiseError() => OnError?.Invoke(this, EventArgs.Empty);
        public string InstanceAlias => Alias;
        public dynamic InstanceSettings => throw new NotSupportedException();
        public UserData UserData { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public TelegramUserData TelegramUserData => throw new NotSupportedException();
        public long SleepDuration { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public DateTime NextWakeUpTime { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public void log(LogLevel level, LogSender sender, string format) => throw new NotSupportedException();
        public Task InitializeFeature(Feature feature) => throw new NotSupportedException();
        public Task StopFeature(Feature feature) => throw new NotSupportedException();
        public bool IsFeatureRunning(Feature feature) => throw new NotSupportedException();
        public Task SendFeatureNotification(dynamic settings, string message, TBot.Model.NotificationKind kind = TBot.Model.NotificationKind.Information) => Task.CompletedTask;
        public Task SendTelegramMessage(string format) => throw new NotSupportedException();
        public Task<bool> TelegramSwitch(decimal speed, Celestial attacked = null, bool fromTelegram = false) => throw new NotSupportedException();
        public Task SleepNow(DateTime wakeUpTime) => throw new NotSupportedException();
        public Task ListProfiles() => throw new NotSupportedException();
        public Task LoadProfile(List<string> names) => throw new NotSupportedException();
        public Task ResetProfile() => throw new NotSupportedException();
    }
}
