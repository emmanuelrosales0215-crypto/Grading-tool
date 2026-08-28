using System;
using System.IO;
using GradingTool;
using GradingTool.Diagnostics;
using Xunit;
using SurfaceUse = GradingTool.AdaComplianceStandards.SurfaceUse;

namespace GradingTool.Tests
{
    /// <summary>
    /// Writes a MunicipalityConfig JSON to a temp file so the real Load() path
    /// (parse + normalize + validate + ADA check) is exercised, not a hand-built object.
    /// </summary>
    internal sealed class TempConfig : IDisposable
    {
        public string Path { get; }
        public TempConfig(string json)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "gradingtool_test_" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(Path, json);
        }
        public MunicipalityConfig Load(IGradingLog? log = null) => MunicipalityConfig.Load(Path, log);
        public void Dispose() { try { File.Delete(Path); } catch { /* best effort */ } }
    }

    // ---- Group 1: municipality stricter than ADA wins --------------------------------
    public class MunicipalityStricterThanAdaTests
    {
        [Fact]
        public void Municipality_tightening_accessible_parking_binds_below_ada()
        {
            // Elgin-style config declaring a stricter 1.5% accessible-parking ceiling.
            using var cfg = new TempConfig(@"{
                ""jurisdictionName"": ""Test City"",
                ""lastVerifiedDate"": ""2026-01-01"",
                ""accessibleParking"": { ""unit"": ""percent"", ""maxSlopePct"": 1.5, ""minSlopePct"": 1.0 }
            }");
            var rules = new ConservativeGradingRules(cfg.Load());

            ResolvedSlopeRule r = rules.Resolve(SurfaceUse.AccessibleParking);

            // Municipal 1.5% is stricter than the ADA design target (2.0 - 0.3 = 1.7%), so it binds.
            Assert.Equal(1.5, r.MaxSlopePct, 6);
            Assert.Contains("Test City", r.MaxSource);
        }

        [Fact]
        public void Municipality_tightening_standard_parking_binds()
        {
            using var cfg = new TempConfig(@"{
                ""jurisdictionName"": ""Test City"",
                ""lastVerifiedDate"": ""2026-01-01"",
                ""standardParking"": { ""unit"": ""percent"", ""maxSlopePct"": 3.0, ""minSlopePct"": 1.0 }
            }");
            var rules = new ConservativeGradingRules(cfg.Load());

            ResolvedSlopeRule r = rules.Resolve(SurfaceUse.StandardParking);

            // Default standard-parking max is 5%; municipal 3% is stricter and wins.
            Assert.Equal(3.0, r.MaxSlopePct, 6);
            Assert.Contains("Test City", r.MaxSource);
        }
    }

    // ---- Group 2: municipality looser than ADA -> hard fail at load ------------------
    public class MunicipalityLooserThanAdaTests
    {
        [Fact]
        public void Accessible_parking_above_ada_max_is_rejected_at_load()
        {
            using var cfg = new TempConfig(@"{
                ""jurisdictionName"": ""Bad City"",
                ""lastVerifiedDate"": ""2026-01-01"",
                ""accessibleParking"": { ""unit"": ""percent"", ""maxSlopePct"": 3.0, ""minSlopePct"": 1.0 }
            }");

            var ex = Assert.Throws<InvalidDataException>(() => cfg.Load());
            Assert.Contains("LOOSER", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ADA", ex.Message);
        }

        [Fact]
        public void Accessible_route_cross_above_ada_is_rejected()
        {
            using var cfg = new TempConfig(@"{
                ""jurisdictionName"": ""Bad City"",
                ""lastVerifiedDate"": ""2026-01-01"",
                ""accessibleRouteCross"": { ""unit"": ""percent"", ""maxSlopePct"": 2.5, ""minSlopePct"": 1.0 }
            }");

            Assert.Throws<InvalidDataException>(() => cfg.Load());
        }
    }

    // ---- Group 3: no municipality config -> ADA + defaults, still finite ------------
    public class NoMunicipalityTests
    {
        [Theory]
        [InlineData(SurfaceUse.AccessibleParking)]
        [InlineData(SurfaceUse.AccessibleRoute)]
        [InlineData(SurfaceUse.Ramp)]
        [InlineData(SurfaceUse.CurbRamp)]
        [InlineData(SurfaceUse.StandardParking)]
        [InlineData(SurfaceUse.DriveAisleOrRoad)]
        [InlineData(SurfaceUse.GeneralLot)]
        public void Every_surface_resolves_to_a_finite_band_without_a_config(SurfaceUse use)
        {
            var rules = new ConservativeGradingRules(municipality: null);

            ResolvedSlopeRule r = rules.Resolve(use);

            Assert.True(double.IsFinite(r.MaxSlopePct), $"{use} max must be finite");
            Assert.True(double.IsFinite(r.MinSlopePct), $"{use} min must be finite");
            Assert.False(double.IsPositiveInfinity(r.MaxSlopePct));
            Assert.True(r.MinSlopePct < r.MaxSlopePct);
        }

        [Fact]
        public void Accessible_parking_uses_ada_target_with_margin()
        {
            var rules = new ConservativeGradingRules();
            ResolvedSlopeRule r = rules.Resolve(SurfaceUse.AccessibleParking);
            Assert.Equal(1.7, r.MaxSlopePct, 6);          // 2.0 - 0.3
            Assert.True(r.IsHardAdaConstraint);
            Assert.Equal(2.0, r.LegalMaxSlopePct, 6);
        }
    }

    // ---- Group 4: safety margin applied exactly once --------------------------------
    public class SafetyMarginTests
    {
        [Fact]
        public void Ada_binding_applies_margin_once()
        {
            var rules = new ConservativeGradingRules();
            ResolvedSlopeRule r = rules.Resolve(SurfaceUse.AccessibleRoute); // running: 5.0 - 0.3
            Assert.Equal(4.7, r.MaxSlopePct, 6);
            Assert.Equal(AdaComplianceStandards.DesignSafetyMarginPct, r.SafetyMarginAppliedPct, 6);
        }

        [Fact]
        public void Municipal_override_does_not_also_subtract_margin()
        {
            // Municipal ADA tightening to 1.6% binds; margin must NOT be subtracted again
            // (would give 1.3%). The 1.6% is already the engineer's stated design value.
            using var cfg = new TempConfig(@"{
                ""jurisdictionName"": ""Test City"",
                ""lastVerifiedDate"": ""2026-01-01"",
                ""accessibleParking"": { ""unit"": ""percent"", ""maxSlopePct"": 1.6, ""minSlopePct"": 1.0 }
            }");
            var rules = new ConservativeGradingRules(cfg.Load());

            ResolvedSlopeRule r = rules.Resolve(SurfaceUse.AccessibleParking);

            Assert.Equal(1.6, r.MaxSlopePct, 6);
            Assert.Equal(0.0, r.SafetyMarginAppliedPct, 6);
        }
    }

    // ---- Group 5: ratio typed where percent expected --------------------------------
    public class UnitDiscriminatorTests
    {
        [Fact]
        public void Missing_unit_is_rejected()
        {
            using var cfg = new TempConfig(@"{
                ""jurisdictionName"": ""Test City"",
                ""lastVerifiedDate"": ""2026-01-01"",
                ""standardParking"": { ""maxSlopePct"": 5.0, ""minSlopePct"": 1.0 }
            }");

            var ex = Assert.Throws<InvalidDataException>(() => cfg.Load());
            Assert.Contains("Unit", ex.Message);
        }

        [Fact]
        public void Ratio_declared_is_converted_to_percent()
        {
            // 3:1 general-lot embankment declared as a ratio -> 33.3%, not 3%.
            using var cfg = new TempConfig(@"{
                ""jurisdictionName"": ""Test City"",
                ""lastVerifiedDate"": ""2026-01-01"",
                ""generalLot"": { ""unit"": ""ratio"", ""maxSlopePct"": 3.0, ""minSlopePct"": 100.0 }
            }");
            var config = cfg.Load();

            // max slope from steepest (3:1 -> 33.3%); min from flattest (100:1 -> 1%).
            Assert.Equal(33.333, config.GeneralLot!.MaxSlopePctNormalized, 2);
            Assert.Equal(1.0, config.GeneralLot!.MinSlopePctNormalized, 2);
        }

        [Fact]
        public void Ratio_typed_into_a_percent_field_is_caught_by_plausibility()
        {
            // Engineer means 3:1 (33.3%) but forgets to set unit=ratio and types 3 as percent.
            // Declared percent 3% for GeneralLot is inside 0.5-33.3% so it would parse... but
            // the danger case is the reverse: a percent value entered as ratio. Here we prove
            // an out-of-band percent (e.g. a 33 ratio mistakenly left as percent) is rejected.
            using var cfg = new TempConfig(@"{
                ""jurisdictionName"": ""Test City"",
                ""lastVerifiedDate"": ""2026-01-01"",
                ""standardParking"": { ""unit"": ""percent"", ""maxSlopePct"": 300.0, ""minSlopePct"": 1.0 }
            }");
            Assert.Throws<InvalidDataException>(() => cfg.Load());
        }
    }

    // ---- Group 6: non-ADA surface never returns infinity ----------------------------
    public class NonAdaSurfaceTests
    {
        [Theory]
        [InlineData(SurfaceUse.StandardParking)]
        [InlineData(SurfaceUse.DriveAisleOrRoad)]
        [InlineData(SurfaceUse.GeneralLot)]
        public void Non_ada_surface_has_finite_max_not_infinity(SurfaceUse use)
        {
            var rules = new ConservativeGradingRules();
            ResolvedSlopeRule r = rules.Resolve(use);
            Assert.False(double.IsInfinity(r.MaxSlopePct));
            Assert.False(double.IsNaN(r.MaxSlopePct));
        }

        [Fact]
        public void Ada_validate_reports_not_applicable_for_road_not_compliant()
        {
            // The scaffold bug: a 40% road must NOT come back IsCompliant.
            var result = AdaComplianceStandards.Validate(SurfaceUse.DriveAisleOrRoad, 40.0);
            Assert.Equal(AdaComplianceStandards.ComplianceStatus.NotApplicable, result.Status);
            Assert.False(result.IsCompliant);
            Assert.True(double.IsNaN(result.AllowedMaxSlopePct));
        }
    }

    // ---- Group 7: infeasible band (min >= max) -> hard error ------------------------
    public class InfeasibleBandTests
    {
        [Fact]
        public void Municipal_max_below_default_drainage_min_throws_at_resolve()
        {
            // The genuine collision the resolver owns: a municipality sets StandardParking
            // max to 0.8%. The config is valid on its own (0.6% min < 0.8% max, both in the
            // plausible band) and loads clean. But the tool's default drainage minimum for
            // standard parking is 1.0%, which the resolver takes as the floor. 1.0% min vs
            // 0.8% max leaves no buildable slope.
            using var cfg = new TempConfig(@"{
                ""jurisdictionName"": ""Squeeze City"",
                ""lastVerifiedDate"": ""2026-01-01"",
                ""standardParking"": { ""unit"": ""percent"", ""maxSlopePct"": 0.8, ""minSlopePct"": 0.6 }
            }");
            var config = cfg.Load(); // loads fine - the collision is cross-source, not in-field
            var rules = new ConservativeGradingRules(config);

            var ex = Assert.Throws<InfeasibleSlopeRuleException>(
                () => rules.Resolve(SurfaceUse.StandardParking));
            Assert.Equal(SurfaceUse.StandardParking, ex.Use);
            Assert.Contains("No buildable slope", ex.Message);
        }

        [Fact]
        public void Feasible_config_resolves_without_throwing()
        {
            // Guard the other direction: a snug-but-feasible band must NOT throw.
            using var cfg = new TempConfig(@"{
                ""jurisdictionName"": ""Tight City"",
                ""lastVerifiedDate"": ""2026-01-01"",
                ""standardParking"": { ""unit"": ""percent"", ""maxSlopePct"": 1.5, ""minSlopePct"": 1.0 }
            }");
            var rules = new ConservativeGradingRules(cfg.Load());
            ResolvedSlopeRule r = rules.Resolve(SurfaceUse.StandardParking);
            Assert.True(r.MinSlopePct < r.MaxSlopePct);
        }

        [Fact]
        public void In_field_min_at_or_above_max_is_rejected_at_load()
        {
            // The in-field version (min >= max within one rule) is caught earlier, at load.
            using var cfg = new TempConfig(@"{
                ""jurisdictionName"": ""Squeeze City"",
                ""lastVerifiedDate"": ""2026-01-01"",
                ""standardParking"": { ""unit"": ""percent"", ""maxSlopePct"": 6.0, ""minSlopePct"": 6.0 }
            }");
            Assert.Throws<InvalidDataException>(() => cfg.Load());
        }
    }

    // ---- Group 8: every plausibility-checked field rejects out-of-band --------------
    public class PlausibilityTests
    {
        [Theory]
        [InlineData("drivewayApproach", 40.0)]     // was unchecked in the scaffold
        [InlineData("standardParking", 40.0)]
        [InlineData("driveAisleOrRoad", 40.0)]
        [InlineData("generalLot", 90.0)]
        public void Out_of_band_percent_field_is_rejected(string field, double badMax)
        {
            using var cfg = new TempConfig($@"{{
                ""jurisdictionName"": ""Test City"",
                ""lastVerifiedDate"": ""2026-01-01"",
                ""{field}"": {{ ""unit"": ""percent"", ""maxSlopePct"": {badMax}, ""minSlopePct"": 1.0 }}
            }}");
            Assert.Throws<InvalidDataException>(() => cfg.Load());
        }

        [Theory]
        [InlineData("maxCutFillSlopeRatio", 33.3)]           // percent typed as ratio
        [InlineData("retainingWallTriggerSlopeRatio", 0.5)]  // rise/run typed as ratio
        public void Out_of_band_ratio_field_is_rejected(string field, double badRatio)
        {
            using var cfg = new TempConfig($@"{{
                ""jurisdictionName"": ""Test City"",
                ""lastVerifiedDate"": ""2026-01-01"",
                ""{field}"": {badRatio}
            }}");
            Assert.Throws<InvalidDataException>(() => cfg.Load());
        }

        [Fact]
        public void Stale_config_warning_goes_through_log_not_console()
        {
            using var cfg = new TempConfig(@"{
                ""jurisdictionName"": ""Undated City""
            }");
            var log = new CollectingGradingLog();
            cfg.Load(log);
            Assert.True(log.Contains(GradingLogLevel.Warning, "LastVerifiedDate"));
        }
    }

    // ---- Shipped configs load clean -------------------------------------------------
    public class ShippedConfigTests
    {
        [Theory]
        [InlineData("elgin-tx.json")]
        [InlineData("bexar-county-tx.json")]
        [InlineData("_template.json")]
        public void Shipped_config_loads_and_passes_ada(string file)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Municipalities", file);
            Assert.True(File.Exists(path), $"expected shipped config at {path}");
            var config = MunicipalityConfig.Load(path, new CollectingGradingLog());
            Assert.False(string.IsNullOrWhiteSpace(config.JurisdictionName));
            // No throw == parsed, normalized, validated, and passed the ADA cross-check.
        }
    }
}
