using System.Collections.Generic;

namespace TBot.Ogame.Infrastructure.Models {
	// Nullable fields distinguish an explicitly reported zero from missing data.
	public sealed class ReportCombatUnitBonus {
		public decimal? StructuralIntegrity { get; set; }
		public decimal? ShieldPower { get; set; }
		public decimal? WeaponPower { get; set; }
	}
	public sealed class ReportCombatBonuses {
		public string Source { get; set; }
		public Dictionary<string, ReportCombatUnitBonus> Units { get; set; } = new();
	}
	public sealed class CombatUnitBonus {
		public decimal StructuralIntegrity { get; set; }
		public decimal ShieldPower { get; set; }
		public decimal WeaponPower { get; set; }
	}
	public sealed class CombatForce {
		public Ships Ships { get; set; } = new();
		public Defences Defenses { get; set; } = new();
		public int Weapons { get; set; }
		public int Shielding { get; set; }
		public int Armour { get; set; }
		public Dictionary<string, CombatUnitBonus> Bonuses { get; set; } = new();
		public decimal BonusMargin { get; set; }
	}
	public sealed class CombatSimulationRequest {
		public CombatForce Attacker { get; set; }
		public CombatForce Defender { get; set; }
		public int Runs { get; set; }
		public long? Seed { get; set; }
		public bool RapidFire { get; set; } = true;
	}
	public sealed class CombatSimulationResult {
		public int Runs { get; set; }
		public int AttackerWins { get; set; }
		public int DefenderWins { get; set; }
		public int Draws { get; set; }
		public decimal MeanAttackerLoss { get; set; }
		public decimal MaxAttackerLoss { get; set; }
		public Dictionary<string, long> MinSurvivors { get; set; }
		public decimal MeanRounds { get; set; }
		public int MaxRounds { get; set; }
	}
}
