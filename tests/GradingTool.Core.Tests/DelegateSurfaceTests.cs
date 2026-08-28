using System;
using System.Collections.Generic;
using System.Linq;
using GradingTool;
using GradingTool.Geometry;
using GradingTool.Grading;
using GradingTool.Surface;
using Xunit;
using SurfaceUse = GradingTool.AdaComplianceStandards.SurfaceUse;

namespace GradingTool.Tests
{
    /// <summary>
    /// <see cref="DelegateSurface"/> is the seam a Dynamo Python node uses to drive the solver
    /// against a live Civil 3D surface. Civil 3D cannot run here, so these tests stand in for
    /// that host: the delegate is pointed at a managed <see cref="TinSurface"/>, whose exact
    /// per-triangle answers are the reference the delegate path has to reproduce.
    /// </summary>
    public class DelegateSurfaceTests
    {
        // Same construction as TinSurfaceTests: Z climbs eastward, so downhill is due west.
        private static TinSurface TiltedPlane(double slopePct, int n = 9, double spacing = 25)
        {
            var pts = new List<Point3d>();
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    pts.Add(new Point3d(i * spacing, j * spacing, 100.0 + (i * spacing) * (slopePct / 100.0)));
            return TinSurface.FromPoints("plane", pts);
        }

        [Fact]
        public void Elevation_matches_the_wrapped_surface()
        {
            var tin = TiltedPlane(5.0);
            var wrapped = DelegateSurface.Wrapping(tin);

            foreach (var (x, y) in new[] { (30.0, 30.0), (77.5, 12.25), (150.0, 175.0) })
                Assert.Equal(tin.ElevationAt(x, y)!.Value, wrapped.ElevationAt(x, y)!.Value, 9);
        }

        [Fact]
        public void Nan_from_the_callback_reads_as_outside()
        {
            var s = new DelegateSurface("probe", (x, y) => x < 50 ? 100.0 : DelegateSurface.Outside,
                0, 0, 100, 100, 100, 100);

            Assert.Equal(100.0, s.ElevationAt(10, 10)!.Value, 9);
            Assert.Null(s.ElevationAt(60, 10));
        }

        [Theory]
        [InlineData(2.0)]
        [InlineData(5.0)]
        [InlineData(8.33)]
        public void Stencil_slope_matches_the_tin_triangle_slope(double slopePct)
        {
            var tin = TiltedPlane(slopePct);
            var wrapped = DelegateSurface.Wrapping(tin);

            // Interior point, well away from the edge so the stencil stays on the surface.
            var exact = tin.SlopeAt(100, 100)!.Value;
            var stencil = wrapped.SlopeAt(100, 100)!.Value;

            // A uniform plane has the same slope in every triangle, so the stencil is not an
            // approximation here - it should agree to floating-point noise.
            Assert.Equal(exact.SlopePct, stencil.SlopePct, 6);
            Assert.Equal(exact.AspectDegrees, stencil.AspectDegrees, 6);
        }

        [Fact]
        public void Aspect_points_downhill_west_for_eastward_climb()
        {
            var wrapped = DelegateSurface.Wrapping(TiltedPlane(5.0));
            Assert.Equal(270.0, wrapped.SlopeAt(100, 100)!.Value.AspectDegrees, 6);
        }

        [Fact]
        public void Flat_ground_reads_zero_slope_with_undefined_aspect()
        {
            var wrapped = new DelegateSurface("flat", (x, y) => 100.0, 0, 0, 500, 500, 100, 100);
            var sample = wrapped.SlopeAt(250, 250)!.Value;

            Assert.Equal(0.0, sample.SlopePct, 9);
            Assert.True(double.IsNaN(sample.AspectDegrees), "a flat surface has no downhill bearing");
        }

