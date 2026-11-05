# Plan 3 real-data verification

Run date: 2026-11-05 (UTC). Stack: `docker compose up -d --build` on the existing `pgdata` volume (never `down -v`), with
FARS 2019–2023, NTSB 2008–2026, BTS passenger and vehicle miles, and the FAA GA survey ingested.
Method: the live test `web/src/experiments/presets.live.test.ts` runs every preset through the real `runQuery` against the
API and the preset's method with its bindings and params, then prints rate, lo and hi (3 significant digits, 95% Garwood
interval, as in the app). Those printed numbers are what the browser table shows.

## Step 1: Build and serve

| Check | Expected | Observed | Result |
|---|---|---|---|
| `docker compose up -d --build` | node build stage and API image build; stack starts | built and started | pass |
| `curl -s localhost:8080/api/health` | `{"status":"ok"}` | `{"status":"ok"}` | pass |
| `curl -s localhost:8080/` | the built `index.html` | HTML with `<title>Transport Analyzer</title>`, `<script type="module" crossorigin src="/assets/index-C9NI-d2Z.js">` and a hashed CSS link | pass |

## Step 2: Live preset test

`cd web && TA_API=http://localhost:8080 npx vitest run presets.live`: 1 file, 7 tests passed (6 presets + the preset 2 check
below), no exception, no warnings, at least one row or cell each. Every rate row has a non-null rate with `hi >= rate`.
Without `TA_API` the file is skipped, so `npm test` stays green.

Preset 2 pooled rates (asserted within 1%): Part 121 0.000128 (plan 2: 0.000128), road 0.618 (plan 1: 0.6180); both have
`hi > rate`.

## Step 3: Numbers per preset

Every rate row below carries `join_level`, `exposure_source` and `provenance` (shown beside it in the app).

### Preset 1: Airlines vs road, deaths per 100M passenger-miles, by year

`/api/rates?measure=occupant_fatalities&metric=passenger_miles&fromYear=2019&toYear=2023&groupBy=regulation_class`.
`exposure_source` bts-passenger-miles. Part 121: `join_level` regulation_class, provenance measured. Road: `join_level`
mode, provenance survey_estimate. Plan-1 and plan-2 references match to the printed digits.

| mode | year | k | rate | lo | hi | reference |
|---|---|---|---|---|---|---|
| Part 121 | 2019 | 4 | 0.000530 | 0.000144 | 0.00136 | plan 2: 0.000530 |
| Part 121 | 2020 | 0 | 0 | 0 | 0.00121 | plan 2: 0 |
| Part 121 | 2021 | 0 | 0 | 0 | 0.000643 | plan 2: 0 |
| Part 121 | 2022 | 0 | 0 | 0 | 0.000520 | plan 2: 0 |
| Part 121 | 2023 | 0 | 0 | 0 | 0.000477 | plan 2: 0 |
| road | 2019 | 28,935 | 0.519 | 0.513 | 0.525 | plan 2: 0.5186 |
| road | 2020 | 31,237 | 0.633 | 0.626 | 0.640 | plan 2: 0.6330 |
| road | 2021 | 34,482 | 0.646 | 0.639 | 0.653 | plan 2: 0.6463 |
| road | 2022 | 33,679 | 0.670 | 0.663 | 0.677 | plan 2: 0.6698 |
| road | 2023 | 32,140 | 0.631 | 0.625 | 0.638 | plan 2: 0.6314 |

The four zero-death years have a rate of 0 and an upper bound above 0 (rule of three: 3/denominator).

### Preset 2: Airlines vs road, 2019–2023 combined

| mode | k | denominator | rate | lo | hi | reference |
|---|---|---|---|---|---|---|
| Part 121 | 4 | 3,116,353,436,693 | 0.000128 | 0.0000350 | 0.000329 | plan 2: 0.000128 |
| road | 160,473 | 25,967,934,919,708 | 0.618 | 0.615 | 0.621 | plan 1: 0.6180 |

The intervals do not overlap: road is about 4,800 times the Part 121 rate. The Part 121 numerator is 4 deaths, 3 of them on
an all-cargo flight (plan 2, Concern 2). The preset note says so.

### Preset 3: GA by aircraft class, 2022, deaths per 100k flight hours

`/api/rates?measure=occupant_fatalities&metric=flight_hours&fromYear=2022&toYear=2022&groupBy=vehicle_class,regulation_class`.
`join_level` vehicle_class+regulation_class_parent, `exposure_source` faa-ga-survey, provenance survey_estimate, on every row.

| vehicle_class | k | hours | rate | lo | hi | plan 2 rate (k) |
|---|---|---|---|---|---|---|
| business_jet | 1 | 5,238,190 | 0.0191 | 0.000483 | 0.106 | 0.019 (1) |
| experimental | 55 | 1,278,815 | 4.30 | 3.24 | 5.60 | 4.30 (55) |
| helicopter | 32 | 2,775,458 | 1.15 | 0.789 | 1.63 | 1.15 (32) |
| **light_aircraft** | **179** | 14,431,449 | **1.24** | 1.07 | 1.44 | **1.23 (177)** |
| other | 16 | 152,664 | 10.5 | 5.99 | 17.0 | 10.5 (16) |
| turboprop | 34 | 2,845,776 | 1.19 | 0.827 | 1.67 | 1.20 (34) |

