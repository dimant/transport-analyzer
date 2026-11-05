# Plan 2 real-data verification

Run date: 2026-11-05 (UTC). Stack: the running app image, built at 2026-11-05T15:45:00Z from commit `baec181` (no code changed after it),
with the existing `pgdata` volume.
Test suite: `dotnet test` was run once, after the real-data checks and with no code changes: 195 passed, 1 skipped,
0 failed.

No defects found by this verification run, and no code or mapping changes were made during it. The final whole-branch
review later found one defect in what is recorded here as Concern 1 (samples changed after every re-ingest); it was
fixed in the commit range `deff435..HEAD` on `main` (fix in `52565c9`) and re-checked on real data (see Concern 1).
Two findings for the user are listed under "Concerns" at the end.

## Step 1: README

| Check | Expected | Observed | Result |
|---|---|---|---|
| Source list | `ntsb` and `faa-ga-survey` listed | source table with all five ids added to "Get the data" | pass |
| Schedules | times (UTC), how to change or disable | "Schedules" section; empty `Sources__fars__Schedule` and `Scheduling__Enabled=false` were each tried with `docker compose run` and removed the schedule(s) from the startup log | pass |
| FAA URL | changes every survey year; where to update | `Sources:faa-ga-survey:Url` in `src/TransportAnalyzer.Api/appsettings.json` or `Sources__faa-ga-survey__Url` (hyphenated env name tried, works) | pass |
| Aviation rates by class | `groupBy=vehicle_class,regulation_class` | documented, with why `groupBy=vehicle_class` alone gives no aviation rows (Step 6) | pass |
| `/api/events/sample` | one example | example added; it returned 200 and the same body twice | pass |
| `/api/rates` scope | US events only | documented | pass |

## Step 2: Ingest

FARS was re-ingested for 2019–2023, then NTSB (all years), then the FAA survey; both BTS tables were re-ingested too.

| Run | Run id | Expected | Observed | Duration | Rows | `unknown:*` | Result |
|---|---|---|---|---|---|---|---|
| `fars?years=2019,2020,2021,2022,2023` | `5d1ef31a-ed0f-4c27-a99b-9081035b96f1` | `succeeded` | `succeeded` | 118.73 s | event 186,398; vehicle 287,250 | operator_type 2,231; vehicle_class 6,300; regulation_class 2,231; maintenance_regime 258,979 | pass |
| `ntsb` | `bc07a050-d759-4661-8e3d-e337573997ad` | `succeeded` | `succeeded` | 410.35 s | event 31,453; vehicle 32,010 | operator_type 1,307; vehicle_class 5,306; regulation_class 1,241; maintenance_regime 6,175 | pass |
| `faa-ga-survey` | `aa686754-b7e8-4892-959c-f59ed6d5776a` | `succeeded` | `succeeded` | 0.69 s | exposure 132 | none | pass |
| `bts-passenger-miles` | `ed7e0596-448f-4fd0-ba4b-65520a84bef9` | `succeeded` | `succeeded` | 0.36 s | exposure 238 | none | pass |
| `bts-vehicle-miles` | `eb2f3cce-1195-4a66-8e77-e820118be142` | `succeeded` | `succeeded` | 0.44 s | exposure 243 | none | pass |

Notes:
- NTSB raw file: `avall.zip`, sha256 `c1a447835b41104737f8c3fcf6b2bbaa4b6a00f506fe7668b528d3b3a2f78bbc`. All 20 tables
  of the MDB are staged in `stg_ntsb` (e.g. events 31,453, aircraft 32,010, injury 182,616, narratives 28,671,
  flight_time 415,295, dt_aircraft 273,196 rows).
- App container memory during the NTSB run, `docker stats --no-stream` every ~4 s (97 samples; run started
  15:50:52): 344.5 MiB at 15:50:57, 167.9 MiB at 15:51:01, 686.1 MiB at 15:51:06, then flat at 686–694 MiB; **peak sampled 694.4 MiB** of a 7.653 GiB limit. A spike
  shorter than the sampling interval would not be seen. App CPU was 0.00–1.68% from 15:51:10 to the end at 15:57:43,
  so almost all of the 410 s was spent waiting on the database. The app still used 694 MiB after the run finished.
