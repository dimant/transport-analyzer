# Plan 1 real-data verification

Run date: 2026-11-04 (container clock 2026-11-05 UTC). Stack: `docker compose up -d --build` on a fresh database.
Test suite before and after: `dotnet test`, 100 passed, 0 failed.
Controller rulings R9 and R22 replace the brief's Step 2 totals and Step 6 metric.

No defects found. No code or mapping changes.

## Step 1: Ingest

| Check | Expected | Observed | Result |
|---|---|---|---|
| `POST /api/admin/ingest/fars` | 202 + runId | 202, run `9cc0be78-92b8-4f4b-8ab6-49a554f7dc6d` | pass |
| `POST /api/admin/ingest/bts-passenger-miles` | 202 + runId | 202, run `6b4d5480-83f8-4eb3-b9e2-f10d4709da46` | pass |
| `POST /api/admin/ingest/bts-vehicle-miles` | 202 + runId | 202, run `44974692-721c-4170-afac-7bca521be9ad` | pass |
| `GET /api/admin/runs`, all three runs | `succeeded` | all `succeeded`. FARS: `event` 186,398 rows, `vehicle` 287,250 rows, raw files FARS2019–2023NationalCSV.zip. BTS 1-40 (`table_01_40_102826.xlsx`): 238 exposure rows. BTS 1-35 (`table_01_35_102826.xlsx`): 243 exposure rows | pass |

## Step 2: FARS yearly fatalities (R9)

Command: `GET /api/events/aggregate?from=2019-01-01&to=2023-12-31&bucket=year`.
Expected value: `select data_year, sum(nullif(trim(fatals),'')::int) from stg_fars.accident group by 1`.

| Year | API `fatalities` | Staged `sum(fatals)` | Result | NHTSA published (not asserted) |
|---|---|---|---|---|
| 2019 | 36,355 | 36,355 | pass | not retrieved |
| 2020 | 39,007 | 39,007 | pass | not retrieved |
| 2021 | 43,230 | 43,230 | pass | not retrieved |
| 2022 | 42,721 | 42,721 | pass | 42,721 final (DOT HS 813 705, "Overview of Motor Vehicle Traffic Crashes in 2023": "final fatality count ... for 2022 was 42,721, which was updated from 42,514 in the 2022 ARF") |
| 2023 | 41,025 | 41,025 | pass | 40,901 in the 2023 ARF (DOT HS 813 705). The 2023 final count was not retrieved from a primary source; a press report of NHTSA's 2024 overview gives 41,025 |

Other measures from the same response (events / serious / minor): 2019 33,487 / 8,685 / 15,271; 2020 35,935 / 9,122 / 14,761; 2021 39,785 / 10,120 / 17,364; 2022 39,422 / 9,789 / 16,685; 2023 37,769 / 9,563 / 16,234.

Casualty invariant check: zero events where `event.fatalities <> sum(vehicle occupant + nonoccupant fatalities) + event.unattributed_fatalities`.

## Step 3: Unattributed share

| Year | `unattributed_fatalities` | Share of `fatalities` | Threshold | Result |
|---|---|---|---|---|
| 2019 | 52 | 0.143% | < 5% | pass |
| 2020 | 44 | 0.113% | < 5% | pass |
| 2021 | 52 | 0.120% | < 5% | pass |
| 2022 | 48 | 0.112% | < 5% | pass |
| 2023 | 64 | 0.156% | < 5% | pass |

The shares are well below the brief's guess of a "small single-digit percentage". The reason: every non-occupant death (pedestrians, cyclists and so on: 7,368 / 7,726 / 8,696 / 8,994 / 8,821 for 2019–2023, from `stg_fars.person`, `inj_sev=4`, `per_typ` not in 1,2,3,9) is attributed to a vehicle. Each year, the unattributed count exactly equals the killed occupants of parked or working vehicles (`per_typ=3`: 52 / 44 / 52 / 48 / 64). Those vehicles are in FARS `parkwork`, not `vehicle`, so they have no normalized vehicle row. This is the expected result of the attribution rule, not a missing case.

## Step 4: Unmapped values

| Check | Expected | Observed | Result |
|---|---|---|---|
| FARS run `unmapped` keys with count > 100 | each one mapped or recorded as deliberate `unknown` | `unmapped` is `{}` (no keys at all) | pass |
| Cross-check: staged `body_typ` values with no `vehicle_class` row in `vocabulary_map` | none | 0 rows | pass |
| Cross-check: staged vehicles with neither `body_typ` nor `spec_use` matching a `regulation_class` row | none | 0 rows | pass |

`unknown` codes that come from deliberate explicit mappings in `data/mappings/fars.csv`, recorded for the user's review:
- `vehicle_class=unknown`: 6,300 vehicles (`body_typ` 99 = 5,318, `body_typ` 98 = 982). FARS codes these as "unknown body type" / "not reported".
- `regulation_class=unknown` and `operator_type=unknown`: 2,231 vehicles each (from `spec_use` 98/99).
- `maintenance_regime=unknown`: 258,979 of 287,250 vehicles. The mapping sets `mandated_periodic` only for 30 commercial body types and `unknown` everywhere else, because FARS does not record maintenance.

