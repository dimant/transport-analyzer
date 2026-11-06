using TransportAnalyzer.Api.Query;

namespace TransportAnalyzer.Tests;

public class RateJoinerTests
{
    private static readonly IReadOnlyDictionary<string, string?> Parents = new Dictionary<string, string?>
    {
        ["light_duty"] = null,
        ["passenger_car"] = "light_duty",
        ["light_truck"] = "light_duty",
        ["motorcycle"] = null,
        ["bus"] = null,
        ["light_aircraft"] = null,
        ["turboprop"] = null,
        ["airliner"] = null,
    };

    private static readonly IReadOnlyDictionary<string, string?> RegParents = new Dictionary<string, string?>
    {
        ["general_aviation"] = null,
        ["part_91"] = "general_aviation",
        ["part_135"] = "general_aviation",
        ["part_137"] = "general_aviation",
        ["part_133"] = "general_aviation",
        ["part_135_scheduled"] = null,
        ["part_121"] = null,
    };

    private static RateKey Key(string? vc, string? rc = null, int year = 2022) => new("highway", year, vc, rc);
    private static NumeratorCell Num(string? vc, long value, string? rc = null, int year = 2022) => new(Key(vc, rc, year), value);
    private static ExposureCell Exp(string? vc, double value, string? rc = null, int year = 2022) =>
        new(Key(vc, rc, year), value, "bts-passenger-miles", "survey_estimate");

    [Fact]
    public void Join_ExactClassMatch()
    {
        var result = RateJoiner.Join([Num("motorcycle", 5)], [Exp("motorcycle", 100)], Parents, RegParents, true, false);

        var row = Assert.Single(result.Rows);
        Assert.Equal(Key("motorcycle"), row.Key);
        Assert.Equal(5, row.Numerator);
        Assert.Equal(100, row.Denominator);
        Assert.Equal("vehicle_class", row.JoinLevel);
        Assert.Equal("bts-passenger-miles", row.ExposureSource);
        Assert.Equal("survey_estimate", row.Provenance);
        Assert.Empty(result.Unmatched);
    }

    [Fact]
    public void Join_RollsNumeratorUpToParent()
    {
        var result = RateJoiner.Join([Num("passenger_car", 3), Num("light_truck", 4)], [Exp("light_duty", 1000)],
            Parents, RegParents, true, false);

        var row = Assert.Single(result.Rows);
        Assert.Equal(Key("light_duty"), row.Key);
        Assert.Equal(7, row.Numerator);
        Assert.Equal(1000, row.Denominator);
        Assert.Equal("vehicle_class_parent", row.JoinLevel);
    }

    [Fact]
    public void Join_NotGroupedByClass_UsesModeTotal()
    {
        var result = RateJoiner.Join([Num("motorcycle", 5), Num("passenger_car", 3)],
            [Exp(null, 2000), Exp("motorcycle", 100)], Parents, RegParents, false, false);

        var row = Assert.Single(result.Rows);
        Assert.Equal(Key(null), row.Key);
        Assert.Equal(8, row.Numerator);
        Assert.Equal(2000, row.Denominator);
        Assert.Equal("mode", row.JoinLevel);
    }

    [Fact]
    public void Join_NoExposure_ReportedUnmatched()
    {
        var result = RateJoiner.Join([Num("bus", 2, year: 2023)], [Exp("bus", 50)], Parents, RegParents, true, false);

        Assert.Empty(result.Rows);
        var unmatched = Assert.Single(result.Unmatched);
        Assert.Equal(Key("bus", year: 2023), unmatched.Key);
        Assert.Equal(2, unmatched.Value);
    }

    [Fact]
    public void Join_GroupedByClass_DoesNotFallBackToModeTotal()
    {
        var result = RateJoiner.Join([Num("bus", 2)], [Exp(null, 2000)], Parents, RegParents, true, false);

        Assert.Empty(result.Rows);
        Assert.Equal(2, Assert.Single(result.Unmatched).Value);
    }

