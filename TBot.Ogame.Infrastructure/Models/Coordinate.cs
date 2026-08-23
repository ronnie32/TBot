using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TBot.Ogame.Infrastructure.Enums;

namespace TBot.Ogame.Infrastructure.Models {
	public static class LogPrivacy {
		public static bool HideCoordinates = false;
		public static bool HideAccountInfo = false;
	}

	public class Coordinate {
		public Coordinate(int galaxy = 1, int system = 1, int position = 1, Celestials type = Celestials.Planet) {
			Galaxy = galaxy;
			System = system;
			Position = position;
			Type = type;
		}
		public int Galaxy { get; set; }
		public int System { get; set; }
		public int Position { get; set; }
		public Celestials Type { get; set; }

		public override string ToString() {
			if (LogPrivacy.HideCoordinates)
				return $"[{GetCelestialCode()}:hidden]";
			return $"[{GetCelestialCode()}:{Galaxy}:{System}:{Position}]";
		}

		static public Coordinate FromString(String arg) {
			Coordinate output = new();
			Regex re = new Regex("(\\d{1}):(\\d{1,3}):(\\d{1,2}) (moon|planet|Moon|Planet)");
			Match m = re.Match(arg);
			if (m.Success) {
				output.Galaxy = Int32.Parse(m.Groups[1].Value);
				output.System = Int32.Parse(m.Groups[2].Value);
				output.Position = Int32.Parse(m.Groups[3].Value);

				if (m.Groups[4].Value.ToLower().Contains("moon")) {
					output.Type = Celestials.Moon;
				} else {
					output.Type = Celestials.Planet;
				}
			} else {
				throw new Exception($"Invalid Coordinate from {arg}");
			}

			return output;
		}

		// Not the same format as ToString() (which can be privacy-masked to "[X:hidden]" when
		// LogPrivacy.HideCoordinates is on) - callers that need to persist/round-trip a coordinate
		// (e.g. PlayersDatabase known-coordinates) should serialize with this raw format instead of
		// ToString(), so storage never depends on a display-only privacy setting.
		public string ToRawString() {
			return $"{Galaxy}:{System}:{Position}:{Type}";
		}

		public static bool TryParse(string raw, out Coordinate coordinate) {
			coordinate = null;
			if (string.IsNullOrWhiteSpace(raw))
				return false;
			var parts = raw.Split(':');
			if (parts.Length != 4)
				return false;
			if (!int.TryParse(parts[0], out int galaxy) || !int.TryParse(parts[1], out int system) || !int.TryParse(parts[2], out int position))
				return false;
			if (!Enum.TryParse(parts[3], out Celestials type))
				return false;
			coordinate = new Coordinate(galaxy, system, position, type);
			return true;
		}

		private string GetCelestialCode() {
			return Type switch {
				Celestials.Planet => "P",
				Celestials.Debris => "DF",
				Celestials.Moon => "M",
				Celestials.DeepSpace => "DS",
				_ => "",
			};
		}

		public bool IsSame(Coordinate otherCoord) {
			return Galaxy == otherCoord.Galaxy
				&& System == otherCoord.System
				&& Position == otherCoord.Position
				&& Type == otherCoord.Type;
		}
	}

}
