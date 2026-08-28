using System;
using System.Collections.Generic;
using System.Linq;

namespace GradingTool.Ingestion
{
    /// <summary>How serious an ingestion finding is.</summary>
    public enum FindingSeverity
    {
        /// <summary>Worth noting; does not block surface generation.</summary>
        Info,

        /// <summary>The data is usable but suspect; the engineer must review.</summary>
        Warning,

        /// <summary>The data cannot be trusted for a surface; generation should not proceed.</summary>
        Error
    }

    /// <summary>One issue found while ingesting or validating a dataset.</summary>
    public sealed class IngestionFinding
    {
        /// <summary>Severity.</summary>
        public FindingSeverity Severity { get; }

        /// <summary>Short category slug, e.g. "units", "crs", "gap", "spike", "density".</summary>
        public string Category { get; }

        /// <summary>Human-readable description.</summary>
        public string Message { get; }

        /// <summary>Construct a finding.</summary>
        public IngestionFinding(FindingSeverity severity, string category, string message)
        {
            Severity = severity;
            Category = category;
            Message = message;
        }

        /// <inheritdoc />
        public override string ToString() => $"[{Severity}] {Category}: {Message}";
    }

    /// <summary>
    /// The outcome of ingesting and validating one dataset: the findings, and whether the
    /// data is fit to build a surface from.
    /// </summary>
    public sealed class IngestionReport
    {
        private readonly List<IngestionFinding> _findings = new List<IngestionFinding>();

        /// <summary>All findings, in the order they were raised.</summary>
        public IReadOnlyList<IngestionFinding> Findings => _findings;

        /// <summary>Add a finding.</summary>
        public void Add(FindingSeverity severity, string category, string message)
            => _findings.Add(new IngestionFinding(severity, category, message));

        /// <summary>True if any finding is an error.</summary>
        public bool HasErrors => _findings.Any(f => f.Severity == FindingSeverity.Error);

        /// <summary>Count of findings at a severity.</summary>
        public int CountAt(FindingSeverity severity) => _findings.Count(f => f.Severity == severity);

        /// <summary>
        /// Whether the dataset is safe to triangulate into a surface. False when any error
        /// was raised - the caller must not build a surface from data with errors.
        /// </summary>
        public bool IsFitForSurface => !HasErrors;

        /// <summary>One-line summary for logs and reports.</summary>
        public string Summary()
            => $"{CountAt(FindingSeverity.Error)} error(s), " +
               $"{CountAt(FindingSeverity.Warning)} warning(s), " +
               $"{CountAt(FindingSeverity.Info)} note(s). " +
               (IsFitForSurface ? "Fit for surface generation." : "NOT fit for surface generation.");
    }
}