- FAA run `unmapped`: `exposure:row_label=Special Light-sport: 1` (same as the earlier run).
- BTS runs `unmapped`: `partial_sum:highway/light_duty/1960: 1`, `partial_sum:highway/light_duty/1965: 1` (as in plan 1).
- A further `fars?years=2019` run (`b2a6d367-7f24-4576-a3ba-390ca45faaab`, succeeded, 22.68 s, event 33,487, vehicle
  51,623) was made later to test the sample endpoint (Concerns). Both rate queries of Step 6 returned byte-identical
  responses after it.

## Step 3: NTSB totals (R-T9b)

API: `GET /api/events/aggregate?from=2019-01-01&to=2023-12-31&bucket=year&f.mode=aviation&f.country=US`.
Staged: `sum(nullif(trim(inj_tot_f),'')::int)` and `sum(coalesce(nullif(trim(inj_f_grnd),'')::int,0))` over
`stg_ntsb.events where trim(ev_country)='USA'`, grouped by `trim(ev_year)`.

| ev_year | Staged events | Staged `inj_tot_f` | Staged `inj_f_grnd` | Expected | API `events` | API `fatalities` | Result |
|---|---|---|---|---|---|---|---|
| 2019 | 1,302 | 410 | 8 | 418 | 1,302 | 418 | pass |
| 2020 | 1,139 | 333 | 1 | 334 | 1,139 | 334 | pass |
| 2021 | 1,216 | 353 | 3 | 356 | 1,216 | 356 | pass |
| 2022 | 1,271 | 336 | 1 | 337 | 1,271 | 337 | pass |
| 2023 | 1,217 | 309 | 2 | 311 | 1,217 | 311 | pass |

Other API measures (serious / minor / unattributed fatalities): 2019 257 / 358 / 0; 2020 210 / 325 / 0;
2021 246 / 319 / 0; 2022 254 / 372 / 0; 2023 231 / 330 / 0.

| Check (all 31,453 `ntsb` events) | Expected | Observed | Result |
|---|---|---|---|
| `fatalities <> Σ vehicle (occupant + nonoccupant) fatalities + unattributed_fatalities` | 0 | 0 | pass |
| same for serious injuries | 0 | 0 | pass |
| same for minor injuries | 0 | 0 | pass |
| events without a vehicle | 0 | 0 | pass |
| Σ `event.fatalities` vs staged Σ `inj_tot_f` + Σ `inj_f_grnd`, all years and countries | equal | 17,149 = 17,149 | pass |

Σ `unattributed_fatalities` over all NTSB events is 1 (ground fatalities go to the aircraft when an event has exactly
one aircraft).

**Recent years are provisional.** The `ntsb` source's own caveat (`/api/sources`): "Final reports take 12–24 months, so
recent years are incomplete and revised." This check compares the API with the snapshot it was loaded from (avall.zip
of 2026-11-01), so it holds regardless; but the 2022–2023 counts, and their fatalities, can still change in later
snapshots.

## Step 4: Unknowns (R-T9c)

`unknown:*` counts of the NTSB run are in Step 2. The run has 70 `unmapped` keys, 6,613 values in total. Keys above 50:

