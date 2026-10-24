using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Compile the real entrypoint against offline collaborators. This executable
// cannot create a game bridge, Telegram connection, WebUI host or bot worker.
namespace ShutdownHarness {
    public sealed class Marker { }

    internal static class EntryPoint {
        public static int Main() {
            var directory = Directory.CreateTempSubdirectory("tbot-shutdown-");
            var path = Path.Combine(directory.FullName, "settings.json");
            try {
                File.WriteAllText(path, "{}");
                typeof(Tbot.Program).GetMethod("Main", BindingFlags.NonPublic | BindingFlags.Static)
                    .Invoke(null, new object[] { new[] { path } });

                if (Tbot.Console.HandledSignals != 2 || Tbot.Services.InstanceManager.Disposals != 1)
                    throw new InvalidOperationException($"Handled signals: {Tbot.Console.HandledSignals}; disposals: {Tbot.Services.InstanceManager.Disposals}");
                System.Console.WriteLine("Two signals handled; instance manager disposed once.");
                return 0;
            } catch (Exception exception) {
                System.Console.Error.WriteLine(exception);
                return 1;
            } finally {
                directory.Delete(true);
            }
        }
    }
}

namespace Tbot {
    internal sealed class CancelSignal : EventArgs {
        public bool Cancel { get; set; }
    }

    internal static class Console {
        public static int HandledSignals { get; private set; }
        public static event EventHandler<CancelSignal> CancelKeyPress {
            add {
                // Deliver both before the main task can enter disposal.
                for (var signal = 0; signal < 2; signal++) {
                    var args = new CancelSignal();
                    value(null, args);
                    if (args.Cancel)
                        HandledSignals++;
                }
            }
            remove { }
        }
    }
}

namespace Tbot.Helpers {
    internal static class ConsoleHelpers {
        public static void SetTitle() { }
    }
}

namespace Tbot.Common.Settings {
    internal static class SettingsService {
        public static string GlobalSettingsPath { get; set; }
        public static string LogsPath { get; set; }
        public static Task<dynamic> GetSettings(string path) => Task.FromResult<dynamic>(new object());
        public static bool IsSettingSet(dynamic settings, string property) => false;
    }
}

namespace Tbot.Includes {
    internal interface ICalculationService { }
    internal interface ITBotMain { }
    internal interface ITBotOgamedBridge { }
    internal interface IFleetScheduler { }
    internal interface IWorkerFactory { }
    internal interface ITelegramMessenger { }
    internal interface IInstanceManager : IAsyncDisposable {
        string SettingsAbsoluteFilepath { get; set; }
        void OnSettingsChanged();
    }
}

namespace Tbot.Services {
    internal static class CmdLineArgsService {
        public static bool printHelp => false;
        public static string helpStr => "";
        public static Argument settingsPath = new();
        public static Argument logPath = new();
        public static void DoParse(string[] args) => settingsPath = new Argument { Value = args[0] };
        internal sealed class Argument {
            public string Value { get; set; }
            public bool IsPresent => Value != null;
            public string Get() => Value;
        }
    }

    internal sealed class InstanceManager : Tbot.Includes.IInstanceManager {
        public static int Disposals { get; private set; }
        public string SettingsAbsoluteFilepath { get; set; }
        public void OnSettingsChanged() { }
        public ValueTask DisposeAsync() {
            Disposals++;
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class CalculationService : Tbot.Includes.ICalculationService { }
    internal sealed class TBotMain : Tbot.Includes.ITBotMain { }
    internal sealed class TBotOgamedBridge : Tbot.Includes.ITBotOgamedBridge { }
    internal sealed class FleetScheduler : Tbot.Includes.IFleetScheduler { }
    internal sealed class TelegramMessenger : Tbot.Includes.ITelegramMessenger { }
}

namespace Tbot.Workers {
    internal sealed class WorkerFactory : Tbot.Includes.IWorkerFactory { }
}

namespace TBot.Common.Logging {
    internal enum LogSender { Tbot, Main }
    internal interface ILoggerService<T> {
        void ConfigureLogging(string path);
        void WriteLog(LogLevel level, LogSender sender, string message);
    }
    internal sealed class LoggerService<T> : ILoggerService<T> {
        public void ConfigureLogging(string path) { }
        public void WriteLog(LogLevel level, LogSender sender, string message) { }
    }
    internal static class ColoredConsoleWriter {
        public static void LogToConsole(LogLevel level, LogSender sender, string message) { }
    }
}

namespace TBot.Ogame.Infrastructure {
    internal interface IOgameService {
        bool ValidatePrerequisites();
    }
    internal sealed class OgameService : IOgameService {
        public bool ValidatePrerequisites() => true;
    }
}

namespace TBot.WebUI {
    internal static class WebApp {
        private static readonly IServiceCollection Services = new ServiceCollection();
        public static IServiceCollection GetServiceCollection() => Services;
        public static Task<ServiceProvider> Build() => Task.FromResult(Services.BuildServiceProvider());
        public static Task Main(CancellationToken token) => throw new InvalidOperationException("Offline harness must not start WebUI.");
    }
}

namespace Tbot.Exceptions { }
namespace Serilog { }
namespace Newtonsoft.Json.Linq { }
namespace TBot.Common.Logging.Enrichers { }
namespace TBot.Common.Logging.Hooks { }
namespace TBot.Common.Logging.Hubs { }
namespace TBot.Common.Logging.Sinks { }
namespace TBot.Common.Logging.TextFormatters { }
namespace TBot.Ogame.Infrastructure.Enums { }
