using System;

namespace GradingTool.Surface
{
    /// <summary>
    /// An <see cref="ISurface"/> backed by a caller-supplied elevation function, so a host that
    /// can answer "what is the elevation here?" can drive the grading engine without writing a
    /// managed adapter class of its own.
    /// <para>
    /// The motivating host is a Dynamo Python Script node inside Civil 3D. Dynamo can load this
    /// assembly directly (Core is <c>netstandard2.0</c> and references no Autodesk assembly), so
    /// a graph can run the real, tested solver against a live Civil 3D surface with no Windows
    /// build step at all - the Python closure just calls <c>TinSurface.FindElevationAtXY</c> and
    /// hands the result back. The compiled add-in remains the shipping form; this is the fast
    /// iteration loop, and the way to exercise the engine on a machine where the add-in cannot
    /// be compiled.
    /// </para>
    /// <para>
    /// <b>The interop-shaped choices here are deliberate.</b> The elevation callback returns a
    /// plain <see cref="double"/> and signals "outside the surface" with <see cref="Outside"/>
    /// (NaN) rather than returning <c>double?</c>, and the extents arrive as eight loose doubles
    /// rather than tuples. Marshalling <c>Nullable&lt;double&gt;</c> and <c>ValueTuple</c> across
    /// the Python/.NET boundary is the fragile part of PythonNet interop; plain doubles are not.
    /// </para>
    /// </summary>
    public sealed class DelegateSurface : ISurface
    {
        /// <summary>
        /// The value an elevation callback returns to mean "this point is outside the surface".
        /// NaN is used instead of a nullable so the callback can be a plain Python function.
        /// </summary>
        public const double Outside = double.NaN;

        private readonly Func<double, double, double> _elevationAt;
        private readonly double _stencilFt;
        private readonly (double MinX, double MinY, double MaxX, double MaxY) _extents;
        private readonly (double Min, double Max) _elevationRange;

        /// <summary>Wrap an elevation function as a surface.</summary>
        /// <param name="name">Surface name, for findings and logs.</param>
        /// <param name="elevationAt">
        /// Elevation lookup taking (x, y) and returning the elevation, or <see cref="Outside"/>
        /// (NaN) when the point is off the surface. The callback owns its own error handling -
        /// a host API that throws outside its extents (Civil 3D's <c>FindElevationAtXY</c> does)
        /// must catch that and return <see cref="Outside"/>.
        /// </param>
        /// <param name="minX">Plan extents, minimum easting.</param>
        /// <param name="minY">Plan extents, minimum northing.</param>
        /// <param name="maxX">Plan extents, maximum easting.</param>
        /// <param name="maxY">Plan extents, maximum northing.</param>
        /// <param name="minZ">Minimum elevation.</param>
        /// <param name="maxZ">Maximum elevation.</param>
        /// <param name="slopeStencilFt">
        /// Central-difference half-step for slope, in feet. Keep it small relative to the host
        /// surface's triangle size; see <see cref="SlopeStencil"/>.
        /// </param>
        public DelegateSurface(
            string name,
            Func<double, double, double> elevationAt,
            double minX,
            double minY,
            double maxX,
            double maxY,
            double minZ,
            double maxZ,
            double slopeStencilFt = SlopeStencil.DefaultHalfStepFt)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            _elevationAt = elevationAt ?? throw new ArgumentNullException(nameof(elevationAt));
            if (double.IsNaN(slopeStencilFt) || double.IsInfinity(slopeStencilFt) || slopeStencilFt <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(slopeStencilFt), slopeStencilFt, "The stencil half-step must be finite and positive.");
            if (maxX < minX || maxY < minY)
                throw new ArgumentException(
                    $"Plan extents are inverted: X [{minX}, {maxX}], Y [{minY}, {maxY}].", nameof(maxX));
            if (maxZ < minZ)
                throw new ArgumentException(
                    $"Elevation range is inverted: [{minZ}, {maxZ}].", nameof(maxZ));

            _stencilFt = slopeStencilFt;
            _extents = (minX, minY, maxX, maxY);
            _elevationRange = (minZ, maxZ);
        }

        /// <summary>
        /// Wrap another surface. Mostly useful in tests, to check that the delegate path and the
        /// stencil agree with a surface that knows its own triangles.
        /// </summary>
        /// <param name="inner">The surface to wrap.</param>
        /// <param name="slopeStencilFt">Central-difference half-step, in feet.</param>
        public static DelegateSurface Wrapping(ISurface inner, double slopeStencilFt = SlopeStencil.DefaultHalfStepFt)
        {
            if (inner == null) throw new ArgumentNullException(nameof(inner));
            var (minX, minY, maxX, maxY) = inner.Extents;
            var (minZ, maxZ) = inner.ElevationRange;
            return new DelegateSurface(
                inner.Name,
                (x, y) => inner.ElevationAt(x, y) ?? Outside,
                minX, minY, maxX, maxY, minZ, maxZ,
                slopeStencilFt);
        }

        /// <inheritdoc />
        public string Name { get; }

        /// <inheritdoc />
        public double? ElevationAt(double x, double y)
        {
            double z = _elevationAt(x, y);
            return double.IsNaN(z) ? (double?)null : z;
        }

        /// <inheritdoc />
        public SlopeSample? SlopeAt(double x, double y) =>
            SlopeStencil.CentralDifference(ElevationAt, x, y, _stencilFt);

        /// <inheritdoc />
        public (double MinX, double MinY, double MaxX, double MaxY) Extents => _extents;

        /// <inheritdoc />
        public (double Min, double Max) ElevationRange => _elevationRange;
    }
}
