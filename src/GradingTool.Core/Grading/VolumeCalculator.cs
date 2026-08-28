using System;
using GradingTool.Surface;

namespace GradingTool.Grading
{
    /// <summary>Cut, fill, and net earthwork between two surfaces, in cubic yards.</summary>
    public sealed class VolumeReport
    {
        /// <summary>Cut volume (existing above proposed), cubic yards, positive.</summary>
        public double CutCubicYards { get; }

        /// <summary>Fill volume (proposed above existing), cubic yards, positive.</summary>
        public double FillCubicYards { get; }

        /// <summary>Net = fill - cut. Positive means net import (fill), negative net export (cut).</summary>
        public double NetCubicYards => FillCubicYards - CutCubicYards;

        /// <summary>Plan area over which the two surfaces overlapped and volume was computed.</summary>
        public double EvaluatedAreaSqFt { get; }

        /// <summary>Construct.</summary>
        public VolumeReport(double cutCy, double fillCy, double areaSqFt)
        {
            CutCubicYards = cutCy;
            FillCubicYards = fillCy;
            EvaluatedAreaSqFt = areaSqFt;
        }

        /// <inheritdoc />
        public override string ToString()
            => $"Cut {CutCubicYards:F1} CY, Fill {FillCubicYards:F1} CY, " +
               $"Net {NetCubicYards:+0.0;-0.0} CY over {EvaluatedAreaSqFt:F0} sq ft.";
    }

    /// <summary>
    /// Computes cut/fill earthwork volume between an existing and a proposed surface by grid
    /// sampling: at each cell the elevation difference times the cell area contributes to cut
    /// or fill. Finer <c>spacing</c> gives a more accurate result at more cost.
    /// <para>
    /// This is the average-end-area idea applied on a grid rather than the TIN-exact
    /// prismoidal method; it converges to the true volume as spacing shrinks and is the
    /// standard approach for a quick, robust earthwork estimate. Cells where either surface
    /// has no data are skipped and excluded from the evaluated area.
    /// </para>
    /// </summary>
    public static class VolumeCalculator
    {
        /// <summary>Compute cut/fill between two surfaces over their overlapping extent.</summary>
        /// <param name="existing">Existing ground.</param>
        /// <param name="proposed">Proposed graded surface.</param>
        /// <param name="spacing">Grid cell size in feet (default 5).</param>
        public static VolumeReport CutFill(ISurface existing, ISurface proposed, double spacing = 5.0)
        {
            if (spacing <= 0) throw new ArgumentOutOfRangeException(nameof(spacing));

            var e = existing.Extents;
            var p = proposed.Extents;
            double minX = Math.Max(e.MinX, p.MinX), minY = Math.Max(e.MinY, p.MinY);
            double maxX = Math.Min(e.MaxX, p.MaxX), maxY = Math.Min(e.MaxY, p.MaxY);
            if (minX >= maxX || minY >= maxY)
                return new VolumeReport(0, 0, 0); // no overlap

            double cellArea = spacing * spacing;
            double cutSum = 0, fillSum = 0, area = 0;

            // Sample cell centres so each sample represents one full cell.
            for (double x = minX + spacing / 2; x < maxX; x += spacing)
                for (double y = minY + spacing / 2; y < maxY; y += spacing)
                {
                    double? ez = existing.ElevationAt(x, y);
                    double? pz = proposed.ElevationAt(x, y);
                    if (ez == null || pz == null) continue;

                    double diff = pz.Value - ez.Value; // + = fill, - = cut
                    if (diff > 0) fillSum += diff * cellArea;
                    else cutSum += -diff * cellArea;
                    area += cellArea;
                }

            const double cubicFeetPerCubicYard = 27.0;
            return new VolumeReport(cutSum / cubicFeetPerCubicYard, fillSum / cubicFeetPerCubicYard, area);
        }
    }
}
