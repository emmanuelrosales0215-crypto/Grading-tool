using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.DatabaseServices;
using GradingTool;
using GradingTool.Grading;
using GtPoint = GradingTool.Geometry.Point3d;
using SurfaceUse = GradingTool.AdaComplianceStandards.SurfaceUse;

[assembly: CommandClass(typeof(GradingTool.Civil3D.GradingCommands))]

namespace GradingTool.Civil3D
{
    /// <summary>
    /// Civil 3D command entry points. Load the DLL with NETLOAD, then run the commands.
    /// These are intentionally thin: they translate drawing objects into the engine's own
    /// types, call the tested Core engine, and write results back - no engineering logic
    /// lives here.
    /// </summary>
    public sealed class GradingCommands
    {
        /// <summary>
        /// GRADEPROBE - pick a TIN surface, then pick points; report elevation and slope at
        /// each. Proves the <see cref="Civil3DSurface"/> bridge against a live surface.
        /// </summary>
        [CommandMethod("GRADEPROBE")]
        public void GradeProbe()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            Database db = doc.Database;

            ObjectId surfId = PromptForSurface(ed);
            if (surfId.IsNull) return;

            using Transaction tr = db.TransactionManager.StartTransaction();
            var surf = (TinSurface)tr.GetObject(surfId, OpenMode.ForRead);
            var surface = new Civil3DSurface(surf);
            ed.WriteMessage($"\nProbing surface '{surface.Name}'. Press Enter to finish.");

            while (true)
            {
                var ppo = new PromptPointOptions("\nPick a point (or Enter to stop): ") { AllowNone = true };
                PromptPointResult ppr = ed.GetPoint(ppo);
                if (ppr.Status != PromptStatus.OK) break;

                double x = ppr.Value.X, y = ppr.Value.Y;
                double? z = surface.ElevationAt(x, y);
                if (z == null) { ed.WriteMessage("\n  (outside the surface)"); continue; }
                var slope = surface.SlopeAt(x, y);
                ed.WriteMessage(slope.HasValue
                    ? $"\n  Elev {z.Value:F3} ft, slope {slope.Value.SlopePct:F2}% toward {slope.Value.AspectDegrees:F0}°"
                    : $"\n  Elev {z.Value:F3} ft (slope undefined at edge)");
            }
            tr.Commit();
        }

        /// <summary>
        /// GRADELINE - pick an existing TIN surface and a 3D polyline (the proposed feature
        /// line), choose the surface type, and run the grading solver. Reports every finding
        /// and offers to write the adjusted elevations back to the polyline.
        /// </summary>
        [CommandMethod("GRADELINE")]
        public void GradeLine()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            Database db = doc.Database;

            ObjectId surfId = PromptForSurface(ed);
            if (surfId.IsNull) return;

            var plo = new PromptEntityOptions("\nSelect the proposed 3D polyline (feature line): ");
            plo.SetRejectMessage("\nMust be a 3D polyline.");
            plo.AddAllowedClass(typeof(Polyline3d), exactMatch: true);
            PromptEntityResult pler = ed.GetEntity(plo);
            if (pler.Status != PromptStatus.OK) return;

            SurfaceUse use = PromptForUse(ed);

            using Transaction tr = db.TransactionManager.StartTransaction();
            var surf = (TinSurface)tr.GetObject(surfId, OpenMode.ForRead);
            var existing = new Civil3DSurface(surf);

            // Read the polyline vertices into engine stations. Endpoints are treated as fixed
            // controls (typical tie-ins); interior vertices are free for the solver to adjust.
            var pl = (Polyline3d)tr.GetObject(pler.ObjectId, OpenMode.ForRead);
            var vertexIds = new List<ObjectId>();
            foreach (ObjectId vId in pl) vertexIds.Add(vId);

            var stations = new List<Station>(vertexIds.Count);
            for (int i = 0; i < vertexIds.Count; i++)
            {
                var v = (PolylineVertex3d)tr.GetObject(vertexIds[i], OpenMode.ForRead);
                bool endpoint = i == 0 || i == vertexIds.Count - 1;
                stations.Add(new Station(new GtPoint(v.Position.X, v.Position.Y, v.Position.Z), isFixed: endpoint));
            }

            var line = new FeatureLine(pl.Handle.ToString(), use, stations);
            var log = new EditorGradingLog(ed);
            var rules = new ConservativeGradingRules(municipality: null, log: log);
            GradingResult result = new GradingSolver(existing, rules, null, log).Solve(new[] { line });

            ed.WriteMessage($"\n\nGRADELINE result: {result.Summary()}");
            foreach (var f in result.Findings)
                ed.WriteMessage($"\n  {f}");

            // Offer to write the solved elevations back to the polyline vertices.
            var yn = new PromptKeywordOptions("\nWrite adjusted elevations back to the polyline? ")
            {
                AllowNone = false
            };
            yn.Keywords.Add("Yes");
            yn.Keywords.Add("No");
            yn.Keywords.Default = "No";
            if (ed.GetKeywords(yn).StringResult == "Yes")
            {
                for (int i = 0; i < vertexIds.Count; i++)
                {
                    var v = (PolylineVertex3d)tr.GetObject(vertexIds[i], OpenMode.ForWrite);
                    var p = line.Stations[i].Point;
                    v.Position = new Autodesk.AutoCAD.Geometry.Point3d(p.X, p.Y, p.Z);
                }
                ed.WriteMessage("\nElevations updated.");
            }
            tr.Commit();
        }

        // ---- prompts --------------------------------------------------------------------

        private static ObjectId PromptForSurface(Editor ed)
        {
            var peo = new PromptEntityOptions("\nSelect a TIN surface: ");
            peo.SetRejectMessage("\nNot a TIN surface.");
            peo.AddAllowedClass(typeof(TinSurface), exactMatch: false);
            PromptEntityResult per = ed.GetEntity(peo);
            return per.Status == PromptStatus.OK ? per.ObjectId : ObjectId.Null;
        }

        private static SurfaceUse PromptForUse(Editor ed)
        {
            var pko = new PromptKeywordOptions("\nSurface type: ");
            foreach (var name in Enum.GetNames(typeof(SurfaceUse)))
                pko.Keywords.Add(name);
            pko.Keywords.Default = nameof(SurfaceUse.StandardParking);
            PromptResult pkr = ed.GetKeywords(pko);
            return pkr.Status == PromptStatus.OK
                ? (SurfaceUse)Enum.Parse(typeof(SurfaceUse), pkr.StringResult)
                : SurfaceUse.StandardParking;
        }
    }
}