| Count | Key | Proposed `ntsb.csv` addition | Reasoning |
|---|---|---|---|
| 1,529 | `vehicle_class:vehicle_profile=AIR::NUSN` | none | Non-US, no engine row; outside `/api/rates` scope |
| 1,139 | `vehicle_class:vehicle_profile=AIR::NUSC` | none | as above |
| 893 | `vehicle_class:vehicle_profile=AIR::091` | `vehicle_class,vehicle_profile,AIR::091,light_aircraft,8` (user decision) | Part 91 airplane with no `engines` row. Of the 912 unknown-class Part 91 airplanes (this key plus `AIR:UNK:091` 13 and `AIR:TS:091` 6), `num_eng` is 1 for 706, 2 for 113, blank for 91, 0 for 1 and 4 for 1; some may be turbine aircraft. The profile has no engine type, so a mapping would be a guess |
| 708 | `operator_type:use_profile=:` | none | blank `far_part` and `type_fly` |
| 507 | `operator_type:use_profile=UNK:` | none | unknown Part, blank purpose |
| 361 | `vehicle_class:vehicle_profile=AIR::` | none | no engine type, no Part |
| 300 | `vehicle_class:vehicle_profile=AIR::129` | `vehicle_class,vehicle_profile,AIR::129,airliner,8` | Part 129 is a foreign air carrier; matches `AIR:TF:129` / `AIR:TJ:129` → `airliner` |
| 282 | `vehicle_class:vehicle_profile=AIR::UNK` | none | no engine type, unknown Part |
| 228 | `vehicle_class:vehicle_profile=AIR::121` | `vehicle_class,vehicle_profile,AIR::121,airliner,8` | Part 121 airplane with no engine row. Of the 229 unknown-class Part 121 airplanes (this key plus `AIR:UNK:121` 1), `num_eng` is 2 for 148, blank for 69, 4 for 7 and 3 for 5 |
| 133 | `vehicle_class:vehicle_profile=::NUSC` | none | no category, no engine, non-US |
| 96 | `vehicle_class:vehicle_profile=::` | none | nothing to map on |
| 60 | `vehicle_class:vehicle_profile=::NUSN` | none | as `::NUSC` |
| 52 | `operator_type:use_profile=091:UNK` | `operator_type,use_profile,091:UNK,private,3` | same as the existing `091:` (blank purpose) → `private` |

The mapping file was not edited during verification. On 2026-11-05 the user accepted the four proposed rows, and they were added to `ntsb.csv`.

Effect on the Step 6 numbers: of all NTSB vehicles with `vehicle_class=unknown`, 107 are US events 2019–2023, with 20
occupant fatalities (part_91 69 vehicles / 17 deaths, part_121 16 / 0, other 9 / 0, part_137 8 / 1, part_135 3 / 2,
part_135_scheduled 1 / 0, unknown 1 / 0). These appear in `unmatched` of the `flight_hours` query. The
regulation-class roll-up does not depend on `vehicle_class`. `unknown:regulation_class` (1,241) has no `unmapped` keys:
all of it comes from the explicit `:%` and `UNK:%` rules.

## Step 5: Exposure

`GET /api/exposure?metric=flight_hours&sourceId=faa-ga-survey&fromYear=2019&toYear=2023`: 55 rows, 11 per year, all
`mode=aviation`. Hours as returned (float noise such as `20582500.999999996` rounded here).

Provenance as returned: general_aviation total and part_135 `survey_estimate`; part_91, part_137 and part_133
`derived` (computed from Table 1.4 use rows), in every year. Only the general_aviation total has a standard error.

| Year | general_aviation total (survey_estimate) | part_91 (derived) | part_135 (survey_estimate) | part_137 (derived) | part_133 (derived) |
|---|---|---|---|---|---|
| 2019 | 25,565,930 (SE 255,659.3) | 20,582,501 | 3,765,242 | 1,060,163 | 158,025 |
| 2020 | 22,491,871 (SE 247,410.581) | 18,293,860 | 3,037,404 | 1,029,392 | 131,215 |
| 2021 | 26,440,574 (SE 290,846.314) | 20,689,616 | 4,474,791 | 1,120,395 | 155,772 |
| 2022 | 26,953,420 (SE 296,487.62) | 21,355,713 | 4,410,422 | 1,018,417 | 168,869 |
| 2023 | 28,562,708 (SE 314,189.788) | 23,396,520 | 3,613,379 | 1,361,431 | 191,378 |

Vehicle classes under `general_aviation` (`survey_estimate`):

