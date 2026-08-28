using System;
using Autodesk.Civil.DatabaseServices;
using GradingTool.Surface;
using CadExtents = Autodesk.AutoCAD.DatabaseServices.Extents3d;

namespace GradingTool.Civil3D
{
    /// <summary>
    /// Adapts a live Civil 3D <see cref="TinSurface"/> to the engine's <see cref="ISurface"/>
    /// contract. This is the production half of the hybrid: the solver and graders were built
    /// and unit-tested against the managed <c>TinSurface</c> on the dev box, and in Civil 3D
    /// they run against this adapter instead - same interface, same results, but now backed by
    /// the exact surface the engineer sees on screen (Civil 3D's own triangulation and
    /// boundaries), so there is no fidelity gap.
    /// <para>
    /// The wrapped surface must be open for read inside an active transaction whenever this
    /// adapter's methods are called; the adapter does not manage the transaction.
    /// </para>
    /// <para>
    /// Elevation comes from <c>FindElevationAtXY</c> (stable across Civil 3D releases); a
    /// query outside the surface throws, which is treated as "no data" (null). Slope is taken
    /// by a small central-difference stencil around the point, which needs no triangle-level
    /// API and matches the TIN's own planar slope to within the stencil size.
    /// </para>
    /// </summary>
    public sealed class Civil3DSurface : ISurface
    {
        private readonly TinSurface _surface;
        private readonly double _stencilFt;

        /// <summary>Wrap a Civil 3D TIN surface.</summary>
        /// <param name="surface">The surface, open for read in the caller's transaction.</param>
        /// <param name="slopeStencilFt">Central-difference half-step for slope, in feet. Default 1.</param>
        public Civil3DSurface(TinSurface surface, double slopeStencilFt = 1.0)
        {
            _surface = surface ?? throw new ArgumentNullException(nameof(surface));
            _stencilFt = slopeStencilFt;
        }

        /// <inheritdoc />
        public string Name => _surface.Name;

        /// <inheritdoc />
        public double? ElevationAt(double x, double y)
        {
            try
            {
                return _surface.FindElevationAtXY(x, y);
            }
            catch
            {
                // FindElevationAtXY throws when (x,y) is outside the surface. That is a valid
                // "no data here" answer for the solver, not an error.
                return null;
            }
        }

        /// <inheritdoc />
        public SlopeSample? SlopeAt(double x, double y)
        {
            double h = _stencilFt;
            double? zxp = ElevationAt(x + h, y);
            double? zxm = ElevationAt(x - h, y);
            double? zyp = ElevationAt(x, y + h);
            double? zym = ElevationAt(x, y - h);
            if (zxp == null || zxm == null || zyp == null || zym == null)
                return null; // stencil ran off the surface edge

            double gx = (zxp.Value - zxm.Value) / (2 * h); // rise/run in +X
            double gy = (zyp.Value - zym.Value) / (2 * h); // rise/run in +Y
            double slope = Math.Sqrt(gx * gx + gy * gy);
            double aspect = slope < 1e-12
                ? double.NaN
                : Mod360(RadToDeg(Math.Atan2(-gx, -gy))); // downslope bearing, cw from north
            return new SlopeSample(slope * 100.0, aspect);
        }

        /// <inheritdoc />
        public (double MinX, double MinY, double MaxX, double MaxY) Extents
        {
            get
            {
                CadExtents e = _surface.GeometricExtents;
                return (e.MinPoint.X, e.MinPoint.Y, e.MaxPoint.X, e.MaxPoint.Y);
            }
        }

        /// <inheritdoc />
        public (double Min, double Max) ElevationRange
        {
            get
            {
                CadExtents e = _surface.GeometricExtents;
                return (e.MinPoint.Z, e.MaxPoint.Z);
            }
        }

        private static double RadToDeg(double r) => r * 180.0 / Math.PI;
        private static double Mod360(double d) { d %= 360.0; return d < 0 ? d + 360.0 : d; }
    }
}
