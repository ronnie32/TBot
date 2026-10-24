using System.Reflection;
using Tbot.Common.Settings;
using Tbot.Includes;
using Tbot.Services;
using Tbot.Workers;
using TBot.Ogame.Infrastructure;
using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;
using Xunit.Abstractions;

namespace TBot.Tests;

// Calls the actual donor worker with a mutable, account-free mailbox.
// Assertions express the desired preservation/traversal guarantees.
public class RonnieMailboxTests(ITestOutputHelper output) {
    [Fact]
    public async Task Deleting_page_one_must_not_skip_the_next_report() {
        var mailbox = new List<int> { 900001, 900002 };
        var (complete, read, deleted) = await ProcessMailbox(mailbox, processAllReports: true);
        output.WriteLine($"complete={complete}; read={string.Join(',', read)}; deleted={string.Join(',', deleted)}; remaining={string.Join(',', mailbox)}");
        Assert.True(complete);
        Assert.Equal(new[] { 900001, 900002 }, read);
        Assert.Empty(mailbox);
    }

    [Fact]
    public async Task An_expired_external_report_must_be_preserved_when_scope_is_owned_only() {
        var mailbox = new List<int> { 900003 };
        var (_, read, deleted) = await ProcessMailbox(mailbox, processAllReports: false);
        output.WriteLine($"read={string.Join(',', read)}; deleted={string.Join(',', deleted)}; remaining={string.Join(',', mailbox)}");
        Assert.Empty(deleted);
        Assert.Equal(new[] { 900003 }, mailbox);
    }

    [Fact]
    public async Task An_expired_manual_report_to_a_known_target_is_preserved() {
        var mailbox = new List<int> { 900006 };
        var result = await ProcessMailbox(mailbox, false, "known-target");
        Assert.Empty(result.deleted);
        Assert.Equal(new[] { 900006 }, mailbox);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("invalid-id")]
    [InlineData("missing-coordinate")]
    public async Task Malformed_manual_report_to_a_known_target_is_preserved(string detailShape) {
        var mailbox = new List<int> { 900007 };
        var result = await ProcessMailbox(mailbox, false, "known-target", detailShape);
        Assert.Empty(result.deleted);
        Assert.Equal(new[] { 900007 }, mailbox);
    }

    [Theory]
    [InlineData("owned-probe", "null")]
    [InlineData("owned-probe", "invalid-id")]
    [InlineData("owned-probe", "missing-coordinate")]
    [InlineData("owned-report", "null")]
    [InlineData("owned-report", "invalid-id")]
    [InlineData("owned-report", "missing-coordinate")]
    public async Task Malformed_owned_report_can_be_cleaned_with_proven_probe_or_report_identity(string ownership, string detailShape) {
        var mailbox = new List<int> { 900008 };
        var result = await ProcessMailbox(mailbox, false, ownership, detailShape);
        Assert.True(result.complete);
        Assert.Equal(new[] { 900008 }, result.deleted);
        Assert.Empty(mailbox);
    }

    [Theory]
    [InlineData("repeat")]
    [InlineData("limit")]
    [InlineData("error")]
    public async Task An_incomplete_mailbox_traversal_keeps_the_report_phase_pending(string mode) {
        var result = await ProcessMailbox(new List<int> { 900004 }, true, mode);
        Assert.False(result.complete);
    }

    [Fact]
    public async Task A_failed_delete_keeps_the_report_phase_pending() {
        var result = await ProcessMailbox(new List<int> { 900005 }, true, "delete-error");
        Assert.False(result.complete);
    }

