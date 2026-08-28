using System;
using System.Collections.Generic;

namespace GradingTool.Surface
{
    /// <summary>
    /// A 2D Delaunay triangulation via the Bowyer-Watson incremental algorithm.
    /// <para>
    /// Point counts for feature-line grading are modest (hundreds to low thousands), so the
    /// straightforward O(n^2)-ish incremental insertion is fine. A million-point DEM tile
    /// would need a spatial index and a sweep-line or divide-and-conquer method - noted here
    /// and where the DEM reader is stubbed.
    /// </para>
    /// </summary>
    internal static class Delaunay
    {
        /// <summary>A triangle as three indices into the caller's point array.</summary>
        internal readonly struct Tri
        {
            public readonly int A, B, C;
            public Tri(int a, int b, int c) { A = a; B = b; C = c; }
        }

        /// <summary>
        /// Triangulate points given as parallel X/Y arrays. Returns triangles as index
        /// triples into those arrays. Points must already be de-duplicated in XY.
        /// </summary>
        internal static List<Tri> Triangulate(double[] xs, double[] ys)
        {
            int n = xs.Length;
            if (n < 3) return new List<Tri>();

            // Super-triangle: big enough to contain every point. Its three vertices are
            // appended at indices n, n+1, n+2 and stripped at the end.
            double minX = xs[0], minY = ys[0], maxX = xs[0], maxY = ys[0];
            for (int i = 1; i < n; i++)
            {
                if (xs[i] < minX) minX = xs[i];
                if (ys[i] < minY) minY = ys[i];
                if (xs[i] > maxX) maxX = xs[i];
                if (ys[i] > maxY) maxY = ys[i];
            }
            double dx = maxX - minX, dy = maxY - minY;
            double dmax = Math.Max(dx, dy);
            if (dmax <= 0) return new List<Tri>(); // all coincident
            double midX = (minX + maxX) / 2, midY = (minY + maxY) / 2;

            var px = new double[n + 3];
            var py = new double[n + 3];
            Array.Copy(xs, px, n);
            Array.Copy(ys, py, n);
            // Generous margin so no real point lies on or outside the super-triangle.
            px[n] = midX - 20 * dmax; py[n] = midY - dmax;
            px[n + 1] = midX; py[n + 1] = midY + 20 * dmax;
            px[n + 2] = midX + 20 * dmax; py[n + 2] = midY - dmax;

            var triangles = new List<Tri> { new Tri(n, n + 1, n + 2) };

            for (int i = 0; i < n; i++)
            {
                // Find triangles whose circumcircle contains point i; collect their edges.
                var badEdges = new List<(int u, int v)>();
                triangles.RemoveAll(t =>
                {
                    if (InCircumcircle(px, py, t, px[i], py[i]))
                    {
                        badEdges.Add((t.A, t.B));
                        badEdges.Add((t.B, t.C));
                        badEdges.Add((t.C, t.A));
                        return true;
                    }
                    return false;
                });

                // Edges on the boundary of the hole appear exactly once; shared edges cancel.
                foreach (var e in BoundaryEdges(badEdges))
                    triangles.Add(new Tri(e.u, e.v, i));
            }

            // Drop any triangle still touching a super-triangle vertex.
            triangles.RemoveAll(t => t.A >= n || t.B >= n || t.C >= n);
            return triangles;
        }

        // Unique edges: those not appearing in reverse elsewhere form the polygon boundary.
        private static IEnumerable<(int u, int v)> BoundaryEdges(List<(int u, int v)> edges)
        {
            for (int i = 0; i < edges.Count; i++)
            {
                bool shared = false;
                for (int j = 0; j < edges.Count; j++)
                {
                    if (i == j) continue;
                    if ((edges[i].u == edges[j].u && edges[i].v == edges[j].v) ||
                        (edges[i].u == edges[j].v && edges[i].v == edges[j].u))
                    {
                        shared = true;
                        break;
                    }
                }
                if (!shared) yield return edges[i];
            }
        }

        // True if d is strictly inside the circumcircle of triangle t.
        private static bool InCircumcircle(double[] px, double[] py, Tri t, double dxp, double dyp)
        {
            double ax = px[t.A], ay = py[t.A];
            double bx = px[t.B], by = py[t.B];
            double cx = px[t.C], cy = py[t.C];

            // Orient CCW so the incircle determinant sign is consistent.
            if (Orient(ax, ay, bx, by, cx, cy) < 0)
            {
                double tx = bx, ty = by; bx = cx; by = cy; cx = tx; cy = ty;
            }

            double adx = ax - dxp, ady = ay - dyp;
            double bdx = bx - dxp, bdy = by - dyp;
            double cdx = cx - dxp, cdy = cy - dyp;

            double abDet = adx * bdy - bdx * ady;
            double bcDet = bdx * cdy - cdx * bdy;
            double caDet = cdx * ady - adx * cdy;
            double aLift = adx * adx + ady * ady;
            double bLift = bdx * bdx + bdy * bdy;
            double cLift = cdx * cdx + cdy * cdy;

            return aLift * bcDet + bLift * caDet + cLift * abDet > 0;
        }

        // Twice the signed area of (a,b,c); >0 CCW, <0 CW.
        private static double Orient(double ax, double ay, double bx, double by, double cx, double cy)
            => (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
    }
}
