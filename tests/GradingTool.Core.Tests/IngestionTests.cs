using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GradingTool.Diagnostics;
using GradingTool.Geometry;
using GradingTool.Ingestion;
using GradingTool.Units;
using Xunit;

namespace GradingTool.Tests
{
    /// <summary>Writes fixture content to a temp file with a chosen extension.</summary>
    internal sealed class TempFile : IDisposable
    {
        public string Path { get; }
        public TempFile(string content, string ext)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "gt_ing_" + Guid.NewGuid().ToString("N") + ext);
            File.WriteAllText(Path, content);
        }
        public void Dispose() { try { File.Delete(Path); } catch { } }
    }

    // Realistic Texas South Central coordinates (Bexar): easting ~ 2.1M, northing ~ 13.7M ft.
    internal static class Fix
    {
        public const double E0 = 2_100_000.0;
        public const double N0 = 13_700_000.0;
    }

    // ---- LandXML reader --------------------------------------------------------------
    public class LandXmlReaderTests
    {
        private static string SurfaceXml(string linearUnit, int? epsg) =>
            $@"<?xml version=""1.0""?>
<LandXML xmlns=""http://www.landxml.org/schema/LandXML-1.2"" version=""1.2"">
  <Units><Imperial linearUnit=""{linearUnit}"" areaUnit=""squareFoot"" volumeUnit=""cubicYard""/></Units>
  {(epsg.HasValue ? $@"<CoordinateSystem epsgCode=""{epsg}"" horizontalName=""TX""/>" : "")}
  <Surfaces>
    <Surface name=""EG"">
      <Definition surfType=""TIN"">
        <Pnts>
          <P id=""1"">{Fix.N0} {Fix.E0} 100.0</P>
          <P id=""2"">{Fix.N0} {Fix.E0 + 100} 105.0</P>
          <P id=""3"">{Fix.N0 + 100} {Fix.E0} 100.0</P>
          <P id=""4"">{Fix.N0 + 100} {Fix.E0 + 100} 105.0</P>
        </Pnts>
        <Faces>
          <F>1 2 3</F><F>2 4 3</F>
        </Faces>
      </Definition>
    </Surface>
  </Surfaces>
</LandXML>";

        [Fact]
        public void Reads_surface_points_swapping_northing_easting()
        {
            using var f = new TempFile(SurfaceXml("USSurveyFoot", 2278), ".xml");
            var log = new CollectingGradingLog();
            var surfaces = LandXmlReader.ReadSurfaces(f.Path, log);

            Assert.Single(surfaces);
            var s = surfaces[0];
            Assert.Equal(4, s.Points.Count);
            // First P is "N0 E0 100" -> X=E0, Y=N0.
            Assert.Equal(Fix.E0, s.Points[0].X, 3);
            Assert.Equal(Fix.N0, s.Points[0].Y, 3);
            Assert.Equal(CoordinateReferenceSystem.TexasSouthCentral, s.Crs);
        }

        [Fact]
        public void Metric_landxml_is_converted_to_survey_feet_and_logged()
        {
            using var f = new TempFile(SurfaceXml("meter", 2278), ".xml");
            var log = new CollectingGradingLog();
            var s = LandXmlReader.ReadSurfaces(f.Path, log)[0];

            // 100 m easting delta between P1 and P2 -> ~328.083 US survey ft.
            double dx = s.Points[1].X - s.Points[0].X;
            Assert.Equal(100.0 * LinearUnits.UsSurveyFeetPerMeter, dx, 3);
            Assert.True(log.Contains(GradingLogLevel.Info, "converted"));
            Assert.Equal(LinearUnit.Meter, s.SourceUnit);
        }

        [Fact]
        public void Ambiguous_foot_is_read_as_survey_with_warning()
        {
            using var f = new TempFile(SurfaceXml("foot", 2278), ".xml");
            var log = new CollectingGradingLog();
            var s = LandXmlReader.ReadSurfaces(f.Path, log)[0];
            Assert.Equal(LinearUnit.UsSurveyFoot, s.SourceUnit);
            Assert.True(log.Contains(GradingLogLevel.Warning, "ambiguous"));
        }

        [Fact]
        public void Missing_crs_yields_null_and_is_flagged_by_validator()
        {
            using var f = new TempFile(SurfaceXml("USSurveyFoot", null), ".xml");
            var s = LandXmlReader.ReadSurfaces(f.Path)[0];
            Assert.Null(s.Crs);
            var report = TopoValidator.Validate(s);
            Assert.Contains(report.Findings, x => x.Category == "crs" && x.Severity == FindingSeverity.Error);
        }
    }

    // ---- CSV / PNEZD reader ----------------------------------------------------------
    public class CsvPointReaderTests
    {
        [Fact]
        public void Reads_pnezd_with_header_and_skips_bad_rows()
        {
            string csv = string.Join("\n", new[]
            {
                "Point,Northing,Easting,Elevation,Description",
                $"1,{Fix.N0},{Fix.E0},100.0,GRD",
                $"2,{Fix.N0 + 50},{Fix.E0 + 50},101.0,GRD",
                "3,NOT,A,NUMBER,GRD",
                $"4,{Fix.N0 + 100},{Fix.E0},102.5,GRD",
            });
            using var f = new TempFile(csv, ".csv");
            var log = new CollectingGradingLog();
            var s = CsvPointReader.Read(f.Path, LinearUnit.UsSurveyFoot,
                CoordinateReferenceSystem.TexasSouthCentral, PointFileFormat.PNEZD, log);

            Assert.Equal(3, s.Points.Count);            // bad row skipped
            Assert.Equal(Fix.E0, s.Points[0].X, 3);     // easting -> X
            Assert.Equal(Fix.N0, s.Points[0].Y, 3);     // northing -> Y
            Assert.True(log.Contains(GradingLogLevel.Warning, "skipped"));
        }

        [Fact]
        public void Whitespace_delimited_no_header_parses()
        {
            string txt = $"{Fix.N0} {Fix.E0} 100.0\n{Fix.N0+10} {Fix.E0+10} 100.5\n{Fix.N0+20} {Fix.E0} 101.0";
            using var f = new TempFile(txt, ".txt");
            var s = CsvPointReader.Read(f.Path, format: PointFileFormat.NEZ);
            Assert.Equal(3, s.Points.Count);
        }

        [Fact]
        public void All_rows_bad_throws()
        {
            using var f = new TempFile("a,b,c,d,e\nx,y,z,w,v", ".csv");
            Assert.Throws<InvalidDataException>(() =>
                CsvPointReader.Read(f.Path, format: PointFileFormat.PNEZD));
        }
    }

    // ---- Validator: density / gaps / spikes ------------------------------------------
    public class TopoValidatorTests
    {
        private static SurfaceInput Grid(int n, double spacing, double slopePct, CoordinateReferenceSystem? crs)
        {
            // A perfect plane tilted at slopePct in +X, on an n x n grid.
            var pts = new List<Point3d>();
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    double x = Fix.E0 + i * spacing;
                    double y = Fix.N0 + j * spacing;
                    double z = 100.0 + (i * spacing) * (slopePct / 100.0);
                    pts.Add(new Point3d(x, y, z));
                }
            return SurfaceInput.FromSourcePoints("grid", "synthetic", pts, LinearUnit.UsSurveyFoot, crs);
        }

        [Fact]
        public void Clean_grid_is_fit_for_surface()
        {
            var s = Grid(6, 25, 3.0, CoordinateReferenceSystem.TexasSouthCentral);
            var r = TopoValidator.Validate(s);
            Assert.True(r.IsFitForSurface);
            Assert.False(r.HasErrors);
        }

        [Fact]
        public void Too_few_points_is_an_error()
        {
            var s = SurfaceInput.FromSourcePoints("tiny", "synthetic",
                new[] { new Point3d(0,0,0), new Point3d(1,1,1) },
                LinearUnit.UsSurveyFoot, CoordinateReferenceSystem.TexasSouthCentral);
            var r = TopoValidator.Validate(s);
            Assert.False(r.IsFitForSurface);
            Assert.Contains(r.Findings, x => x.Category == "density" && x.Severity == FindingSeverity.Error);
        }

        [Fact]
        public void Vertical_spike_is_flagged()
        {
            var s = Grid(6, 25, 1.0, CoordinateReferenceSystem.TexasSouthCentral);
            var pts = s.Points.ToList();
            pts[pts.Count / 2] = new Point3d(pts[pts.Count / 2].X, pts[pts.Count / 2].Y, 999.0); // bust
            var s2 = SurfaceInput.FromSourcePoints("spiked", "synthetic", pts,
                LinearUnit.UsSurveyFoot, CoordinateReferenceSystem.TexasSouthCentral);
            var r = TopoValidator.Validate(s2);
            Assert.Contains(r.Findings, x => x.Category == "spike");
        }

        [Fact]
        public void Isolated_point_is_flagged_as_gap()
        {
            var s = Grid(6, 25, 1.0, CoordinateReferenceSystem.TexasSouthCentral);
            var pts = s.Points.ToList();
            pts.Add(new Point3d(Fix.E0 + 100000, Fix.N0 + 100000, 100.0)); // far away
            var s2 = SurfaceInput.FromSourcePoints("gapped", "synthetic", pts,
                LinearUnit.UsSurveyFoot, CoordinateReferenceSystem.TexasSouthCentral);
            var r = TopoValidator.Validate(s2);
            Assert.Contains(r.Findings, x => x.Category == "gap");
        }
    }

    // ---- Multi-dataset CRS guard -----------------------------------------------------
    public class ProjectDatasetsTests
    {
        private static SurfaceInput Ds(string name, CoordinateReferenceSystem? crs) =>
            SurfaceInput.FromSourcePoints(name, "synthetic",
                new[] { new Point3d(0,0,0), new Point3d(1,0,0), new Point3d(0,1,0) },
                LinearUnit.UsSurveyFoot, crs);

        [Fact]
        public void Same_crs_merges()
        {
            var crs = CoordinateReferenceSystem.TexasSouthCentral;
            var result = ProjectDatasets.AssertCommonCrs(new[] { Ds("a", crs), Ds("b", crs) });
            Assert.Equal(crs, result);
        }

        [Fact]
        public void Different_zones_throw()
        {
            Assert.Throws<ProjectDatasets.CrsMismatchException>(() =>
                ProjectDatasets.AssertCommonCrs(new[]
                {
                    Ds("central", CoordinateReferenceSystem.TexasCentral),
                    Ds("south", CoordinateReferenceSystem.TexasSouthCentral),
                }));
        }

        [Fact]
        public void Unreferenced_dataset_throws()
        {
            Assert.Throws<ProjectDatasets.CrsMismatchException>(() =>
                ProjectDatasets.AssertCommonCrs(new[]
                {
                    Ds("ok", CoordinateReferenceSystem.TexasSouthCentral),
                    Ds("nocrs", null),
                }));
        }
    }

    // ---- Stub readers announce themselves --------------------------------------------
    public class StubReaderTests
    {
        [Fact]
        public void Dem_and_las_throw_notimplemented_with_guidance()
        {
            var dem = Assert.Throws<NotImplementedException>(() => StubReaders.ReadDem("x.tif"));
            Assert.Contains("GeoTIFF", dem.Message);
            var las = Assert.Throws<NotImplementedException>(() => StubReaders.ReadLas("x.las"));
            Assert.Contains("LAS", las.Message);
        }
    }
}
