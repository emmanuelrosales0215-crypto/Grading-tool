using System;
using System.Collections.Generic;
using SurfaceUse = GradingTool.AdaComplianceStandards.SurfaceUse;

namespace GradingTool
{
    /// <summary>
    /// The tool's own conservative engineering defaults for surfaces ADA does not govern,
    /// and the drainage minimums that apply to every paved surface.
    /// <para>
    /// These are starting points, not code. A municipality config may tighten any of them
    /// (see <see cref="ConservativeGradingRules"/>); it may not loosen an ADA value. Verify
    /// against the governing UDC or ordinance for the actual jurisdiction before relying on
    /// a default here.
    /// </para>
    /// <para>
    /// Minimums exist for drainage: a paved surface flatter than its minimum ponds. The
    /// values below assume asphalt. Concrete finishes to a tighter tolerance and can hold
    /// 0.5%, but the conservative default does not assume the paving material, so 1.0% is
    /// used where the surface type is likely asphalt.
    /// </para>
    /// </summary>
    public static class GeneralSlopeStandards
    {
        /// <summary>A minimum/maximum slope pair in percent, with the reasoning attached.</summary>
        public sealed class DefaultRule
        {
            /// <summary>Drainage minimum, percent.</summary>
            public double MinSlopePct { get; }

            /// <summary>Maximum design slope, percent.</summary>
            public double MaxSlopePct { get; }

            /// <summary>Why these numbers, for the exception report.</summary>
            public string Rationale { get; }

            /// <summary>Create a default rule.</summary>
            public DefaultRule(double minSlopePct, double maxSlopePct, string rationale)
            {
                MinSlopePct = minSlopePct;
                MaxSlopePct = maxSlopePct;
                Rationale = rationale;
            }
        }

        /// <summary>Label used as the provenance string when a default is the binding rule.</summary>
        public const string SourceName = "GradingTool conservative engineering default";

        private static readonly Dictionary<SurfaceUse, DefaultRule> Defaults
            = new Dictionary<SurfaceUse, DefaultRule>
            {
                [SurfaceUse.StandardParking] = new DefaultRule(
                    1.0, 5.0,
                    "Asphalt parking bay: 1% drainage minimum to avoid ponding; 5% maximum " +
                    "as the common municipal ceiling for parking areas."),

                [SurfaceUse.DriveAisleOrRoad] = new DefaultRule(
                    0.5, 8.0,
                    "Drive aisle / local road: 0.5% minimum along a curbed gutter line; 8% " +
                    "maximum, the conservative end of the AASHTO local-road grade range."),

                [SurfaceUse.GeneralLot] = new DefaultRule(
                    1.0, 25.0,
                    "General lot grading: 1% minimum for positive drainage away from " +
                    "structures; 25% (4:1) maximum so the slope stays mowable and stable, " +
                    "one step conservative of the 3:1 (33.3%) commonly permitted."),
            };

        /// <summary>
        /// The default rule for a surface use.
        /// <para>
        /// Throws for ADA-governed uses - those resolve through
        /// <see cref="AdaComplianceStandards"/>, and for any enum member added later
        /// without a default here. That last case is deliberate: a new surface type must
        /// fail loudly rather than silently inherit an unbounded slope.
        /// </para>
        /// </summary>
        public static DefaultRule For(SurfaceUse use)
        {
            if (Defaults.TryGetValue(use, out DefaultRule rule))
                return rule;

            if (AdaComplianceStandards.IsAdaGoverned(use))
                throw new ArgumentException(
                    $"{use} is ADA-governed; its ceiling comes from AdaComplianceStandards, " +
                    "not from the general engineering defaults.", nameof(use));

            throw new NotSupportedException(
                $"No general slope default is defined for surface use '{use}'. Add one to " +
                "GeneralSlopeStandards before the solver can grade this surface type - it " +
                "must not default to an unbounded slope.");
        }

        /// <summary>
        /// Drainage minimum applied to ADA-governed paved surfaces, which have an ADA
        /// ceiling but no ADA floor. Water still has to leave an accessible stall.
        /// </summary>
        public const double AdaSurfaceDrainageMinPct = 1.0;

        /// <summary>The minimum slope that applies to a use before any municipality override.</summary>
        public static double DefaultMinSlopePct(SurfaceUse use)
            => AdaComplianceStandards.IsAdaGoverned(use)
                ? AdaSurfaceDrainageMinPct
                : For(use).MinSlopePct;
    }
}
