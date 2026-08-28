using System;
using GradingTool.Diagnostics;

namespace GradingTool.Ingestion
{
    /// <summary>
    /// Placeholders for the topo formats not yet implemented. Each throws with a clear reason
    /// and a note on what building it entails, so a caller that reaches for one gets a real
    /// message rather than a silent empty result.
    /// </summary>
    public static class StubReaders
    {
        /// <summary>
        /// TODO(Phase 2b): DEM / GeoTIFF raster reader. A DEM is a regular elevation grid;
        /// ingestion means reading the GeoTIFF's geotransform + CRS (EPSG from the file's
        /// GeoKeys), sampling cells to points, and converting metres->US survey feet if the
        /// raster is metric (common for USGS 3DEP / USDA sources). Needs a GeoTIFF decoder
        /// and, for large tiles, a spatial index the brute-force validator does not have.
        /// </summary>
        public static SurfaceInput ReadDem(string path, IGradingLog? log = null)
            => throw new NotImplementedException(
                "DEM/GeoTIFF ingestion is not implemented yet (Phase 2b). It requires a GeoTIFF " +
                "decoder to read the raster grid, its geotransform, and its EPSG from the file's " +
                "GeoKeys, then convert metres to US survey feet if metric. Use LandXML or CSV for now.");

        /// <summary>
        /// TODO(Phase 2b): LiDAR LAS/LAZ reader. LAS carries a point cloud with its own header
        /// (scale/offset, and a CRS in a VLR/WKT). LAZ is compressed LAS. Ingestion means
        /// decoding the header, applying scale+offset, reading the CRS, converting units, and
        /// thinning (millions of points must be decimated before triangulation).
        /// </summary>
        public static SurfaceInput ReadLas(string path, IGradingLog? log = null)
            => throw new NotImplementedException(
                "LiDAR LAS/LAZ ingestion is not implemented yet (Phase 2b). It requires an LAS " +
                "header/point decoder (and LAZ decompression), CRS from the VLR/WKT, unit " +
                "conversion, and point thinning before triangulation. Use LandXML or CSV for now.");

        /// <summary>
        /// TODO(Phase 4): DWG contour / 3D polyline reader. DWG is a closed binary format; the
        /// add-in reads contours and feature lines directly through the Civil 3D API, so this
        /// lives in GradingTool.Civil3D, not here. Outside the add-in a DWG must first be
        /// converted to DXF.
        /// </summary>
        public static SurfaceInput ReadDwgContours(string path, IGradingLog? log = null)
            => throw new NotImplementedException(
                "DWG contour ingestion is handled by the Civil 3D add-in (Phase 4) via the API, " +
                "or by converting DWG to DXF first. Not available in the platform-neutral core.");

        /// <summary>
        /// TODO(Phase 2c): IFC / BIM terrain reader. IFC site/terrain (IfcGeographicElement,
        /// IfcTriangulatedFaceSet) is uncommon in civil topo but appears on BIM-coordinated
        /// projects. Ingestion means parsing the IFC mesh and its placement/units.
        /// </summary>
        public static SurfaceInput ReadIfcTerrain(string path, IGradingLog? log = null)
            => throw new NotImplementedException(
                "IFC/BIM terrain ingestion is not implemented yet (Phase 2c). It requires parsing " +
                "IfcTriangulatedFaceSet geometry, placement, and unit assignment. Use LandXML or CSV for now.");
    }
}
