using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Ogame.Infrastructure.Enums {
	public enum FarmState {
		/// Target listed, no action taken or to be taken.
		Idle,
		/// Espionage probes are to be sent to this target.
		ProbesPending,
		/// Espionage probes are sent, no report received yet.
		ProbesSent,
		/// Additional espionage probes are required for more info.
		ProbesRequired,
		/// Additional espionage probes were sent, but insufficient, more required.
		FailedProbesRequired,
		/// Defenses couldn't be determined from an espionage report - a single Espionage Probe was
		/// sent as an Attack mission instead, to detect defenses by whether it survives.
		DefenseProbing,
		/// Suitable target detected, attack is pending.
		AttackPending,
		/// Suitable target detected, attack is ongoing.
		AttackSent,
		/// Target not suitable (insufficient resources / too much defense / insufficicent information available).
		NotSuitable
	}
}
