using System.Collections.Generic;

namespace TBot.Ogame.Infrastructure.Models {
    /// <summary>Only coordinates backed by newly observed outgoing movements are returned.</summary>
    public sealed class BoundedDiscoveryResult {
        public List<Coordinate> Coordinates { get; set; } = new();
        public bool Complete { get; set; }
        public string? Error { get; set; }
    }
}
