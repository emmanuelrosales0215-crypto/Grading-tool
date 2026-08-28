using System;

namespace GradingTool.Geometry
{
    /// <summary>
    /// A single XYZ survey point. X is easting, Y is northing, Z is elevation.
    /// <para>
    /// Once a point is inside a <see cref="Ingestion.SurfaceInput"/> it is understood to be
    /// in the project working unit (US survey feet) and the project CRS. Conversion happens
    /// at ingestion, never later, so nothing downstream has to ask what unit a coordinate is in.
    /// </para>
    /// </summary>
    public readonly struct Point3d : IEquatable<Point3d>
    {
        /// <summary>Easting.</summary>
        public double X { get; }

        /// <summary>Northing.</summary>
        public double Y { get; }

        /// <summary>Elevation.</summary>
        public double Z { get; }

        /// <summary>Construct a point.</summary>
        public Point3d(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        /// <summary>Horizontal (plan) distance to another point, ignoring elevation.</summary>
        public double HorizontalDistanceTo(Point3d other)
        {
            double dx = X - other.X;
            double dy = Y - other.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <inheritdoc />
        public bool Equals(Point3d other) => X == other.X && Y == other.Y && Z == other.Z;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is Point3d p && Equals(p);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            unchecked
            {
                int h = X.GetHashCode();
                h = (h * 397) ^ Y.GetHashCode();
                h = (h * 397) ^ Z.GetHashCode();
                return h;
            }
        }

        /// <inheritdoc />
        public override string ToString() => $"({X:F3}, {Y:F3}, {Z:F3})";
    }
}
