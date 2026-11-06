# Carry-over for later plans (from plans 1 to 4)

Items found during plan 1 that were deliberately deferred. Each names the plan where it should be picked up.

## Plan 2 (NTSB, flight hours, schedules)

- **Guard empty FARS years.** The stale-crash delete in `fars-normalize.sql` removes a whole year's FARS events if a requested year stages zero accident rows. Fail the run instead when that happens, before the monthly schedule starts re-ingesting revised years.
- **Mixed-granularity exposure.** If an exposure source ever supplies both a parent class (`light_duty`) and a child (`passenger_car`), `RateJoiner` would give `light_truck` a denominator that also covers cars. Guard or decide before adding finer exposure sources.
- **`flight_hours` default source.** `/api/rates` returns 400 for `metric=flight_hours` without `exposureSource`. Add a default once the FAA GA / Part 135 adapter exists.
- **Airline passenger-miles are domestic only** in the current BTS Table 1-40 edition. Restrict the NTSB numerator to domestic Part 121 accidents to match.
- **BTS cannot be auto-refreshed.** bts.gov blocks automated clients, so the BTS adapters read from `data/inbox/`. A monthly Quartz schedule should skip them or only re-run when a new file appears.
- **RawStore same-second collision.** Two runs of one source within the same second collide on the raw-store path. The 409 rule makes this unlikely, but schedules could hit it.
- **Stuck `running` rows.** A run whose status write itself fails stays `running` until restart and blocks that source with 409. Consider a stale-run timeout.

## Plan 3 (frontend, stats library)

Done in plan 3 (see `docs/verification/plan-3.md`):
- **Vocabulary descriptions** are real; the UI shows them on hover, and `light_duty` says it is cars plus light trucks combined, as FHWA/BTS report it since 2007.
- **Regulation-class fallback.** Every rate row shows its `join_level` beside the rate, so the Part 121 vs all-road comparison (`regulation_class` against `mode`) is visible. The Part 121 presets also say that cargo is included.
- **JSON conventions.** The client reads `columns` for names and camelCase keys for `unmatched` / `unattributed`. Run records before 2026-11-04 are not shown by the UI.

Moved to "Later" below: selecting nulls, zero-event exposure cells, NTSB UTC dates for day/hour views.

## Plan 4 (regression)

Done in plan 4 (see `docs/verification/plan-4.md`):
- **Part 121 beside road vehicle classes.** `/api/rates` grouped by `vehicle_class,regulation_class` falls back to a classless regulation-class key such as `(null, part_121)`, guarded against splitting a regulation class (Task 1).
- **Logistic outcome from full counts.** The vehicle-grain measure `vehicles_with_occupant_fatality`, and a `rate_class` column on every rates table (Task 2).
- **`poisson-regression`** (rate ratios with an exposure offset, Poisson or negative binomial, overdispersion warning) and **`logistic-regression`** (odds ratios, separation warning), with a forest plot. Four presets use them.
- **Logistic overdispersion** is reported: a `Pearson dispersion` summary row, and a warning above 1.5 (the road logistic preset shows 8.46). The aviation logistic preset is filtered to US accidents, to match the US-only exposure.

## User review pending (accepted 2026-11-05)

- `data/mappings/fars.csv` choices:
  - motor homes (body types 65, 73) map to `heavy_truck` / `road_commercial`
  - government heavy vehicles map to `road_commercial`
  - government special-use vehicles map to regulation class `other`, and `government` ranks first for operator type
  - special-use codes 6, 11, 19, 21, 23 and 24 were ambiguous

## Added after plan 2

Plan 2 closed these plan-1 items: the empty-FARS-year guard, mixed granularity in rates, the `flight_hours` default, US-only rate scope, the BTS schedule question, the RawStore collision, and stuck `running` rows.

### Decided by the user (2026-11-05)
- **Part 121 rates include cargo flights.** The Part 121 numerator keeps all-cargo flights (e.g. Atlas Air 3591, DCA19MA086) although the denominator is passenger-miles. The UI should note this next to Part 121 rates.
- **`data/mappings/ntsb.csv` choices accepted as proposed**, including the Step 4 additions from `docs/verification/plan-2.md`: `AIR::121` and `AIR::129` → `airliner`, `AIR::091` → `light_aircraft`, `091:UNK` → `private`. Some `AIR::091` airplanes may be turbine aircraft.
- **`data/mappings/fars.csv` choices accepted as proposed** (listed under "User review pending" above).

### Plan 3 (frontend), all handled in the UI
- **NTSB dates are UTC.** NTSB `occurred_on` is the UTC date, while FARS dates are local. Day-of-week and hour views must say so, or filter by source.
- **Rates labels and cases.** `/api/rates` `join_level` and `unmatched.reason` values are listed in the README. Aviation rates by class need `groupBy=vehicle_class,regulation_class`, because GA exposure exists only under `general_aviation`.
- **Sample key columns.** `/api/events/sample` rows lead with `source_id, source_key, vehicle_no`, so a sampled row can be linked back to its event. Samples stay stable across re-ingests.
- **Column order.** `Dimensions.All` is a dictionary. Clients should read `columns` rather than assume a fixed column order.

### Later
- **Selecting nulls.** Aggregate filters cannot select null dimension values, but null groups appear in results (the UI shows them as `(none)`). Decide whether to add `f.<dim>=null`.
- **Zero-event exposure cells.** A class/year with exposure but no events at all produces no `/api/rates` row, so its 0/x rate is invisible.
- **NTSB dates are UTC** (from the Plan 3 notes above): future day-of-week and hour views must say so, or filter by source. No current view uses them.
- **Remaining mixed-granularity gap.** Plan 4 (Task 1) covers the classless regulation-class fallback: under a vehicle-class grouping, a cell matched to `(null, X)` is kept only if every cell of that mode and year whose regulation class is `X` or has parent `X` matched `(null, X)` too; otherwise they are all unmatched as `mixed_granularity`. Still not covered: the parent guard only recognises a child that was matched exactly, so a key reached by dropping the regulation class to null (`(vc, null)` or `(pv, null)`) beside a sibling matched to `(vc, rc)` is not caught, and nothing checks the classless fallback when only `regulation_class` is grouped. No current data can trigger this; it becomes reachable only if highway exposure gains regulation-class rows.
- **Memory after NTSB ingest.** The app holds about 694 MiB after an NTSB ingest, because CsvStager buffers each whole table and the GC then keeps the heap. Fix by streaming the decode, or set `DOTNET_GCConserveMemory`.
- **NTSB Enterprise API.** The MDB download moves to the NTSB Enterprise API on 2027-04-05. Swap the adapter behind `IMdbExporter` and `NtsbAdapter.FetchAsync` before then.
- **Quasi-binomial or scaled SEs for logistic regression.** `logistic-regression` warns when Pearson dispersion exceeds 1.5 but leaves the Wald intervals unscaled. Add a quasi-binomial option (scale SEs by √dispersion) so overdispersed fits, such as the road logistic preset (8.46), get honest intervals.
- **Temp-directory sweep.** The startup sweep of `ta-ntsb-*` temp directories assumes a single app instance per host.
