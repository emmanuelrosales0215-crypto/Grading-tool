using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using GradingTool.Diagnostics;
using GradingTool.Geometry;
using GradingTool.Units;

namespace GradingTool.Ingestion
{
    /// <summary>
    /// Reads a LandXML file - the standard interchange for Civil 3D surfaces and COGO points.
    /// <para>
    /// Two LandXML conventions matter here:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// Point coordinates are written <c>northing easting elevation</c> (Y X Z), not X Y Z.
    /// This reader swaps them on the way in. Pass <c>northingFirst: false</c> for a
    /// non-conforming file.
    /// </description></item>
    /// <item><description>
    /// The <c>&lt;Units&gt;</c> block's <c>linearUnit</c> tells us metres vs feet. A bare
    /// "foot" is ambiguous between US survey and international; per the project rule (US
    /// survey feet unless stated otherwise) it is read as US survey foot, WITH a warning,
    /// so the assumption is visible rather than silent.
    /// </description></item>
    /// </list>
    /// </summary>
    public static class LandXmlReader
    {
        /// <summary>Read every TIN surface's points (and breaklines) from a LandXML file.</summary>
        /// <param name="path">LandXML file path.</param>
        /// <param name="log">Diagnostics sink.</param>
        /// <param name="northingFirst">
        /// True (default) if point text is northing-easting-elevation, per the LandXML norm.
        /// </param>
        /// <returns>One <see cref="SurfaceInput"/> per surface found; empty if none.</returns>
        public static IReadOnlyList<SurfaceInput> ReadSurfaces(
            string path, IGradingLog? log = null, bool northingFirst = true)
        {
            IGradingLog sink = log ?? NullGradingLog.Instance;
            XDocument doc = LoadDocument(path);

            LinearUnit unit = ReadLinearUnit(doc, path, sink);
            CoordinateReferenceSystem? crs = ReadCrs(doc, path, sink);

            var results = new List<SurfaceInput>();
            foreach (XElement surfaceEl in Descendants(doc.Root, "Surface"))
            {
                string name = (string?)surfaceEl.Attribute("name") ?? "surface";

                // LandXML point ids are arbitrary labels; faces reference them by id, so map
                // id -> parsed point. Points that fail to parse are skipped and counted.
                var byId = new Dictionary<string, Point3d>();
                var ordered = new List<Point3d>();
                int badPoints = 0;
                foreach (XElement pEl in Descendants(surfaceEl, "P"))
                {
                    if (TryParsePoint(pEl.Value, northingFirst, out Point3d pt))
                    {
                        string? id = (string?)pEl.Attribute("id");
                        if (id != null) byId[id] = pt;
                        ordered.Add(pt);
                    }
                    else badPoints++;
                }

                if (ordered.Count == 0)
                    continue;
                if (badPoints > 0)
                    sink.Warning($"{path}: surface '{name}' had {badPoints} unparseable point(s), skipped.");

                // Breaklines, if the file carries them, become polylines in source units.
                var breaklines = new List<IReadOnlyList<Point3d>>();
                foreach (XElement blEl in Descendants(surfaceEl, "Breakline"))
                {
                    var pts = new List<Point3d>();
                    foreach (XElement pntListEl in Descendants(blEl, "PntList3D"))
                        pts.AddRange(ParseCoordList(pntListEl.Value, northingFirst));
                    if (pts.Count >= 2) breaklines.Add(pts);
                }

                results.Add(SurfaceInput.FromSourcePoints(
                    name, path, ordered, unit, crs, sink,
                    breaklines.Count > 0 ? breaklines : null));
            }

            if (results.Count == 0)
                sink.Warning(
                    $"{path}: no TIN surfaces found. In Civil 3D use Output > Export to LandXML " +
                    "and tick the surface to export.");
            return results;
        }

        /// <summary>Read COGO points from a LandXML <c>&lt;CgPoints&gt;</c> block.</summary>
        public static SurfaceInput? ReadCogoPoints(
            string path, IGradingLog? log = null, bool northingFirst = true)
        {
            IGradingLog sink = log ?? NullGradingLog.Instance;
            XDocument doc = LoadDocument(path);
            LinearUnit unit = ReadLinearUnit(doc, path, sink);
            CoordinateReferenceSystem? crs = ReadCrs(doc, path, sink);

            var pts = new List<Point3d>();
            foreach (XElement cg in Descendants(doc.Root, "CgPoint"))
                if (TryParsePoint(cg.Value, northingFirst, out Point3d pt))
                    pts.Add(pt);

            if (pts.Count == 0)
            {
                sink.Warning($"{path}: no COGO points found in <CgPoints>.");
                return null;
            }
            return SurfaceInput.FromSourcePoints($"{Path.GetFileNameWithoutExtension(path)}-cogo", path, pts, unit, crs, sink);
        }

