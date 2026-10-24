using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Tbot.Common.Settings;
using Tbot.Includes;
using Tbot.Services;
using Tbot.Workers;
using TBot.Common.Logging;
using TBot.Model;
using TBot.Ogame.Infrastructure;
using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;

namespace TBot.Tests;

public class FleetDispatchBoundaryTests {
    [Fact]
    public async Task Fleet_save_result_cannot_claim_a_prior_workers_reconciled_dispatch() {
        using var f = new Fixture();
        f.Origin.Ships = f.Ships;
        f.Destination.Type = Celestials.Moon;
        f.Data.celestials = new() { f.Origin, new Moon { ID = 102, Coordinate = f.Destination } };
        f.FleetSaveFuelCapacity = 100000;
        Assert.Equal((int)SendFleetCode.AmbiguousDispatch, await f.Send(Missions.Deploy));
        var priorWorkersFleet = f.Movement(881003);
        priorWorkersFleet.Mission = Missions.Deploy;
        priorWorkersFleet.BackTime = DateTime.UtcNow.AddHours(1);
        f.AfterSend.Add(priorWorkersFleet);
        Assert.Null(await f.SaveWithResult());
        Assert.Equal(1, f.Posts);
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("accepted-followup-fails")]
    [InlineData("reconciled")]
    [InlineData("uncertain")]
    [InlineData("no-ships")]
    public async Task Fleet_save_result_identifies_only_a_confirmed_dispatch_from_that_save(string outcome) {
        using var f = new Fixture();
        f.Origin.Ships = outcome == "no-ships" ? new Ships() : f.Ships;
        f.Destination.Type = Celestials.Moon;
        f.Data.celestials = new() { f.Origin, new Moon { ID = 102, Coordinate = f.Destination } };
        f.FleetSaveFuelCapacity = 100000;
        f.BeforeSend.Add(f.Movement(881001));
        if (outcome.StartsWith("accepted") || outcome == "reconciled") {
            var dispatched = f.Movement(881002);
            dispatched.Mission = Missions.Deploy;
            if (outcome != "accepted-followup-fails") dispatched.BackTime = DateTime.UtcNow.AddHours(1);
            if (outcome == "reconciled") f.AfterSend.Add(dispatched);
            else {
                f.Status = HttpStatusCode.OK;
                f.Body = JsonConvert.SerializeObject(new { status = "ok", result = dispatched });
            }
        }
        Assert.Equal(outcome.StartsWith("accepted") || outcome == "reconciled" ? 881002 : (int?)null, await f.SaveWithResult());
        Assert.Equal(outcome == "no-ships" ? 0 : 1, f.Posts);
    }

    [Fact]
    public async Task A_later_request_with_changed_ships_cannot_reconcile_the_original_uncertain_action() {
        using var f = new Fixture();
        Assert.Equal((int)SendFleetCode.AmbiguousDispatch, await f.Send());
        f.Ships.LargeCargo = 2;
        f.AfterSend.Add(f.Movement(4010));
        Assert.Equal((int)SendFleetCode.AmbiguousDispatch, await f.Send());
        Assert.Equal(1, f.Posts);
    }

    [Fact]
    public async Task A_confirmed_fleet_ID_stays_successful_when_the_following_slot_refresh_fails() {
        using var f = new Fixture();
        f.Status = HttpStatusCode.OK;
        f.Body = JsonConvert.SerializeObject(new { status = "ok", result = f.Movement(4011) });
        f.FailSlotRefresh = true;
        Assert.Equal(4011, await f.Send());
        Assert.Equal(1, f.Posts);
    }

