using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using GradingTool.Diagnostics;
using SurfaceUse = GradingTool.AdaComplianceStandards.SurfaceUse;

namespace GradingTool
{
    /// <summary>
    /// Represents one jurisdiction's slope/grading standards, loaded from a
    /// JSON file. Municipality values are USER-EDITABLE inputs, not code -
    /// this file just defines the schema and the loader. See
    /// /Municipalities/*.json for real examples (Elgin, Bexar County) and
    /// /Municipalities/_template.json for a blank starting point.
    /// <para>
    /// A municipality may make any standard MORE restrictive than the tool's defaults or
    /// ADA; it may never make one LESS restrictive than ADA. That invariant is enforced by
    /// <see cref="AssertDoesNotLoosenAda"/>, which <see cref="Load"/> runs before the
    /// config is returned.
    /// </para>
    /// </summary>
    public class MunicipalityConfig
    {
        public string JurisdictionName { get; set; } = string.Empty;
        public string? SourceDocument { get; set; }     // e.g. "City of Elgin UDC Ch. 5, Sec 5.4"
        public string? LastVerifiedDate { get; set; }    // manual field - flag stale configs

        // Texas State Plane NAD83 zone this jurisdiction sits in (US survey feet), e.g. 2277
        // for Texas Central (Elgin) or 2278 for Texas South Central (Bexar). Used to default
        // the project CRS in Phase 2 ingestion.
        public int? StatePlaneEpsg { get; set; }

        // Non-ADA surfaces the tool governs by engineering default; a config tightens them.
        public SlopeRule? StandardParking { get; set; }
        public SlopeRule? DriveAisleOrRoad { get; set; }
        public SlopeRule? GeneralLot { get; set; }
        public SlopeRule? DrivewayApproach { get; set; }

        // Optional ADA-surface tightenings. A jurisdiction may state a stricter ceiling for
        // accessible parking or the accessible route; it may NOT state a looser one - that
        // is rejected by AssertDoesNotLoosenAda. Present in the schema precisely so
        // "more restrictive than ADA" is expressible and "less restrictive" is catchable.
        public SlopeRule? AccessibleParking { get; set; }
        public SlopeRule? AccessibleRouteRunning { get; set; }
        public SlopeRule? AccessibleRouteCross { get; set; }

        public double? MaxCutFillSlopeRatio { get; set; } // e.g. 3.0 means 3:1 (H:V) max for embankments
        public double? RetainingWallTriggerSlopeRatio { get; set; } // slope ratio beyond which a wall is required instead of graded slope

        /// <summary>How a <see cref="SlopeRule"/>'s numeric values are expressed.</summary>
        public enum SlopeUnit
        {
            /// <summary>Percent grade, e.g. 5.0 == 5%. The expected unit for slope rules.</summary>
            Percent,

            /// <summary>H:V ratio, e.g. 3.0 == 3:1 == 33.3%. Converted to percent on load.</summary>
            Ratio
        }

        public class SlopeRule
        {
            /// <summary>
            /// Whether <see cref="MaxSlopePct"/> / <see cref="MinSlopePct"/> hold percent or
            /// ratio values. REQUIRED in the JSON. It exists to catch the one error a range
            /// check cannot: a 3:1 ratio typed into a percent field reads as 3.0%, which is
            /// inside the plausible percent band and would otherwise pass silently. Forcing
            /// the author to declare the unit removes the ambiguity.
            /// </summary>
            [JsonConverter(typeof(JsonStringEnumConverter))]
            public SlopeUnit? Unit { get; set; }

            /// <summary>Maximum slope, in the declared <see cref="Unit"/>.</summary>
            public double MaxSlopePct { get; set; }

            /// <summary>Drainage minimum, in the declared <see cref="Unit"/>.</summary>
            public double MinSlopePct { get; set; }

            public string? Notes { get; set; }