        [Fact]
        public void Slope_is_null_when_the_stencil_runs_off_the_edge()
        {
            // Elevation is defined only for x < 50; a stencil centred at 49.5 with a 1 ft
            // half-step probes x = 50.5, which is off the surface.
            var s = new DelegateSurface("half", (x, y) => x < 50 ? 100.0 + x : DelegateSurface.Outside,
                0, 0, 50, 100, 100, 150);

            Assert.NotNull(s.SlopeAt(25, 50));
            Assert.Null(s.SlopeAt(49.5, 50));
        }

        [Fact]
        public void Stencil_half_step_is_honoured()
        {
            // A surface that is flat except for a narrow ridge at x = 30. Centred at x = 25, a
            // 1 ft stencil probes 24 and 26 and never sees the ridge; a 5 ft stencil probes 20
            // and 30, landing one probe on the crest.
            Func<double, double, double> ridge = (x, y) => Math.Abs(x - 30) < 2 ? 110.0 : 100.0;

            var tight = new DelegateSurface("ridge", ridge, 0, 0, 100, 100, 100, 110, slopeStencilFt: 1.0);
            var wide = new DelegateSurface("ridge", ridge, 0, 0, 100, 100, 100, 110, slopeStencilFt: 5.0);

            Assert.Equal(0.0, tight.SlopeAt(25, 50)!.Value.SlopePct, 9);
            // (110 - 100) / (2 * 5) = 1.0 rise/run = 100%.
            Assert.Equal(100.0, wide.SlopeAt(25, 50)!.Value.SlopePct, 9);
        }

        [Fact]
        public void Extents_and_elevation_range_pass_through()
        {
            var s = new DelegateSurface("x", (x, y) => 0.0, 10, 20, 110, 220, 95.5, 130.25);

            Assert.Equal((10.0, 20.0, 110.0, 220.0), s.Extents);
            Assert.Equal((95.5, 130.25), s.ElevationRange);
            Assert.Equal("x", s.Name);
        }

        [Fact]
        public void Constructor_rejects_nonsense()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new DelegateSurface("x", null!, 0, 0, 1, 1, 0, 1));
            Assert.Throws<ArgumentNullException>(() =>
                new DelegateSurface(null!, (x, y) => 0.0, 0, 0, 1, 1, 0, 1));
            Assert.Throws<ArgumentException>(() =>
                new DelegateSurface("x", (a, b) => 0.0, 10, 0, 1, 1, 0, 1));   // inverted X extent
            Assert.Throws<ArgumentException>(() =>
                new DelegateSurface("x", (a, b) => 0.0, 0, 0, 1, 1, 50, 1));   // inverted Z range
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new DelegateSurface("x", (a, b) => 0.0, 0, 0, 1, 1, 0, 1, slopeStencilFt: 0));
        }

        [Fact]
        public void Solver_reaches_the_same_answer_through_the_delegate_seam()
        {
            // The point of the whole exercise: the untouched solver runs against a surface it
            // only knows through a callback, and lands where it lands on the surface directly.
            var tin = TiltedPlane(0.0, n: 21, spacing: 25); // flat ground at z = 100
            var wrapped = DelegateSurface.Wrapping(tin);

            FeatureLine Line() => new FeatureLine("L", SurfaceUse.StandardParking, new[]
            {
                new Station(new Point3d(0, 0, 100), isFixed: true),
                new Station(new Point3d(100, 0, 120), isFixed: false),
            });

            var direct = Line();
            var viaDelegate = Line();

            var onTin = new GradingSolver(tin, new ConservativeGradingRules()).Solve(new[] { direct });
            var onDelegate = new GradingSolver(wrapped, new ConservativeGradingRules()).Solve(new[] { viaDelegate });

            Assert.Equal(onTin.Converged, onDelegate.Converged);
            Assert.Equal(
                onTin.Findings.Select(f => f.ToString()),
                onDelegate.Findings.Select(f => f.ToString()));
            Assert.Equal(
                direct.Stations.Select(s => s.Point.Z),
                viaDelegate.Stations.Select(s => s.Point.Z));
        }
    }
}