| Year | light_aircraft | turboprop | business_jet | helicopter | experimental | other |
|---|---|---|---|---|---|---|
| 2019 | 14,431,419 | 2,619,092 | 3,926,485 | 2,996,531 | 1,268,788 | 134,806 |
| 2020 | 12,939,473 | 2,344,429 | 3,336,227 | 2,408,316 | 1,175,922 | 85,887 |
| 2021 | 14,302,049 | 2,719,652 | 4,867,644 | 2,756,054 | 1,393,694 | 156,325 |
| 2022 | 14,431,449 | 2,845,776 | 5,238,190 | 2,775,458 | 1,278,815 | 152,664 |
| 2023 | 16,105,302 | 2,841,207 | 4,628,088 | 2,906,790 | 1,594,331 | 132,246 |

| Check | Expected | Observed | Result |
|---|---|---|---|
| `part_91`, `part_135`, `part_137` present 2019–2023 | 15 rows | 15 rows (plus `part_133`) | pass |
| each vehicle class under `general_aviation` 2019–2023 | 6 classes × 5 years | 30 rows | pass |
| part_91 + part_135 + part_137 + part_133 = general_aviation total | equal | equal in 2020, 2021, 2023; 1 hour over in 2019 (25,565,931 vs 25,565,930) and 2022 (26,953,421 vs 26,953,420), from rounding in the derived Parts | pass |

The six classes sum to less than the total: 2019 25,377,121 of 25,565,930 (188,809 short), 2020 201,617 short,
2021 245,156, 2022 231,068, 2023 354,744. The gap is the Table 1.3 row "Special Light-sport", which the mapping
leaves `unmapped` (the run reports it). Staged Table 1.3 (thousand hours): All Aircraft − the seven mapped rows =
Special Light-sport in every year, within 0.002 thousand hours (rounding of the source cells):

| Year | All Aircraft | Σ mapped rows (Piston, Turboprop, Turbojet, Rotorcraft, Experimental, Gliders, Lighter-than-air) | Special Light-sport | Gap in Step 5 (hours) |
|---|---|---|---|---|
| 2019 | 25,565.930 | 25,377.121 | 188.808 | 188,809 |
| 2020 | 22,491.871 | 22,290.254 | 201.615 | 201,617 |
| 2021 | 26,440.574 | 26,195.418 | 245.156 | 245,156 |
| 2022 | 26,953.420 | 26,722.352 | 231.068 | 231,068 |
| 2023 | 28,562.708 | 28,207.964 | 354.745 | 354,744 |

## Step 6: The first question (R-T9d)

Scope notes for every number in this step: `/api/rates` counts US events only. The `passenger_miles` denominator for
`part_121` is BTS "U.S. air carrier, certificated, domestic, all services", i.e. domestic passenger-miles only. NTSB
final reports take 12–24 months, so the 2022–2023 numerators are provisional. GA rates use the FAA calendar-year survey
estimate as denominator.

### Part 121 against road travel, per 100M passenger-miles

`GET /api/rates?measure=occupant_fatalities&metric=passenger_miles&fromYear=2019&toYear=2023&groupBy=regulation_class`
(`scope`: `{"country": "US"}`). All rows:

| mode | year | regulation_class | numerator | denominator | join_level | provenance | per 100M passenger-miles |
|---|---|---|---|---|---|---|---|
| aviation | 2019 | part_121 | 4 | 754,450,055,719 | regulation_class | measured | 0.000530 |
| aviation | 2020 | part_121 | 0 | 306,058,029,968 | regulation_class | measured | 0 |
| aviation | 2021 | part_121 | 0 | 573,403,841,131 | regulation_class | measured | 0 |
| aviation | 2022 | part_121 | 0 | 708,960,150,712 | regulation_class | measured | 0 |
| aviation | 2023 | part_121 | 0 | 773,481,359,163 | regulation_class | measured | 0 |
| highway | 2019 | (null) | 28,935 | 5,579,125,972,272 | mode | survey_estimate | 0.5186 |
| highway | 2020 | (null) | 31,237 | 4,934,929,062,720 | mode | survey_estimate | 0.6330 |
| highway | 2021 | (null) | 34,482 | 5,335,698,541,655 | mode | survey_estimate | 0.6463 |
| highway | 2022 | (null) | 33,679 | 5,027,965,006,953 | mode | survey_estimate | 0.6698 |
| highway | 2023 | (null) | 32,140 | 5,090,216,336,108 | mode | survey_estimate | 0.6314 |