            /// <summary>
            /// The maximum expressed in percent, whatever the source unit. Populated by
            /// <see cref="MunicipalityConfig.Normalize"/> on load.
            /// </summary>
            [JsonIgnore] public double MaxSlopePctNormalized { get; internal set; }

            /// <summary>The minimum expressed in percent, whatever the source unit.</summary>
            [JsonIgnore] public double MinSlopePctNormalized { get; internal set; }
        }

        public static MunicipalityConfig Load(string jsonFilePath, IGradingLog? log = null)
        {
            IGradingLog sink = log ?? NullGradingLog.Instance;

            if (!File.Exists(jsonFilePath))
                throw new FileNotFoundException($"Municipality config not found: {jsonFilePath}");

            var json = File.ReadAllText(jsonFilePath);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            MunicipalityConfig? config;
            try
            {
                config = JsonSerializer.Deserialize<MunicipalityConfig>(json, options);
            }
            catch (JsonException exc)
            {
                throw new InvalidDataException($"Could not parse municipality config {jsonFilePath}: {exc.Message}", exc);
            }

            if (config == null)
                throw new InvalidDataException($"Could not parse municipality config: {jsonFilePath}");

            Normalize(config, jsonFilePath, sink);
            // ADA cross-check first: a value that loosens ADA is also out of the plausible
            // band, so running this ahead of Validate surfaces the precise "looser than ADA"
            // reason rather than a generic "implausible range" one.
            config.AssertDoesNotLoosenAda(sink);
            Validate(config, jsonFilePath, sink);
            return config;
        }

        // -- unit normalization --------------------------------------------------------

        private static readonly (string Name, Func<MunicipalityConfig, SlopeRule?> Get)[] AllRules =
        {
            ("StandardParking",       c => c.StandardParking),
            ("DriveAisleOrRoad",      c => c.DriveAisleOrRoad),
            ("GeneralLot",            c => c.GeneralLot),
            ("DrivewayApproach",      c => c.DrivewayApproach),
            ("AccessibleParking",     c => c.AccessibleParking),
            ("AccessibleRouteRunning",c => c.AccessibleRouteRunning),
            ("AccessibleRouteCross",  c => c.AccessibleRouteCross),
        };

        /// <summary>
        /// Convert every rule to percent according to its declared <see cref="SlopeUnit"/>,
        /// logging any ratio conversion. A rule with no <see cref="SlopeRule.Unit"/> is a
        /// hard error: the whole point of the field is that its absence must not be guessed.
        /// </summary>
        private static void Normalize(MunicipalityConfig config, string source, IGradingLog log)
        {
            foreach (var (name, get) in AllRules)
            {
                SlopeRule? rule = get(config);
                if (rule == null) continue;

                if (rule.Unit == null)
                    throw new InvalidDataException(
                        $"{name}.Unit is missing in {source}. Every slope rule must declare " +
                        "\"unit\": \"percent\" or \"ratio\". This is required so a slope ratio " +
                        "typed into a percent field (e.g. 3 meaning 3:1) is caught rather than " +
                        "silently read as 3%.");

                if (rule.Unit == SlopeUnit.Percent)
                {
                    rule.MaxSlopePctNormalized = rule.MaxSlopePct;
                    rule.MinSlopePctNormalized = rule.MinSlopePct;
                    continue;
                }

                // Ratio: percent = 100 / ratio. A steeper slope is a SMALLER ratio, so the
                // two endpoints invert; reorder after converting so min stays <= max.
                if (rule.MaxSlopePct <= 0 || rule.MinSlopePct <= 0)
                    throw new InvalidDataException(
                        $"{name} in {source} is declared unit=ratio but has a non-positive value; " +
                        "an H:V ratio must be > 0.");

                double a = 100.0 / rule.MaxSlopePct;
                double b = 100.0 / rule.MinSlopePct;
                rule.MinSlopePctNormalized = Math.Min(a, b);
                rule.MaxSlopePctNormalized = Math.Max(a, b);
                log.Info(
                    $"{source}: {name} converted from H:V ratio to percent " +
                    $"({rule.MinSlopePct:G}:1 / {rule.MaxSlopePct:G}:1 -> " +
                    $"{rule.MinSlopePctNormalized:F2}% / {rule.MaxSlopePctNormalized:F2}%).");
            }
        }

