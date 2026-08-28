using System;

namespace GradingTool
{
    /// <summary>
    /// ADA (2010 ADA Standards for Accessible Design, Sections 402 &amp; 502) slope
    /// thresholds. These are treated as HARD FLOORS/CEILINGS in this tool and
    /// are never relaxed by a municipality config or user override. Municipality
    /// or project standards may be MORE restrictive than ADA, never less.
    /// </summary>
    public static class AdaComplianceStandards
    {
        // Accessible parking spaces and access aisles: max slope any direction.
        public const double AccessibleParkingMaxSlopePct = 2.0;

        // Accessible route (walkways connecting parking to facility): running slope.
        public const double AccessibleRouteMaxRunningSlopePct = 5.0;

        // Accessible route: cross slope.
        public const double AccessibleRouteMaxCrossSlopePct = 2.0;

        // Above 5% running slope, the route is legally a RAMP and triggers
        // separate ramp design rules (handrails, landings, max rise per run).
        public const double RampThresholdPct = 5.0;

        // Ramp max running slope (1:12).
        public const double RampMaxRunningSlopePct = 8.33;

        // Ramp max cross slope.
        public const double RampMaxCrossSlopePct = 2.0;

        // Ramp max rise per run before a landing is required (30 in over 1:12 run).
        public const double RampMaxRiseInchesPerRun = 30.0;

        // Curb ramps: max running slope.
        public const double CurbRampMaxRunningSlopePct = 8.33;

        /// <summary>
        /// Conservative safety margin applied on top of the raw ADA ceiling.
        /// The solver targets this reduced value, not the literal legal max,
        /// so as-built survey tolerance and construction variance don't push
        /// a finished surface into non-compliance.
        /// </summary>
        public const double DesignSafetyMarginPct = 0.3;

        public static double AccessibleParkingDesignTargetMax => AccessibleParkingMaxSlopePct - DesignSafetyMarginPct;
        public static double AccessibleRouteRunningDesignTargetMax => AccessibleRouteMaxRunningSlopePct - DesignSafetyMarginPct;
        public static double AccessibleRouteCrossDesignTargetMax => AccessibleRouteMaxCrossSlopePct - DesignSafetyMarginPct;
        public static double RampRunningDesignTargetMax => RampMaxRunningSlopePct - DesignSafetyMarginPct;
        public static double RampCrossDesignTargetMax => RampMaxCrossSlopePct - DesignSafetyMarginPct;

        public enum SurfaceUse
        {
            AccessibleParking,
            AccessibleRoute,
            Ramp,
            CurbRamp,
            StandardParking,   // not ADA-governed, handled by GeneralSlopeStandards
            DriveAisleOrRoad,  // not ADA-governed
            GeneralLot         // not ADA-governed
        }

        /// <summary>
        /// Outcome of an ADA check. <see cref="NotApplicable"/> is distinct from
        /// <see cref="Compliant"/> on purpose: a surface ADA does not govern has not
        /// been cleared by anything, it has merely not been examined here.
        /// </summary>
        public enum ComplianceStatus
        {
            /// <summary>Checked against an ADA ceiling and within it.</summary>
            Compliant,

            /// <summary>Checked against an ADA ceiling and over it.</summary>
            NonCompliant,

            /// <summary>ADA does not govern this surface type. Use ConservativeGradingRules.</summary>
            NotApplicable
        }

        public class ComplianceResult
        {
            /// <summary>What the check concluded.</summary>
            public ComplianceStatus Status { get; set; }

            /// <summary>
            /// True only when the surface was actually checked against an ADA ceiling and
            /// passed. A <see cref="ComplianceStatus.NotApplicable"/> result is false here,
            /// so a caller that forgets to route a non-ADA surface to
            /// <see cref="ConservativeGradingRules"/> gets it flagged rather than passed.
            /// </summary>
            public bool IsCompliant => Status == ComplianceStatus.Compliant;

            public double MeasuredSlopePct { get; set; }

            /// <summary>
            /// The ceiling applied, or <see cref="double.NaN"/> when ADA does not govern.
            /// Deliberately not <see cref="double.PositiveInfinity"/>: infinity silently
            /// satisfies every comparison, whereas NaN makes an unrouted surface fail any
            /// bounds test it reaches.
            /// </summary>
            public double AllowedMaxSlopePct { get; set; }

