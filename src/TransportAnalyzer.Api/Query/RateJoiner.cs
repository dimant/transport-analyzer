namespace TransportAnalyzer.Api.Query;

public sealed record RateKey(string Mode, int Year, string? VehicleClass, string? RegulationClass);
public sealed record NumeratorCell(RateKey Key, long Value);
public sealed record ExposureCell(RateKey Key, double Value, string SourceId, string Provenance);
public sealed record RateRow(RateKey Key, long Numerator, double Denominator, string JoinLevel,
    string ExposureSource, string Provenance);
public sealed record RateResult(IReadOnlyList<RateRow> Rows, IReadOnlyList<NumeratorCell> Unmatched);

/// <summary>Pairs casualty counts with exposure without ever dividing a denominator across cells: each numerator
/// cell climbs to the finest exposure key the source provides and is summed there.</summary>
public static class RateJoiner
{
    /// <summary>
    /// Dimensions not grouped by are nulled on the numerator (which is summed over them); exposure is then used only
    /// from rows that have that dimension null themselves. Each numerator cell takes the first exposure key present
    /// of <c>(vc, rc)</c>, <c>(vc, null)</c>, <c>(parent(vc), rc)</c>, <c>(parent(vc), null)</c>, <c>(null, null)</c>;
    /// when grouped by vehicle class a key without a vehicle class is never used. Cells without a match are
    /// returned in <see cref="RateResult.Unmatched"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Two usable exposure cells share a key.</exception>
    public static RateResult Join(IEnumerable<NumeratorCell> numerators, IEnumerable<ExposureCell> exposure,
        IReadOnlyDictionary<string, string?> vehicleClassParent, bool byVehicleClass, bool byRegulationClass)
    {
        var denominators = new Dictionary<RateKey, ExposureCell>();
        foreach (var cell in exposure)
        {
            if (!byVehicleClass && cell.Key.VehicleClass is not null) continue;
            if (!byRegulationClass && cell.Key.RegulationClass is not null) continue;
            if (!denominators.TryAdd(cell.Key, cell))
                throw new ArgumentException($"more than one exposure cell for {cell.Key}", nameof(exposure));
        }

        var grouped = numerators
            .GroupBy(n => n.Key with
            {
                VehicleClass = byVehicleClass ? n.Key.VehicleClass : null,
                RegulationClass = byRegulationClass ? n.Key.RegulationClass : null,
            })
            .Select(g => new NumeratorCell(g.Key, g.Sum(n => n.Value)));

        // Per matched exposure key: summed numerator, and whether any cell reached it through its parent class.
        var matched = new Dictionary<RateKey, (long Sum, bool ViaParent)>();
        var unmatched = new List<NumeratorCell>();
        foreach (var cell in grouped)
        {
            if (Match(cell.Key, denominators, vehicleClassParent, byVehicleClass) is { } key)
            {
                var (sum, viaParent) = matched.GetValueOrDefault(key);
                matched[key] = (sum + cell.Value, viaParent || key.VehicleClass != cell.Key.VehicleClass);
            }
            else
                unmatched.Add(cell);
        }

        var rows = matched
            .Select(m => (Exposure: denominators[m.Key], m.Value.Sum, m.Value.ViaParent))
            .Select(x => new RateRow(x.Exposure.Key, x.Sum, x.Exposure.Value, JoinLevel(x.Exposure.Key, x.ViaParent),
                x.Exposure.SourceId, x.Exposure.Provenance))
            .OrderBy(r => r.Key, KeyOrder.Instance)
            .ToList();
        return new RateResult(rows, unmatched.OrderBy(u => u.Key, KeyOrder.Instance).ToList());
    }

    private static RateKey? Match(RateKey key, Dictionary<RateKey, ExposureCell> denominators,
        IReadOnlyDictionary<string, string?> vehicleClassParent, bool byVehicleClass)
    {
        var vc = key.VehicleClass;
        var rc = key.RegulationClass;
        var parent = vc is null ? null : vehicleClassParent.GetValueOrDefault(vc);

        var candidates = new List<(string? Vc, string? Rc)> { (vc, rc), (vc, null) };
        if (parent is not null) candidates.AddRange([(parent, rc), (parent, null)]);
        candidates.Add((null, null));

        foreach (var (cvc, crc) in candidates)
        {
            // Grouped by class, a classless key would silently merge every class into the mode total.
            if (byVehicleClass && cvc is null) continue;
            var candidate = key with { VehicleClass = cvc, RegulationClass = crc };
            if (denominators.ContainsKey(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>The shape of the matched exposure key. A class key reached by any numerator through its parent is a
    /// parent-level join, even if the parent class itself also contributed.</summary>
    private static string JoinLevel(RateKey matched, bool viaParent)
    {
        var classPart = matched.VehicleClass is null ? null : viaParent ? "vehicle_class_parent" : "vehicle_class";
        return (classPart, matched.RegulationClass) switch
        {
            (null, null) => "mode",
            (null, _) => "regulation_class",
            (_, null) => classPart,
            _ => classPart + "+regulation_class",
        };
    }

    private sealed class KeyOrder : IComparer<RateKey>
    {
        public static readonly KeyOrder Instance = new();

        public int Compare(RateKey? x, RateKey? y)
        {
            var c = string.CompareOrdinal(x!.Mode, y!.Mode);
            if (c == 0) c = x.Year.CompareTo(y.Year);
            if (c == 0) c = string.CompareOrdinal(x.VehicleClass, y.VehicleClass);
            if (c == 0) c = string.CompareOrdinal(x.RegulationClass, y.RegulationClass);
            return c;
        }
    }
}
