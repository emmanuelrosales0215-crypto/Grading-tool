using System;
using GradingTool.Diagnostics;
using SurfaceUse = GradingTool.AdaComplianceStandards.SurfaceUse;

namespace GradingTool
{
    /// <summary>
    /// Reconciles ADA standards, a municipality config and the tool's engineering defaults
    /// into one <see cref="ResolvedSlopeRule"/> per surface, always taking the most
    /// restrictive applicable value on each bound.
    /// <para>
    /// This is the single entry point the solver and reporting layers use. They never read
    /// <see cref="AdaComplianceStandards"/>, <see cref="GeneralSlopeStandards"/> or a
    /// <see cref="MunicipalityConfig"/> directly - routing everything through here is what
    /// guarantees ADA is never silently loosened and the safety margin is applied exactly
    /// once.
    /// </para>
    /// </summary>
    public sealed class ConservativeGradingRules
    {
        private readonly MunicipalityConfig? _municipality;
        private readonly IGradingLog _log;

        /// <summary>
        /// Build a resolver.
        /// </summary>
        /// <param name="municipality">
        /// The jurisdiction config, already loaded and ADA-validated by
        /// <see cref="MunicipalityConfig.Load"/>. Null runs on ADA plus engineering
        /// defaults alone.
        /// </param>
        /// <param name="log">Diagnostics sink. Null is treated as no logging.</param>
        public ConservativeGradingRules(MunicipalityConfig? municipality = null, IGradingLog? log = null)
        {
            _municipality = municipality;
            _log = log ?? NullGradingLog.Instance;

            // Defence in depth: Load() already rejects a config that loosens ADA, but a
            // config could reach this constructor by another path (deserialized directly,
            // constructed in a test). Re-checking here means no ConservativeGradingRules
            // instance can ever exist around an ADA-loosening config.
            if (_municipality != null)
                _municipality.AssertDoesNotLoosenAda(_log);
        }

        /// <summary>
        /// Resolve the slope band for a surface.
        /// </summary>
        /// <param name="use">The surface type being graded.</param>
        /// <param name="isCrossSlope">
        /// True to resolve the cross-slope band, false for running slope. Only affects
        /// ADA-governed surfaces, where the two ceilings differ.
        /// </param>
        /// <exception cref="InfeasibleSlopeRuleException">
        /// The applicable minimum has risen to or above the applicable maximum, so no
        /// buildable slope exists. Rejected rather than resolved to a silently wrong value.
        /// </exception>
        public ResolvedSlopeRule Resolve(SurfaceUse use, bool isCrossSlope = false)
        {
            bool adaGoverned = AdaComplianceStandards.IsAdaGoverned(use);

            // --- Maximum: the most restrictive ceiling wins -----------------------------
            // Start from the ADA legal ceiling where ADA governs; otherwise from the tool's
            // general default. A municipality may only pull this lower.
            double legalMax;
            double resolvedMax;
            string maxSource;
            bool hardAda;
            double marginApplied;

            if (adaGoverned)
            {
                legalMax = AdaComplianceStandards.LegalMaxSlopePct(use, isCrossSlope);
                // The safety margin is deducted here, once, and only against an ADA ceiling.
                resolvedMax = legalMax - AdaComplianceStandards.DesignSafetyMarginPct;
                marginApplied = AdaComplianceStandards.DesignSafetyMarginPct;
                maxSource = $"ADA legal maximum {legalMax:F2}% less {AdaComplianceStandards.DesignSafetyMarginPct:F1}% design safety margin";
                hardAda = true;

                // A jurisdiction may state a stricter ceiling for an ADA surface (Load has
                // already rejected any that is looser than ADA). If it is stricter than the
                // ADA design target, it binds - and because it is not itself the ADA
                // constraint, the margin no longer applies and the max is no longer the
                // hard ADA value. The ADA ceiling still stands above it either way.
                double? munAda = _municipality?.AdaMaxSlopePctFor(use, isCrossSlope);
                if (munAda.HasValue && munAda.Value < resolvedMax)
                {
                    resolvedMax = munAda.Value;
                    maxSource = $"{_municipality!.JurisdictionName} (ADA-surface tightening)";
                    marginApplied = 0.0;
                    hardAda = false;
                }
            }
            else
            {
                GeneralSlopeStandards.DefaultRule def = GeneralSlopeStandards.For(use);
                legalMax = def.MaxSlopePct;
                resolvedMax = def.MaxSlopePct;
                marginApplied = 0.0;
                maxSource = GeneralSlopeStandards.SourceName;
                hardAda = false;
            }

            // A municipality ceiling replaces the current one only if it is stricter.
            double? munMax = _municipality?.MaxSlopePctFor(use);
            if (munMax.HasValue && munMax.Value < resolvedMax)
            {
                resolvedMax = munMax.Value;
                maxSource = $"{_municipality!.JurisdictionName} ({DescribeMunicipalField(use)})";
                // The municipality value is a hard cap in its own right, but it is not the
                // ADA constraint; whether the *binding* max is ADA is now false because a
                // stricter local rule bound instead. The ADA ceiling still stands above it.
                hardAda = false;
                marginApplied = 0.0; // margin belongs to the ADA target, which is no longer binding
            }

            // --- Minimum: the most restrictive floor wins -------------------------------
            // Drainage minimum. The most restrictive (highest) minimum wins, because a
            // surface flatter than any applicable minimum ponds.
            double resolvedMin = GeneralSlopeStandards.DefaultMinSlopePct(use);
            string minSource = adaGoverned
                ? $"{GeneralSlopeStandards.SourceName} (drainage minimum on ADA surface)"
                : GeneralSlopeStandards.SourceName;

            double? munMin = _municipality?.MinSlopePctFor(use);
            if (munMin.HasValue && munMin.Value > resolvedMin)
            {
                resolvedMin = munMin.Value;
                minSource = $"{_municipality!.JurisdictionName} ({DescribeMunicipalField(use)} drainage minimum)";
            }

            // --- Feasibility ------------------------------------------------------------
            if (resolvedMin >= resolvedMax)
            {
                throw new InfeasibleSlopeRuleException(use,
                    $"No buildable slope exists for {use}" +
                    $"{(isCrossSlope ? " (cross slope)" : string.Empty)}: the {resolvedMin:F2}% " +
                    $"minimum from [{minSource}] is not below the {resolvedMax:F2}% maximum from " +
                    $"[{maxSource}]. The drainage minimum and the compliance ceiling have " +
                    "collided; this surface cannot be graded within the rules and needs a " +
                    "design change (e.g. area drains, a different surface treatment, or a " +
                    "variance request).");
            }

            return new ResolvedSlopeRule(
                use, isCrossSlope, resolvedMin, resolvedMax, legalMax,
                minSource, maxSource, hardAda, marginApplied);
        }

        /// <summary>The municipality config field name that governs a surface use, for messages.</summary>
        private static string DescribeMunicipalField(SurfaceUse use)
        {
            switch (use)
            {
                case SurfaceUse.StandardParking: return "StandardParking";
                case SurfaceUse.DriveAisleOrRoad: return "DriveAisleOrRoad";
                case SurfaceUse.GeneralLot: return "GeneralLot";
                default: return use.ToString();
            }
        }
    }
}