        // ---- parsing helpers -------------------------------------------------------------

        private static XDocument LoadDocument(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"LandXML file not found: {path}", path);
            try
            {
                return XDocument.Load(path);
            }
            catch (System.Xml.XmlException exc)
            {
                throw new InvalidDataException($"Could not parse LandXML {path}: {exc.Message}", exc);
            }
        }

        // Namespace-agnostic descendant search: LandXML files vary in namespace declaration.
        private static IEnumerable<XElement> Descendants(XElement? root, string localName)
            => root?.Descendants().Where(e => e.Name.LocalName == localName) ?? Enumerable.Empty<XElement>();

        private static bool TryParsePoint(string text, bool northingFirst, out Point3d point)
        {
            point = default;
            double[] v = ParseDoubles(text);
            if (v.Length < 3) return false;
            point = northingFirst ? new Point3d(v[1], v[0], v[2]) : new Point3d(v[0], v[1], v[2]);
            return true;
        }

        private static IEnumerable<Point3d> ParseCoordList(string text, bool northingFirst)
        {
            double[] v = ParseDoubles(text);
            for (int i = 0; i + 2 < v.Length + 1 && i + 2 < v.Length; i += 3)
                yield return northingFirst
                    ? new Point3d(v[i + 1], v[i], v[i + 2])
                    : new Point3d(v[i], v[i + 1], v[i + 2]);
        }

        private static double[] ParseDoubles(string text)
            => (text ?? string.Empty)
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                    ? (double?)d : null)
                .Where(d => d.HasValue)
                .Select(d => d!.Value)
                .ToArray();

        private static LinearUnit ReadLinearUnit(XDocument doc, string path, IGradingLog log)
        {
            XElement? units = Descendants(doc.Root, "Units").FirstOrDefault();
            // Under <Units> sits either <Imperial .../> or <Metric .../> with a linearUnit attr.
            XElement? system = units?.Elements().FirstOrDefault();
            string? linear = (string?)system?.Attribute("linearUnit");

            if (linear == null)
            {
                log.Warning(
                    $"{path}: no <Units> linearUnit declared. Assuming US survey feet per the " +
                    "project rule (US survey feet unless a source states otherwise). Verify the source.");
                return LinearUnit.UsSurveyFoot;
            }

            switch (linear.Trim().ToLowerInvariant())
            {
                case "meter":
                case "metre":
                case "meters":
                    return LinearUnit.Meter;
                case "ussurveyfoot":
                case "ussurveyfeet":
                case "surveyfoot":
                    return LinearUnit.UsSurveyFoot;
                case "internationalfoot":
                case "foot_international":
                    return LinearUnit.InternationalFoot;
                case "foot":
                case "feet":
                    log.Warning(
                        $"{path}: <Units> declares an ambiguous \"foot\". Reading as US survey foot " +
                        "per the project rule; if this file is international feet, coordinates will be " +
                        "~2 ppm off (feet apart at State Plane magnitudes). Confirm the source.");
                    return LinearUnit.UsSurveyFoot;
                default:
                    log.Warning(
                        $"{path}: unrecognized linearUnit \"{linear}\". Assuming US survey feet; verify the source.");
                    return LinearUnit.UsSurveyFoot;
            }
        }

        private static CoordinateReferenceSystem? ReadCrs(XDocument doc, string path, IGradingLog log)
        {
            XElement? cs = Descendants(doc.Root, "CoordinateSystem").FirstOrDefault();
            if (cs == null) return null;

            // LandXML carries the CRS as an epsgCode attribute, or a horizontalName / desc.
            string? epsgText = (string?)cs.Attribute("epsgCode");
            if (epsgText != null && int.TryParse(epsgText, out int epsg))
            {
                CoordinateReferenceSystem? known = CoordinateReferenceSystem.FromEpsg(epsg);
                if (known != null) return known;
                string? nm = (string?)cs.Attribute("horizontalName") ?? (string?)cs.Attribute("desc");
                log.Warning($"{path}: CRS EPSG:{epsg} is not a Texas State Plane zone this tool ships. Confirm the project zone.");
                return new CoordinateReferenceSystem(epsg, nm ?? $"EPSG:{epsg}", LinearUnit.UsSurveyFoot);
            }

            string? name = (string?)cs.Attribute("horizontalName") ?? (string?)cs.Attribute("desc");
            log.Warning(
                $"{path}: <CoordinateSystem> present but no epsgCode. Named \"{name ?? "unknown"}\"; " +
                "assign a project EPSG explicitly before merging with other datasets.");
            return null;
        }
    }
}