`vehicle_class` is null in every row; `exposure_source` is `bts-passenger-miles` in every row. Highway denominators are
returned with fractions (e.g. `5579125972271.872`) and are rounded here.

2019–2023 combined: Part 121 4 / 3,116,353,436,693 = 0.000128 per 100M passenger-miles; highway 160,473 /
25,967,934,919,708 = 0.6180. Road occupants died at about 4,815 times the Part 121 rate per passenger-mile.

> **Read this ratio with these caveats.**
> - **Four deaths in five years.** The Part 121 numerator is 4 (all in 2019) and 0 in 2020–2023. One more or one fewer
>   crash moves the five-year rate by 25% or more; the yearly rates are not stable.
> - **Three of the four died on a cargo flight.** The staged NTSB record of the Atlas Air crash (DCA19MA086, 3 deaths) has
>   `oper_pax_cargo` = `CARG`, `oper_sched` = `NSCH`; its narrative says "Part 121 domestic cargo flight". The
>   denominator counts passenger-miles only. Only 1 of the 4 deaths (Peninsula AviationServices Inc., DBA PenAir,
>   DCA20MA002, `PAX` / `SCHD`) was on a passenger flight. The rate definition was not changed here.
> - **The denominator is domestic only.** The BTS row mapped to `part_121` is "U.S. air carrier, certificated,
>   domestic, all services"; the numerator is every US-located Part 121 accident.
> - **NTSB lag.** Final reports take 12–24 months (source caveat), so 2022–2023 can still be revised.

What the staged NTSB data say about each US Part 121 aircraft with a fatality, 2019–2023 (`stg_ntsb.aircraft`,
`stg_ntsb.events`, `stg_ntsb.injury`; no flight-number column exists, the flight numbers are from `narratives`):

| ntsb_no | `oper_name` (`oper_dba`) | Aircraft | `oper_pax_cargo` | `oper_sched` | `oper_dom_int` | Fatalities (FATL injury rows; event `inj_tot_f` / `inj_f_grnd`) | Narrative |
|---|---|---|---|---|---|---|---|
| DCA19MA086 | Atlas Air (blank) | Boeing 767 | CARG | NSCH | DOM | 3 occupants; 3 / blank | "flight 3591"; "Part 121 domestic cargo flight for Amazon" |
| DCA20MA002 | Peninsula AviationServices Inc., (PenAir) | Saab 2000 | PAX | SCHD | DOM | 1 occupant; 1 / blank | "PenAir flight 3296" |
| DCA23LA109 | Envoy Air Inc. (American Eagle) | EMBRAER S A ERJ 170-200 LR | PAX | SCHD | DOM | 0 occupants (no FATL rows); 0 / 1 ground | "Envoy Air flight 3408" |

`type_fly` is blank for all three. The ground fatality of DCA23LA109 is a nonoccupant, which `occupant_fatalities`
does not count. `occurred_on` follows `ev_date`: DCA20MA002 is 2019-10-18 in the data, while its narrative says
"October 17, 2019, about 1740 Alaska daylight time".

`unmatched` (all `reason: no_exposure`, all aviation; `vehicleClass` null):

| regulationClass | 2019 | 2020 | 2021 | 2022 | 2023 |
|---|---|---|---|---|---|
| part_91 | 361 | 288 | 311 | 295 | 273 |
| part_135 | 31 | 20 | 26 | 8 | 19 |
| part_137 | 7 | 9 | 12 | 9 | 12 |
| part_133 | 2 | 6 | 0 | 5 | 2 |
| part_135_scheduled | 2 | 5 | 0 | 10 | 0 |
| other | 3 | 5 | 4 | 9 | 3 |
| unknown | 0 | 0 | 0 | 0 | 0 |

`unattributed`: highway 2019 52, 2020 44, 2021 52, 2022 48, 2023 64. No aviation entries.