    [Fact]
    public void Join_FallbackOrder_PrefersClassOverParentAndRegulationOverNone()
    {
        Assert.Equal("vehicle_class+regulation_class", JoinLevel(Exp("passenger_car", 1, "part_a"), Exp("passenger_car", 1),
            Exp("light_duty", 1, "part_a"), Exp("light_duty", 1)));
        Assert.Equal("vehicle_class", JoinLevel(Exp("passenger_car", 1), Exp("light_duty", 1, "part_a"), Exp("light_duty", 1)));
        Assert.Equal("vehicle_class_parent+regulation_class", JoinLevel(Exp("light_duty", 1, "part_a"), Exp("light_duty", 1)));
        Assert.Equal("vehicle_class_parent", JoinLevel(Exp("light_duty", 1), Exp("light_duty", 1, "part_b")));

        static string JoinLevel(params ExposureCell[] exposure) =>
            Assert.Single(RateJoiner.Join([Num("passenger_car", 1, "part_a")], exposure, Parents, RegParents, true, true).Rows).JoinLevel;
    }

    [Fact]
    public void Join_RegulationClassRollsUpWhenExposureHasNoRegulationBreakdown()
    {
        var result = RateJoiner.Join([Num("bus", 2, "part_a"), Num("bus", 3, "part_b")], [Exp("bus", 50)], Parents, RegParents, true, true);

        var row = Assert.Single(result.Rows);
        Assert.Equal(Key("bus"), row.Key);
        Assert.Equal(5, row.Numerator);
        Assert.Equal("vehicle_class", row.JoinLevel);
    }

    [Fact]
    public void Join_NotGroupedByClass_IgnoresClassLevelExposureWithoutModeTotal()
    {
        var result = RateJoiner.Join([Num("motorcycle", 5)], [Exp("motorcycle", 100)], Parents, RegParents, false, false);

        Assert.Empty(result.Rows);
        var unmatched = Assert.Single(result.Unmatched);
        Assert.Equal(Key(null), unmatched.Key);
        Assert.Equal(5, unmatched.Value);
    }