Proposed additions to `fars.csv`: none needed (no value is unmapped). The file was not edited.

Note: the BTS runs each report `unmapped` `{partial_sum:highway/light_duty/1960: 1, partial_sum:highway/light_duty/1965: 1}`. That is the partial-sum guard firing on years outside 2019–2023 and does not affect this check.

## Step 5: Exposure

| Check | Expected | Observed | Result |
|---|---|---|---|
| `GET /api/exposure?metric=passenger_miles&fromYear=2019&toYear=2023` | aviation `part_121`, plus highway `light_duty`, `motorcycle`, `bus`, `heavy_truck` and a total row, for each published year | all 6 rows for every year 2019–2023 (30 rows) | pass |
| Same query with `metric=vehicle_miles` | same shape | all 6 rows for every year 2019–2023 (30 rows) | pass |

2022 passenger-miles: aviation part_121 708,960,150,712; highway total 5,027,965,006,953; light_duty 4,291,908,799,297; motorcycle 24,369,327,209; bus 380,414,454,199; heavy_truck 331,272,426,248.
2022 vehicle-miles: highway total 3,196,191,396,670; light_duty 2,822,663,943,466; motorcycle 23,764,862,512; bus 18,490,164,443; heavy_truck 331,272,426,248. The four classes sum to the total.

## Step 6: Hand-checked rate (R22, vehicle-miles)

Command: `GET /api/rates?measure=occupant_fatalities&metric=vehicle_miles&fromYear=2022&toYear=2022&groupBy=vehicle_class`.

| vehicle_class | numerator | denominator (VMT) | join_level | Rate per 100M VMT |
|---|---|---|---|---|
| motorcycle | 6,255 | 23,764,862,512 | vehicle_class | 26.32 |
| light_duty | 25,532 | 2,822,663,943,466 | vehicle_class_parent | 0.90 |
| heavy_truck | 1,136 | 331,272,426,248 | vehicle_class | 0.34 |
| bus | 19 | 18,490,164,443 | vehicle_class | 0.10 |

Unmatched: `other` 598, `unknown` 139. Unattributed: 48.
Numerator check against Postgres: 2022 occupant fatalities by class are passenger_car 13,763 + light_truck 11,769 = 25,532 (light_duty), motorcycle 6,255. All classes together come to 33,679, which equals staged `person` rows with `inj_sev=4`, `per_typ` in 1,2,9 and `veh_no<>0`.

| Check | Expected | Observed | Result |
|---|---|---|---|
| Motorcycle rate against NHTSA "Motorcycles: 2023 Data" (DOT HS 813 732), Table 1/2, 2022 row: 6,251 killed, 23,765M VMT, 26.30 per 100M VMT | within 20% | 26.32 (+0.08%). The VMT denominator matches (23,765M); the numerator differs by 4 (6,255 occupants of motorcycle body types vs 6,251 motorcyclists) | pass |
| Same rate against the original 2022 fact sheet (DOT HS 813 589), 26.16, as reported by search results (not opened) | within 20% | +0.61% | pass |
| Motorcycle rate about 10x the light-duty rate | about an order of magnitude | 26.32 / 0.90 = 29.1x. NHTSA gives passenger cars 1.20 and light trucks 0.72 for 2022; our combined light_duty value of 0.90 lies between them | pass |

## Headline: occupant fatalities per passenger-mile, 2019–2023

Command: `GET /api/rates?measure=occupant_fatalities&metric=passenger_miles&fromYear=2019&toYear=2023`.

| Year | Highway numerator | Denominator (passenger-miles) | join_level | Rate per 100M passenger-miles |
|---|---|---|---|---|
| 2019 | 28,935 | 5,579,125,972,272 | mode | 0.519 |
| 2020 | 31,237 | 4,934,929,062,720 | mode | 0.633 |
| 2021 | 34,482 | 5,335,698,541,655 | mode | 0.646 |
| 2022 | 33,679 | 5,027,965,006,953 | mode | 0.670 |
| 2023 | 32,140 | 5,090,216,336,108 | mode | 0.631 |

- `unmatched`: `[]`.
- `unattributed`: highway 2019 52, 2020 44, 2021 52, 2022 48, 2023 64.
- Aviation: no rows and no unmatched entries, as expected. No aviation events exist until plan 2 adds NTSB. The aviation part_121 exposure rows are present (Step 5) but have no numerator to join.

With `groupBy=vehicle_class`, motorcycle per 100M passenger-miles is 22.08 / 25.99 / 25.94 / 25.67 / 30.77 and light_duty is 0.457 / 0.555 / 0.571 / 0.595 / 0.549 (2019–2023). Unmatched each year is `other` (495 / 654 / 611 / 598 / 589) and `unknown` (79 / 164 / 171 / 139 / 108).

## Reference access

- `cdan.dot.gov/tsftables/tsfar.htm` returned HTTP 403. It was not retried.
- No bts.gov pages were requested.
- NHTSA 813 705 and 813 732 were fetched from crashstats.nhtsa.dot.gov without any problem.