| Check | Expected | Observed | Result |
|---|---|---|---|
| Part 121 row per year with a numerator and a BTS denominator | 5 rows | 5 rows, `join_level` `regulation_class` | pass |
| Highway rows | match plan 1 headline | numerators and rounded denominators identical to plan-1.md | pass |
| GA Parts in `unmatched`, not in rows | no passenger-miles for GA | all `no_exposure` | pass |

### General aviation by class, per 100k flight hours

`GET /api/rates?measure=occupant_fatalities&metric=flight_hours&fromYear=2019&toYear=2023&groupBy=vehicle_class,regulation_class`
(`scope`: `{"country": "US"}`; `exposure_source` `faa-ga-survey`, the default; `provenance` `survey_estimate`;
`join_level` `vehicle_class+regulation_class_parent` in every row: part_91, part_135, part_137 and part_133 numerators
are rolled up to `general_aviation`). All rows:

| year | vehicle_class | numerator | denominator (hours) | per 100k hours |
|---|---|---|---|---|
| 2019 | business_jet | 8 | 3,926,485 | 0.204 |
| 2019 | experimental | 47 | 1,268,788 | 3.704 |
| 2019 | helicopter | 42 | 2,996,531 | 1.402 |
| 2019 | light_aircraft | 222 | 14,431,419 | 1.538 |
| 2019 | other | 12 | 134,806 | 8.902 |
| 2019 | turboprop | 62 | 2,619,092 | 2.367 |
| 2020 | business_jet | 4 | 3,336,227 | 0.120 |
| 2020 | experimental | 49 | 1,175,922 | 4.167 |
| 2020 | helicopter | 31 | 2,408,316 | 1.287 |
| 2020 | light_aircraft | 203 | 12,939,473 | 1.569 |
| 2020 | other | 5 | 85,887 | 5.822 |
| 2020 | turboprop | 22 | 2,344,429 | 0.938 |
| 2021 | business_jet | 24 | 4,867,644 | 0.493 |
| 2021 | experimental | 46 | 1,393,694 | 3.301 |
| 2021 | helicopter | 42 | 2,756,054 | 1.524 |
| 2021 | light_aircraft | 196 | 14,302,049 | 1.370 |
| 2021 | other | 11 | 156,325 | 7.037 |
| 2021 | turboprop | 29 | 2,719,652 | 1.066 |
| 2022 | business_jet | 1 | 5,238,190 | 0.019 |
| 2022 | experimental | 55 | 1,278,815 | 4.301 |
| 2022 | helicopter | 32 | 2,775,458 | 1.153 |
| 2022 | light_aircraft | 177 | 14,431,449 | 1.226 |
| 2022 | other | 16 | 152,664 | 10.481 |
| 2022 | turboprop | 34 | 2,845,776 | 1.195 |
| 2023 | business_jet | 15 | 4,628,088 | 0.324 |
| 2023 | experimental | 40 | 1,594,331 | 2.509 |
| 2023 | helicopter | 25 | 2,906,790 | 0.860 |
| 2023 | light_aircraft | 187 | 16,105,302 | 1.161 |
| 2023 | other | 3 | 132,246 | 2.268 |
| 2023 | turboprop | 36 | 2,841,207 | 1.267 |

`unmatched` (all `reason: no_exposure`). Aviation entries with a non-zero numerator:

| year | vehicleClass / regulationClass = numerator |
|---|---|
| 2019 | airliner/part_121 3; helicopter/other 1; light_aircraft/other 2; light_aircraft/part_135_scheduled 2; turboprop/part_121 1; unknown/part_91 8 |
| 2020 | helicopter/other 2; light_aircraft/part_135_scheduled 5; turboprop/other 3; unknown/part_135 2; unknown/part_91 7 |
| 2021 | business_jet/other 1; other/other 1; turboprop/other 2; unknown/part_137 1 |
| 2022 | helicopter/other 9; turboprop/part_135_scheduled 10; unknown/part_91 2 |
| 2023 | helicopter/other 3 |

