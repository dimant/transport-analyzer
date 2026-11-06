namespace TransportAnalyzer.Api.Query;

public sealed record RateKey(string Mode, int Year, string? VehicleClass, string? RegulationClass);
public sealed record NumeratorCell(RateKey Key, long Value);
public sealed record ExposureCell(RateKey Key, double Value, string SourceId, string Provenance);
public sealed record RateRow(RateKey Key, long Numerator, double Denominator, string JoinLevel,
    string ExposureSource, string Provenance);
public sealed record UnmatchedCell(RateKey Key, long Value, string Reason);
public sealed record RateResult(IReadOnlyList<RateRow> Rows, IReadOnlyList<UnmatchedCell> Unmatched);

/// <summary>Pairs casualty counts with exposure without ever dividing a denominator across cells: each numerator
/// cell climbs to the finest exposure key the source provides and is summed there.</summary>
public static class RateJoiner
{
    public const string NoExposure = "no_exposure";
    public const string MixedGranularity = "mixed_granularity";

    /// <summary>
    /// Dimensions not grouped by are nulled on the numerator (which is summed over them); exposure is then used only
    /// from rows that have that dimension null themselves. With <c>pv</c>/<c>pr</c> the parents of the cell's vehicle
    /// and regulation class, each numerator cell takes the first exposure key present of <c>(vc, rc)</c>,
    /// <c>(vc, pr)</c>, <c>(vc, null)</c>, <c>(pv, rc)</c>, <c>(pv, pr)</c>, <c>(pv, null)</c>, <c>(null, rc)</c>,
    /// <c>(null, pr)</c>, <c>(null, null)</c>; when grouped by vehicle class a key without a vehicle class is used only
    /// if it has a regulation class (<c>(null, rc)</c>, <c>(null, pr)</c>), never <c>(null, null)</c>. Cells without a
    /// match are unmatched with <see cref="NoExposure"/>; parent roll-ups that would sit beside an exactly matched child
    /// of the same parent are unmatched with <see cref="MixedGranularity"/>, and so are the cells matched to a classless
    /// regulation-class key <c>(null, X)</c> unless every numerator cell of that mode and year whose regulation class is
    /// <c>X</c> or has parent <c>X</c> matched it too, and no other matched key of that mode and year has a vehicle class
    /// and a regulation class that is <c>X</c>, <c>X</c>'s parent or a child of <c>X</c>.
    /// </summary>
    /// <exception cref="ArgumentException">Two usable exposure cells share a key.</exception>
    public static RateResult Join(IEnumerable<NumeratorCell> numerators, IEnumerable<ExposureCell> exposure,
        IReadOnlyDictionary<string, string?> vehicleClassParent, IReadOnlyDictionary<string, string?> regulationClassParent,
        bool byVehicleClass, bool byRegulationClass)
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

        var matches = new List<Matched>();
        var unmatched = new List<UnmatchedCell>();
        foreach (var cell in grouped)
        {
            if (Match(cell.Key, denominators, vehicleClassParent, regulationClassParent, byVehicleClass) is { } m)
                matches.Add(new Matched(cell, m.Key, m.VcViaParent, m.RcViaParent));
            else
                unmatched.Add(new UnmatchedCell(cell.Key, cell.Value, NoExposure));
        }

        // Mixed-granularity guard. A parent fallback is a substitution of pv or pr; dropping a dimension to null
        // because the exposure has no finer row (e.g. highway cells rolling up to the highway total while the regulation
        // class is grouped) is not one and is not guarded. If a parent key P was reached through a parent fallback
        // while another cell matched a child of P exactly, P's exposure would be counted beside a part of itself, so
        // every cell matched to P (including any that matched P exactly, since P's row is dropped) becomes unmatched.
        var exact = matches.Where(m => m.Key == m.Cell.Key).Select(m => m.Key).ToHashSet();
        var mixed = matches.Where(m => m.VcViaParent || m.RcViaParent).Select(m => m.Key).Distinct()
            .Where(p => exact.Any(c => IsChildOf(c, p, vehicleClassParent, regulationClassParent)))
            .ToHashSet();

