using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using GradingTool.Diagnostics;
using GradingTool.Geometry;
using GradingTool.Units;

namespace GradingTool.Ingestion
{
    /// <summary>
    /// Column order of a survey point file. Names follow surveyor convention:
    /// P=point number, N=northing, E=easting, Z=elevation, D=description.
    /// </summary>
    public enum PointFileFormat
    {
        /// <summary>Point, Northing, Easting, Z, Description - the most common export.</summary>
        PNEZD,

        /// <summary>Point, Easting, Northing, Z, Description.</summary>
        PENZD,

        /// <summary>Northing, Easting, Z (no point number or description).</summary>
        NEZ,

        /// <summary>Easting, Northing, Z.</summary>
        ENZ
    }

    /// <summary>
    /// Reads a delimited survey point file (CSV/TXT) into a <see cref="SurfaceInput"/>.
    /// <para>
    /// Real point files vary: comma or whitespace delimited, with or without a header row,
    /// and in a handful of column orders. This reader sniffs the delimiter and header, takes
    /// the column order explicitly (defaulting to PNEZD), and reports how many rows it
    /// skipped rather than silently dropping malformed data.
    /// </para>
    /// <para>
    /// A point file carries no units or CRS of its own, so both must be supplied by the
    /// caller (from the project settings). Per the project rule the unit defaults to US
    /// survey feet.
    /// </para>
    /// </summary>
    public static class CsvPointReader
    {
        /// <summary>Read a point file.</summary>
        /// <param name="path">File path.</param>
        /// <param name="sourceUnit">The unit the coordinates are in. Defaults to US survey feet.</param>
        /// <param name="crs">The CRS to tag the data with, or null to assign later.</param>
        /// <param name="format">Column order. Defaults to PNEZD.</param>
        /// <param name="log">Diagnostics sink.</param>
        public static SurfaceInput Read(
            string path,
            LinearUnit sourceUnit = LinearUnit.UsSurveyFoot,
            CoordinateReferenceSystem? crs = null,
            PointFileFormat format = PointFileFormat.PNEZD,
            IGradingLog? log = null)
        {
            IGradingLog sink = log ?? NullGradingLog.Instance;
            if (!File.Exists(path))
                throw new FileNotFoundException($"Point file not found: {path}", path);

            string[] lines = File.ReadAllLines(path);
            var points = new List<Point3d>();
            int skipped = 0;
            int firstDataLine = 0;

            // Sniff delimiter from the first non-empty line: comma, tab, or whitespace.
            string? sample = lines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
            char[] delimiters = sample != null && sample.Contains(',')
                ? new[] { ',' }
                : new[] { ' ', '\t' };

            // Skip a header row if the first data line's coordinate fields are non-numeric.
            if (sample != null && !LooksNumeric(sample, delimiters, format))
            {
                firstDataLine = Array.IndexOf(lines, sample) + 1;
                sink.Info($"{path}: header row detected and skipped.");
            }

            (int nIdx, int eIdx, int zIdx) = ColumnIndices(format);
            int need = Math.Max(nIdx, Math.Max(eIdx, zIdx)) + 1;

            for (int i = firstDataLine; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#"))
                    continue;

                string[] cols = line.Split(delimiters, StringSplitOptions.RemoveEmptyEntries);
                if (cols.Length < need
                    || !TryD(cols[nIdx], out double n)
                    || !TryD(cols[eIdx], out double e)
                    || !TryD(cols[zIdx], out double z))
                {
                    skipped++;
                    continue;
                }
                // Store as X=easting, Y=northing, Z=elevation.
                points.Add(new Point3d(e, n, z));
            }

            if (skipped > 0)
                sink.Warning($"{path}: skipped {skipped} row(s) that did not parse as {format}.");
            if (points.Count == 0)
                throw new InvalidDataException(
                    $"{path}: no valid points parsed as {format}. Check the column order and delimiter.");

            sink.Info($"{path}: read {points.Count} point(s) as {format}.");
            return SurfaceInput.FromSourcePoints(
                Path.GetFileNameWithoutExtension(path), path, points, sourceUnit, crs, sink);
        }

        private static (int n, int e, int z) ColumnIndices(PointFileFormat format)
        {
            switch (format)
            {
                case PointFileFormat.PNEZD: return (1, 2, 3); // P N E Z D
                case PointFileFormat.PENZD: return (2, 1, 3); // P E N Z D
                case PointFileFormat.NEZ: return (0, 1, 2);   // N E Z
                case PointFileFormat.ENZ: return (1, 0, 2);   // E N Z
                default: throw new ArgumentOutOfRangeException(nameof(format));
            }
        }

        private static bool LooksNumeric(string line, char[] delimiters, PointFileFormat format)
        {
            string[] cols = line.Split(delimiters, StringSplitOptions.RemoveEmptyEntries);
            (int n, int e, int z) = ColumnIndices(format);
            int need = Math.Max(n, Math.Max(e, z)) + 1;
            if (cols.Length < need) return false;
            return TryD(cols[n], out _) && TryD(cols[e], out _) && TryD(cols[z], out _);
        }

        private static bool TryD(string s, out double d)
            => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
    }
}
