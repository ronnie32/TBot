using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using TBot.Ogame.Infrastructure.Enums;
using System.Reflection;
using System.Runtime.InteropServices;

namespace TBot.Ogame.Infrastructure.Models {
	public class TransportSettings {
		public TransportSettings(bool active = false, Buildables cargoType = Buildables.SmallCargo, long deutToLeave = 0, bool roundResources = true, int roundTo = 1000, bool sendToTheMoonIfPossible = true, Celestial origin = null, long maxSlots = 0, bool checkMoonOrPlanetFirst = false, bool multipleTransports = false, MultipleOrigins multipleOrigins = null, bool stockpileForRoundTrip = false) {
            Active = active;
            CargoType = cargoType;
            DeutToLeave = deutToLeave;
            RoundResources = roundResources;
            RoundTo = roundTo;
            SendToTheMoonIfPossible = sendToTheMoonIfPossible;
            Origin = origin ?? new();
            MaxSlots = maxSlots;
            CheckMoonOrPlanetFirst = checkMoonOrPlanetFirst;
            DoMultipleTransportIsNotEnoughShipButSamePosition = multipleTransports;
            MultipleOrigin = multipleOrigins ?? new();
            StockpileForRoundTrip = stockpileForRoundTrip;
        }
		public bool Active { get; set; }
        public Buildables CargoType { get; set; }
        public long DeutToLeave { get; set; }
        public bool RoundResources { get; set; }
        public int RoundTo { get; set; }
        public bool SendToTheMoonIfPossible { get; set; }
        public Celestial Origin { get; set; }
        public long MaxSlots { get; set; }
        public bool CheckMoonOrPlanetFirst { get; set; }
        public bool DoMultipleTransportIsNotEnoughShipButSamePosition { get; set; }
        public MultipleOrigins MultipleOrigin { get; set; }
        // When true, a transport sends enough resources to cover every construction that would
        // sequentially run on the destination while the fleet is away, not just the next one -
        // sized against the round-trip flight time. See BrainTransportCoordinator and project
        // memory 2026-08-03.
        public bool StockpileForRoundTrip { get; set; }
	}

    public class MultipleOrigins {
        public MultipleOrigins(bool active = false, bool onlyFromMoons = true, long minimumResourcesToSend = 0, bool priorityToP = true, List<Celestial> exclude = null) {
            Active = active;
            OnlyFromMoons = onlyFromMoons;
            MinimumResourcesToSend = minimumResourcesToSend;
            PriorityToProximityOverQuantity = priorityToP;
            Exclude = exclude ?? new();
        }
        public bool Active { get; set; }
        public bool OnlyFromMoons { get; set; }
        public long MinimumResourcesToSend { get; set; }
        public bool PriorityToProximityOverQuantity { get; set; }
        public List<Celestial> Exclude { get; set; }
    }

}