using System;

namespace GradingTool.Surface
{
    /// <summary>
    /// Slope and aspect by a central-difference stencil around a point, for surfaces that can
    /// answer "what is the elevation here?" but expose no triangle-level API.
    /// <para>
    /// Civil 3D's own surface is the motivating case: <c>FindElevationAtXY</c> is stable across
    /// releases, while the triangle enumeration API is not, so the add-in derives slope from
    /// four elevation probes instead. On a TIN the result matches the containing triangle's
    /// planar slope exactly whenever the stencil stays inside that triangle, and degrades
    /// gracefully to an average across a break when it does not - so keep the half-step small
    /// relative to the triangle size.
    /// </para>
    /// <para>
    /// This lives in Core rather than in the add-in so it is covered by the test suite: the
    /// same math is used by <see cref="DelegateSurface"/>, which can be pointed at a managed
    /// <see cref="TinSurface"/> and checked against that surface's exact per-triangle slope.
    /// </para>
    /// </summary>
    public static class SlopeStencil
    {
        /// <summary>Default half-step for the stencil, in feet.</summary>
        public const double DefaultHalfStepFt = 1.0;

        /// <summary>
        /// Sample slope and aspect at (x, y) using four elevation probes a half-step away.
        /// </summary>
        /// <param name="elevationAt">
        /// Elevation lookup, returning null outside the surface. If any of the four probes
        /// lands outside, the stencil has run off the edge and the result is null.
        /// </param>
        /// <param name="x">Easting.</param>
        /// <param name="y">Northing.</param>
        /// <param name="halfStepFt">Half-step, in feet. Must be finite and positive.</param>
        /// <returns>The slope sample, or null if the stencil ran off the surface.</returns>
        public static SlopeSample? CentralDifference(
            Func<double, double, double?> elevationAt,
            double x,
            double y,
            double halfStepFt = DefaultHalfStepFt)
        {
            if (elevationAt == null) throw new ArgumentNullException(nameof(elevationAt));
            if (double.IsNaN(halfStepFt) || double.IsInfinity(halfStepFt) || halfStepFt <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(halfStepFt), halfStepFt, "The stencil half-step must be finite and positive.");

            double h = halfStepFt;
            double? zxp = elevationAt(x + h, y);
            double? zxm = elevationAt(x - h, y);
            double? zyp = elevationAt(x, y + h);
            double? zym = elevationAt(x, y - h);
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

        private static double RadToDeg(double r) => r * 180.0 / Math.PI;

        private static double Mod360(double d) { d %= 360.0; return d < 0 ? d + 360.0 : d; }
    }
}
