using System;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Tbot.Common.Settings;
using Tbot.Services;
using TBot.Common.Logging;
using TBot.Ogame.Infrastructure.Models;

namespace TBot.Model {
 public static class AutoFarmCargoDiagnostics {
  public static bool Enabled(ITBotMain host) => SettingsService.IsSettingSet(host.InstanceSettings.AutoFarm, "CargoDiagnostics") &&
   SettingsService.IsSettingSet(host.InstanceSettings.AutoFarm.CargoDiagnostics, "Active") && (bool)host.InstanceSettings.AutoFarm.CargoDiagnostics.Active;
  public static bool BlocksDispatch(ITBotMain host) => Enabled(host) &&
   (!SettingsService.IsSettingSet(host.InstanceSettings.AutoFarm.CargoDiagnostics, "BlockFleetDispatch") || (bool)host.InstanceSettings.AutoFarm.CargoDiagnostics.BlockFleetDispatch);
  public static void Write(ITBotMain host, string stage, Celestial origin, object values) {
   if (!Enabled(host)) return;
   host.log(LogLevel.Information, LogSender.AutoFarm, "CargoDiagnostic " + JsonConvert.SerializeObject(new {
    Utc = DateTime.UtcNow, Stage = stage, OriginId = origin?.ID, OriginType = origin?.Coordinate?.Type.ToString(), Values = values
   }));
  }
 }
}
