using System;
using System.Collections.Generic;
using GradingTool.Diagnostics;
using GradingTool.Geometry;
using GradingTool.Surface;
using SurfaceUse = GradingTool.AdaComplianceStandards.SurfaceUse;

namespace GradingTool.Grading
{
    /// <summary>Tunable solver settings.</summary>
    public sealed class GradingOptions
    {
        /// <summary>
        /// Cut or fill depth (feet) beyond which a graded slope is impractical and a
        /// retaining wall is flagged instead. A project/municipality value; defaults to 4 ft.
        /// </summary>
        public double RetainingWallTriggerFt { get; set; } = 4.0;

        /// <summary>Maximum relaxation sweeps before giving up on convergence. Default 100.</summary>
        public int MaxIterations { get; set; } = 100;

        /// <summary>Elevation change (feet) below which a sweep is considered to have settled. Default 0.001.</summary>
        public double ConvergenceToleranceFt { get; set; } = 0.001;
    }

    /// <summary>
    /// Adjusts proposed feature-line elevations to satisfy the resolved slope rules, and
    /// flags what cannot be graded within them.
    /// <para>
    /// The core solve is one-dimensional: along each feature line the running grade of every
    /// segment must sit inside the resolved band [min, max]. Free stations are relaxed toward
    /// the nearest bound in alternating forward/backward sweeps (Gauss-Seidel), holding fixed
    /// stations. A segment pinned between two fixed stations that violates the band is
    /// reported as infeasible rather than forced.
    /// </para>
    /// <para>
    /// After solving, three checks run against the existing ground: retaining-wall triggers
    /// (cut/fill deeper than the threshold), the ADA ramp landing rule (cumulative rise over
    /// 30 in requires a landing), and off-surface stations. The slope band always comes from
    /// <see cref="ConservativeGradingRules"/>, so ADA can never be silently loosened here.
    /// </para>
    /// </summary>
    public sealed class GradingSolver
    {
        private readonly ISurface _existing;
        private readonly ConservativeGradingRules _rules;
        private readonly GradingOptions _options;
        private readonly IGradingLog _log;

        /// <summary>Construct a solver.</summary>
        /// <param name="existingGround">The existing surface, for cut/fill and daylight.</param>
        /// <param name="rules">The resolved rule provider (ADA + municipality + defaults).</param>
        /// <param name="options">Solver settings, or null for defaults.</param>
        /// <param name="log">Diagnostics sink.</param>
        public GradingSolver(
            ISurface existingGround,
            ConservativeGradingRules rules,
            GradingOptions? options = null,
            IGradingLog? log = null)
        {
            _existing = existingGround ?? throw new ArgumentNullException(nameof(existingGround));
            _rules = rules ?? throw new ArgumentNullException(nameof(rules));
            _options = options ?? new GradingOptions();
            _log = log ?? NullGradingLog.Instance;
        }

        /// <summary>Solve one or more feature lines.</summary>
        public GradingResult Solve(IReadOnlyList<FeatureLine> lines)
        {
            var result = new GradingResult(lines) { Converged = true };
            foreach (var line in lines)
            {
                ResolvedSlopeRule rule = _rules.Resolve(line.Use);
                bool converged = RelaxRunningSlope(line, rule, result);
                if (!converged) result.Converged = false;
                CheckAgainstExisting(line, rule, result);
                if (line.Use == SurfaceUse.Ramp) CheckRampLandings(line, result);
            }
            _log.Info($"Grading solve: {result.Summary()}");
            return result;
        }

        // ---- 1D running-slope relaxation -------------------------------------------------

        private bool RelaxRunningSlope(FeatureLine line, ResolvedSlopeRule rule, GradingResult result)
        {
            double gmin = rule.MinSlopePct / 100.0;   // rise/run
            double gmax = rule.MaxSlopePct / 100.0;
            var st = line.Stations;

            // Report segments pinned between two fixed stations that cannot comply; the solver
            // will not move fixed control, so these are genuine design conflicts.
            for (int i = 0; i < st.Count - 1; i++)
            {
                if (st[i].IsFixed && st[i + 1].IsFixed)
                {
                    double g = Math.Abs(line.SegmentGrade(i));
                    if (g > gmax + 1e-9 || g < gmin - 1e-9)
                        result.Add(GradingSeverity.Error, "infeasible", line.Name, i,
                            $"Segment {i}->{i + 1} is pinned between two fixed stations at {g * 100:F2}% " +
                            $"but the rule requires {rule.MinSlopePct:F2}%-{rule.MaxSlopePct:F2}% " +
                            $"({rule.MaxSource}). Grading cannot resolve this without moving a control point.");
                }
            }

            bool converged = false;
            for (int iter = 0; iter < _options.MaxIterations; iter++)
            {
                double maxChange = 0.0;
                // Forward sweep: fix i, adjust i+1 if free. Backward sweep: fix i+1, adjust i.
                for (int i = 0; i < st.Count - 1; i++)
                    maxChange = Math.Max(maxChange, TryFixSegment(line, i, adjustForward: true, gmin, gmax));
                for (int i = st.Count - 2; i >= 0; i--)
                    maxChange = Math.Max(maxChange, TryFixSegment(line, i, adjustForward: false, gmin, gmax));

                if (maxChange <= _options.ConvergenceToleranceFt) { converged = true; break; }
            }

            // Final compliance pass: report any segment still outside the band (e.g. because
            // both ends were constrained through the chain).
            for (int i = 0; i < st.Count - 1; i++)
            {
                double g = Math.Abs(line.SegmentGrade(i));
                if (g > gmax + 1e-6)
                    result.Add(GradingSeverity.Error, "running-slope", line.Name, i,
                        $"Segment {i}->{i + 1} running slope {g * 100:F2}% exceeds the {rule.MaxSlopePct:F2}% " +
                        $"maximum ({rule.MaxSource})." +
                        (rule.IsHardAdaConstraint ? " Hard ADA constraint - cannot be relaxed." : ""));
                else if (g < gmin - 1e-6)
                    result.Add(GradingSeverity.Warning, "drainage-min", line.Name, i,
                        $"Segment {i}->{i + 1} running slope {g * 100:F2}% is below the {rule.MinSlopePct:F2}% " +
                        $"drainage minimum ({rule.MinSource}); this segment will pond.");
            }

            return converged;
        }

