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
    public class GradingSolverTests
    {
        // Flat existing ground at z=100 over a wide area, so cut/fill is just proposed - 100.
        private static ISurface FlatGround(double z = 100.0, double span = 2000.0)
        {
            var pts = new List<Point3d>();
            for (int i = 0; i <= 4; i++)
                for (int j = 0; j <= 4; j++)
                    pts.Add(new Point3d(i * span / 4, j * span / 4, z));
            return TinSurface.FromPoints("EG", pts);
        }

        private static ConservativeGradingRules NoMuni() => new ConservativeGradingRules();

        private static FeatureLine Line(SurfaceUse use, params (double x, double y, double z, bool fixd)[] sts)
            => new FeatureLine(use.ToString(), use,
                sts.Select(s => new Station(new Point3d(s.x, s.y, s.z), s.fixd)));

        [Fact]
        public void Steep_free_line_is_brought_within_max()
        {
            // Standard parking, default max 5%. Start at 20% (0 -> 20 over 100 ft), first station fixed.
            var line = Line(SurfaceUse.StandardParking,
                (0, 0, 100, true), (100, 0, 120, false));
            var solver = new GradingSolver(FlatGround(), NoMuni());
            var r = solver.Solve(new[] { line });

            Assert.True(r.Converged);
            double g = Math.Abs(line.SegmentGrade(0)) * 100;
            Assert.True(g <= 5.0 + 1e-6, $"expected <=5%, got {g:F3}%");
            Assert.DoesNotContain(r.Findings, f => f.Category == "running-slope" && f.Severity == GradingSeverity.Error);
        }

        [Fact]
        public void Ada_route_max_is_enforced_at_4point7()
        {
            // Accessible route: design target 5.0 - 0.3 = 4.7%. Start at 9%.
            var line = Line(SurfaceUse.AccessibleRoute,
                (0, 0, 100, true), (100, 0, 109, false));
            var r = new GradingSolver(FlatGround(), NoMuni()).Solve(new[] { line });
            double g = Math.Abs(line.SegmentGrade(0)) * 100;
            Assert.Equal(4.7, g, 4);
        }

        [Fact]
        public void Pinned_segment_that_violates_is_infeasible()
        {
            // Both ends fixed, 15% apart, accessible route (max 4.7%). Cannot resolve.
            var line = Line(SurfaceUse.AccessibleRoute,
                (0, 0, 100, true), (100, 0, 115, true));
            var r = new GradingSolver(FlatGround(), NoMuni()).Solve(new[] { line });
            Assert.Contains(r.Findings, f => f.Category == "infeasible");
            Assert.True(r.HasErrors);
        }

        [Fact]
        public void Drainage_minimum_flat_segment_is_flagged_and_raised()
        {
            // Standard parking min 1%. A dead-flat free segment should be raised to 1% and,
            // because it started flat, also noted. After solving it must meet the minimum.
            var line = Line(SurfaceUse.StandardParking,
                (0, 0, 100, true), (100, 0, 100, false));
            var r = new GradingSolver(FlatGround(), NoMuni()).Solve(new[] { line });
            double g = Math.Abs(line.SegmentGrade(0)) * 100;
            Assert.True(g >= 1.0 - 1e-6, $"expected >=1%, got {g:F3}%");
        }

        [Fact]
        public void Deep_fill_triggers_retaining_wall()
        {
            // Existing ground at 100; propose a line 10 ft above it -> fill 10 ft > 4 ft trigger.
            var line = Line(SurfaceUse.GeneralLot,
                (500, 500, 110, true), (600, 500, 110.5, false));
            var opts = new GradingOptions { RetainingWallTriggerFt = 4.0 };
            var r = new GradingSolver(FlatGround(), NoMuni(), opts).Solve(new[] { line });
            Assert.Contains(r.Findings, f => f.Category == "retaining-wall");
        }

        [Fact]
        public void Station_outside_existing_surface_is_flagged()
        {
            var line = Line(SurfaceUse.GeneralLot,
                (50000, 50000, 100, true), (50100, 50000, 101, false));
            var r = new GradingSolver(FlatGround(), NoMuni()).Solve(new[] { line });
            Assert.Contains(r.Findings, f => f.Category == "off-surface");
        }

        [Fact]
        public void Ramp_cumulative_rise_over_30in_requires_landing()
        {
            // Ramp at ~8% over three 40-ft segments: each rises 3.2 ft; by the end cumulative
            // rise far exceeds 30 in (2.5 ft), so a landing must be flagged.
            var line = Line(SurfaceUse.Ramp,
                (0, 0, 100, true),
                (40, 0, 103.2, false),
                (80, 0, 106.4, false),
                (120, 0, 109.6, false));
            var r = new GradingSolver(FlatGround(z: 105), NoMuni()).Solve(new[] { line });
            Assert.Contains(r.Findings, f => f.Category == "ada-landing" && f.Severity == GradingSeverity.Error);
        }

        [Fact]
        public void Municipality_stricter_max_binds_in_solver()
        {
            // Municipal standard-parking max 3% (stricter than 5% default) should bind.
            using var cfg = new TempConfig(@"{
                ""jurisdictionName"": ""Strict City"",
                ""lastVerifiedDate"": ""2026-01-01"",
                ""standardParking"": { ""unit"": ""percent"", ""maxSlopePct"": 3.0, ""minSlopePct"": 1.0 }
            }");
            var rules = new ConservativeGradingRules(cfg.Load());
            var line = Line(SurfaceUse.StandardParking, (0, 0, 100, true), (100, 0, 110, false));
            new GradingSolver(FlatGround(), rules).Solve(new[] { line });
            double g = Math.Abs(line.SegmentGrade(0)) * 100;
            Assert.True(g <= 3.0 + 1e-6, $"expected <=3%, got {g:F3}%");
        }
    }
}