Aviation entries with numerator 0 (no fatalities, listed because the cell had events): 2019 business_jet/other,
helicopter/unknown, light_aircraft/part_121, turboprop/other, turboprop/part_135_scheduled, unknown/part_121;
2020 airliner/part_121, light_aircraft/other, light_aircraft/unknown, turboprop/part_135_scheduled, unknown/other,
unknown/part_121, unknown/part_137; 2021 airliner/other, airliner/part_121, helicopter/other,
helicopter/part_135_scheduled, light_aircraft/other, light_aircraft/part_135_scheduled, turboprop/part_135_scheduled,
unknown/other, unknown/part_121, unknown/part_91, unknown/unknown; 2022 airliner/other, airliner/part_121,
business_jet/other, helicopter/part_135_scheduled, light_aircraft/other, light_aircraft/part_135_scheduled, turboprop/other, turboprop/unknown,
unknown/part_135_scheduled, unknown/part_137; 2023 airliner/other, airliner/part_121, business_jet/other,
helicopter/unknown, light_aircraft/other, light_aircraft/part_135_scheduled, light_aircraft/unknown, other/other,
turboprop/other, unknown/other, unknown/part_121, unknown/part_135, unknown/part_91.

Highway entries: every FARS (vehicle_class, regulation_class) cell is `unmatched`, because there are no highway flight
hours. The full list (88 entries, e.g. 2022 passenger_car/road_private 13,727, light_truck/road_private 11,716,
motorcycle/road_private 6,244, heavy_truck/road_commercial 1,136) is in the task report.

`unattributed`: highway 2019 52, 2020 44, 2021 52, 2022 48, 2023 64. No aviation entries.

Same query with `groupBy=regulation_class` (one row per Part, `join_level` `regulation_class`):

| year | part_91 | part_135 | part_137 | part_133 | GA total occupant deaths / GA total hours |
|---|---|---|---|---|---|
| 2019 | 361 / 20,582,501 = 1.754 | 31 / 3,765,242 = 0.823 | 7 / 1,060,163 = 0.660 | 2 / 158,025 = 1.266 | 401 / 25,565,930 = 1.568 |
| 2020 | 288 / 18,293,860 = 1.574 | 20 / 3,037,404 = 0.658 | 9 / 1,029,392 = 0.874 | 6 / 131,215 = 4.573 | 323 / 22,491,871 = 1.436 |
| 2021 | 311 / 20,689,616 = 1.503 | 26 / 4,474,791 = 0.581 | 12 / 1,120,395 = 1.071 | 0 / 155,772 = 0.000 | 349 / 26,440,574 = 1.320 |
| 2022 | 295 / 21,355,713 = 1.381 | 8 / 4,410,422 = 0.181 | 9 / 1,018,417 = 0.884 | 5 / 168,869 = 2.961 | 317 / 26,953,420 = 1.176 |
| 2023 | 273 / 23,396,520 = 1.167 | 19 / 3,613,379 = 0.526 | 12 / 1,361,431 = 0.881 | 2 / 191,378 = 1.045 | 306 / 28,562,708 = 1.071 |

(per 100k flight hours; the GA total adds the four Part rows and divides by the `general_aviation` total hours of Step 5).

With `groupBy=vehicle_class` alone (2022 tried): `rows` is empty and every aviation class is in `unmatched`, which is
what the README says.

### Published comparison: GA fatal-accident rate

Fatal GA accidents from the API:
`GET /api/events/aggregate?from=2019-01-01&to=2023-12-31&bucket=year&f.mode=aviation&f.country=US&f.event_type=accident&f.severity=fatal&groupBy=operation_class`,
taking `operation_class` part_91 + part_135 + part_137 + part_133 (the classes the FAA survey covers), divided by the
`general_aviation` total hours.

