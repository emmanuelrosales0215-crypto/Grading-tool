using System;
using System.Collections.Generic;
using System.Linq;
using GradingTool.Geometry;
using GradingTool.Ingestion;

namespace GradingTool.Surface
{
    /// <summary>
    /// A triangulated irregular network built and queried entirely in managed code, so the
    /// grading solver can be exercised without Civil 3D. Ported from the proven approach in
    /// the sitegrade Python: Delaunay triangulation, breakline densification, hull-sliver
    /// removal, optional boundary clip, barycentric interpolation, and per-triangle slope.
    /// <para>
    /// This is an <em>approximation</em> of Civil 3D's own TIN, not a bit-identical copy - it
    /// converges to the same surface but may split an ambiguous quad differently. For slope
    /// compliance that is immaterial (results agree well within the 0.3% design margin). In
    /// production the add-in supplies a Civil3DSurface implementing the same
    /// <see cref="ISurface"/> contract, for exact on-screen fidelity.
    /// </para>
    /// </summary>
    public sealed class TinSurface : ISurface
    {
        private readonly Point3d[] _points;
        private readonly int[][] _triangles;   // each is a length-3 index triple

        /// <inheritdoc />
        public string Name { get; }

        /// <summary>The surface vertices.</summary>
        public IReadOnlyList<Point3d> Points => _points;

        /// <summary>The triangles, as index triples into <see cref="Points"/>.</summary>
        public IReadOnlyList<int[]> Triangles => _triangles;

        private TinSurface(string name, Point3d[] points, int[][] triangles)
        {
            Name = name;
            _points = points;
            _triangles = triangles;
        }

        /// <summary>
        /// Triangles whose longest edge exceeds this multiple of the median edge length are
        /// treated as hull slivers spanning a concave gap and removed. Matches sitegrade.
        /// </summary>
        public const double DefaultSliverFactor = 4.0;

        /// <summary>Build a TIN from an ingested, validated dataset.</summary>
        /// <param name="input">The normalized dataset (already in project units + CRS).</param>
        /// <param name="boundary">
        /// Optional clip polygon (XY); triangles whose centroid falls outside are dropped, so
        /// the surface gets a real edge instead of the convex hull of the survey.
        /// </param>
        /// <param name="sliverFactor">Sliver threshold, or null to skip sliver removal.</param>
        /// <param name="breaklineSpacing">
        /// Densification interval for breaklines, or null to auto-pick from the data extent.
        /// </param>
        public static TinSurface FromInput(
            SurfaceInput input,
            IReadOnlyList<Point3d>? boundary = null,
            double? sliverFactor = DefaultSliverFactor,
            double? breaklineSpacing = null)
        {
            var cloud = new List<Point3d>(input.Points);

            // Breaklines are honoured by densifying them and inserting their vertices before
            // triangulation. Not a true constrained Delaunay, but it converges to the same
            // surface as the densification gets finer - the sitegrade approximation.
            if (input.Breaklines.Count > 0)
            {
                double spacing = breaklineSpacing ?? SuggestSpacing(input.Points);
                foreach (var line in input.Breaklines)
                    cloud.AddRange(Densify(line, spacing));
            }

            return FromPoints(input.Name, cloud, boundary, sliverFactor);
        }

        /// <summary>Build a TIN directly from points (used by tests and grid inputs).</summary>
        public static TinSurface FromPoints(
            string name,
            IReadOnlyList<Point3d> points,
            IReadOnlyList<Point3d>? boundary = null,
            double? sliverFactor = DefaultSliverFactor)
        {
            Point3d[] cloud = Dedupe(points);
            if (cloud.Length < 3)
                throw new ArgumentException(
                    $"Need at least 3 distinct points to build a surface, got {cloud.Length}.", nameof(points));

            double[] xs = cloud.Select(p => p.X).ToArray();
            double[] ys = cloud.Select(p => p.Y).ToArray();
            List<Delaunay.Tri> tris = Delaunay.Triangulate(xs, ys);
            if (tris.Count == 0)
                throw new ArgumentException("Could not triangulate - points may be collinear.", nameof(points));

            var triangles = tris.Select(t => new[] { t.A, t.B, t.C }).ToList();
            if (sliverFactor.HasValue)
                triangles = DropSlivers(cloud, triangles, sliverFactor.Value);
            if (boundary != null && boundary.Count >= 3)
                triangles = ClipToBoundary(cloud, triangles, boundary);
            if (triangles.Count == 0)
                throw new ArgumentException("No triangles survived filtering - check the boundary polygon.", nameof(points));

            return new TinSurface(name, cloud, triangles.ToArray());
        }

        // -- ISurface ---------------------------------------------------------------------