        // Bring one segment to the nearest bound by moving its adjustable endpoint. Returns
        // the elevation change applied (0 if compliant or the target endpoint is fixed).
        private double TryFixSegment(FeatureLine line, int i, bool adjustForward, double gmin, double gmax)
        {
            var st = line.Stations;
            Station a = st[i], b = st[i + 1];
            double d = line.SegmentLength(i);
            if (d < 1e-9) return 0.0;

            double g = (b.Point.Z - a.Point.Z) / d;      // signed
            double mag = Math.Abs(g);
            if (mag <= gmax + 1e-12 && mag >= gmin - 1e-12) return 0.0; // already in band

            // Preserve drainage direction; if perfectly flat, default to rising forward so a
            // minimum can be established (direction is a design choice the solver notes).
            double sign = g > 0 ? 1.0 : (g < 0 ? -1.0 : 1.0);
            double target = mag > gmax ? gmax : gmin;    // nearest violated bound
            double targetDz = sign * target * d;

            if (adjustForward)
            {
                if (b.IsFixed) return 0.0;
                double newZ = a.Point.Z + targetDz;
                double change = Math.Abs(newZ - b.Point.Z);
                b.Point = new Point3d(b.Point.X, b.Point.Y, newZ);
                return change;
            }
            else
            {
                if (a.IsFixed) return 0.0;
                double newZ = b.Point.Z - targetDz;
                double change = Math.Abs(newZ - a.Point.Z);
                a.Point = new Point3d(a.Point.X, a.Point.Y, newZ);
                return change;
            }
        }

        // ---- checks against existing ground ----------------------------------------------

        private void CheckAgainstExisting(FeatureLine line, ResolvedSlopeRule rule, GradingResult result)
        {
            var st = line.Stations;
            for (int i = 0; i < st.Count; i++)
            {
                double? existing = _existing.ElevationAt(st[i].Point.X, st[i].Point.Y);
                if (existing == null)
                {
                    result.Add(GradingSeverity.Warning, "off-surface", line.Name, i,
                        "Station falls outside the existing surface; cut/fill and daylight cannot be " +
                        "evaluated here. Extend the survey or the surface boundary.");
                    continue;
                }
                double cutFill = st[i].Point.Z - existing.Value; // + = fill, - = cut
                if (Math.Abs(cutFill) > _options.RetainingWallTriggerFt)
                    result.Add(GradingSeverity.Warning, "retaining-wall", line.Name, i,
                        $"{(cutFill > 0 ? "Fill" : "Cut")} of {Math.Abs(cutFill):F2} ft exceeds the " +
                        $"{_options.RetainingWallTriggerFt:F1} ft retaining-wall trigger. A graded slope is " +
                        "impractical here; a retaining wall is likely required.");
            }
        }

        // ADA ramp landing rule (closes the Phase 1 (f) gap): a ramp run may rise at most 30 in
        // before a level landing is required. Walk the line accumulating rise; flag the station
        // where the cumulative rise since the start (or a would-be landing) passes 30 in.
        private void CheckRampLandings(FeatureLine line, GradingResult result)
        {
            double maxRiseFt = AdaComplianceStandards.RampMaxRiseInchesPerRun / 12.0; // 30 in -> 2.5 ft
            double cumulative = 0.0;
            var st = line.Stations;
            for (int i = 0; i < st.Count - 1; i++)
            {
                double rise = Math.Abs(st[i + 1].Point.Z - st[i].Point.Z);
                cumulative += rise;
                if (cumulative > maxRiseFt + 1e-9)
                {
                    result.Add(GradingSeverity.Error, "ada-landing", line.Name, i + 1,
                        $"Cumulative ramp rise reaches {cumulative * 12:F1} in by station {i + 1}, exceeding " +
                        $"the {AdaComplianceStandards.RampMaxRiseInchesPerRun:F0} in maximum per run. A level " +
                        "landing is required before this point. Hard ADA constraint.");
                    cumulative = 0.0; // assume a landing is inserted; continue checking the next run
                }
            }
        }
    }
}