    [Fact]
    public async Task Defender_can_run_between_individual_launches_of_a_planned_system_batch() {
        using var f = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<int>();
        var ogame = AuditProxy<IOgameService>.Make((method, args) => {
            if (method.Name == nameof(IOgameService.SendBoundedSystemDiscovery)) return Discovery((IReadOnlyList<int>)args[4]);
            if (method.Name == nameof(IOgameService.SendFleet)) { order.Add(3); return Task.FromResult(f.Movement(5001)); }
            if (method.Name == nameof(IOgameService.GetFleets)) return Task.FromResult(new List<Fleet>());
            return AuditProxy<IOgameService>.Default(method.ReturnType);
        });
        var scheduler = f.CreateScheduler(ogame);
        var discoveries = scheduler.SendSystemDiscovery(f.Origin, 1, 2, 2, new[] { 1, 2 });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var defender = scheduler.SendFleet(f.Origin, f.Ships, f.Destination, Missions.Deploy, Speeds.HundredPercent, force: true);
        release.SetResult();
        await Task.WhenAll(discoveries, defender).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new[] { 1, 3, 2 }, order);
        Assert.True((await discoveries).IsConfirmed);
        async Task<BoundedDiscoveryResult> Discovery(IReadOnlyList<int> positions) {
            foreach (var position in positions) {
                order.Add(position);
                if (position == 1) { entered.TrySetResult(); await release.Task; }
            }
            return new BoundedDiscoveryResult { Coordinates = positions.Select(position => new Coordinate(1, 2, position)).ToList(), Complete = true };
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unconfirmed_discovery_position_cannot_be_replayed_by_a_later_cycle_or_another_origin(bool bounded) {
        using var f = new Fixture();
        int actions = 0;
        int refreshedMovements = 0;
        var ogame = AuditProxy<IOgameService>.Make((method, args) => {
            if (method.Name == nameof(IOgameService.GetFleets)) {
                refreshedMovements++;
                return Task.FromResult(new List<Fleet>());
            }
            if (method.Name == nameof(IOgameService.SendDiscovery)) {
                actions++;
                throw new HttpRequestException("discovery response lost after acceptance");
            }
            if (method.Name == nameof(IOgameService.SendBoundedSystemDiscovery)) {
                actions++;
                var first = ((IReadOnlyList<int>)args[4])[0] == 3;
                return Task.FromResult(new BoundedDiscoveryResult { Coordinates = first ? new() { new Coordinate(1, 2, 3) } : new(), Complete = first, Error = "second response lost" });
            }
            return AuditProxy<IOgameService>.Default(method.ReturnType);
        });
        var scheduler = f.CreateScheduler(ogame);
        if (bounded) {
            var partial = await scheduler.SendSystemDiscovery(f.Origin, 1, 2, 2, new[] { 3, 7 });
            Assert.Equal(3, Assert.Single(partial.Coordinates).Position);
        } else {
            Assert.False((await scheduler.SendDiscovery(f.Origin, new Coordinate(1, 2, 7))).IsConfirmed);
        }
        var otherOrigin = new Planet { ID = 102, Coordinate = new Coordinate(1, 4, 5) };
        Assert.False((await scheduler.SendDiscovery(otherOrigin, new Coordinate(1, 2, 7))).IsConfirmed);
        Assert.Equal(bounded ? 2 : 1, actions);
        Assert.True(refreshedMovements > 0);
    }

    [Theory]
    [InlineData("partial", 1)]
    [InlineData("foreign", 0)]
    [InlineData("duplicate", 0)]
    public async Task Bounded_scheduler_preserves_only_valid_reserved_movement_evidence(string mode, int expectedCount) {
        using var f = new Fixture();
        var ogame = AuditProxy<IOgameService>.Make((method, args) => {
            if (method.Name == nameof(IOgameService.SendBoundedSystemDiscovery)) {
                Assert.Equal(1, (int)args[3]);
                Assert.Contains(Assert.Single((IReadOnlyList<int>)args[4]), new[] { 3, 7 });
                var coordinates = new List<Coordinate> { new(1, 2, mode == "foreign" ? 9 : 3) };
                if (mode == "duplicate") coordinates.Add(new(1, 2, 3));
                return Task.FromResult(new BoundedDiscoveryResult { Coordinates = coordinates, Complete = false, Error = "synthetic partial result" });
            }
            if (method.Name == nameof(IOgameService.SendSystemDiscovery)) throw new InvalidOperationException("unbounded endpoint must never be used");
            return AuditProxy<IOgameService>.Default(method.ReturnType);
        });
        var scheduler = f.CreateScheduler(ogame);
        var result = await scheduler.SendSystemDiscovery(f.Origin, 1, 2, 2, new[] { 3, 7 });
        Assert.False(result.IsConfirmed);
        Assert.Equal(expectedCount, result.Coordinates.Count);
    }

    [Fact]
    public async Task Accepted_fleet_followed_by_HTTP500_is_reconciled_without_replaying_POST() {
        using var f = new Fixture();
        f.AfterSend = new List<Fleet> { f.Movement(4001) };
        var result = await f.Send();
        Assert.Equal(4001, result);
        Assert.Equal(1, f.Posts);
        Assert.Equal(4001, Assert.Single(f.Data.fleets).ID);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("old")]
    [InlineData("returning")]
    [InlineData("wrong-ships")]
    [InlineData("multiple")]
    public async Task Uncertain_send_cannot_be_confirmed_by_unrelated_movements_or_replayed_next_cycle(string evidence) {
        using var f = new Fixture();
        var fleet = f.Movement(4002);
        if (evidence == "old") f.BeforeSend.Add(fleet);
        if (evidence == "returning") fleet.ReturnFlight = true;
        if (evidence == "wrong-ships") fleet.Ships = new Ships(largeCargo: 2);
        if (evidence != "none") f.AfterSend.Add(fleet);
        if (evidence == "multiple") f.AfterSend.Add(f.Movement(4003));
        Assert.Equal((int)SendFleetCode.AmbiguousDispatch, await f.Send());
        Assert.Equal((int)SendFleetCode.AmbiguousDispatch, await f.Send());
        Assert.Equal(1, f.Posts);
    }

    [Fact]
    public async Task A_rejected_send_is_not_replayed_by_the_transport() {
        using var f = new Fixture();
        f.Status = HttpStatusCode.BadRequest;
        f.Body = "{\"status\":\"error\",\"message\":\"target rejected\"}";
        Assert.Equal((int)SendFleetCode.GenericError, await f.Send());
        Assert.Equal(1, f.Posts);
    }

    [Theory]
    [InlineData("{} ")]
    [InlineData("null")]
    public async Task Malformed_fleet_result_is_ambiguous_and_not_replayed(string result) {
        using var f = new Fixture();
        f.Status = HttpStatusCode.OK;
        f.Body = "{\"status\":\"ok\",\"result\":" + result + "}";
        Assert.Equal((int)SendFleetCode.AmbiguousDispatch, await f.Send());
        Assert.Equal((int)SendFleetCode.AmbiguousDispatch, await f.Send());
        Assert.Equal(1, f.Posts);
    }

    [Fact]
    public async Task Defender_force_dispatch_runs_before_already_queued_normal_workers() {
        using var f = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<int>();
        var ogame = AuditProxy<IOgameService>.Make(asyncHandler);
        var scheduler = f.CreateScheduler(ogame);
        var first = Send(1, false);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var normal = Send(2, false);
        var defender = Send(3, true);
        release.SetResult();
        await Task.WhenAll(first, normal, defender).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new[] { 1, 3, 2 }, order);

        Task<int> Send(int position, bool force) => scheduler.SendFleet(f.Origin, f.Ships,
            new Coordinate(1, 3, position), Missions.Deploy, Speeds.HundredPercent, new Resources(), force: force);
        object asyncHandler(MethodInfo method, object[] args) {
            if (method.Name == nameof(IOgameService.GetFleets)) return Task.FromResult(new List<Fleet>());
            if (method.Name == nameof(IOgameService.SendFleet)) return Complete((Coordinate)args[2]);
            return AuditProxy<IOgameService>.Default(method.ReturnType);
        }
        async Task<Fleet> Complete(Coordinate destination) {
            order.Add(destination.Position);
            if (destination.Position == 1) { entered.SetResult(); await release.Task; }
            var fleet = f.Movement(5000 + destination.Position);
            fleet.Destination = destination;
            fleet.Mission = Missions.Deploy;
            return fleet;
        }
    }

