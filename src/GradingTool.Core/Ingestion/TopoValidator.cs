using System;
using System.Collections.Generic;
using System.Linq;
using GradingTool.Geometry;

namespace GradingTool.Ingestion
{
    /// <summary>
    /// Checks a <see cref="SurfaceInput"/> for the problems that ruin a TIN before it is
    /// built: too few or too sparse points, large data gaps, vertical spikes, a missing or
    /// non-Texas CRS, and duplicate XY with conflicting elevation.
    /// <para>
    /// The result is an <see cref="IngestionReport"/>. Errors mean "do not triangulate";
    /// warnings mean "review, then decide". Nothing here silently discards data.
    /// </para>
    /// </summary>
    public static class TopoValidator
    {
        /// <summary>Tunable thresholds for the checks.</summary>
        public sealed class Options
        {
            /// <summary>Below this many points, a surface is meaningless. Default 3.</summary>
            public int MinPoints { get; set; } = 3;

            /// <summary>
            /// A point whose nearest neighbour is more than this multiple of the median
            /// nearest-neighbour spacing is flagged as isolated (a gap). Default 8x.
            /// </summary>
            public double GapSpacingFactor { get; set; } = 8.0;

            /// <summary>
            /// A point whose elevation differs from its neighbours' median by more than this
            /// many feet is flagged as a probable spike/bust. Default 25 ft.
            /// </summary>
            public double SpikeElevationFt { get; set; } = 25.0;

            /// <summary>XY tolerance (ft) for treating two points as the same location. Default 0.01.</summary>
            public double DuplicateXyToleranceFt { get; set; } = 0.01;
        }

        /// <summary>Validate a dataset.</summary>
        public static IngestionReport Validate(SurfaceInput input, Options? options = null)
        {
            var opt = options ?? new Options();
            var report = new IngestionReport();
            var pts = input.Points;

            // ---- CRS / units --------------------------------------------------------------
            if (input.Crs == null)
                report.Add(FindingSeverity.Error, "crs",
                    $"{input.Name} has no coordinate reference system. Assign a Texas State Plane " +
                    "zone before merging or building - an unreferenced dataset cannot be aligned.");
            else if (!input.Crs.IsTexasStatePlane)
                report.Add(FindingSeverity.Warning, "crs",
                    $"{input.Name} CRS is {input.Crs}, not a Texas State Plane NAD83 zone. Confirm the project location.");

            // SurfaceInput is always in project units by construction, but record what the
            // source was so a metre-origin dataset is visible in the report.
            report.Add(FindingSeverity.Info, "units",
                $"{input.Name} ingested from {Units.LinearUnits.Describe(input.SourceUnit)}; " +
                $"stored in {Units.LinearUnits.Describe(Units.LinearUnits.ProjectUnit)}.");

            // ---- point count --------------------------------------------------------------
            if (pts.Count < opt.MinPoints)
            {
                report.Add(FindingSeverity.Error, "density",
                    $"{input.Name} has only {pts.Count} point(s); need at least {opt.MinPoints} to triangulate.");
                return report; // nothing else is meaningful
            }

            // ---- duplicate XY with conflicting Z -----------------------------------------
            var seen = new Dictionary<(long, long), double>();
            double scale = Math.Max(opt.DuplicateXyToleranceFt, 1e-9);
            int conflicts = 0;
            foreach (var p in pts)
            {
                var key = ((long)Math.Round(p.X / scale), (long)Math.Round(p.Y / scale));
                if (seen.TryGetValue(key, out double z0))
                {
                    if (Math.Abs(z0 - p.Z) > opt.SpikeElevationFt)
                        conflicts++;
                }
                else seen[key] = p.Z;
            }
            if (conflicts > 0)
                report.Add(FindingSeverity.Warning, "duplicate",
                    $"{input.Name}: {conflicts} location(s) have duplicate XY with elevations differing " +
                    $"by more than {opt.SpikeElevationFt} ft. A TIN cannot be vertical; the first Z will win.");

            // ---- nearest-neighbour spacing: density + gaps -------------------------------
            double[] nn = NearestNeighbourDistances(pts);
            double median = Median(nn.Where(d => d > 0).ToArray());
            if (median <= 0)
            {
                report.Add(FindingSeverity.Error, "density",
                    $"{input.Name}: points are coincident or collinear; cannot triangulate.");
                return report;
            }

            int gapPoints = nn.Count(d => d > median * opt.GapSpacingFactor);
            if (gapPoints > 0)
                report.Add(FindingSeverity.Warning, "gap",
                    $"{input.Name}: {gapPoints} point(s) sit more than {opt.GapSpacingFactor:G}x the median " +
                    $"spacing ({median:F1} ft) from any neighbour - likely data gaps; the surface will " +
                    "interpolate across them.");

            report.Add(FindingSeverity.Info, "density",
                $"{input.Name}: {pts.Count} points, median spacing {median:F1} ft.");

            // ---- vertical spikes ----------------------------------------------------------
            int spikes = CountSpikes(pts, opt.SpikeElevationFt);
            if (spikes > 0)
                report.Add(FindingSeverity.Warning, "spike",
                    $"{input.Name}: {spikes} point(s) differ from their local neighbours by more than " +
                    $"{opt.SpikeElevationFt} ft in elevation - probable survey busts. Review before building.");

            return report;
        }

        // Brute-force nearest neighbour: fine for the point counts a single feature-line
        // grading job carries. A DEM tile with millions of points would need a spatial index;
        // that is noted where the DEM reader is stubbed.
        private static double[] NearestNeighbourDistances(IReadOnlyList<Point3d> pts)
        {
            var result = new double[pts.Count];
            for (int i = 0; i < pts.Count; i++)
            {
                double best = double.PositiveInfinity;
                for (int j = 0; j < pts.Count; j++)
                {
                    if (i == j) continue;
                    double d = pts[i].HorizontalDistanceTo(pts[j]);
                    if (d < best) best = d;
                }
                result[i] = double.IsInfinity(best) ? 0 : best;
            }
            return result;
        }

        private static int CountSpikes(IReadOnlyList<Point3d> pts, double thresholdFt)
        {
            int spikes = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                // Compare each point to the median elevation of its k nearest neighbours.
                var neighbours = pts
                    .Select((p, j) => (dist: pts[i].HorizontalDistanceTo(p), z: p.Z, j))
                    .Where(t => t.j != i)
                    .OrderBy(t => t.dist)
                    .Take(6)
                    .Select(t => t.z)
                    .ToArray();
                if (neighbours.Length == 0) continue;
                double localMedian = Median(neighbours);
                if (Math.Abs(pts[i].Z - localMedian) > thresholdFt) spikes++;
            }
            return spikes;
        }

        private static double Median(double[] values)
        {
            if (values.Length == 0) return 0;
            double[] sorted = (double[])values.Clone();
            Array.Sort(sorted);
            int mid = sorted.Length / 2;
            return sorted.Length % 2 == 1 ? sorted[mid] : 0.5 * (sorted[mid - 1] + sorted[mid]);
        }
    }
}