        // -- accessors used by the resolver -------------------------------------------

        /// <summary>Municipal max slope (percent) for a non-ADA surface, or null if unset.</summary>
        public double? MaxSlopePctFor(SurfaceUse use)
        {
            SlopeRule? rule = RuleFor(use);
            return rule == null ? (double?)null : rule.MaxSlopePctNormalized;
        }

        /// <summary>Municipal min slope (percent) for a non-ADA surface, or null if unset.</summary>
        public double? MinSlopePctFor(SurfaceUse use)
        {
            SlopeRule? rule = RuleFor(use);
            return rule == null ? (double?)null : rule.MinSlopePctNormalized;
        }

        /// <summary>Municipal ADA-surface tightening (percent), or null if not stated.</summary>
        public double? AdaMaxSlopePctFor(SurfaceUse use, bool isCrossSlope)
        {
            SlopeRule? rule;
            switch (use)
            {
                case SurfaceUse.AccessibleParking: rule = AccessibleParking; break;
                case SurfaceUse.AccessibleRoute:
                    rule = isCrossSlope ? AccessibleRouteCross : AccessibleRouteRunning; break;
                default: rule = null; break;
            }
            return rule == null ? (double?)null : rule.MaxSlopePctNormalized;
        }

        private SlopeRule? RuleFor(SurfaceUse use)
        {
            switch (use)
            {
                case SurfaceUse.StandardParking: return StandardParking;
                case SurfaceUse.DriveAisleOrRoad: return DriveAisleOrRoad;
                case SurfaceUse.GeneralLot: return GeneralLot;
                default: return null;
            }
        }

        // -- ADA cross-check -----------------------------------------------------------

        /// <summary>
        /// Reject the config if any ADA-surface tightening it declares is actually looser
        /// than the ADA legal ceiling. A jurisdiction may pull an ADA limit down, never up.
        /// <para>
        /// Run automatically by <see cref="Load"/>, and again by the
        /// <see cref="ConservativeGradingRules"/> constructor as defence in depth.
        /// </para>
        /// </summary>
        public void AssertDoesNotLoosenAda(IGradingLog? log = null)
        {
            CheckNotLooser(AccessibleParking, SurfaceUse.AccessibleParking, false, "AccessibleParking");
            CheckNotLooser(AccessibleRouteRunning, SurfaceUse.AccessibleRoute, false, "AccessibleRouteRunning");
            CheckNotLooser(AccessibleRouteCross, SurfaceUse.AccessibleRoute, true, "AccessibleRouteCross");
        }

        private void CheckNotLooser(SlopeRule? rule, SurfaceUse use, bool isCrossSlope, string fieldName)
        {
            if (rule == null) return;
            double adaLegalMax = AdaComplianceStandards.LegalMaxSlopePct(use, isCrossSlope);
            // A tiny tolerance so a config stating exactly the ADA number is accepted.
            if (rule.MaxSlopePctNormalized > adaLegalMax + 1e-9)
                throw new InvalidDataException(
                    $"{JurisdictionName}: {fieldName}.MaxSlopePct resolves to " +
                    $"{rule.MaxSlopePctNormalized:F2}%, which is LOOSER than the ADA legal " +
                    $"maximum of {adaLegalMax:F2}%. A municipality config may only make a " +
                    "standard more restrictive than ADA, never less. Fix the config; ADA is " +
                    "a hard floor this tool will not relax.");
        }

        // -- plausibility --------------------------------------------------------------