Turboprop: 34 / 2,845,776 = 1.195 per 100k, which plan 2 printed as 1.195 and the brief rounds to 1.20; the 3-digit
rounding here (1.19, from 1.1948) is the same number.

**Light aircraft differs: 177 before, 179 now (1.226 to 1.240).** The cause is the four NTSB mapping rows accepted on
2026-11-05 (commit `3a1ba5e`; `AIR::091` and `AIR::121`/`AIR::129` as `vehicle_class,vehicle_profile,...` plus
`operator_type,use_profile,091:UNK`). `AIR::091` maps Part 91 airplanes with no engine record to `light_aircraft`, which
were `unknown` before. Before/after for the same query (`groupBy=vehicle_class,regulation_class`, 2019–2023), numerators:

| year | light_aircraft before (plan 2) | light_aircraft now | `unknown`/part_91 before (unmatched) | `unknown`/part_91 now |
|---|---|---|---|---|
| 2019 | 222 | 230 | 8 | 0 |
| 2020 | 203 | 208 | 7 | 2 |
| 2021 | 196 | 196 | 0 | 0 |
| 2022 | 177 | 179 | 2 | 0 |
| 2023 | 187 | 187 | 0 | 0 |

The gain in light_aircraft equals the loss in `unknown`/part_91 in every year (+8, +5, 0, +2, 0). Query output for 2022:
`/api/events/aggregate?grain=vehicle&from=2022-01-01&to=2022-12-31&bucket=none&groupBy=vehicle_class,regulation_class&f.mode=aviation&f.country=US&f.vehicle_class=unknown,light_aircraft&f.regulation_class=part_91`
returns `light_aircraft / part_91`: 792 vehicles, 179 occupant fatalities; `unknown / part_91`: 3 vehicles, 0 fatalities.
The other 2019–2023 classes (business_jet, experimental, helicopter, other, turboprop) and the `airliner`/`part_121`
numerators (3 in 2019, `unmatched`) are unchanged from plan 2. The airliner mapping rows (`AIR::121`, `AIR::129`) moved no
fatalities between rows with a numerator: plan 2 listed `unknown`/part_121 with numerator 0 in every year, and now the
`unknown`/part_121 cell is gone. `091:UNK` changes `operator_type` only, which these rates do not use. Denominators did not change.

### Preset 4: Road by vehicle class, deaths per 100M vehicle-miles

`exposure_source` bts-vehicle-miles, provenance survey_estimate. `join_level` is vehicle_class for bus, heavy_truck and
motorcycle, and `vehicle_class_parent` for light_duty (cars plus light trucks, as FHWA/BTS report them since 2007).

| class | year | k | rate | lo | hi |
|---|---|---|---|---|---|
| bus | 2019 | 35 | 0.195 | 0.136 | 0.271 |
| bus | 2020 | 16 | 0.106 | 0.0608 | 0.173 |
| bus | 2021 | 12 | 0.0717 | 0.0370 | 0.125 |
| bus | 2022 | 19 | 0.103 | 0.0619 | 0.160 |
| bus | 2023 | 24 | 0.136 | 0.0869 | 0.202 |
| heavy_truck | 2019 | 912 | 0.304 | 0.285 | 0.324 |
| heavy_truck | 2020 | 844 | 0.284 | 0.265 | 0.303 |
| heavy_truck | 2021 | 1,043 | 0.319 | 0.300 | 0.339 |
| heavy_truck | 2022 | 1,136 | 0.343 | 0.323 | 0.363 |
| heavy_truck | 2023 | 1,002 | 0.304 | 0.285 | 0.323 |
| light_duty | 2019 | 22,370 | 0.765 | 0.755 | 0.775 |
| light_duty | 2020 | 23,940 | 0.930 | 0.919 | 0.942 |
| light_duty | 2021 | 26,508 | 0.957 | 0.946 | 0.969 |
| light_duty | 2022 | 25,532 | 0.905 | 0.893 | 0.916 |
| light_duty | 2023 | 24,050 | 0.835 | 0.825 | 0.846 |
| motorcycle | 2019 | 5,044 | 25.6 | 24.9 | 26.3 |
| motorcycle | 2020 | 5,619 | 31.3 | 30.5 | 32.1 |
| motorcycle | 2021 | 6,137 | 31.2 | 30.5 | 32.0 |
| motorcycle | 2022 | 6,255 | 26.3 | 25.7 | 27.0 |
| motorcycle | 2023 | 6,367 | 31.5 | 30.8 | 32.3 |

Motorcycle 2022: 6,255 / 23,764,862,512 miles = 26.32 per 100M VMT (NHTSA published 26.30). Pass.

### Preset 5: US aviation deaths by aircraft class and year (heatmap)

`/api/events/aggregate?grain=vehicle&from=2019-01-01&to=2023-12-31&bucket=year&groupBy=vehicle_class&f.mode=aviation&f.country=US`.
40 cells: 5 years × 8 classes (airliner, business_jet, experimental, helicopter, light_aircraft, other, turboprop, unknown),
every cell present. No warnings.

### Preset 6: Road deaths by vehicle class and operator type (crosstab)

`grain=vehicle`, 2022, `f.mode=highway`, `groupBy=vehicle_class,operator_type`. 18 query rows, 7 vehicle-class rows in the
crosstab. No warnings.

## Browser check

Pending: controller browser check.
