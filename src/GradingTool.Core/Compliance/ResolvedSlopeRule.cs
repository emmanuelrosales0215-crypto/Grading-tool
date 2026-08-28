using System;
using SurfaceUse = GradingTool.AdaComplianceStandards.SurfaceUse;

namespace GradingTool
{
    /// <summary>
    /// The single slope band the solver designs to for one surface, after ADA, the
    /// municipality config and the tool's engineering defaults have been reconciled.
    /// <para>
    /// Both bounds carry the name of the standard that produced them, so the exception
    /// report can cite the specific rule a zone violates rather than just "out of spec".
    /// </para>
    /// </summary>
    public sealed class ResolvedSlopeRule
    {
        /// <summary>The surface this band applies to.</summary>
        public SurfaceUse Use { get; }

        /// <summary>True if this band describes cross slope rather than running slope.</summary>
        public bool IsCrossSlope { get; }

        /// <summary>Drainage minimum, percent. The flattest the surface may be built.</summary>
        public double MinSlopePct { get; }

        /// <summary>
        /// Design maximum, percent - the safety margin is already deducted where an ADA
        /// ceiling is the binding constraint. This is the number the solver targets.
        /// </summary>
        public double MaxSlopePct { get; }

        /// <summary>
        /// The undiscounted legal/regulatory ceiling, for reporting. Where ADA binds, this
        /// is the literal ADA maximum and <see cref="MaxSlopePct"/> sits
        /// <see cref="AdaComplianceStandards.DesignSafetyMarginPct"/> below it.
        /// </summary>
        public double LegalMaxSlopePct { get; }

        /// <summary>Which standard produced <see cref="MinSlopePct"/>.</summary>
        public string MinSource { get; }

        /// <summary>Which standard produced <see cref="MaxSlopePct"/>.</summary>
        public string MaxSource { get; }

        /// <summary>
        /// True when the maximum traces back to ADA. Such a ceiling is a hard constraint:
        /// no config, user input or solver relaxation may raise it.
        /// </summary>
        public bool IsHardAdaConstraint { get; }

        /// <summary>The margin deducted from the legal ceiling, or 0 where none was.</summary>
        public double SafetyMarginAppliedPct { get; }

        /// <summary>Construct a resolved band. Built by <see cref="ConservativeGradingRules"/>.</summary>
        public ResolvedSlopeRule(
            SurfaceUse use,
            bool isCrossSlope,
            double minSlopePct,
            double maxSlopePct,
            double legalMaxSlopePct,
            string minSource,
            string maxSource,
            bool isHardAdaConstraint,
            double safetyMarginAppliedPct)
        {
            Use = use;
            IsCrossSlope = isCrossSlope;
            MinSlopePct = minSlopePct;
            MaxSlopePct = maxSlopePct;
            LegalMaxSlopePct = legalMaxSlopePct;
            MinSource = minSource;
            MaxSource = maxSource;
            IsHardAdaConstraint = isHardAdaConstraint;
            SafetyMarginAppliedPct = safetyMarginAppliedPct;
        }

        /// <summary>How much room the solver has between the two bounds, in percent.</summary>
        public double BandWidthPct => MaxSlopePct - MinSlopePct;

        /// <summary>True if a measured slope sits inside the band, inclusive.</summary>
        public bool Contains(double measuredSlopePct)
            => measuredSlopePct >= MinSlopePct && measuredSlopePct <= MaxSlopePct;

        /// <summary>
        /// Describe how a measured slope sits against this band, citing the binding rule.
        /// Returns null when the slope complies.
        /// </summary>
        public string? DescribeViolation(double measuredSlopePct)
        {
            if (measuredSlopePct > MaxSlopePct)
            {
                string hard = IsHardAdaConstraint
                    ? " This is a hard ADA constraint and cannot be relaxed by any municipality or project override."
                    : string.Empty;
                return $"{Use}{(IsCrossSlope ? " (cross slope)" : string.Empty)}: " +
                       $"{measuredSlopePct:F2}% exceeds the {MaxSlopePct:F2}% design maximum " +
                       $"from {MaxSource}.{hard}";
            }

            if (measuredSlopePct < MinSlopePct)
            {
                return $"{Use}{(IsCrossSlope ? " (cross slope)" : string.Empty)}: " +
                       $"{measuredSlopePct:F2}% is below the {MinSlopePct:F2}% drainage minimum " +
                       $"from {MinSource}. This surface will pond.";
            }

            return null;
        }

        /// <inheritdoc />
        public override string ToString()
            => $"{Use}{(IsCrossSlope ? " cross" : string.Empty)}: " +
               $"{MinSlopePct:F2}%-{MaxSlopePct:F2}% (max from {MaxSource})";
    }

    /// <summary>
    /// Thrown when the applicable standards leave no slope a surface could legally be
    /// built at - the drainage minimum has risen above the compliance maximum.
    /// <para>
    /// This is a real condition, not a defensive check. An accessible stall carries a
    /// drainage minimum from the municipality and a 2% ADA ceiling; push the minimum to
    /// 2% and the band is empty. Silently picking one bound would produce a design that
    /// either ponds or violates ADA, so the rule set is rejected instead.
    /// </para>
    /// </summary>
    public class InfeasibleSlopeRuleException : Exception
    {
        /// <summary>The surface whose band collapsed.</summary>
        public SurfaceUse Use { get; }

        /// <summary>Create the exception.</summary>
        public InfeasibleSlopeRuleException(SurfaceUse use, string message) : base(message)
            => Use = use;
    }
}