    private sealed class Fixture : IDisposable {
        public Celestial Origin = new Planet { ID = 101, Coordinate = new Coordinate(1, 2, 5), Resources = new Resources(0, 0, 100000), LFBonuses = new() };
        public Ships Ships = new(largeCargo: 1);
        public Coordinate Destination = new(1, 3, 6);
        public UserData Data = new() { fleets = new(), slots = new Slots { Total = 20 }, userInfo = new(), researches = new(), serverData = new ServerData { Galaxies = 9, Systems = 499 } };
        public List<Fleet> BeforeSend = new();
        public List<Fleet> AfterSend = new();
        public int Posts;
        public bool FailSlotRefresh;
        public long FleetSaveFuelCapacity;
        private int slotReads;
        public HttpStatusCode Status = HttpStatusCode.InternalServerError;
        public string Body = "{\"status\":\"error\",\"message\":\"response lost after acceptance\"}";
        private readonly HttpClient client;
        private readonly FleetScheduler scheduler;
        public Fixture() {
            var logger = AuditProxy<ILoggerService<OgameService>>.Make((m, _) => AuditProxy<ILoggerService<OgameService>>.Default(m.ReturnType));
            var service = new OgameService(logger);
            client = new HttpClient(new Handler(request => {
                if (request.Method == HttpMethod.Post) {
                    Posts++; if (Posts > 1) throw new InvalidOperationException("unexpected replay");
                    return new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonConvert.SerializeObject(new { status = "ok", result = Posts == 0 ? BeforeSend : AfterSend })) };
            })) { BaseAddress = new Uri("http://offline.invalid/") };
            typeof(OgameService).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(service, client);
            scheduler = CreateScheduler(service);
        }
        public Fleet Movement(int id) => new() { ID = id, Origin = Origin.Coordinate, Destination = Destination, Mission = Missions.Attack, Ships = Ships, Resources = new() };
        public Task<int> Send(Missions mission = Missions.Attack) => scheduler.SendFleet(Origin, Ships, Destination, mission, Speeds.HundredPercent, new Resources());
        public Task<int?> SaveWithResult() {
            var method = typeof(FleetScheduler).GetMethod("AutoFleetSaveWithResult");
            Assert.NotNull(method);
            return (Task<int?>)method.Invoke(scheduler, new object[] { Origin, 30L });
        }
        public FleetScheduler CreateScheduler(IOgameService ogame) {
            var settings = SettingsService.GetMergedSettingsFromContents("{\"General\":{\"SlotsToLeaveFree\":0},\"SleepMode\":{\"Active\":false,\"AutoFleetSave\":{\"DeutToLeave\":0,\"Recall\":false,\"DefaultMission\":\"Deploy\"}}}", new List<string> { "{}" });
            var parent = AuditProxy<ITBotMain>.Make((method, _) => method.Name switch { "get_InstanceSettings" => (object)settings, "get_InstanceAlias" => "offline-fleets", "get_UserData" => Data, _ => AuditProxy<ITBotMain>.Default(method.ReturnType) });
            var calc = AuditProxy<ICalculationService>.Make((method, _) => method.Name switch {
                nameof(ICalculationService.GetValidSpeedsForClass) => new List<decimal> { Speeds.HundredPercent },
                nameof(ICalculationService.CalcFleetPrediction) => new FleetPrediction { Time = 60, Fuel = 10 },
                nameof(ICalculationService.CalcFleetFuelCapacity) => FleetSaveFuelCapacity,
                _ => AuditProxy<ICalculationService>.Default(method.ReturnType)
            });
            var bridge = AuditProxy<ITBotOgamedBridge>.Make((method, args) => method.Name switch {
                nameof(ITBotOgamedBridge.UpdatePlanet) => Task.FromResult((Celestial)args[0]),
                nameof(ITBotOgamedBridge.UpdateSlots) => GetSlots(),
                _ => AuditProxy<ITBotOgamedBridge>.Default(method.ReturnType)
            });
            var result = new FleetScheduler(calc, ogame); result.SetTBotInstance(parent); result.SetTBotOgameBridge(bridge); return result;
        }
        private Task<Slots> GetSlots() {
            if (++slotReads > 1 && FailSlotRefresh) throw new IOException("synthetic slot refresh failure");
            return Task.FromResult(new Slots { Total = 20 });
        }
        public void Dispose() => client.Dispose();
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }
}
