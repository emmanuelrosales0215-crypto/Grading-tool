using System;
using GradingTool.Geometry;

namespace GradingTool.Surface
{
    /// <summary>
    /// A slope reading at a point on a surface: the grade magnitude and the downhill bearing.
    /// </summary>
    public readonly struct SlopeSample
    {
        /// <summary>Slope magnitude as a percent grade (5.0 == 5%).</summary>
        public double SlopePct { get; }

        /// <summary>Downhill direction, degrees clockwise from north (0-360). NaN if flat.</summary>
        public double AspectDegrees { get; }

        /// <summary>Construct a sample.</summary>
        public SlopeSample(double slopePct, double aspectDegrees)
        {
            SlopePct = slopePct;
            AspectDegrees = aspectDegrees;
        }
    }

    /// <summary>
    /// A queryable ground surface. The grading solver reasons about elevation and slope
    /// entirely through this interface, so it does not care whether the surface is a TIN
    /// this tool triangulated (<see cref="TinSurface"/>, testable off Civil 3D) or one backed
    /// by Civil 3D's own surface engine in the add-in. Both implement the same contract, so
    /// the solver is written once and the results agree to within the design safety margin.
    /// </summary>
    public interface ISurface
    {
        /// <summary>Surface name.</summary>
        string Name { get; }

        /// <summary>Elevation at an XY location, or null if the point is outside the surface.</summary>
        double? ElevationAt(double x, double y);

        /// <summary>
        /// Slope and aspect at an XY location, or null if outside the surface. On a TIN this
        /// is the constant slope of the triangle containing the point.
        /// </summary>
        SlopeSample? SlopeAt(double x, double y);

        /// <summary>(minX, minY, maxX, maxY) plan extents.</summary>
        (double MinX, double MinY, double MaxX, double MaxY) Extents { get; }

        /// <summary>(min, max) elevation range.</summary>
        (double Min, double Max) ElevationRange { get; }
    }
}
