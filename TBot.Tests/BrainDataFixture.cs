using System.Reflection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Tbot.Common.Settings;
using Tbot.Includes;
using Tbot.Services;
using Tbot.Workers;
using Tbot.Workers.Brain;
using TBot.Common.Logging;
using TBot.Ogame.Infrastructure;
using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;

namespace TBot.Tests;

internal sealed class BrainDataFixture {
    public dynamic Settings;
    public UserData Data;
    public ITBotMain Main;
    public IOgameService Ogame;
    public ITBotOgamedBridge Bridge;
    public IFleetScheduler Scheduler;
    public ICalculationService Calculation;
    public IWorkerFactory Factory;
    public bool ReplaceSnapshots;
    public Func<MethodInfo, object[], object> Calculate;
    public List<Celestial> Children = new();
    public List<(Celestial Origin, Ships Ships, Coordinate Destination, Resources Resources)> Sends = new();
    public List<(Celestial Input, UpdateTypes Type)> Updates = new();
    public List<string> Errors = new();

    public static async Task<BrainDataFixture> Create() {
        var f = new BrainDataFixture {
            Settings = await SettingsService.GetSettings(Path.Combine(AppContext.BaseDirectory, "instance_settings.json")),
            Data = new UserData {
                celestials = new() { Planet(101, 5) }, fleets = new(),
                slots = new Slots { Total = 20 }, researches = new Researches(),
                userInfo = new UserInfo(), staff = new Staff(),
                serverData = new ServerData { Speed = 1, Galaxies = 9, Systems = 499 }
            }
        };
        f.Settings.Brain.Active = true;
        f.Settings.Brain.AutoMine.Active = true;
        f.Settings.Brain.AutoMine.RandomOrder = false;
        f.Settings.Brain.AutoResearch.Active = true;
        f.Settings.Brain.AutoResearch.Target.Galaxy = 1;
        f.Settings.Brain.AutoResearch.Target.System = 2;
        f.Settings.Brain.AutoResearch.Target.Position = 5;
        f.Settings.Brain.AutoResearch.PrioritizeAstrophysics = false;
        f.Settings.Brain.AutoResearch.PrioritizePlasmaTechnology = false;
        f.Settings.Brain.AutoResearch.PrioritizeEnergyTechnology = false;
        f.Settings.Brain.AutoResearch.PrioritizeIntergalacticResearchNetwork = false;
        f.Settings.Brain.LifeformAutoMine.Active = true;
        f.Settings.Brain.LifeformAutoMine.StartFromCrystalMineLvl = 0;
        f.Settings.Brain.LifeformAutoMine.PreventIfMoreExpensiveThanNextMine = false;
        f.Settings.Brain.LifeformAutoResearch.Active = true;
        f.Settings.Brain.LifeformAutoResearch.MaxTechs11 = 2;
        f.Settings.AutoColonize.Abandon.Active = false;
        f.Main = StrictInterfaceProxy.Make<ITBotMain>((m, a) => m.Name switch {
            "get_InstanceSettings" => (object)f.Settings,
            "get_InstanceAlias" => "brain-tests",
            "get_UserData" => f.Data,
            "log" => f.Log((LogLevel)a[0], (string)a[2]),
            _ => throw new NotSupportedException(m.Name)
        });
        f.Ogame = StrictInterfaceProxy.Make<IOgameService>((m, a) => m.Name switch {
            "GetResearches" => Task.FromResult(f.Data.researches),
            _ => throw new NotSupportedException(m.Name)
        });
        f.Bridge = StrictInterfaceProxy.Make<ITBotOgamedBridge>((m, a) => m.Name switch {
            "UpdatePlanet" => Task.FromResult(f.Update((Celestial)a[0], (UpdateTypes)a[1])),
            "UpdateCelestials" or "UpdatePlanets" => Task.FromResult(f.Data.celestials),
            "UpdateSlots" => Task.FromResult(f.Data.slots),
            "CheckCelestials" => Task.FromResult(true),
            "GetDateTime" => Task.FromResult(new DateTime(2026, 10, 23, 12, 0, 0)),
            _ => throw new NotSupportedException(m.Name)
        });
        f.Scheduler = StrictInterfaceProxy.Make<IFleetScheduler>((m, a) => m.Name switch {
            "UpdateFleets" => Task.FromResult(f.Data.fleets),
            "SendFleet" => f.Send(a),
            _ => throw new NotSupportedException(m.Name)
        });
        var logger = StrictInterfaceProxy.Make<ILoggerService<CalculationService>>((m, a) => m.Name switch {
            "WriteLog" => null,
            _ => throw new NotSupportedException(m.Name)
        });
        var real = new CalculationService(logger, f.Ogame);
        f.Calculation = StrictInterfaceProxy.Make<ICalculationService>((m, a) => f.Calculate?.Invoke(m, a) ?? m.Invoke(real, a));
        var child = StrictInterfaceProxy.Make<ITBotCelestialWorker>((m, a) => m.Name switch {
            "StartWorker" or "StartAfter" or "get_FirstExecutionCompleted" => Task.CompletedTask,
            _ => throw new NotSupportedException(m.Name)
        });
        f.Factory = StrictInterfaceProxy.Make<IWorkerFactory>((m, a) => {
            if (m.Name != "InitializeCelestialWorker") throw new NotSupportedException(m.Name);
            f.Children.Add((Celestial)a[4]);
            return child;
        });
        return f;
    }

