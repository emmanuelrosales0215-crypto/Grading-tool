using System;
using System.Collections.Generic;
using GradingTool.Geometry;
using GradingTool.Surface;

namespace GradingTool.Grading
{
    /// <summary>The result of daylighting one edge point out to existing ground.</summary>
    public sealed class DaylightPoint
    {
        /// <summary>The proposed edge point the slope started from.</summary>
        public Point3d Origin { get; }

        /// <summary>Where the projected slope met existing ground (the daylight point).</summary>
        public Point3d Daylight { get; }

        /// <summary>Horizontal run from origin to daylight.</summary>
        public double RunFt { get; }

        /// <summary>True if existing ground was found within the search distance.</summary>
        public bool Reached { get; }

        /// <summary>Construct.</summary>
        public DaylightPoint(Point3d origin, Point3d daylight, double runFt, bool reached)
        {
            Origin = origin;
            Daylight = daylight;
            RunFt = runFt;
            Reached = reached;
        }
    }

    /// <summary>
    /// Projects a graded slope from the edge of a proposed pad out to existing ground - the
    /// "daylight" line where cut or fill returns to natural grade.
    /// <para>
    /// From each edge point the slope steps outward along a given direction at a fixed H:V
    /// ratio (e.g. 3:1). Fill steps down, cut steps up; the daylight point is where the
    /// projected elevation crosses the existing surface. This is the geometry that ties a
    /// proposed surface into the site and bounds the graded area.
    /// </para>
    /// </summary>
    public static class Daylight
    {
        /// <summary>
        /// Daylight one edge point to existing ground.
        /// </summary>
        /// <param name="origin">Proposed edge point (its Z is the finished elevation).</param>
        /// <param name="bearingDegrees">Direction to project, clockwise from north.</param>
        /// <param name="slopeRatioHtoV">H:V ratio of the graded slope, e.g. 3 for 3:1.</param>
        /// <param name="existing">Existing ground.</param>
        /// <param name="step">Search step in feet (default 1).</param>
        /// <param name="maxRun">Maximum horizontal run to search before giving up (default 500).</param>
        public static DaylightPoint Project(
            Point3d origin, double bearingDegrees, double slopeRatioHtoV,
            ISurface existing, double step = 1.0, double maxRun = 500.0)
        {
            if (slopeRatioHtoV <= 0) throw new ArgumentOutOfRangeException(nameof(slopeRatioHtoV));
            double rad = bearingDegrees * Math.PI / 180.0;
            double dx = Math.Sin(rad), dy = Math.Cos(rad);
            double gradePerFt = 1.0 / slopeRatioHtoV; // rise/run

            double? egAtOrigin = existing.ElevationAt(origin.X, origin.Y);
            // Fill if the pad sits above existing (slope goes down to meet it); cut if below.
            // If existing is unknown at the origin, assume fill (project downward).
            double direction = (egAtOrigin.HasValue && origin.Z < egAtOrigin.Value) ? +1.0 : -1.0;

            double prevDiff = egAtOrigin.HasValue ? origin.Z - egAtOrigin.Value : double.NaN;
            for (double run = step; run <= maxRun; run += step)
            {
                double x = origin.X + dx * run;
                double y = origin.Y + dy * run;
                double projectedZ = origin.Z + direction * gradePerFt * run;
                double? eg = existing.ElevationAt(x, y);
                if (eg == null) continue;

                double diff = projectedZ - eg.Value; // proposed - existing
                // Daylight where the projected surface crosses existing (sign change of diff).
                if (!double.IsNaN(prevDiff) && Math.Sign(diff) != Math.Sign(prevDiff) && prevDiff != 0)
                {
                    // Linear interpolation to the crossing for a cleaner daylight point.
                    double frac = Math.Abs(prevDiff) / (Math.Abs(prevDiff) + Math.Abs(diff));
                    double runX = run - step + step * frac;
                    double px = origin.X + dx * runX;
                    double py = origin.Y + dy * runX;
                    double pz = origin.Z + direction * gradePerFt * runX;
                    return new DaylightPoint(origin, new Point3d(px, py, pz), runX, reached: true);
                }
                prevDiff = diff;
            }

            // Never met existing ground within the search distance.
            double ex = origin.X + dx * maxRun, ey = origin.Y + dy * maxRun;
            double ez = origin.Z + direction * gradePerFt * maxRun;
            return new DaylightPoint(origin, new Point3d(ex, ey, ez), maxRun, reached: false);
        }

        /// <summary>Daylight a whole edge polyline, one point per vertex.</summary>
        public static IReadOnlyList<DaylightPoint> ProjectEdge(
            IReadOnlyList<Point3d> edge, double bearingDegrees, double slopeRatioHtoV,
            ISurface existing, double step = 1.0, double maxRun = 500.0)
        {
            var outp = new List<DaylightPoint>(edge.Count);
            foreach (var p in edge)
                outp.Add(Project(p, bearingDegrees, slopeRatioHtoV, existing, step, maxRun));
            return outp;
        }
    }
}