        // A classless regulation key reached under a vehicle-class grouping is a roll-up over vehicle class. It is valid
        // only if all of that regulation class's cells roll up with it; otherwise its hours would be divided among a
        // subset of the class (e.g. unknown-class Part 91 aircraft against all Part 91 hours, light aircraft against
        // general-aviation hours). It is also invalid beside a classed key whose regulation class is the same, its parent
        // or its child, whose hours overlap it (e.g. unknown-class Part 133 against Part 133 hours while light aircraft
        // use general-aviation hours, which include Part 133).
        if (byVehicleClass)
        {
            var allCells = matches.Select(m => (m.Cell.Key, Matched: (RateKey?)m.Key))
                .Concat(unmatched.Select(u => (u.Key, Matched: (RateKey?)null))).ToList();
            foreach (var x in matches.Where(m => m.Key.VehicleClass is null).Select(m => m.Key).Distinct())
            {
                var complete = allCells
                    .Where(c => c.Key.Mode == x.Mode && c.Key.Year == x.Year && c.Key.RegulationClass is { } crc
                        && (crc == x.RegulationClass || regulationClassParent.GetValueOrDefault(crc) == x.RegulationClass))
                    .All(c => c.Matched == x);
                var overlapsClassed = matches.Any(m => m.Key.Mode == x.Mode && m.Key.Year == x.Year
                    && m.Key.VehicleClass is not null && m.Key.RegulationClass is { } mrc
                    && (mrc == x.RegulationClass || mrc == regulationClassParent.GetValueOrDefault(x.RegulationClass!)
                        || regulationClassParent.GetValueOrDefault(mrc) == x.RegulationClass));
                if (!complete || overlapsClassed) mixed.Add(x);
            }
        }
        unmatched.AddRange(matches.Where(m => mixed.Contains(m.Key))
            .Select(m => new UnmatchedCell(m.Cell.Key, m.Cell.Value, MixedGranularity)));

        var rows = matches
            .Where(m => !mixed.Contains(m.Key))
            .GroupBy(m => m.Key)
            .Select(g => (Exposure: denominators[g.Key], Sum: g.Sum(m => m.Cell.Value),
                VcViaParent: g.Any(m => m.VcViaParent), RcViaParent: g.Any(m => m.RcViaParent)))
            .Select(x => new RateRow(x.Exposure.Key, x.Sum, x.Exposure.Value,
                JoinLevel(x.Exposure.Key, x.VcViaParent, x.RcViaParent), x.Exposure.SourceId, x.Exposure.Provenance))
            .OrderBy(r => r.Key, KeyOrder.Instance)
            .ToList();
        return new RateResult(rows, unmatched.OrderBy(u => u.Key, KeyOrder.Instance).ToList());
    }

    private sealed record Matched(NumeratorCell Cell, RateKey Key, bool VcViaParent, bool RcViaParent);

    /// <summary>True when <paramref name="child"/> equals <paramref name="parent"/> except in one dimension, where the
    /// child's code has the parent's code as its parent.</summary>
    private static bool IsChildOf(RateKey child, RateKey parent, IReadOnlyDictionary<string, string?> vehicleClassParent,
        IReadOnlyDictionary<string, string?> regulationClassParent)
    {
        if (child.Mode != parent.Mode || child.Year != parent.Year) return false;
        if (child.RegulationClass == parent.RegulationClass && child.VehicleClass is { } cvc && parent.VehicleClass is { } pvc)
            return vehicleClassParent.GetValueOrDefault(cvc) == pvc;
        if (child.VehicleClass == parent.VehicleClass && child.RegulationClass is { } crc && parent.RegulationClass is { } prc)
            return regulationClassParent.GetValueOrDefault(crc) == prc;
        return false;
    }

    private static (RateKey Key, bool VcViaParent, bool RcViaParent)? Match(RateKey key,
        Dictionary<RateKey, ExposureCell> denominators, IReadOnlyDictionary<string, string?> vehicleClassParent,
        IReadOnlyDictionary<string, string?> regulationClassParent, bool byVehicleClass)
    {
        // Ungrouped dimensions are already null on the key, so their parents are null too and the candidates collapse.
        var vc = key.VehicleClass;
        var rc = key.RegulationClass;
        var pv = vc is null ? null : vehicleClassParent.GetValueOrDefault(vc);
        var pr = rc is null ? null : regulationClassParent.GetValueOrDefault(rc);

        string?[] rcs = [.. new[] { rc, pr, null }.Distinct()];
        foreach (var cvc in new[] { vc, pv, null }.Distinct())
        {
            foreach (var crc in rcs)
            {
                // Grouped by class, a key with neither class would silently merge every class into the mode total.
                if (byVehicleClass && cvc is null && crc is null) continue;
                var candidate = key with { VehicleClass = cvc, RegulationClass = crc };
                if (denominators.ContainsKey(candidate))
                    return (candidate, cvc is not null && cvc != vc, crc is not null && crc != rc);
            }
        }
        return null;
    }

    /// <summary>The shape of the matched exposure key. A class key reached by any numerator through its parent is a
    /// parent-level join in that dimension, even if the parent class itself also contributed.</summary>
    private static string JoinLevel(RateKey matched, bool vcViaParent, bool rcViaParent)
    {
        string?[] parts =
        [
            matched.VehicleClass is null ? null : vcViaParent ? "vehicle_class_parent" : "vehicle_class",
            matched.RegulationClass is null ? null : rcViaParent ? "regulation_class_parent" : "regulation_class",
        ];
        var present = parts.OfType<string>().ToList();
        return present.Count == 0 ? "mode" : string.Join("+", present);
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
