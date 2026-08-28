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
    public class SurfaceGraderTests
    {
        private static ConservativeGradingRules Rules() => new ConservativeGradingRules();

        // A surface sloped purely along the diagonal at a chosen percent. Its curb lines
        // (along X and along Y) each read a smaller grade, but the true max is on the diagonal.
        private static TinSurface DiagonalPlane(double diagPct, double span = 100)
        {
            // z = k*(x+y)/sqrt2 gives gradient magnitude k. Choose k = diagPct/100.
            double k = diagPct / 100.0;
            var pts = new List<Point3d>();
            for (int i = 0; i <= 4; i++)
                for (int j = 0; j <= 4; j++)
                {
                    double x = i * span / 4, y = j * span / 4;
                    pts.Add(new Point3d(x, y, 100 + k * (x + y) / Math.Sqrt(2)));
                }
            return TinSurface.FromPoints("pad", pts);
        }

        [Fact]
        public void Any_direction_catches_diagonal_slope_over_max()
        {
            // Accessible parking, 2% any direction (design target 1.7%). Diagonal at 3%.
            var s = DiagonalPlane(3.0);
            var r = Rules().Resolve(SurfaceUse.AccessibleParking); // design target 1.7
            var result = new SurfaceGrader(Rules()).ValidateAnyDirection(s, SurfaceUse.AccessibleParking);
            Assert.Contains(result.Findings, f => f.Category == "surface-slope" && f.Severity == GradingSeverity.Error);
        }

        [Fact]
        public void Any_direction_passes_a_compliant_pad()
        {
            var s = DiagonalPlane(1.5); // under the 1.7% design target
            var result = new SurfaceGrader(Rules()).ValidateAnyDirection(s, SurfaceUse.AccessibleParking);
            Assert.DoesNotContain(result.Findings, f => f.Category == "surface-slope");
        }

        [Fact]
        public void Directional_separates_running_from_cross()
        {
            // Plane sloping only in +X at 4%. Flow bearing due east (90 deg): running = 4%,
            // cross = 0%. For an accessible route (running max 4.7, cross max 1.7) this passes.
            var pts = new List<Point3d>();
            for (int i = 0; i <= 4; i++)
                for (int j = 0; j <= 4; j++)
                    pts.Add(new Point3d(i * 25, j * 25, 100 + (i * 25) * 0.04));
            var s = TinSurface.FromPoints("route", pts);

            var alongFlow = new SurfaceGrader(Rules()).ValidateDirectional(s, SurfaceUse.AccessibleRoute, 90);
            Assert.Empty(alongFlow.Findings);

            // Now treat that same 4% as CROSS slope (flow due north): cross 4% > 1.7% max -> error.
            var acrossFlow = new SurfaceGrader(Rules()).ValidateDirectional(s, SurfaceUse.AccessibleRoute, 0);
            Assert.Contains(acrossFlow.Findings, f => f.Category == "cross-slope");
        }
    }

    public class DaylightTests
    {
        private static ISurface FlatGround(double z, double span = 1000) =>
            TinSurface.FromPoints("EG",
                Enumerable.Range(0, 5).SelectMany(i => Enumerable.Range(0, 5)
                    .Select(j => new Point3d(i * span / 4, j * span / 4, z))).ToList());

        [Fact]
        public void Fill_daylights_at_the_expected_run()
        {
            // Pad top at 110, existing at 100 -> 10 ft of fill. At 3:1 the run to daylight is
            // 10 * 3 = 30 ft.
            var eg = FlatGround(100);
            var dp = Daylight.Project(new Point3d(500, 500, 110), bearingDegrees: 90,
                slopeRatioHtoV: 3.0, existing: eg, step: 0.5);
            Assert.True(dp.Reached);
            Assert.Equal(30.0, dp.RunFt, 1);
            Assert.Equal(100.0, dp.Daylight.Z, 1); // meets existing grade
        }

        [Fact]
        public void Cut_daylights_uphill_to_existing()
        {
            // Pad at 90, existing at 100 -> 10 ft cut. At 2:1 the run is 20 ft.
            var eg = FlatGround(100);
            var dp = Daylight.Project(new Point3d(500, 500, 90), 90, 2.0, eg, step: 0.5);
            Assert.True(dp.Reached);
            Assert.Equal(20.0, dp.RunFt, 1);
        }
    }

    public class VolumeCalculatorTests
    {
        private static ISurface Flat(string name, double z, double span = 100) =>
            TinSurface.FromPoints(name,
                Enumerable.Range(0, 6).SelectMany(i => Enumerable.Range(0, 6)
                    .Select(j => new Point3d(i * span / 5, j * span / 5, z))).ToList());

        [Fact]
        public void Uniform_fill_volume_matches_hand_calc()
        {
            // Proposed 2 ft above existing over 100x100 = 10,000 sq ft * 2 ft = 20,000 cf
            // = 20000/27 = 740.7 CY of fill, 0 cut.
            var eg = Flat("EG", 100);
            var pr = Flat("PR", 102);
            var v = VolumeCalculator.CutFill(eg, pr, spacing: 2.0);
            Assert.Equal(740.7, v.FillCubicYards, 0);
            Assert.Equal(0.0, v.CutCubicYards, 3);
            Assert.True(v.NetCubicYards > 0); // net import
        }

        [Fact]
        public void Uniform_cut_volume_is_negative_net()
        {
            var eg = Flat("EG", 100);
            var pr = Flat("PR", 97); // 3 ft below -> cut
            var v = VolumeCalculator.CutFill(eg, pr, spacing: 2.0);
            Assert.True(v.CutCubicYards > 0);
            Assert.Equal(0.0, v.FillCubicYards, 3);
            Assert.True(v.NetCubicYards < 0); // net export
        }

        [Fact]
        public void No_overlap_yields_zero()
        {
            var a = Flat("A", 100, span: 50);
            var b = TinSurface.FromPoints("B",
                Enumerable.Range(0, 4).SelectMany(i => Enumerable.Range(0, 4)
                    .Select(j => new Point3d(10000 + i * 10, 10000 + j * 10, 100))).ToList());
            var v = VolumeCalculator.CutFill(a, b, spacing: 5);
            Assert.Equal(0.0, v.FillCubicYards, 6);
            Assert.Equal(0.0, v.CutCubicYards, 6);
        }
    }
}
