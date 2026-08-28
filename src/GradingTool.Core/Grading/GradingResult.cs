using System;
using System.Collections.Generic;
using System.Linq;

namespace GradingTool.Grading
{
    /// <summary>Severity of a grading finding.</summary>
    public enum GradingSeverity
    {
        /// <summary>Informational.</summary>
        Info,

        /// <summary>Design attention needed, but the surface can still be built.</summary>
        Warning,

        /// <summary>A rule is violated or grading is impossible as specified.</summary>
        Error
    }

    /// <summary>
    /// One issue the solver found or could not resolve, tied to a feature line and a station
    /// or segment, and citing the rule involved.
    /// </summary>
    public sealed class GradingFinding
    {
        /// <summary>Severity.</summary>
        public GradingSeverity Severity { get; }

        /// <summary>Category slug: running-slope, drainage-min, retaining-wall, ada-landing, infeasible, off-surface.</summary>
        public string Category { get; }

        /// <summary>Feature line involved.</summary>
        public string FeatureLine { get; }

        /// <summary>Station or segment index the finding attaches to (-1 if line-wide).</summary>
        public int StationIndex { get; }

        /// <summary>Description, including the specific rule where relevant.</summary>
        public string Message { get; }

        /// <summary>Construct a finding.</summary>
        public GradingFinding(GradingSeverity severity, string category, string featureLine, int stationIndex, string message)
        {
            Severity = severity;
            Category = category;
            FeatureLine = featureLine;
            StationIndex = stationIndex;
            Message = message;
        }

        /// <inheritdoc />
        public override string ToString()
            => $"[{Severity}] {Category} @ {FeatureLine}" +
               (StationIndex >= 0 ? $" sta {StationIndex}" : "") + $": {Message}";
    }

    /// <summary>
    /// The outcome of a grading solve: the (possibly adjusted) feature lines, all findings,
    /// and whether the iterative solve converged.
    /// </summary>
    public sealed class GradingResult
    {
        private readonly List<GradingFinding> _findings = new List<GradingFinding>();

        /// <summary>The feature lines after solving (elevations may have been adjusted).</summary>
        public IReadOnlyList<FeatureLine> FeatureLines { get; }

        /// <summary>True if the running-slope solve reached a stable state within the iteration budget.</summary>
        public bool Converged { get; internal set; }

        /// <summary>All findings.</summary>
        public IReadOnlyList<GradingFinding> Findings => _findings;

        /// <summary>Construct a result over the given lines.</summary>
        public GradingResult(IReadOnlyList<FeatureLine> featureLines) => FeatureLines = featureLines;

        internal void Add(GradingSeverity sev, string cat, string line, int sta, string msg)
            => _findings.Add(new GradingFinding(sev, cat, line, sta, msg));

        /// <summary>True if any finding is an error - grading is not fully compliant/feasible.</summary>
        public bool HasErrors => _findings.Any(f => f.Severity == GradingSeverity.Error);

        /// <summary>Count of findings at a severity.</summary>
        public int CountAt(GradingSeverity s) => _findings.Count(f => f.Severity == s);

        /// <summary>Findings in a category.</summary>
        public IEnumerable<GradingFinding> InCategory(string category)
            => _findings.Where(f => f.Category == category);

        /// <summary>One-line summary.</summary>
        public string Summary()
            => $"{(Converged ? "Converged" : "DID NOT converge")}; " +
               $"{CountAt(GradingSeverity.Error)} error(s), {CountAt(GradingSeverity.Warning)} warning(s), " +
               $"{CountAt(GradingSeverity.Info)} note(s).";
    }
}