Published: FAA "General Aviation Safety" fact sheet
(https://www.faa.gov/newsroom/fact_sheet/2025_General_Aviation_Safety_Fact_Sheet.pdf, linked from
https://www.faa.gov/newsroom/general-aviation-safety, fetched with plain curl, HTTP 200), Table 1 "GA fatal accident
rates and numbers". It gives **fiscal** years (October to September). NTSB's own calendar-year GA rate tables
(ntsb.gov/safety/data, table10.aspx) stop at 2011 and could not be used.

The pass threshold is the plan-1 convention for published comparisons (within 20%, plan-1.md Step 6).

| Year | Our fatal GA accidents (calendar year) | Our rate per 100k hours | FAA FY rate | FAA FY fatal accidents | Rate difference | Result (within 20%) |
|---|---|---|---|---|---|---|
| 2019 | 215 + 11 + 6 + 2 = 234 | 0.915 | FY19 0.95 | 243 | −3.7% | pass |
| 2020 | 177 + 6 + 9 + 4 = 196 | 0.871 | FY20 0.91 | 211 | −4.3% | pass |
| 2021 | 191 + 9 + 12 + 0 = 212 | 0.802 | FY21 0.75 | 192 | +6.9% | pass |
| **2022** | 183 + 4 + 9 + 2 = 198 | **0.735** | **FY22 0.90** | 242 | **−18.3%** | pass (barely) |
| 2023 | 173 + 8 + 12 + 1 = 194 | 0.679 | FY23 0.71 | 200 | −4.4% | pass |

To separate the calendar/fiscal-year difference, the same aggregate was run with fiscal-year dates
(`from=2021-10-01&to=2022-09-30` etc., `bucket=none`): fatal GA accidents FY19 216 + 4 + 6 + 6 = 232 (FAA 243, −4.5%),
FY22 208 + 2 + 6 + 11 = 227 (FAA 242, −6.2%), FY23 175 + 10 + 11 = 196 (FAA 200, −2.0%). Most of the 2022 gap is the
calendar/fiscal-year shift (CY2022 had 198 fatal GA accidents, FY22 227). The rest is the denominator (FAA's FY hours
are not the calendar-year survey estimate) and the definition (we count by the lowest-numbered aircraft's Part; FAA's
counting rules were not retrieved).

## Concerns

1. **`/api/events/sample` was reproducible only until the next ingest (fixed).** Ordering was
   `md5(vehicle_id || seed)` as the Task 7 brief specified, and every FARS/NTSB normalize deletes and re-inserts
   vehicles with new `gen_random_uuid()` ids. Found here: `GET /api/events/sample?from=2019-01-01&to=2019-12-31&f.mode=highway&n=5&seed=7`,
   then `POST /api/admin/ingest/fars?years=2019` (same file), then the same GET: `total` 51,623 both times, rows not
   identical (first row Montana 2019-11-06, then Wyoming 2019-08-14). The final review rated this a defect; fixed in
   `52565c9`: the order is now `md5(concat_ws('|', source_id, source_key, vehicle_no, seed))` with
   `source_id, source_key, vehicle_no` as tie-break, and each row starts with those three columns. Re-check on the
   rebuilt stack (2026-11-05 16:30 UTC): the same GET, then `fars?years=2019` again (run
   `120811ee-6d16-4f1d-9fc6-c6dd063ab052`, succeeded, event 33,487, vehicle 51,623), then the same GET: the two
   response bodies are byte-identical (`cmp`); first row `fars`, `2019-40301`, vehicle 2, Arizona, 2019-04-21. That run
   also reused the unchanged raw file (`/data/raw/fars/20261105T160438966/FARS2019NationalCSV.zip`, sha256
   `0974abb9…`) instead of storing a second copy.
2. **Part 121 numerator includes cargo.** 3 of the 4 Part 121 occupant deaths are from DCA19MA086 (Atlas Air,
   `oper_pax_cargo` = `CARG`, narrative "Part 121 domestic cargo flight"), while the BTS denominator is domestic
   passenger-miles. The user decides separately whether Part 121 rates should exclude cargo.
3. **App memory after NTSB.** The app container stayed at about 694 MiB after the run finished (57.5 MiB before the
   FARS run). Not a failure, but the monthly NTSB run will hold that memory on the user's machine.
