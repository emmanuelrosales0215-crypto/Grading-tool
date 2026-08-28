using System;
using GradingTool.Diagnostics;

namespace GradingTool.Units
{
    /// <summary>
    /// Linear units a source dataset may arrive in.
    /// <para>
    /// There is deliberately no member called just "Foot". The US survey foot and the
    /// international foot differ by 2 parts per million, which is invisible on a site
    /// dimension and catastrophic on a State Plane coordinate: at a Texas easting of
    /// 3,000,000 ft the two definitions disagree by 6 ft, and at a northing of
    /// 13,500,000 ft by 27 ft. Anything that names a "foot" without saying which one is
    /// a latent misalignment, so this enum forces the choice.
    /// </para>
    /// </summary>
    public enum LinearUnit
    {
        /// <summary>1200/3937 m exactly. The foot Texas State Plane NAD83 zones are defined in.</summary>
        UsSurveyFoot,

        /// <summary>0.3048 m exactly. Common in DEM/LiDAR products and non-US CAD data.</summary>
        InternationalFoot,

        /// <summary>Metres. Common in DEM and LiDAR sources; must be converted on ingestion.</summary>
        Meter
    }

    /// <summary>
    /// Conversions between <see cref="LinearUnit"/> values.
    /// <para>
    /// The project working unit is the US survey foot (see
    /// <see cref="ProjectUnit"/>). Every conversion routed through
    /// <see cref="ToProjectUnit"/> is written to the supplied log, because the brief
    /// requires an imported dataset's units to be converted and recorded, never
    /// silently assumed to already be correct.
    /// </para>
    /// </summary>
    public static class LinearUnits
    {
        /// <summary>The unit all internal geometry is held in.</summary>
        public const LinearUnit ProjectUnit = LinearUnit.UsSurveyFoot;

        /// <summary>Metres per US survey foot: 1200/3937 exactly.</summary>
        public const double MetersPerUsSurveyFoot = 1200.0 / 3937.0;

        /// <summary>Metres per international foot: 0.3048 exactly.</summary>
        public const double MetersPerInternationalFoot = 0.3048;

        /// <summary>US survey feet per metre: 3937/1200 = 3.28083333...</summary>
        public const double UsSurveyFeetPerMeter = 3937.0 / 1200.0;

        /// <summary>International feet per metre: 1/0.3048 = 3.28083989...</summary>
        public const double InternationalFeetPerMeter = 1.0 / 0.3048;

        /// <summary>Metres represented by one unit of <paramref name="unit"/>.</summary>
        public static double MetersPer(LinearUnit unit)
        {
            switch (unit)
            {
                case LinearUnit.UsSurveyFoot: return MetersPerUsSurveyFoot;
                case LinearUnit.InternationalFoot: return MetersPerInternationalFoot;
                case LinearUnit.Meter: return 1.0;
                default:
                    // Fail loudly rather than assuming feet: a unit this code does not
                    // know how to convert must never reach the surface builder.
                    throw new ArgumentOutOfRangeException(
                        nameof(unit), unit, "Unhandled linear unit; refusing to guess a conversion factor.");
            }
        }

        /// <summary>Convert a length between two units.</summary>
        public static double Convert(double value, LinearUnit from, LinearUnit to)
            => from == to ? value : value * MetersPer(from) / MetersPer(to);

        /// <summary>
        /// Convert a length into the project working unit, recording what was done.
        /// </summary>
        /// <param name="value">Length in <paramref name="from"/> units.</param>
        /// <param name="from">The source dataset's declared unit.</param>
        /// <param name="log">Diagnostics sink; the conversion is written here.</param>
        /// <param name="context">
        /// What is being converted, for the log entry - typically the source filename.
        /// </param>
        public static double ToProjectUnit(double value, LinearUnit from, IGradingLog? log, string context)
        {
            if (from == ProjectUnit)
            {
                log.Info($"{context}: already in {Describe(ProjectUnit)}; no conversion applied.");
                return value;
            }

            double converted = Convert(value, from, ProjectUnit);
            log.Info(
                $"{context}: converted from {Describe(from)} to {Describe(ProjectUnit)} " +
                $"(factor {MetersPer(from) / MetersPer(ProjectUnit):G17}).");
            return converted;
        }

        /// <summary>Human-readable name, used in log entries and reports.</summary>
        public static string Describe(LinearUnit unit)
        {
            switch (unit)
            {
                case LinearUnit.UsSurveyFoot: return "US survey feet";
                case LinearUnit.InternationalFoot: return "international feet";
                case LinearUnit.Meter: return "metres";
                default: return unit.ToString();
            }
        }
    }
}