    public ITBotWorker Worker(string name) => name switch {
        "mine" => new AutoMineWorker(Main, Ogame, Scheduler, Calculation, Bridge, Factory),
        "lf-mine" => new LifeformsAutoMineWorker(Main, Ogame, Scheduler, Calculation, Bridge, Factory),
        "lf-research" => new LifeformsAutoResearchWorker(Main, Ogame, Scheduler, Calculation, Bridge, Factory),
        "research" => new AutoResearchWorker(Main, Ogame, Scheduler, Calculation, Bridge),
        "mine-celestial" => new AutoMineCelestialWorker(Main, null, Ogame, Scheduler, Calculation, Bridge, Data.celestials[0]),
        "lf-mine-celestial" => new LifeformsAutoMineCelestialWorker(Main, null, Ogame, Scheduler, Calculation, Bridge, Data.celestials[0]),
        "lf-research-celestial" => new LifeformsAutoResearchCelestialWorker(Main, null, Ogame, Scheduler, Calculation, Bridge, Data.celestials[0]),
        _ => throw new ArgumentException(name)
    };

    public async Task Run(string worker) {
        var instance = Worker(worker);
        await ((Task)instance.GetType().GetMethod("Execute", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(instance, null));
        Assert.Empty(Errors);
    }

    public static Planet Planet(int id, int position) => new() {
        ID = id, Name = $"Fixture {id}", Coordinate = new Coordinate(1, 2, position),
        Fields = new Fields { Built = 10, Total = 200 }, Temperature = new Temperature(),
        Buildings = new Buildings { CrystalMine = 1 }, Facilities = new Facilities { ResearchLab = 5 },
        Resources = new Resources(), Ships = new Ships(), Defences = new Defences(),
        Constructions = new Constructions(), Productions = new(), Researches = new Researches(),
        ResourceSettings = new ResourceSettings(), LFBuildings = new LFBuildings(), LFTechs = new LFTechs(),
        LFBonuses = new LFBonuses(), LFtype = LFTypes.Humans,
        ResourcesProduction = new ResourcesProduction {
            Metal = new Resource(), Crystal = new Resource(), Deuterium = new Resource(),
            Energy = new Energy(), Food = new Food(), Population = new Population(), Darkmatter = new Darkmatter()
        }
    };

    private Celestial Update(Celestial input, UpdateTypes type) {
        Updates.Add((input, type));
        if (!ReplaceSnapshots) return input;
        var result = JsonConvert.DeserializeObject<Planet>(JsonConvert.SerializeObject(input));
        switch (type) {
            case UpdateTypes.Buildings: result.Buildings = new Buildings { CrystalMine = 27 }; break;
            case UpdateTypes.Facilities: result.Facilities = new Facilities { ResearchLab = 17 }; break;
            case UpdateTypes.LFBuildings: result.LFBuildings = new LFBuildings { ResidentialSector = 9 }; break;
            case UpdateTypes.LFTechs: result.LFTechs = new LFTechs { IntergalacticEnvoys = 1 }; break;
            case UpdateTypes.LFBonuses: result.LFBonuses = CargoBonus(Buildables.SmallCargo, 1); break;
            case UpdateTypes.Resources: result.Resources = new Resources { Metal = 1701 }; break;
            case UpdateTypes.ResourcesProduction: result.ResourcesProduction.Metal.CurrentProduction = 1234; break;
        }
        return result;
    }

    public static LFBonuses CargoBonus(Buildables ship, float bonus) => new() {
        LfShipBonuses = new() { [((int)ship).ToString()] = new LFBonusesShip(cargo: bonus) }
    };

    private object Log(LogLevel level, string message) {
        if (level >= LogLevel.Error) Errors.Add(message);
        return null;
    }

    private Task<int> Send(object[] args) {
        Sends.Add(((Celestial)args[0], (Ships)args[1], (Coordinate)args[2], (Resources)args[5]));
        // The real worker's stop branch finishes without waiting for a remote fleet.
        return Task.FromResult((int)SendFleetCode.AfterSleepTime);
    }
}
