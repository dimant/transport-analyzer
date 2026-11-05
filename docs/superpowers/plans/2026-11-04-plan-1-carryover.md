# Carry-over for later plans (from plans 1 and 2)

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

- **Rewrite vocabulary descriptions.** The descriptions in `data/vocabulary.csv` are placeholders. The UI shows them on hover; `light_duty` in particular must explain that it is cars plus light trucks combined, as FHWA/BTS report it since 2007.
- **Selecting nulls.** Aggregate filters cannot select null dimension values, but null groups appear in results. Decide whether the UI needs `f.<dim>=null`.
- **Zero-event exposure cells.** A class/year with exposure but no events at all produces no `/api/rates` row, so its 0/x rate is invisible.
- **Regulation-class fallback.** When grouping rates by regulation class only, cells can fall back to the mode total. This is what lets Part 121 be compared with all road travel, but the asymmetry with the vehicle-class guard should be shown in the UI.
- **JSON conventions.** Object keys are camelCase. Tabular `columns` keep snake_case whitelist names. Data keys such as `unknown:vehicle_class` are opaque strings. BTS sources report `mode: "multi"`, which is not a vocabulary code.
- **Run records before 2026-11-04** keep PascalCase `raw_files` / `parameters`.

## User review pending

- `data/mappings/fars.csv` choices:
  - motor homes (body types 65, 73) map to `heavy_truck` / `road_commercial`
  - government heavy vehicles map to `road_commercial`
  - government special-use vehicles map to regulation class `other`, and `government` ranks first for operator type
  - special-use codes 6, 11, 19, 21, 23 and 24 were ambiguous

## Added after plan 2

Plan 2 closed these plan-1 items: the empty-FARS-year guard, mixed granularity in rates, the `flight_hours` default, US-only rate scope, the BTS schedule question, the RawStore collision, and stuck `running` rows.

### Waiting on the user
- **Part 121 rates and cargo flights.** The Part 121 numerator includes all-cargo flights, while the denominator is passenger-miles. Of 4 occupant deaths in 2019–2023, 3 were on Atlas Air flight 3591 (DCA19MA086, `oper_pax_cargo = CARG`). Should Part 121 rates count passenger flights (`PAX`) only?
- **`data/mappings/ntsb.csv` choices:**
  - `AIR:TF:NUSC` maps to `business_jet`.
  - Airplanes with engine type TS, LR, HR, NONE, UNK or blank map to `unknown`.
  - Gyroplanes map to `helicopter`; homebuilt helicopters map to `experimental`.
  - Part 91 with a blank purpose maps to `private`.
  - `docs/verification/plan-2.md` Step 4 proposes additions: `AIR::121`, `AIR::129`, `091:UNK`, `AIR::091`.

### Plan 3 (frontend)
- **NTSB dates are UTC.** NTSB `occurred_on` is the UTC date, while FARS dates are local. Day-of-week and hour views must say so, or filter by source.
- **Rates labels and cases.** `/api/rates` `join_level` and `unmatched.reason` values are listed in the README. Aviation rates by class need `groupBy=vehicle_class,regulation_class`, because GA exposure exists only under `general_aviation`.
- **Sample key columns.** `/api/events/sample` rows lead with `source_id, source_key, vehicle_no`, so a sampled row can be linked back to its event. Samples stay stable across re-ingests.
- **Column order.** `Dimensions.All` is a dictionary. Clients should read `columns` rather than assume a fixed column order.

### Later
- **Remaining mixed-granularity gap.** The guard only recognises a child that was matched exactly. A sibling that reaches that child by dropping a dimension to null is not caught. No current data can trigger this; it becomes reachable only if highway exposure gains regulation-class rows.
- **Memory after NTSB ingest.** The app holds about 694 MiB after an NTSB ingest, because CsvStager buffers each whole table and the GC then keeps the heap. Fix by streaming the decode, or set `DOTNET_GCConserveMemory`.
- **NTSB Enterprise API.** The MDB download moves to the NTSB Enterprise API on 2027-04-05. Swap the adapter behind `IMdbExporter` and `NtsbAdapter.FetchAsync` before then.
- **Temp-directory sweep.** The startup sweep of `ta-ntsb-*` temp directories assumes a single app instance per host.