        /// <summary>
        /// Sanity-checks an imported municipality file. Rejects configs that
        /// look malformed or that appear to permit slopes wildly outside plausible
        /// engineering ranges - this catches typos before they reach the solver.
        /// Every declared field is checked, including the cut/fill and retaining-wall
        /// ratios and the driveway approach.
        /// </summary>
        private static void Validate(MunicipalityConfig config, string source, IGradingLog log)
        {
            if (string.IsNullOrWhiteSpace(config.JurisdictionName))
                throw new InvalidDataException($"Municipality config missing JurisdictionName: {source}");

            // Bands are in percent; rules are already normalized to percent by this point.
            CheckPlausible(config.StandardParking, "StandardParking", 0.5, 10, source);
            CheckPlausible(config.DriveAisleOrRoad, "DriveAisleOrRoad", 0.5, 15, source);
            CheckPlausible(config.GeneralLot, "GeneralLot", 0.5, 33.34, source);  // admit exactly 3:1 (33.333%)
            CheckPlausible(config.DrivewayApproach, "DrivewayApproach", 0.5, 15, source);
            CheckPlausible(config.AccessibleParking, "AccessibleParking", 0.5, 2.0, source);
            CheckPlausible(config.AccessibleRouteRunning, "AccessibleRouteRunning", 0.5, 5.0, source);
            CheckPlausible(config.AccessibleRouteCross, "AccessibleRouteCross", 0.3, 2.0, source);

            // Embankment / wall ratios are true H:V ratios. A plausible graded embankment
            // sits between ~1.5:1 (steep, engineered) and ~10:1 (very shallow). A value
            // outside that band is almost certainly a percent typed where a ratio was
            // expected, or vice versa.
            CheckRatioPlausible(config.MaxCutFillSlopeRatio, "MaxCutFillSlopeRatio", 1.5, 10.0, source);
            CheckRatioPlausible(config.RetainingWallTriggerSlopeRatio, "RetainingWallTriggerSlopeRatio", 1.5, 10.0, source);

            if (config.StatePlaneEpsg.HasValue &&
                !(config.StatePlaneEpsg.Value >= 2275 && config.StatePlaneEpsg.Value <= 2279))
            {
                log.Warning(
                    $"{config.JurisdictionName}: StatePlaneEpsg {config.StatePlaneEpsg} is not a " +
                    "Texas State Plane NAD83 (US ft) zone (2275-2279). Confirm the project CRS.");
            }

            if (string.IsNullOrWhiteSpace(config.LastVerifiedDate))
            {
                log.Warning(
                    $"{config.JurisdictionName} config has no LastVerifiedDate. Municipal code " +
                    "changes - re-verify against the current UDC/ordinance before relying on this file.");
            }
        }

        private static void CheckPlausible(SlopeRule? rule, string fieldName, double plausibleMin, double plausibleMax, string source)
        {
            if (rule == null) return; // optional - falls back to GeneralSlopeStandards default
            double max = rule.MaxSlopePctNormalized;
            double min = rule.MinSlopePctNormalized;
            if (max <= 0 || max > plausibleMax)
                throw new InvalidDataException(
                    $"{fieldName}.MaxSlopePct = {max:F2}% in {source} is outside plausible range " +
                    $"(0-{plausibleMax}%). Check for a units error (ratio vs. percent) before using this config.");
            if (min < 0 || min >= max)
                throw new InvalidDataException(
                    $"{fieldName}.MinSlopePct = {min:F2}% in {source} is invalid relative to its max ({max:F2}%).");
        }

        private static void CheckRatioPlausible(double? ratio, string fieldName, double plausibleMin, double plausibleMax, string source)
        {
            if (ratio == null) return;
            if (ratio.Value < plausibleMin || ratio.Value > plausibleMax)
                throw new InvalidDataException(
                    $"{fieldName} = {ratio.Value:G} in {source} is outside the plausible H:V ratio " +
                    $"range ({plausibleMin}:1 to {plausibleMax}:1). A 3:1 embankment is entered as " +
                    "3.0, not 33.3 (percent) and not 0.33 (rise/run). Fix the units in the config.");
        }
    }
}
