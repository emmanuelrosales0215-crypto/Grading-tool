using System;
using System.Collections.Generic;
using System.Linq;
using GradingTool.Diagnostics;

namespace GradingTool.Ingestion
{
    /// <summary>
    /// Guards the rule that every dataset merged into one project surface must share a single
    /// Texas State Plane zone. Mismatched CRSs between inputs are a hard error, not a silent
    /// misalignment - two surveys in different zones overlaid without reprojection can be off
    /// by hundreds of feet.
    /// </summary>
    public static class ProjectDatasets
    {
        /// <summary>
        /// Thrown when datasets that are about to be merged do not all share one CRS.
        /// </summary>
        public sealed class CrsMismatchException : Exception
        {
            /// <summary>Create the exception.</summary>
            public CrsMismatchException(string message) : base(message) { }
        }

        /// <summary>
        /// Verify that all datasets share one CRS. Throws <see cref="CrsMismatchException"/>
        /// if any dataset is unreferenced or in a different zone than the others.
        /// </summary>
        /// <remarks>
        /// This does not reproject. Bringing differing zones into a common one requires a real
        /// projection engine (planned: ProjNet, referenced from the add-in/host, not guessed
        /// here). Until that is wired in, the correct behaviour is to refuse the merge loudly
        /// rather than combine misaligned data.
        /// </remarks>
        public static CoordinateReferenceSystem AssertCommonCrs(
            IReadOnlyList<SurfaceInput> datasets, IGradingLog? log = null)
        {
            IGradingLog sink = log ?? NullGradingLog.Instance;
            if (datasets == null || datasets.Count == 0)
                throw new ArgumentException("No datasets to merge.", nameof(datasets));

            var unreferenced = datasets.Where(d => d.Crs == null).ToList();
            if (unreferenced.Count > 0)
                throw new CrsMismatchException(
                    "Cannot merge: these datasets have no CRS assigned: " +
                    string.Join(", ", unreferenced.Select(d => d.Name)) +
                    ". Assign the project Texas State Plane zone to every dataset first.");

            var distinct = datasets.Select(d => d.Crs!).Distinct().ToList();
            if (distinct.Count > 1)
                throw new CrsMismatchException(
                    "Cannot merge datasets in different coordinate reference systems: " +
                    string.Join(" vs ", distinct.Select(c => c.ToString())) +
                    ". Reproject all inputs to one Texas State Plane zone before merging " +
                    "(reprojection is not yet wired in; align the sources upstream for now).");

            sink.Info($"All {datasets.Count} dataset(s) share CRS {distinct[0]}; safe to merge.");
            return distinct[0];
        }
    }
}
