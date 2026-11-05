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
    };

    private static RateKey Key(string? vc, string? rc = null, int year = 2022) => new("highway", year, vc, rc);
    private static NumeratorCell Num(string? vc, long value, string? rc = null, int year = 2022) => new(Key(vc, rc, year), value);
    private static ExposureCell Exp(string? vc, double value, string? rc = null, int year = 2022) =>
        new(Key(vc, rc, year), value, "bts-passenger-miles", "survey_estimate");

    [Fact]
    public void Join_ExactClassMatch()
    {
        var result = RateJoiner.Join([Num("motorcycle", 5)], [Exp("motorcycle", 100)], Parents, true, false);

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
            Parents, true, false);

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
            [Exp(null, 2000), Exp("motorcycle", 100)], Parents, false, false);

        var row = Assert.Single(result.Rows);
        Assert.Equal(Key(null), row.Key);
        Assert.Equal(8, row.Numerator);
        Assert.Equal(2000, row.Denominator);
        Assert.Equal("mode", row.JoinLevel);
    }

    [Fact]
    public void Join_NoExposure_ReportedUnmatched()
    {
        var result = RateJoiner.Join([Num("bus", 2, year: 2023)], [Exp("bus", 50)], Parents, true, false);

        Assert.Empty(result.Rows);
        var unmatched = Assert.Single(result.Unmatched);
        Assert.Equal(Key("bus", year: 2023), unmatched.Key);
        Assert.Equal(2, unmatched.Value);
    }

    [Fact]
    public void Join_GroupedByClass_DoesNotFallBackToModeTotal()
    {
        var result = RateJoiner.Join([Num("bus", 2)], [Exp(null, 2000)], Parents, true, false);

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
            Assert.Single(RateJoiner.Join([Num("passenger_car", 1, "part_a")], exposure, Parents, true, true).Rows).JoinLevel;
    }

    [Fact]
    public void Join_RegulationClassRollsUpWhenExposureHasNoRegulationBreakdown()
    {
        var result = RateJoiner.Join([Num("bus", 2, "part_a"), Num("bus", 3, "part_b")], [Exp("bus", 50)], Parents, true, true);

        var row = Assert.Single(result.Rows);
        Assert.Equal(Key("bus"), row.Key);
        Assert.Equal(5, row.Numerator);
        Assert.Equal("vehicle_class", row.JoinLevel);
    }

    [Fact]
    public void Join_NotGroupedByClass_IgnoresClassLevelExposureWithoutModeTotal()
    {
        var result = RateJoiner.Join([Num("motorcycle", 5)], [Exp("motorcycle", 100)], Parents, false, false);

        Assert.Empty(result.Rows);
        var unmatched = Assert.Single(result.Unmatched);
        Assert.Equal(Key(null), unmatched.Key);
        Assert.Equal(5, unmatched.Value);
    }

    [Fact]
    public void Join_DuplicateExposureKey_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            RateJoiner.Join([Num("bus", 2)], [Exp("bus", 50), Exp("bus", 60)], Parents, true, false));
    }
}
