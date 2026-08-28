using System;
using System.Collections.Generic;
using System.Linq;
using GradingTool.Geometry;
using SurfaceUse = GradingTool.AdaComplianceStandards.SurfaceUse;

namespace GradingTool.Grading
{
    /// <summary>
    /// One station along a feature line: an XYZ point plus whether the solver may move its
    /// elevation. Fixed stations are design tie-ins (a match into an existing road, a set
    /// finished-floor at a door) that must not move; free stations are the ones the solver
    /// adjusts to satisfy slope rules.
    /// </summary>
    public sealed class Station
    {
        /// <summary>Plan location and current (proposed) elevation.</summary>
        public Point3d Point { get; set; }

        /// <summary>True if the elevation is a hard control and must not be adjusted.</summary>
        public bool IsFixed { get; }

        /// <summary>Construct a station.</summary>
        public Station(Point3d point, bool isFixed = false)
        {
            Point = point;
            IsFixed = isFixed;
        }
    }

    /// <summary>
    /// A proposed design element to be graded: an ordered 3D polyline (curb, pad edge, swale
    /// invert, walk centreline) tagged with the surface type that governs its slope. The
    /// solver adjusts the free stations' elevations so the running grade along the line stays
    /// within the resolved rule band.
    /// </summary>
    public sealed class FeatureLine
    {
        /// <summary>Name, for reports.</summary>
        public string Name { get; }

        /// <summary>The surface type that governs this line's slope rule.</summary>
        public SurfaceUse Use { get; }

        /// <summary>Ordered stations along the line.</summary>
        public IReadOnlyList<Station> Stations { get; }

        /// <summary>Construct a feature line.</summary>
        public FeatureLine(string name, SurfaceUse use, IEnumerable<Station> stations)
        {
            Name = name;
            Use = use;
            Stations = stations.ToList();
            if (Stations.Count < 2)
                throw new ArgumentException($"Feature line '{name}' needs at least 2 stations.", nameof(stations));
        }

        /// <summary>Horizontal length of segment <paramref name="i"/> (station i to i+1).</summary>
        public double SegmentLength(int i) => Stations[i].Point.HorizontalDistanceTo(Stations[i + 1].Point);

        /// <summary>Signed running grade of segment i, as rise/run (positive = rising forward).</summary>
        public double SegmentGrade(int i)
        {
            double d = SegmentLength(i);
            return d < 1e-9 ? 0.0 : (Stations[i + 1].Point.Z - Stations[i].Point.Z) / d;
        }
    }
}
