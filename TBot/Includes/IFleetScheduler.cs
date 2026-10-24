using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Tbot.Services;
using Tbot.Workers;
using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;
using TBot.Model;

namespace Tbot.Includes {
	public interface IFleetScheduler {
		void SetTBotInstance(ITBotMain tbotInstance);
		void SetTBotOgameBridge(ITBotOgamedBridge tbotOgameBridge);
		Task SpyCrash(Celestial fromCelestial, Coordinate target = null);
		Task AutoFleetSave(Celestial celestial, bool isSleepTimeFleetSave = false, long minDuration = 0, bool WaitFleetsReturn = false, Missions TelegramMission = Missions.None, bool fromTelegram = false, bool saveall = false);
		Task<int?> AutoFleetSaveWithResult(Celestial celestial, long minDuration = 0);
		Task<int> SendFleet(Celestial origin, Ships ships, Coordinate destination, Missions mission, decimal speed, Resources payload = null, CharacterClass playerClass = CharacterClass.NoClass, bool force = false);
		Task<int> SendValidatedFleet(Celestial origin, Ships ships, Coordinate destination, Missions mission, decimal speed, Resources payload = null, CharacterClass playerClass = CharacterClass.NoClass, bool force = false);
		Task<int> SendExpedition(Celestial origin, Ships ships, Coordinate destination, decimal speed, Resources payload = null, CharacterClass playerClass = CharacterClass.NoClass);
		Task<DiscoveryDispatchResult> SendDiscovery(Celestial origin, Coordinate destination);
		Task<DiscoverySystemDispatchResult> SendSystemDiscovery(Celestial origin, int galaxy, int system, int expectedFleetCount);
		Task<DiscoverySystemDispatchResult> SendSystemDiscovery(Celestial origin, int galaxy, int system, int expectedFleetCount, IReadOnlyList<int> positions);
		bool LastFleetSnapshotWasFresh { get; }
		Task CancelFleet(Fleet fleet);
		Task<List<Fleet>> UpdateFleets();
		void RetireFleet(object fleet);
		Task<int> HandleMinerTransport(Celestial origin, Celestial destination, Celestial differentDestination, Resources resources, Buildables buildable = Buildables.Null, Buildings maxBuildings = null, Facilities maxFacilities = null, Facilities maxLunarFacilities = null, AutoMinerSettings autoMinerSettings = null);
		Task<int> HandleMinerTransport(Celestial origin, Celestial destination, Celestial differentDestination, Resources resources, LFBuildables buildable = LFBuildables.None, LFBuildings maxLFBuildings = null, bool preventIfMoreExpensiveThanNextMine = false);
		Task Collect(bool noLimit = false);
		Task<RepatriateCode> CollectImpl(bool fromTelegram = false, bool noLimit = false);
		Task CollectDeut(long minAmount = 0);
	}

	/// <summary>
	/// Provides an atomic fleet snapshot result for workers that must not pair a
	/// list returned by one refresh with the freshness flag of another refresh.
	/// </summary>
	public interface IFleetSnapshotProvider {
		Task<FleetSnapshotResult> UpdateFleetsWithFreshness();
	}
}