        /// <inheritdoc />
        public double? ElevationAt(double x, double y)
        {
            int t = LocateTriangle(x, y, out double wa, out double wb, out double wc);
            if (t < 0) return null;
            int[] tri = _triangles[t];
            return wa * _points[tri[0]].Z + wb * _points[tri[1]].Z + wc * _points[tri[2]].Z;
        }

        /// <inheritdoc />
        public SlopeSample? SlopeAt(double x, double y)
        {
            int t = LocateTriangle(x, y, out _, out _, out _);
            return t < 0 ? (SlopeSample?)null : TriangleSlope(_triangles[t]);
        }

        /// <inheritdoc />
        public (double MinX, double MinY, double MaxX, double MaxY) Extents
        {
            get
            {
                double minX = _points[0].X, minY = _points[0].Y, maxX = minX, maxY = minY;
                foreach (var p in _points)
                {
                    if (p.X < minX) minX = p.X; if (p.Y < minY) minY = p.Y;
                    if (p.X > maxX) maxX = p.X; if (p.Y > maxY) maxY = p.Y;
                }
                return (minX, minY, maxX, maxY);
            }
        }

        /// <inheritdoc />
        public (double Min, double Max) ElevationRange
        {
            get
            {
                double min = _points[0].Z, max = min;
                foreach (var p in _points) { if (p.Z < min) min = p.Z; if (p.Z > max) max = p.Z; }
                return (min, max);
            }
        }

        /// <summary>Slope of one triangle, as percent grade and downhill bearing.</summary>
        public SlopeSample SlopeForTriangle(int index) => TriangleSlope(_triangles[index]);

        /// <summary>
        /// The plane gradient (dz/dx, dz/dy) of a triangle as rise/run. Used by the 2D
        /// surface grader to decompose slope into running and cross components along a flow
        /// direction. (0,0) for a flat or degenerate triangle.
        /// </summary>
        public (double Gx, double Gy) GradientForTriangle(int index)
        {
            int[] tri = _triangles[index];
            Point3d a = _points[tri[0]], b = _points[tri[1]], c = _points[tri[2]];
            double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
            double vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
            double nx = uy * vz - uz * vy;
            double ny = uz * vx - ux * vz;
            double nz = ux * vy - uy * vx;
            return Math.Abs(nz) < 1e-12 ? (0.0, 0.0) : (-nx / nz, -ny / nz);
        }

        /// <summary>Plan centroid (XY) of a triangle, for locating a finding on the surface.</summary>
        public Point3d CentroidOf(int index)
        {
            int[] t = _triangles[index];
            return new Point3d(
                (_points[t[0]].X + _points[t[1]].X + _points[t[2]].X) / 3.0,
                (_points[t[0]].Y + _points[t[1]].Y + _points[t[2]].Y) / 3.0,
                (_points[t[0]].Z + _points[t[1]].Z + _points[t[2]].Z) / 3.0);
        }

        /// <summary>Plan (horizontal-projected) area of a triangle.</summary>
        public double PlanAreaOf(int index)
        {
            int[] t = _triangles[index];
            Point3d a = _points[t[0]], b = _points[t[1]], c = _points[t[2]];
            return 0.5 * Math.Abs((b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y));
        }

        // -- geometry helpers -------------------------------------------------------------

        private SlopeSample TriangleSlope(int[] tri)
        {
            Point3d a = _points[tri[0]], b = _points[tri[1]], c = _points[tri[2]];
            // Plane normal from two edge vectors.
            double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
            double vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
            double nx = uy * vz - uz * vy;
            double ny = uz * vx - ux * vz;
            double nz = ux * vy - uy * vx;

            if (Math.Abs(nz) < 1e-12)
                return new SlopeSample(0.0, double.NaN); // vertical/degenerate: report flat

            // Gradient of the plane; slope magnitude is rise/run.
            double gx = -nx / nz;
            double gy = -ny / nz;
            double slope = Math.Sqrt(gx * gx + gy * gy);
            double aspect = slope < 1e-12
                ? double.NaN
                : Mod360(RadToDeg(Math.Atan2(-gx, -gy))); // downslope bearing, cw from north
            return new SlopeSample(slope * 100.0, aspect);
        }

        // Linear scan for the containing triangle, returning barycentric weights. Fine for
        // grading point counts; a bucket grid or the triangulation's walk would speed this up
        // for very large surfaces (noted for the DEM path).
        private int LocateTriangle(double x, double y, out double wa, out double wb, out double wc)
        {
            for (int i = 0; i < _triangles.Length; i++)
            {
                int[] t = _triangles[i];
                if (Barycentric(x, y, _points[t[0]], _points[t[1]], _points[t[2]], out wa, out wb, out wc))
                    return i;
            }
            wa = wb = wc = 0;
            return -1;
        }

