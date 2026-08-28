using System;
using System.Collections.Generic;
using GradingTool.Geometry;
using GradingTool.Surface;
using SurfaceUse = GradingTool.AdaComplianceStandards.SurfaceUse;

namespace GradingTool.Grading
{
    /// <summary>
    /// Checks slope compliance across a whole proposed surface in two dimensions - the check
    /// the 1D along-line solver cannot do.
    /// <para>
    /// A parking bay can be within grade along its curb lines yet still exceed 2% diagonally;
    /// only looking at the surface in every direction catches that. Each triangle's plane has
    /// one true maximum slope (its gradient magnitude, in the steepest direction), and for a
    /// route or ramp that slope also decomposes into a running component (along the flow of
    /// travel) and a cross component (perpendicular). This grader evaluates both.
    /// </para>
    /// <para>
    /// Every band comes from <see cref="ConservativeGradingRules"/>, so the 2D check enforces
    /// the same most-restrictive ADA/municipality value the rest of the tool does. Findings
    /// carry the triangle centroid so the exception report and heat map can place them.
    /// </para>
    /// </summary>
    public sealed class SurfaceGrader
    {
        private readonly ConservativeGradingRules _rules;

        /// <summary>Construct a surface grader over a resolved rule provider.</summary>
        public SurfaceGrader(ConservativeGradingRules rules)
            => _rules = rules ?? throw new ArgumentNullException(nameof(rules));

        /// <summary>
        /// Validate a surface whose slope limit applies in any direction (accessible parking's
        /// 2%-any-direction, standard parking, drive aisles, general lots). Each triangle's
        /// maximum slope is checked against the resolved max, and its minimum drainage grade
        /// against the resolved min.
        /// </summary>
        public GradingResult ValidateAnyDirection(TinSurface surface, SurfaceUse use)
        {
            ResolvedSlopeRule rule = _rules.Resolve(use);
            var result = new GradingResult(Array.Empty<FeatureLine>()) { Converged = true };

            for (int i = 0; i < surface.Triangles.Count; i++)
            {
                SlopeSample s = surface.SlopeForTriangle(i);
                Point3d c = surface.CentroidOf(i);
                if (s.SlopePct > rule.MaxSlopePct + 1e-6)
                    result.Add(GradingSeverity.Error, "surface-slope", surface.Name, i,
                        $"Triangle at ({c.X:F1},{c.Y:F1}) slopes {s.SlopePct:F2}% (steepest direction), " +
                        $"exceeding the {rule.MaxSlopePct:F2}% maximum ({rule.MaxSource})." +
                        (rule.IsHardAdaConstraint ? " Hard ADA constraint." : ""));
                else if (s.SlopePct < rule.MinSlopePct - 1e-6)
                    result.Add(GradingSeverity.Warning, "surface-drainage", surface.Name, i,
                        $"Triangle at ({c.X:F1},{c.Y:F1}) slopes only {s.SlopePct:F2}%, below the " +
                        $"{rule.MinSlopePct:F2}% drainage minimum ({rule.MinSource}); it will pond.");
            }
            return result;
        }

        /// <summary>
        /// Validate a directional surface (accessible route, ramp) where running and cross
        /// slope have different limits. Each triangle's gradient is decomposed along the given
        /// flow bearing; the running component is checked against the running max and the
        /// cross component against the (tighter) cross max.
        /// </summary>
        /// <param name="surface">Proposed surface.</param>
        /// <param name="use">Directional ADA surface (AccessibleRoute or Ramp).</param>
        /// <param name="flowBearingDegrees">
        /// Direction of travel / primary flow, degrees clockwise from north. Running slope is
        /// measured along this axis; cross slope perpendicular to it.
        /// </param>
        public GradingResult ValidateDirectional(TinSurface surface, SurfaceUse use, double flowBearingDegrees)
        {
            ResolvedSlopeRule running = _rules.Resolve(use, isCrossSlope: false);
            ResolvedSlopeRule cross = _rules.Resolve(use, isCrossSlope: true);
            var result = new GradingResult(Array.Empty<FeatureLine>()) { Converged = true };

            // Flow unit vector (east = sin(bearing), north = cos(bearing)) and its perpendicular.
            double rad = flowBearingDegrees * Math.PI / 180.0;
            double fx = Math.Sin(rad), fy = Math.Cos(rad);
            double px = -fy, py = fx;

            for (int i = 0; i < surface.Triangles.Count; i++)
            {
                (double gx, double gy) = surface.GradientForTriangle(i);
                double runPct = Math.Abs(gx * fx + gy * fy) * 100.0;   // component along flow
                double crossPct = Math.Abs(gx * px + gy * py) * 100.0; // component across flow
                Point3d c = surface.CentroidOf(i);

                if (runPct > running.MaxSlopePct + 1e-6)
                    result.Add(GradingSeverity.Error, "running-slope", surface.Name, i,
                        $"Triangle at ({c.X:F1},{c.Y:F1}) running slope {runPct:F2}% exceeds the " +
                        $"{running.MaxSlopePct:F2}% maximum ({running.MaxSource})." +
                        (running.IsHardAdaConstraint ? " Hard ADA constraint." : ""));
                if (crossPct > cross.MaxSlopePct + 1e-6)
                    result.Add(GradingSeverity.Error, "cross-slope", surface.Name, i,
                        $"Triangle at ({c.X:F1},{c.Y:F1}) cross slope {crossPct:F2}% exceeds the " +
                        $"{cross.MaxSlopePct:F2}% maximum ({cross.MaxSource})." +
                        (cross.IsHardAdaConstraint ? " Hard ADA constraint." : ""));
            }
            return result;
        }
    }
}