            public string Message { get; set; } = string.Empty;
            public bool IsHardAdaConstraint { get; set; }
        }

        /// <summary>True if ADA governs this surface type at all.</summary>
        public static bool IsAdaGoverned(SurfaceUse use)
        {
            switch (use)
            {
                case SurfaceUse.AccessibleParking:
                case SurfaceUse.AccessibleRoute:
                case SurfaceUse.Ramp:
                case SurfaceUse.CurbRamp:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// The literal ADA legal ceiling for a surface use, with no safety margin removed.
        /// Returns <see cref="double.NaN"/> for surfaces ADA does not govern.
        /// <para>
        /// This is the value a municipality config is checked against: a jurisdiction may
        /// require anything at or below it, never above.
        /// </para>
        /// </summary>
        public static double LegalMaxSlopePct(SurfaceUse use, bool isCrossSlope = false)
        {
            switch (use)
            {
                case SurfaceUse.AccessibleParking:
                    // 2% in any direction, so cross and running share one ceiling.
                    return AccessibleParkingMaxSlopePct;
                case SurfaceUse.AccessibleRoute:
                    return isCrossSlope ? AccessibleRouteMaxCrossSlopePct : AccessibleRouteMaxRunningSlopePct;
                case SurfaceUse.Ramp:
                    return isCrossSlope ? RampMaxCrossSlopePct : RampMaxRunningSlopePct;
                case SurfaceUse.CurbRamp:
                    return isCrossSlope ? RampMaxCrossSlopePct : CurbRampMaxRunningSlopePct;
                default:
                    return double.NaN;
            }
        }

        /// <summary>
        /// The ADA ceiling reduced by <see cref="DesignSafetyMarginPct"/> - what the solver
        /// actually designs to. <see cref="double.NaN"/> where ADA does not govern.
        /// </summary>
        public static double DesignTargetMaxSlopePct(SurfaceUse use, bool isCrossSlope = false)
        {
            double legal = LegalMaxSlopePct(use, isCrossSlope);
            return double.IsNaN(legal) ? double.NaN : legal - DesignSafetyMarginPct;
        }

        /// <summary>
        /// Validates a measured slope against the ADA ceiling for the given use.
        /// Returns non-compliant if the measured slope exceeds the DESIGN target
        /// (legal max minus safety margin), not just the literal legal max.
        /// </summary>
        public static ComplianceResult Validate(SurfaceUse use, double measuredSlopePct, bool isCrossSlope = false)
        {
            if (!IsAdaGoverned(use))
            {
                // Not an ADA-governed surface type; the caller must use
                // ConservativeGradingRules, which resolves general engineering defaults
                // against the municipality config. Reporting "compliant" here would let a
                // 40% road pass an ADA check it was never subjected to, so this reports
                // NotApplicable with a NaN ceiling instead.
                return new ComplianceResult
                {
                    Status = ComplianceStatus.NotApplicable,
                    MeasuredSlopePct = measuredSlopePct,
                    AllowedMaxSlopePct = double.NaN,
                    Message = $"ADA does not govern {use}. Resolve this surface through " +
                              "ConservativeGradingRules; this result clears nothing.",
                    IsHardAdaConstraint = false
                };
            }

            double allowed = DesignTargetMaxSlopePct(use, isCrossSlope);
            bool compliant = measuredSlopePct <= allowed;

            return new ComplianceResult
            {
                Status = compliant ? ComplianceStatus.Compliant : ComplianceStatus.NonCompliant,
                MeasuredSlopePct = measuredSlopePct,
                AllowedMaxSlopePct = allowed,
                IsHardAdaConstraint = true,
                Message = compliant
                    ? $"Compliant: {measuredSlopePct:F2}% <= design target {allowed:F2}% (legal max minus {DesignSafetyMarginPct}% margin)."
                    : $"NON-COMPLIANT: {measuredSlopePct:F2}% exceeds design target {allowed:F2}%. This is a hard ADA constraint and cannot be relaxed by any municipality or project override."
            };
        }
    }
}
