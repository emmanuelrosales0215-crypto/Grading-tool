using System;
using System.Collections.Generic;
using GradingTool.Units;

namespace GradingTool.Ingestion
{
    /// <summary>
    /// A horizontal coordinate reference system, identified by EPSG code, together with the
    /// linear unit its coordinates are expressed in.
    /// <para>
    /// The brief requires every dataset to carry or be assigned a CRS, and requires all
    /// datasets in a project to share one Texas State Plane zone before they are merged.
    /// Two inputs in different CRSs that are combined without reprojection produce a silent
    /// misalignment, so mismatches are a hard error - see <see cref="ProjectDatasets"/>.
    /// </para>
    /// </summary>
    public sealed class CoordinateReferenceSystem : IEquatable<CoordinateReferenceSystem>
    {
        /// <summary>EPSG code, e.g. 2277 for NAD83 Texas Central (US survey feet).</summary>
        public int Epsg { get; }

        /// <summary>Human-readable name.</summary>
        public string Name { get; }

        /// <summary>The linear unit coordinates in this CRS are expressed in.</summary>
        public LinearUnit Unit { get; }

        /// <summary>Construct a CRS.</summary>
        public CoordinateReferenceSystem(int epsg, string name, LinearUnit unit)
        {
            Epsg = epsg;
            Name = name;
            Unit = unit;
        }

        // ---- Texas State Plane NAD83 zones (all defined in US survey feet) ---------------
        // These are the zones a Texas land-development project will use. The project must be
        // reprojected into whichever one covers the site before surfaces are merged.

        /// <summary>NAD83 Texas North (EPSG:2275).</summary>
        public static readonly CoordinateReferenceSystem TexasNorth =
            new CoordinateReferenceSystem(2275, "NAD83 / Texas North (ftUS)", LinearUnit.UsSurveyFoot);

        /// <summary>NAD83 Texas North Central (EPSG:2276).</summary>
        public static readonly CoordinateReferenceSystem TexasNorthCentral =
            new CoordinateReferenceSystem(2276, "NAD83 / Texas North Central (ftUS)", LinearUnit.UsSurveyFoot);

        /// <summary>NAD83 Texas Central (EPSG:2277) - City of Elgin.</summary>
        public static readonly CoordinateReferenceSystem TexasCentral =
            new CoordinateReferenceSystem(2277, "NAD83 / Texas Central (ftUS)", LinearUnit.UsSurveyFoot);

        /// <summary>NAD83 Texas South Central (EPSG:2278) - Bexar County / San Antonio.</summary>
        public static readonly CoordinateReferenceSystem TexasSouthCentral =
            new CoordinateReferenceSystem(2278, "NAD83 / Texas South Central (ftUS)", LinearUnit.UsSurveyFoot);

        /// <summary>NAD83 Texas South (EPSG:2279).</summary>
        public static readonly CoordinateReferenceSystem TexasSouth =
            new CoordinateReferenceSystem(2279, "NAD83 / Texas South (ftUS)", LinearUnit.UsSurveyFoot);

        private static readonly Dictionary<int, CoordinateReferenceSystem> KnownByEpsg =
            new Dictionary<int, CoordinateReferenceSystem>
            {
                [2275] = TexasNorth,
                [2276] = TexasNorthCentral,
                [2277] = TexasCentral,
                [2278] = TexasSouthCentral,
                [2279] = TexasSouth,
            };

        /// <summary>A known Texas State Plane CRS for an EPSG code, or null if not one we ship.</summary>
        public static CoordinateReferenceSystem? FromEpsg(int epsg)
            => KnownByEpsg.TryGetValue(epsg, out var crs) ? crs : null;

        /// <summary>True if this is one of the Texas State Plane NAD83 zones (EPSG 2275-2279).</summary>
        public bool IsTexasStatePlane => Epsg >= 2275 && Epsg <= 2279;

        /// <inheritdoc />
        public bool Equals(CoordinateReferenceSystem? other) => other != null && other.Epsg == Epsg;

        /// <inheritdoc />
        public override bool Equals(object? obj) => Equals(obj as CoordinateReferenceSystem);

        /// <inheritdoc />
        public override int GetHashCode() => Epsg;

        /// <inheritdoc />
        public override string ToString() => $"{Name} [EPSG:{Epsg}]";
    }
}
