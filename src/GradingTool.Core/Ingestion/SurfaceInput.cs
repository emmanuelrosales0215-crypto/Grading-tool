using System;
using System.Collections.Generic;
using System.Linq;
using GradingTool.Diagnostics;
using GradingTool.Geometry;
using GradingTool.Units;

namespace GradingTool.Ingestion
{
    /// <summary>
    /// A normalized topographic dataset ready to become a TIN surface: the points (and any
    /// breaklines), tagged with the CRS and the unit they are now in.
    /// <para>
    /// A <see cref="SurfaceInput"/> is always in the project working unit
    /// (<see cref="LinearUnits.ProjectUnit"/>, US survey feet). A reader that receives data
    /// in metres or international feet must convert during construction via
    /// <see cref="FromSourcePoints"/>, which logs the conversion. Points are never stored in
    /// their raw source unit - that is what keeps "assume feet" from ever happening silently.
    /// </para>
    /// </summary>
    public sealed class SurfaceInput
    {
        /// <summary>Dataset name, usually derived from the source file.</summary>
        public string Name { get; }

        /// <summary>Source file path or origin description, for provenance.</summary>
        public string Source { get; }

        /// <summary>Points, in the project working unit and CRS.</summary>
        public IReadOnlyList<Point3d> Points { get; }

        /// <summary>Breakline polylines, in the project working unit and CRS. May be empty.</summary>
        public IReadOnlyList<IReadOnlyList<Point3d>> Breaklines { get; }

        /// <summary>
        /// The dataset's CRS. Null means the source did not declare one and none was assigned
        /// yet - which validation flags, because an unreferenced dataset cannot be safely
        /// merged with others.
        /// </summary>
        public CoordinateReferenceSystem? Crs { get; }

        /// <summary>The unit the source file declared, before conversion. For the record.</summary>
        public LinearUnit SourceUnit { get; }

        private SurfaceInput(
            string name, string source, IReadOnlyList<Point3d> points,
            IReadOnlyList<IReadOnlyList<Point3d>> breaklines,
            CoordinateReferenceSystem? crs, LinearUnit sourceUnit)
        {
            Name = name;
            Source = source;
            Points = points;
            Breaklines = breaklines;
            Crs = crs;
            SourceUnit = sourceUnit;
        }

        /// <summary>
        /// Build a <see cref="SurfaceInput"/> from raw source points, converting from the
        /// declared source unit into the project working unit and logging what was done.
        /// </summary>
        /// <param name="name">Dataset name.</param>
        /// <param name="source">Source path / origin, for provenance and log context.</param>
        /// <param name="rawPoints">Points as read, in <paramref name="sourceUnit"/>.</param>
        /// <param name="sourceUnit">The unit the source declared.</param>
        /// <param name="crs">Declared CRS, or null if the source gave none.</param>
        /// <param name="log">Diagnostics sink; unit conversion is recorded here.</param>
        /// <param name="rawBreaklines">Optional breakline polylines in the same source unit.</param>
        public static SurfaceInput FromSourcePoints(
            string name,
            string source,
            IEnumerable<Point3d> rawPoints,
            LinearUnit sourceUnit,
            CoordinateReferenceSystem? crs,
            IGradingLog? log = null,
            IEnumerable<IReadOnlyList<Point3d>>? rawBreaklines = null)
        {
            IGradingLog sink = log ?? NullGradingLog.Instance;

            List<Point3d> converted = rawPoints.Select(p => ConvertPoint(p, sourceUnit)).ToList();

            var breaklines = new List<IReadOnlyList<Point3d>>();
            if (rawBreaklines != null)
                foreach (var line in rawBreaklines)
                    breaklines.Add(line.Select(p => ConvertPoint(p, sourceUnit)).ToList());

            if (sourceUnit == LinearUnits.ProjectUnit)
            {
                sink.Info($"{source}: {converted.Count} point(s) already in {LinearUnits.Describe(sourceUnit)}; no conversion.");
            }
            else
            {
                double factor = LinearUnits.MetersPer(sourceUnit) / LinearUnits.MetersPer(LinearUnits.ProjectUnit);
                sink.Info(
                    $"{source}: converted {converted.Count} point(s) from {LinearUnits.Describe(sourceUnit)} " +
                    $"to {LinearUnits.Describe(LinearUnits.ProjectUnit)} (factor {factor:G17}).");
            }

            return new SurfaceInput(name, source, converted, breaklines, crs, sourceUnit);
        }

        private static Point3d ConvertPoint(Point3d p, LinearUnit from)
            => from == LinearUnits.ProjectUnit
                ? p
                : new Point3d(
                    LinearUnits.Convert(p.X, from, LinearUnits.ProjectUnit),
                    LinearUnits.Convert(p.Y, from, LinearUnits.ProjectUnit),
                    LinearUnits.Convert(p.Z, from, LinearUnits.ProjectUnit));

        /// <summary>A copy of this input reassigned to a CRS it was missing.</summary>
        public SurfaceInput WithAssignedCrs(CoordinateReferenceSystem crs, IGradingLog? log = null)
        {
            (log ?? NullGradingLog.Instance).Info(
                $"{Source}: CRS assigned as {crs} (source declared none).");
            return new SurfaceInput(Name, Source, Points, Breaklines, crs, SourceUnit);
        }
    }
}
