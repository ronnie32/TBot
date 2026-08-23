using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using TBot.Ogame.Infrastructure.Enums;

namespace TBot.Ogame.Infrastructure.Models {
	public class Defences {
		public long RocketLauncher { get; set; }
		public long LightLaser { get; set; }
		public long HeavyLaser { get; set; }
		public long GaussCannon { get; set; }
		public long IonCannon { get; set; }
		public long PlasmaTurret { get; set; }
		public long SmallShieldDome { get; set; }
		public long LargeShieldDome { get; set; }
		public long AntiBallisticMissiles { get; set; }
		public long InterplanetaryMissiles { get; set; }

		public Defences(
			long rocketlauncher = 0,
			long lightlaser = 0,
			long heavylaser = 0,
			long gausscannon = 0,
			long ioncannon = 0,
			long plasmaturret = 0,
			long smallshielddome = 0,
			long largeshielddome = 0,
			long antiballisticmissiles = 0,
			long interplanetarymissiles = 0
		) {
			RocketLauncher = rocketlauncher;
			LightLaser = lightlaser;
			HeavyLaser = heavylaser;
			GaussCannon = gausscannon;
			IonCannon = ioncannon;
			PlasmaTurret = plasmaturret;
			SmallShieldDome = smallshielddome;
			LargeShieldDome = largeshielddome;
			AntiBallisticMissiles = antiballisticmissiles;
			InterplanetaryMissiles = interplanetarymissiles;
		}
		public bool IsEmpty() {
			return RocketLauncher == 0
				&& LightLaser == 0
				&& HeavyLaser == 0
				&& GaussCannon == 0
				&& IonCannon == 0
				&& PlasmaTurret == 0
				&& SmallShieldDome == 0
				&& LargeShieldDome == 0
				&& AntiBallisticMissiles == 0
				&& InterplanetaryMissiles == 0;
		}
		public long GetDefencePoints() {
			long output = 0;
			output += RocketLauncher * 20;
			output += LightLaser * 20;
			output += HeavyLaser * 80;
			output += GaussCannon * 370;
			output += IonCannon * 80;
			output += PlasmaTurret * 1300;
			output += SmallShieldDome * 200;
			output += LargeShieldDome * 1000;
			output += AntiBallisticMissiles * 100;
			output += InterplanetaryMissiles * 245;
			return output / 10;
		}
		public Resources GetDefenceCost() {
			Resources output = new();
			output.Metal = RocketLauncher * 2000 + LightLaser * 1500 + HeavyLaser * 6000 + GaussCannon * 20000 + IonCannon * 3000 + PlasmaTurret * 50000 + SmallShieldDome * 10000 + LargeShieldDome * 50000 + AntiBallisticMissiles * 2000 + InterplanetaryMissiles * 5000;
			output.Crystal = RocketLauncher * 1000 + LightLaser * 500 + HeavyLaser * 2000 + GaussCannon * 15000 + IonCannon * 1000 + PlasmaTurret * 50000 + SmallShieldDome * 5000 + LargeShieldDome * 25000 + AntiBallisticMissiles * 2000 + InterplanetaryMissiles * 2500;
			output.Deuterium = RocketLauncher * 0 + LightLaser * 0 + HeavyLaser * 0 + GaussCannon * 0 + IonCannon * 0 + PlasmaTurret * 2000 + SmallShieldDome * 0 + LargeShieldDome * 10000 + AntiBallisticMissiles * 0 + InterplanetaryMissiles * 1000;
			return output;
		}
		public Defences Add(String buildable, long quantity) {
			foreach (PropertyInfo prop in this.GetType().GetProperties()) {
				if (prop.Name == buildable) {
					prop.SetValue(this, (long) prop.GetValue(this) + quantity);
				}
			}
			return this;
		}

		public Defences Remove(String buildable, int quantity) {
			foreach (PropertyInfo prop in this.GetType().GetProperties()) {
				if (prop.Name == buildable) {
					long val = (long) prop.GetValue(this);
					if (val >= quantity)
						prop.SetValue(this, val);
					else
						prop.SetValue(this, 0);
				}
			}
			return this;
		}

		public long GetAmount(Buildables buildable) {
			foreach (PropertyInfo prop in this.GetType().GetProperties()) {
				if (prop.Name == buildable.ToString()) {
					return (long) prop.GetValue(this);
				}
			}
			return 0;
		}