    private static async Task<(bool complete, List<int> read, List<int> deleted)> ProcessMailbox(List<int> mailbox, bool processAllReports, string mode = "", string detailShape = "") {
        var now = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        var reports = mailbox.ToDictionary(id => id, id => new EspionageReport {
            ID = id,
            Coordinate = new Coordinate(1, 1, id % 10 + 1, Celestials.Planet),
            Date = now.AddDays(-2),
            IsInactive = true,
            HasFleetInformation = true,
            HasDefensesInformation = true
        });
        var read = new List<int>();
        var deleted = new List<int>();
        var settings = SettingsService.GetMergedSettingsFromContents(
            "{\"AutoFarm\":{\"Active\":true,\"ProcessAllReports\":" + processAllReports.ToString().ToLowerInvariant() + ",\"KeepReportFor\":1}}",
            new List<string> { "{}" });
        var data = new UserData {
            celestials = new List<Celestial>(), farmTargets = new List<FarmTarget>(),
            fleets = new List<Fleet>(), slots = new Slots { Total = 10 },
            serverData = new ServerData(), userInfo = new UserInfo(), researches = new Researches()
        };
        if (mode == "known-target" || mode.StartsWith("owned-")) {
            var report = reports.Values.Single();
            var target = new FarmTarget(new Planet { Coordinate = report.Coordinate },
                mode == "owned-probe" ? FarmState.ProbesSent : FarmState.AttackPending);
            if (mode == "owned-probe") target.ProbeFleetId = 700008;
            if (mode == "owned-report") target.Report = report;
            data.farmTargets.Add(target);
        }
        var alias = "audit-" + Guid.NewGuid().ToString("N");
        var parent = AuditProxy<ITBotMain>.Make((method, _) => method.Name switch {
            "get_InstanceAlias" => alias,
            "get_InstanceSettings" => (object)settings,
            "get_UserData" => data,
            _ => AuditProxy<ITBotMain>.Default(method.ReturnType)
        });
        var ogame = AuditProxy<IOgameService>.Make((method, args) => {
            if (method.Name == nameof(IOgameService.GetEspionageReportsPage)) {
                var page = (int)args[0];
                if (mode == "error" && page == 2) throw new IOException("synthetic page failure");
                if (mode == "repeat" || mode == "limit")
                    return Task.FromResult(new List<EspionageReportSummary> { new() { ID = mode == "repeat" ? 900004 : 910000 + page, Type = EspionageReportType.Action } });
                return Task.FromResult(mailbox.Skip(page - 1).Take(1)
                    .Select(id => new EspionageReportSummary { ID = id, Type = EspionageReportType.Report, Target = reports[id].Coordinate }).ToList());
            }
            if (method.Name == nameof(IOgameService.GetEspionageReport)) {
                read.Add((int)args[0]);
                if (detailShape == "null") return Task.FromResult<EspionageReport>(null);
                if (detailShape == "invalid-id") return Task.FromResult(new EspionageReport { ID = 0, Coordinate = reports[(int)args[0]].Coordinate });
                if (detailShape == "missing-coordinate") return Task.FromResult(new EspionageReport { ID = (int)args[0] });
                return Task.FromResult(reports[(int)args[0]]);
            }
            if (method.Name == nameof(IOgameService.DeleteReport)) {
                if (mode == "delete-error") throw new IOException("synthetic delete failure");
                deleted.Add((int)args[0]);
                mailbox.Remove((int)args[0]);
                return Task.CompletedTask;
            }
            return AuditProxy<IOgameService>.Default(method.ReturnType);
        });
        var bridge = AuditProxy<ITBotOgamedBridge>.Make((method, _) => method.Name == nameof(ITBotOgamedBridge.GetDateTime)
            ? Task.FromResult(now) : AuditProxy<ITBotOgamedBridge>.Default(method.ReturnType));
        var scheduler = AuditProxy<IFleetScheduler>.Make((method, _) => AuditProxy<IFleetScheduler>.Default(method.ReturnType));
        var calculation = AuditProxy<ICalculationService>.Make((method, _) => AuditProxy<ICalculationService>.Default(method.ReturnType));
        var worker = new AutoFarmWorker(parent, ogame, scheduler, calculation, bridge);
        var methodInfo = typeof(AutoFarmWorker).GetMethod("AutoFarmProcessReports", BindingFlags.Instance | BindingFlags.NonPublic);
        var complete = await (Task<bool>)methodInfo.Invoke(worker, null);
        return (complete, read, deleted);
    }
}

public class AuditProxy<T> : DispatchProxy where T : class {
    private Func<MethodInfo, object[], object> handler;
    public static T Make(Func<MethodInfo, object[], object> callback) {
        var proxy = Create<T, AuditProxy<T>>();
        ((AuditProxy<T>)(object)proxy).handler = callback;
        return proxy;
    }
    protected override object Invoke(MethodInfo method, object[] args) => handler(method, args ?? Array.Empty<object>()) ?? Default(method.ReturnType);
    public static object Default(Type type) {
        if (type == typeof(void)) return null;
        if (type == typeof(Task)) return Task.CompletedTask;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>)) {
            var resultType = type.GetGenericArguments()[0];
            return typeof(Task).GetMethod(nameof(Task.FromResult)).MakeGenericMethod(resultType)
                .Invoke(null, new[] { resultType.IsValueType ? Activator.CreateInstance(resultType) : null });
        }
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}