        private static bool Barycentric(double x, double y, Point3d a, Point3d b, Point3d c,
            out double wa, out double wb, out double wc)
        {
            double v0x = b.X - a.X, v0y = b.Y - a.Y;
            double v1x = c.X - a.X, v1y = c.Y - a.Y;
            double v2x = x - a.X, v2y = y - a.Y;
            double den = v0x * v1y - v1x * v0y;
            if (Math.Abs(den) < 1e-15) { wa = wb = wc = 0; return false; }
            wb = (v2x * v1y - v1x * v2y) / den;
            wc = (v0x * v2y - v2x * v0y) / den;
            wa = 1.0 - wb - wc;
            const double eps = -1e-9; // small tolerance so edge/vertex hits count as inside
            return wa >= eps && wb >= eps && wc >= eps;
        }

        // -- construction helpers ---------------------------------------------------------

        private static Point3d[] Dedupe(IReadOnlyList<Point3d> points, double tolerance = 1e-6)
        {
            // Drop points sharing an XY location; the first elevation wins so results are
            // deterministic (a TIN cannot represent a vertical wall of duplicate XY).
            var seen = new HashSet<(long, long)>();
            double scale = Math.Max(tolerance, 1e-12);
            var outp = new List<Point3d>(points.Count);
            foreach (var p in points)
            {
                var key = ((long)Math.Round(p.X / scale), (long)Math.Round(p.Y / scale));
                if (seen.Add(key)) outp.Add(p);
            }
            return outp.ToArray();
        }

        private static IEnumerable<Point3d> Densify(IReadOnlyList<Point3d> line, double maxSpacing)
        {
            if (line.Count < 2 || maxSpacing <= 0) { foreach (var p in line) yield return p; yield break; }
            yield return line[0];
            for (int i = 1; i < line.Count; i++)
            {
                Point3d s = line[i - 1], e = line[i];
                double len = s.HorizontalDistanceTo(e);
                int steps = Math.Max(1, (int)Math.Ceiling(len / maxSpacing));
                for (int k = 1; k <= steps; k++)
                {
                    double f = (double)k / steps;
                    yield return new Point3d(
                        s.X + (e.X - s.X) * f, s.Y + (e.Y - s.Y) * f, s.Z + (e.Z - s.Z) * f);
                }
            }
        }

        private static double SuggestSpacing(IReadOnlyList<Point3d> points)
        {
            double minX = points[0].X, minY = points[0].Y, maxX = minX, maxY = minY;
            foreach (var p in points)
            {
                if (p.X < minX) minX = p.X; if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X; if (p.Y > maxY) maxY = p.Y;
            }
            double diag = Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY));
            return Math.Max(diag / 200.0, 1e-6);
        }

        private static List<int[]> DropSlivers(Point3d[] pts, List<int[]> tris, double factor)
        {
            if (tris.Count == 0) return tris;
            double[] longest = tris.Select(t => LongestEdge(pts, t)).ToArray();
            double median = Median(longest);
            if (median <= 0) return tris;
            var kept = new List<int[]>(tris.Count);
            for (int i = 0; i < tris.Count; i++)
                if (longest[i] <= median * factor) kept.Add(tris[i]);
            return kept;
        }

        private static double LongestEdge(Point3d[] pts, int[] t)
        {
            double e0 = pts[t[0]].HorizontalDistanceTo(pts[t[1]]);
            double e1 = pts[t[1]].HorizontalDistanceTo(pts[t[2]]);
            double e2 = pts[t[2]].HorizontalDistanceTo(pts[t[0]]);
            return Math.Max(e0, Math.Max(e1, e2));
        }

        private static List<int[]> ClipToBoundary(Point3d[] pts, List<int[]> tris, IReadOnlyList<Point3d> boundary)
            => tris.Where(t =>
            {
                double cx = (pts[t[0]].X + pts[t[1]].X + pts[t[2]].X) / 3.0;
                double cy = (pts[t[0]].Y + pts[t[1]].Y + pts[t[2]].Y) / 3.0;
                return PointInPolygon(cx, cy, boundary);
            }).ToList();

        private static bool PointInPolygon(double x, double y, IReadOnlyList<Point3d> poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                double xi = poly[i].X, yi = poly[i].Y, xj = poly[j].X, yj = poly[j].Y;
                bool intersect = ((yi > y) != (yj > y)) &&
                                 (x < (xj - xi) * (y - yi) / (yj - yi + double.Epsilon) + xi);
                if (intersect) inside = !inside;
            }
            return inside;
        }

        private static double Median(double[] values)
        {
            if (values.Length == 0) return 0;
            double[] s = (double[])values.Clone();
            Array.Sort(s);
            int m = s.Length / 2;
            return s.Length % 2 == 1 ? s[m] : 0.5 * (s[m - 1] + s[m]);
        }

        private static double RadToDeg(double r) => r * 180.0 / Math.PI;
        private static double Mod360(double d) { d %= 360.0; return d < 0 ? d + 360.0 : d; }
    }
}
