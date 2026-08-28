using System;
using GradingTool.Surface;
using CadExtents = Autodesk.AutoCAD.DatabaseServices.Extents3d;
// Aliased, not imported: Autodesk.Civil.DatabaseServices and GradingTool.Surface BOTH define a
// TinSurface, so importing both namespaces makes every bare reference ambiguous (CS0104). The
// alias names the Civil 3D one explicitly and leaves the unqualified name to the engine's.
using C3dTinSurface = Autodesk.Civil.DatabaseServices.TinSurface;

namespace GradingTool.Civil3D
{
    /// <summary>
    /// Adapts a live Civil 3D <see cref="C3dTinSurface"/> to the engine's <see cref="ISurface"/>
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
    /// by <see cref="SlopeStencil"/>, a small central-difference stencil that needs no
    /// triangle-level API and matches the TIN's own planar slope to within the stencil size.
    /// That helper lives in Core so it is covered by the test suite, which this project - being
    /// uncompilable without Civil 3D - is not.
    /// </para>
    /// </summary>
    public sealed class Civil3DSurface : ISurface
    {
        private readonly C3dTinSurface _surface;
        private readonly double _stencilFt;

        /// <summary>Wrap a Civil 3D TIN surface.</summary>
        /// <param name="surface">The surface, open for read in the caller's transaction.</param>
        /// <param name="slopeStencilFt">Central-difference half-step for slope, in feet. Default 1.</param>
        public Civil3DSurface(C3dTinSurface surface, double slopeStencilFt = 1.0)
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
        public SlopeSample? SlopeAt(double x, double y) =>
            SlopeStencil.CentralDifference(ElevationAt, x, y, _stencilFt);

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
    }
}
