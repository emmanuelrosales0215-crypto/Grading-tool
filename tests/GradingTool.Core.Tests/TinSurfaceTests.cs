using System;
using System.Collections.Generic;
using System.Linq;
using GradingTool.Geometry;
using GradingTool.Surface;
using Xunit;

namespace GradingTool.Tests
{
    public class TinSurfaceTests
    {
        // A flat grid tilted at a known percent grade in +X. Z increases eastward, so the
        // downhill (aspect) direction points west = 270 degrees.
        private static TinSurface TiltedPlane(double slopePct, int n = 5, double spacing = 25)
        {
            var pts = new List<Point3d>();
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    pts.Add(new Point3d(i * spacing, j * spacing, 100.0 + (i * spacing) * (slopePct / 100.0)));
            return TinSurface.FromPoints("plane", pts);
        }

        [Theory]
        [InlineData(2.0)]
        [InlineData(5.0)]
        [InlineData(8.33)]
        [InlineData(0.5)]
        public void Slope_of_known_plane_matches(double slopePct)
        {
            var s = TiltedPlane(slopePct);
            // Sample interior triangles; every one should read the same known grade.
            var sample = s.SlopeAt(30, 30);
            Assert.NotNull(sample);
            Assert.Equal(slopePct, sample!.Value.SlopePct, 4);
        }

        [Fact]
        public void Aspect_points_downhill_west_for_eastward_climb()
        {
            var s = TiltedPlane(5.0);
            var sample = s.SlopeAt(30, 30)!.Value;
            // Climbs toward +X (east), so downhill bearing is due west = 270 degrees.
            Assert.Equal(270.0, sample.AspectDegrees, 3);
        }

        [Fact]
        public void Interpolation_is_exact_on_a_plane()
        {
            var s = TiltedPlane(4.0, n: 5, spacing: 25);
            // On a 4% eastward plane through z=100 at x=0, z at x=50 is 100 + 50*0.04 = 102.
            double? z = s.ElevationAt(50, 40);
            Assert.NotNull(z);
            Assert.Equal(102.0, z!.Value, 6);
        }

        [Fact]
        public void Query_outside_surface_returns_null()
        {
            var s = TiltedPlane(3.0);
            Assert.Null(s.ElevationAt(100000, 100000));
            Assert.Null(s.SlopeAt(-500, -500));
        }

        [Fact]
        public void Flat_surface_reads_zero_slope()
        {
            var pts = new List<Point3d>();
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                    pts.Add(new Point3d(i * 10, j * 10, 555.0));
            var s = TinSurface.FromPoints("flat", pts);
            var sample = s.SlopeAt(15, 15)!.Value;
            Assert.Equal(0.0, sample.SlopePct, 9);
        }

        [Fact]
        public void Duplicate_xy_is_deduped_not_fatal()
        {
            var pts = new List<Point3d>
            {
                new Point3d(0, 0, 100), new Point3d(10, 0, 100), new Point3d(0, 10, 100),
                new Point3d(10, 10, 100), new Point3d(0, 0, 200), // duplicate XY, different Z
            };
            var s = TinSurface.FromPoints("dup", pts);
            Assert.Equal(4, s.Points.Count); // duplicate dropped
        }

        [Fact]
        public void Too_few_points_throws()
        {
            Assert.Throws<ArgumentException>(() =>
                TinSurface.FromPoints("tiny", new[] { new Point3d(0, 0, 0), new Point3d(1, 1, 1) }));
        }

        [Fact]
        public void Triangulation_covers_a_square_with_two_triangles()
        {
            var pts = new[]
            {
                new Point3d(0, 0, 0), new Point3d(10, 0, 0),
                new Point3d(0, 10, 0), new Point3d(10, 10, 0),
            };
            var s = TinSurface.FromPoints("square", pts, sliverFactor: null);
            Assert.Equal(2, s.Triangles.Count);
        }

        [Fact]
        public void Boundary_clip_drops_outside_triangles()
        {
            // 5x5 grid over [0,100]; clip to the lower-left quarter.
            var pts = new List<Point3d>();
            for (int i = 0; i <= 4; i++)
                for (int j = 0; j <= 4; j++)
                    pts.Add(new Point3d(i * 25, j * 25, 0));
            var boundary = new[]
            {
                new Point3d(-1, -1, 0), new Point3d(50, -1, 0),
                new Point3d(50, 50, 0), new Point3d(-1, 50, 0),
            };
            var full = TinSurface.FromPoints("full", pts, sliverFactor: null);
            var clipped = TinSurface.FromPoints("clip", pts, boundary, sliverFactor: null);
            Assert.True(clipped.Triangles.Count < full.Triangles.Count);
            // Every kept triangle's centroid must be inside the clip region.
            foreach (var t in clipped.Triangles)
            {
                double cx = (clipped.Points[t[0]].X + clipped.Points[t[1]].X + clipped.Points[t[2]].X) / 3;
                double cy = (clipped.Points[t[0]].Y + clipped.Points[t[1]].Y + clipped.Points[t[2]].Y) / 3;
                Assert.True(cx <= 50 && cy <= 50);
            }
        }
    }
}