		public void SetAmount(Buildables buildable, long number) {
			foreach (PropertyInfo prop in this.GetType().GetProperties()) {
				if (prop.Name == buildable.ToString()) {
					prop.SetValue(this, number);
					return;
				}
			}
		}

		public bool HasAtLeast(Defences defences, long times = 1) {
			foreach (PropertyInfo prop in this.GetType().GetProperties()) {
				if ((long) prop.GetValue(this) * times < (long) prop.GetValue(defences)) {
					return false;
				}
			}
			return true;
		}

		public override string ToString() {
			string output = "";
			foreach (PropertyInfo prop in this.GetType().GetProperties()) {
				if ((long) prop.GetValue(this) == 0)
					continue;
				output += $"{prop.Name}: {prop.GetValue(this)}; ";
			}
			return output;
		}
		public Defences Sum(Defences defencesToSum) {
			Defences output = new();
			output.RocketLauncher = RocketLauncher + defencesToSum.RocketLauncher;
			output.LightLaser = LightLaser + defencesToSum.LightLaser;
			output.HeavyLaser = HeavyLaser + defencesToSum.HeavyLaser;
			output.GaussCannon = GaussCannon + defencesToSum.GaussCannon;
			output.IonCannon = IonCannon + defencesToSum.IonCannon;
			output.PlasmaTurret = PlasmaTurret + defencesToSum.PlasmaTurret;
			output.SmallShieldDome = SmallShieldDome + defencesToSum.SmallShieldDome;
			output.LargeShieldDome = LargeShieldDome + defencesToSum.LargeShieldDome;
			output.AntiBallisticMissiles = AntiBallisticMissiles + defencesToSum.AntiBallisticMissiles;
			output.InterplanetaryMissiles = InterplanetaryMissiles + defencesToSum.InterplanetaryMissiles;

			return output;
		}
		public Defences Difference(Defences defencesToSubtract) {
			Defences output = new();
			output.RocketLauncher = RocketLauncher - defencesToSubtract.RocketLauncher;
			if (output.RocketLauncher < 0)
				output.RocketLauncher = 0;
			output.LightLaser = LightLaser - defencesToSubtract.LightLaser;
			if (output.LightLaser < 0)
				output.LightLaser = 0;
			output.HeavyLaser = HeavyLaser - defencesToSubtract.HeavyLaser;
			if (output.HeavyLaser < 0)
				output.HeavyLaser = 0;
			output.GaussCannon = GaussCannon - defencesToSubtract.GaussCannon;
			if (output.GaussCannon < 0)
				output.GaussCannon = 0;
			output.IonCannon = IonCannon - defencesToSubtract.IonCannon;
			if (output.IonCannon < 0)
				output.IonCannon = 0;
			output.PlasmaTurret = PlasmaTurret - defencesToSubtract.PlasmaTurret;
			if (output.PlasmaTurret < 0)
				output.PlasmaTurret = 0;
			output.SmallShieldDome = SmallShieldDome - defencesToSubtract.SmallShieldDome;
			if (output.SmallShieldDome < 0)
				output.SmallShieldDome = 0;
			output.LargeShieldDome = LargeShieldDome - defencesToSubtract.LargeShieldDome;
			if (output.LargeShieldDome < 0)
				output.LargeShieldDome = 0;
			output.AntiBallisticMissiles = AntiBallisticMissiles - defencesToSubtract.AntiBallisticMissiles;
			if (output.AntiBallisticMissiles < 0)
				output.AntiBallisticMissiles = 0;
			output.InterplanetaryMissiles = InterplanetaryMissiles - defencesToSubtract.InterplanetaryMissiles;
			if (output.InterplanetaryMissiles < 0)
				output.InterplanetaryMissiles = 0;

			return output;
		}
		public Dictionary<Buildables, long> GetDefenceTypesWithAmount() {
			Dictionary<Buildables, long> defenceTypes = new();
			foreach (var prop in this.GetType().GetProperties()) {
				Buildables buildable = (Buildables) Enum.Parse(typeof(Buildables), prop.Name);
				long amount = (long) prop.GetValue(this);
				if (amount > 0)
					defenceTypes.Add(buildable, amount);
			}
			return defenceTypes;
		}

		public Defences Clone() {
			return new Defences(
				RocketLauncher,
				LightLaser,
				HeavyLaser,
				GaussCannon,
				IonCannon,
				PlasmaTurret,
				SmallShieldDome,
				LargeShieldDome,
				AntiBallisticMissiles,
				InterplanetaryMissiles
			);
		}
	}
}