    [Fact]
    public void Join_DuplicateExposureKey_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            RateJoiner.Join([Num("bus", 2)], [Exp("bus", 50), Exp("bus", 60)], Parents, RegParents, true, false));
    }

    private static NumeratorCell Air(string? vc, string? rc, long value) => new(new("aviation", 2022, vc, rc), value);
    private static ExposureCell AirExp(string? vc, string? rc, double value) =>
        new(new("aviation", 2022, vc, rc), value, "faa-ga-survey", "survey_estimate");
    private static RateKey AirKey(string? vc, string? rc) => new("aviation", 2022, vc, rc);

    [Fact]
    public void Join_RegulationParent_RollsUpToGeneralAviation()
    {
        var result = RateJoiner.Join([Air("light_aircraft", "part_91", 3), Air("light_aircraft", "part_135", 2)],
            [AirExp("light_aircraft", "general_aviation", 1000)], Parents, RegParents, true, true);

        var row = Assert.Single(result.Rows);
        Assert.Equal(AirKey("light_aircraft", "general_aviation"), row.Key);
        Assert.Equal(5, row.Numerator);
        Assert.Equal(1000, row.Denominator);
        Assert.Equal("vehicle_class+regulation_class_parent", row.JoinLevel);
        Assert.Empty(result.Unmatched);
    }

    [Fact]
    public void Join_RollUpBesideExactSibling_IsMixedGranularity()
    {
        var result = RateJoiner.Join([Air(null, "part_91", 3), Air(null, "part_133", 1)],
            [AirExp(null, "part_91", 900), AirExp(null, "general_aviation", 1000)], Parents, RegParents, false, true);

        var row = Assert.Single(result.Rows);
        Assert.Equal(AirKey(null, "part_91"), row.Key);
        Assert.Equal(3, row.Numerator);
        Assert.Equal("regulation_class", row.JoinLevel);
        var unmatched = Assert.Single(result.Unmatched);
        Assert.Equal(AirKey(null, "part_133"), unmatched.Key);
        Assert.Equal(1, unmatched.Value);
        Assert.Equal("mixed_granularity", unmatched.Reason);
    }

    [Fact]
    public void Join_GroupedByClass_Part121FallsBackToRegulationClass()
    {
        var result = RateJoiner.Join([Air("airliner", "part_121", 3), Air("turboprop", "part_121", 1), Num("bus", 19, "road_commercial")],
            [AirExp(null, "part_121", 5000), Exp("bus", 80)], Parents, RegParents, true, true);

        Assert.Equal([(AirKey(null, "part_121"), 4L, "regulation_class"), (Key("bus"), 19L, "vehicle_class")],
            result.Rows.Select(r => (r.Key, r.Numerator, r.JoinLevel)));
        Assert.Empty(result.Unmatched);
    }

    [Fact]
    public void Join_GroupedByClass_ClasslessRegulationKeyBesideClassedSibling_IsMixedGranularity()
    {
        var result = RateJoiner.Join([Air("light_aircraft", "part_91", 10), Air("unknown", "part_91", 2)],
            [AirExp("light_aircraft", "general_aviation", 1000), AirExp(null, "part_91", 900)], Parents, RegParents, true, true);

        var row = Assert.Single(result.Rows);
        Assert.Equal(AirKey("light_aircraft", "general_aviation"), row.Key);
        Assert.Equal(10, row.Numerator);
        var unmatched = Assert.Single(result.Unmatched);
        Assert.Equal(AirKey("unknown", "part_91"), unmatched.Key);
        Assert.Equal(2, unmatched.Value);
        Assert.Equal("mixed_granularity", unmatched.Reason);
    }

    [Fact]
    public void Join_Part135Scheduled_HasNoParent()
    {
        var result = RateJoiner.Join([Air("turboprop", "part_135_scheduled", 1)],
            [AirExp("turboprop", "general_aviation", 1000)], Parents, RegParents, true, true);

        Assert.Empty(result.Rows);
        var unmatched = Assert.Single(result.Unmatched);
        Assert.Equal(AirKey("turboprop", "part_135_scheduled"), unmatched.Key);
        Assert.Equal("no_exposure", unmatched.Reason);
    }

    [Fact]
    public void Join_Part121_NeverReachesGeneralAviation()
    {
        var result = RateJoiner.Join([Air("airliner", "part_121", 4)],
            [AirExp(null, "general_aviation", 1000), AirExp("airliner", "general_aviation", 500)],
            Parents, RegParents, true, true);

        Assert.Empty(result.Rows);
        var unmatched = Assert.Single(result.Unmatched);
        Assert.Equal(AirKey("airliner", "part_121"), unmatched.Key);
        Assert.Equal("no_exposure", unmatched.Reason);
    }

    [Fact]
    public void Join_VehicleClassMixedGranularity_IsGuarded()
    {
        var result = RateJoiner.Join([Num("passenger_car", 2), Num("light_truck", 3)],
            [Exp("passenger_car", 50), Exp("light_duty", 100)], Parents, RegParents, true, false);

        var row = Assert.Single(result.Rows);
        Assert.Equal(Key("passenger_car"), row.Key);
        Assert.Equal(2, row.Numerator);
        Assert.Equal("vehicle_class", row.JoinLevel);
        var unmatched = Assert.Single(result.Unmatched);
        Assert.Equal(Key("light_truck"), unmatched.Key);
        Assert.Equal(3, unmatched.Value);
        Assert.Equal("mixed_granularity", unmatched.Reason);
    }

    [Fact]
    public void Join_UnmatchedCellsWithoutAnyExposure_HaveReasonNoExposure()
    {
        var result = RateJoiner.Join([Num("bus", 2, year: 2023)], [Exp("bus", 50)], Parents, RegParents, true, false);

        Assert.Equal("no_exposure", Assert.Single(result.Unmatched).Reason);
    }

    [Fact]
    public void Join_DropToNullBesideExactChild_IsNotGuarded()
    {
        // Dropping a dimension to null (bus has no regulation breakdown) is not a parent fallback: an exact
        // (bus, part_a) sibling elsewhere must not pull the (bus, null) roll-up into mixed_granularity.
        var result = RateJoiner.Join([Num("bus", 2, "part_a"), Num("bus", 3, "part_b")],
            [Exp("bus", 50, "part_a"), Exp("bus", 60)], Parents, RegParents, true, true);

        Assert.Equal([(Key("bus"), 3L), (Key("bus", "part_a"), 2L)], result.Rows.Select(r => (r.Key, r.Numerator)));
        Assert.Empty(result.Unmatched);
    }

    [Fact]
    public void Join_GuardedParent_DropsExactContributionsToParentToo()
    {
        // light_duty's own cell matched (light_duty, null) exactly, but that row is dropped once it is mixed, so the
        // cell goes to unmatched with the roll-ups rather than leaving a row whose numerator misses light_truck.
        var result = RateJoiner.Join([Num("passenger_car", 2), Num("light_truck", 3), Num("light_duty", 1)],
            [Exp("passenger_car", 50), Exp("light_duty", 100)], Parents, RegParents, true, false);

        Assert.Equal(Key("passenger_car"), Assert.Single(result.Rows).Key);
        Assert.Equal([(Key("light_duty"), "mixed_granularity"), (Key("light_truck"), "mixed_granularity")],
            result.Unmatched.Select(u => (u.Key, u.Reason)));
    }

    [Fact]
    public void Join_Part121VsRoad_HighwayRollsToModeTotalUnguarded()
    {
        // Plan-1 first experiment: grouped by regulation class only, highway cells drop to the highway total (not a
        // parent fallback) while Part 121 matches its own aviation row exactly.
        var result = RateJoiner.Join([Num(null, 10, "road_private"), Num(null, 2, "road_commercial"), Air(null, "part_121", 1)],
            [Exp(null, 5000), AirExp(null, "part_121", 900)], Parents, RegParents, false, true);

        Assert.Equal([(AirKey(null, "part_121"), 1L, "regulation_class"), (Key(null), 12L, "mode")],
            result.Rows.Select(r => (r.Key, r.Numerator, r.JoinLevel)));
        Assert.Empty(result.Unmatched);
    }

    public static TheoryData<string> ConservationCases => new(Cases.Keys);

    private static readonly Dictionary<string, (NumeratorCell[] N, ExposureCell[] E, bool ByVc, bool ByRc)> Cases = new()
    {
        ["exact"] = ([Num("motorcycle", 5)], [Exp("motorcycle", 100)], true, false),
        ["parent"] = ([Num("passenger_car", 3), Num("light_truck", 4)], [Exp("light_duty", 1000)], true, false),
        ["mode"] = ([Num("motorcycle", 5), Num("passenger_car", 3)], [Exp(null, 2000), Exp("motorcycle", 100)], false, false),
        ["no_exposure"] = ([Num("bus", 2, year: 2023)], [Exp("bus", 50)], true, false),
        ["no_mode_fallback"] = ([Num("bus", 2)], [Exp(null, 2000)], true, false),
        ["reg_rollup"] = ([Num("bus", 2, "part_a"), Num("bus", 3, "part_b")], [Exp("bus", 50)], true, true),
        ["ga_parent"] = ([Air("light_aircraft", "part_91", 3), Air("light_aircraft", "part_135", 2)],
            [AirExp("light_aircraft", "general_aviation", 1000)], true, true),
        ["ga_mixed"] = ([Air(null, "part_91", 3), Air(null, "part_133", 1)],
            [AirExp(null, "part_91", 900), AirExp(null, "general_aviation", 1000)], false, true),
        ["part_135_scheduled"] = ([Air("turboprop", "part_135_scheduled", 1)],
            [AirExp("turboprop", "general_aviation", 1000)], true, true),
        ["part_121"] = ([Air("airliner", "part_121", 4)],
            [AirExp(null, "general_aviation", 1000), AirExp("airliner", "general_aviation", 500)], true, true),
        ["part_121_class_free"] = ([Air("airliner", "part_121", 3), Air("turboprop", "part_121", 1), Num("bus", 19, "road_commercial")],
            [AirExp(null, "part_121", 5000), Exp("bus", 80)], true, true),
        ["classless_reg_mixed"] = ([Air("light_aircraft", "part_91", 10), Air("unknown", "part_91", 2)],
            [AirExp("light_aircraft", "general_aviation", 1000), AirExp(null, "part_91", 900)], true, true),
        ["vc_mixed"] = ([Num("passenger_car", 2), Num("light_truck", 3), Num("light_duty", 1)],
            [Exp("passenger_car", 50), Exp("light_duty", 100)], true, false),
        ["combined"] = ([Num("passenger_car", 2), Num("light_truck", 3), Num("motorcycle", 7, "part_a"),
                Num("bus", 1, year: 2023), Air(null, "part_91", 3), Air(null, "part_133", 1), Air("airliner", "part_121", 4),
                Air("light_aircraft", "part_137", 6)],
            [Exp("passenger_car", 50), Exp("light_duty", 100), Exp("motorcycle", 9), Exp("bus", 1),
                AirExp(null, "part_91", 900), AirExp(null, "general_aviation", 1000), AirExp("light_aircraft", "general_aviation", 10)],
            true, true),
    };

    [Theory]
    [MemberData(nameof(ConservationCases))]
    public void Join_ConservesNumerator(string name)
    {
        var (numerators, exposure, byVc, byRc) = Cases[name];
        var result = RateJoiner.Join(numerators, exposure, Parents, RegParents, byVc, byRc);

        Assert.Equal(numerators.Sum(n => n.Value), result.Rows.Sum(r => r.Numerator) + result.Unmatched.Sum(u => u.Value));
    }
}
