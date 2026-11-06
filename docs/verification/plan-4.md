# Plan 4 real-data verification

Run date: 2026-11-06 (UTC). Stack: `docker compose up -d --build` on the existing `pgdata` volume (never `down -v`), with
FARS 2019–2023, NTSB 2008–2026, BTS passenger and vehicle miles, and the FAA GA survey ingested. The build includes the
plan 4 server changes (Part 121 beside road vehicle classes under `groupBy=vehicle_class,regulation_class`; the
`vehicles_with_occupant_fatality` measure) and the new frontend.
Method: the live test `web/src/experiments/presets.live.test.ts` runs every preset through the real `runQuery` against the
API and the preset's method with its bindings and params, and prints each regression term (ratio, lo, hi and p to 3
significant digits, as the app's `rate` format shows them), the summary and the warnings. Those printed numbers are what
the browser tables show. Intervals are 95% Wald intervals, `exp(β ± 1.96·se)`.

## Step 1: Build and serve

| Check | Expected | Observed | Result |
|---|---|---|---|
| `docker compose up -d --build` | node build stage and API image build; stack starts | built and started | pass |
| `curl -s localhost:8080/api/health` | `{"status":"ok"}` | `{"status":"ok"}` | pass |
| `curl -s localhost:8080/` | the built `index.html` with the new presets | `/assets/index-DuJU-bg_.js` (rebuilt after the controller's rulings below), which contains the new presets | pass |

## Step 2: Live preset test

`cd web && TA_API=http://localhost:8080 npx vitest run src/experiments/presets.live.test.ts`: 1 file, 14 tests passed
(10 presets, the plan-3 preset 2 check, presets 7 and 8 refit under `poisson`, and the cross-check below). For each
regression preset the test asserts at least one estimated term, `converged: yes` in the summary and no
`No rows left to fit.` warning. Without `TA_API` the file is skipped, so `npm test` stays green (24 files passed, 1
skipped; 187 tests passed, 14 skipped). Presets 1–6 give the same numbers as in `plan-3.md`, with no warnings.

## Step 3: Numbers per preset

Columns: the ratio, its 95% interval, the two-sided Wald p, and the events and exposure (or trials) of that level. A
reference level has ratio 1 and no interval. A p of 0 is below double precision (the app shows it as 0).

### Preset 7: Deaths per passenger-mile by vehicle class, airlines included, adjusted for year

`/api/rates?measure=occupant_fatalities&metric=passenger_miles&fromYear=2019&toYear=2023&groupBy=vehicle_class,regulation_class`,
negative binomial, covariates `rate_class` and `year`, per 100M passenger-miles.

**Reference: `rate_class=light_duty, year=2019`.**

| term | level | rate ratio | lo | hi | p | deaths | passenger-miles |
|---|---|---|---|---|---|---|---|
| rate_class | bus | 0.0111 | 0.00904 | 0.0136 | 0 | 106 | 1.755e12 |
| rate_class | heavy_truck | 0.571 | 0.529 | 0.616 | 4.1e-47 | 4,937 | 1.586e12 |
| rate_class | light_duty | 1 (reference) | | | | 122,400 | 2.251e13 |
| rate_class | motorcycle | 47.8 | 44.5 | 51.3 | 0 | 29,422 | 1.132e11 |
| rate_class | **part_121** | **0.000235** | **0.0000880** | **0.000628** | 2.3e-62 | 4 | 3.116e12 |
| year | 2019 | 1 (reference) | | | | 28,365 | 6.334e12 |
| year | 2020 | 1.09 | 0.989 | 1.20 | 0.082 | 30,419 | 5.241e12 |
| year | 2021 | 1.13 | 1.02 | 1.24 | 0.015 | 33,700 | 5.909e12 |
| year | 2022 | 1.17 | 1.06 | 1.28 | 0.0014 | 32,942 | 5.737e12 |
| year | 2023 | 1.18 | 1.07 | 1.30 | 0.00075 | 31,443 | 5.864e12 |

Summary: family negative binomial (θ = 307.3); 25 rows used; 9 parameters; deviance 41.30 on 16 residual df; Pearson
dispersion 2.82; AIC 289.1; baseline rate 0.490 per 100M (light-duty, 2019); converged yes; exposure sources
`bts-passenger-miles (measured) + bts-passenger-miles (survey_estimate)`. **Warnings: none.**

Under `poisson` (same covariates): **Pearson dispersion 16.66** on 16 df, deviance 257.3, with the warning "Pearson
dispersion 16.66 exceeds 1.5: counts vary more than Poisson allows, so these intervals are too narrow. Try family
negative_binomial." Point estimates barely move (part_121 0.000236 [0.0000887, 0.000630], motorcycle 47.6 [47.0, 48.2]);
the road intervals are much narrower (year 2020 1.20 [1.18, 1.22]), which is the overdispersion the preset avoids.

Reading: holding year fixed, a Part 121 passenger-mile carries about 1/4,250 of the light-duty death rate. The
like-for-like large-vehicle comparison the note suggests, part_121 against bus, is 0.000235 / 0.0111 ≈ 0.021: about 1/47
of the bus rate. Part 121 rests on 4 deaths (3 on an all-cargo flight), hence the 7-fold width of its interval.

θ check (the Task 6 risk: the NB θ score keeps a digamma difference for counts above 1000, which can cancel at large θ;
road counts here are about 25,000). The profile log-likelihood, refitting β at fixed θ and computed through `logGamma`
rather than the score, peaks at the fitted θ: −134.5604 at 0.99θ, −134.5602 at θ = 307.30, −134.5604 at 1.01θ,
−134.686 at 1.25θ, −135.551 at 0.5θ. The fitted θ is the maximum, so `glm.ts` was not changed.

### Preset 8: General aviation by aircraft class, adjusted for year — rate ratios per flight hour

`/api/rates?measure=occupant_fatalities&metric=flight_hours&fromYear=2019&toYear=2023&groupBy=vehicle_class,regulation_class`,
negative binomial, covariates `vehicle_class` and `year`, per 100k flight hours. Every row is `general_aviation` (the FAA
survey's only regulation class), so `vehicle_class` alone separates the rows.

**Reference: `vehicle_class=light_aircraft, year=2023`** (each term's reference is its level with the largest exposure).

| term | level | rate ratio | lo | hi | p | deaths | flight hours |
|---|---|---|---|---|---|---|---|
| vehicle_class | business_jet | 0.170 | 0.123 | 0.235 | 6.4e-27 | 52 | 21,996,634 |
| vehicle_class | experimental | 2.57 | 2.07 | 3.18 | 8.2e-18 | 237 | 6,711,550 |
| vehicle_class | helicopter | 0.888 | 0.706 | 1.12 | 0.31 | 172 | 13,843,149 |
| vehicle_class | light_aircraft | 1 (reference) | | | | 1,000 | 72,209,692 |
| vehicle_class | other | 5.05 | 3.61 | 7.07 | 3.5e-21 | 47 | 661,928 |
| vehicle_class | turboprop | 0.977 | 0.779 | 1.23 | 0.84 | 183 | 13,370,156 |
| year | 2019 | 1.50 | 1.18 | 1.91 | 0.00084 | 401 | 25,377,121 |
| year | 2020 | 1.25 | 0.973 | 1.60 | 0.081 | 319 | 22,290,254 |
| year | 2021 | 1.31 | 1.03 | 1.67 | 0.027 | 348 | 26,195,418 |
| year | 2022 | 1.16 | 0.906 | 1.48 | 0.24 | 317 | 26,722,352 |
| year | 2023 | 1 (reference) | | | | 306 | 28,207,964 |

Summary: family negative binomial (θ = 58.84); 30 rows used; 10 parameters; deviance 48.79 on 20 residual df; Pearson
dispersion 2.25; AIC 243.7; baseline rate 1.12 per 100k hours (light aircraft, 2023); converged yes; exposure sources
`faa-ga-survey (survey_estimate)`. **Warnings: none.**

Under `poisson`: **Pearson dispersion 3.15** on 20 df, deviance 66.48, with the "exceeds 1.5 … Try family
negative_binomial" warning. Point estimates within 2% of the NB ones (experimental 2.56 [2.22, 2.95], business_jet 0.172
[0.130, 0.227]).

θ check: the profile log-likelihood peaks at the fitted θ (−110.83064 at 0.99θ, −110.83060 at θ = 58.84, −110.83064
at 1.01θ).

Reading: with year held fixed, experimental (amateur-built) aircraft have 2.6 times the light-aircraft death rate per
flight hour, business jets about a sixth; helicopters and turboprops are not distinguishable from light aircraft.

### Preset 9: Road: did an occupant die? By vehicle class and vehicle age

`/api/events/aggregate?grain=vehicle&from=2019-01-01&to=2023-12-31&bucket=none&groupBy=vehicle_class,vehicle_age_years&f.mode=highway`,
outcome `vehicles_with_occupant_fatality` out of `vehicles`, covariates `vehicle_class` (categorical) and
`vehicle_age_years` (continuous).

**Reference: `vehicle_class=light_truck`** (the class with the most vehicles); vehicle age is per year of age.

| term | level | odds ratio | lo | hi | p | vehicles with a death | vehicles |
|---|---|---|---|---|---|---|---|
| vehicle_age_years | per unit | 1.0563 | 1.0551 | 1.0575 | 0 | 147,028 | 277,625 |
| vehicle_class | bus | 0.128 | 0.102 | 0.161 | 3.0e-69 | 81 | 980 |
| vehicle_class | heavy_truck | 0.280 | 0.270 | 0.290 | 0 | 4,582 | 26,440 |
| vehicle_class | light_truck | 1 (reference) | | | | 50,676 | 111,267 |
| vehicle_class | motorcycle | 29.4 | 27.8 | 31.2 | 0 | 28,235 | 29,482 |
| vehicle_class | other | 11.2 | 9.91 | 12.6 | 0 | 2,421 | 2,736 |
| vehicle_class | passenger_car | 1.69 | 1.66 | 1.72 | 0 | 60,987 | 106,634 |
| vehicle_class | unknown | 0.720 | 0.444 | 1.17 | 0.18 | 46 | 86 |

(The app shows the age ratio and its bounds as 1.06 at 3 significant digits; the 5-digit values are from the same fit.)

Summary: 443 rows used; 8 parameters; deviance 1,789 on 435 residual df; Pearson dispersion 8.46; AIC 3,181; baseline
probability 0.305 (a light truck of age 0); converged yes. **Warnings:**
- "7 rows dropped: missing vehicle_age_years"
- "Pearson dispersion 8.46 exceeds 1.5: outcomes vary more between groups than the binomial allows, so these intervals
  are too narrow."

(450 rows returned; the 7 are the null-age group of each of the 7 classes, 9,625 vehicles in all. They include 6,214
of the 6,300 `unknown`-class vehicles, so that class rests on 86 vehicles here).

Reading: FARS covers fatal crashes only, so these are odds that someone aboard died given the vehicle was in a fatal
crash. Each year of vehicle age multiplies those odds by 1.056 (about 1.73 over 10 years). Pearson dispersion 8.46 says
the straight-line age term and the class term do not explain the cell-to-cell variation, so the intervals here are
narrower than the data support (by roughly √8.46 ≈ 2.9 on the log scale); the second warning says so. The estimates are
not rescaled.

### Preset 10: Aviation accidents: did an occupant die? By regulation class and aircraft class

`/api/events/aggregate?grain=vehicle&from=2019-01-01&to=2023-12-31&bucket=none&groupBy=regulation_class,vehicle_class&f.mode=aviation&f.country=US`,
outcome `vehicles_with_occupant_fatality` out of `vehicles`, covariates `regulation_class` and `vehicle_class`. US
accidents only (6,234 aircraft; 8,118 without the country filter), to match the US-only exposure used elsewhere.

**Reference: `regulation_class=part_91, vehicle_class=light_aircraft`.**

| term | level | odds ratio | lo | hi | p | aircraft with a death | aircraft |
|---|---|---|---|---|---|---|---|
| regulation_class | other | 1.13 | 0.627 | 2.03 | 0.69 | 15 | 87 |
| regulation_class | part_121 | 3.59 | 0.222 | 58.1 | 0.37 | 2 | 163 |
| regulation_class | part_133 | 1.70 | 0.764 | 3.76 | 0.19 | 9 | 35 |
| regulation_class | part_135 | 1.13 | 0.771 | 1.64 | 0.54 | 39 | 192 |
| regulation_class | part_135_scheduled | 0.391 | 0.116 | 1.31 | 0.13 | 3 | 31 |
| regulation_class | part_137 | 0.669 | 0.462 | 0.971 | 0.034 | 49 | 284 |
| regulation_class | part_91 | 1 (reference) | | | | 948 | 5,435 |
| regulation_class | unknown | — (not estimable: all or none had the outcome) | | | | 0 | 7 |
| vehicle_class | airliner | 0.00901 | 0.000311 | 0.261 | 0.0061 | 1 | 171 |
| vehicle_class | business_jet | 0.855 | 0.513 | 1.43 | 0.55 | 18 | 126 |
| vehicle_class | experimental | 1.48 | 1.23 | 1.78 | 3.2e-5 | 182 | 830 |
| vehicle_class | helicopter | 1.08 | 0.833 | 1.39 | 0.57 | 98 | 579 |
| vehicle_class | light_aircraft | 1 (reference) | | | | 630 | 3,962 |
| vehicle_class | other | 1.36 | 0.946 | 1.95 | 0.097 | 39 | 190 |
| vehicle_class | turboprop | 2.34 | 1.74 | 3.14 | 1.5e-8 | 94 | 343 |
| vehicle_class | unknown | 0.757 | 0.223 | 2.57 | 0.66 | 3 | 26 |

Summary: 32 rows used; 14 parameters; deviance 19.08 on 18 residual df; Pearson dispersion 0.920; AIC 136.7; baseline
probability 0.159 (a Part 91 light aircraft); converged yes. **Warnings: "regulation_class=unknown: all or none had the
outcome; not estimable"** (expected: the 7 US aircraft of unknown regulation class had no occupant death, so the level and
its rows are left out). No dispersion warning (0.92 ≤ 1.5).

Reading: Part 121 (163 aircraft) and the airliner class (171) largely overlap in US data, so the model
can barely tell them apart, and the two ratios are wide and pull in opposite directions (part_121 3.59 [0.222, 58.1],
airliner 0.00901 [0.000311, 0.261]). Read them together: an airliner under Part 121 against a Part 91 light aircraft is
3.59 × 0.00901 ≈ 0.032. Turboprops (2.34) and experimental aircraft (1.48) have higher odds of a death per accident than
light aircraft; Part 137 (agricultural) has lower odds (0.669) than Part 91.

Without the country filter (the first run of this task) 8,118 aircraft entered, and the `other` and `unknown` regulation
classes were almost all foreign (1,602 of 1,689 and 184 of 191), with odds ratios 3.13 and 3.89 and Pearson dispersion
2.56. The controller ruled to filter to the US.

## Step 4: Cross-check (saturated one-factor model)

Preset 7's query under `poisson` with `rate_class` as the only covariate is saturated in `rate_class`, so its maximum
likelihood rate ratio is the ratio of pooled rates. The live test
`preset 7 with rate_class only: part_121 rate ratio equals the ratio of pooled rates` fits it and runs the `rate` method
on the same table grouped by `rate_class`:

| quantity | value |
|---|---|
| pooled part_121 rate (rate method, per 100M passenger-miles) | 0.00012835514588629924 |
| pooled light_duty rate (rate method, per 100M passenger-miles) | 0.5436651531210698 |
| ratio of pooled rates | 0.00023609228060588169 |
| part_121 rate ratio (poisson-regression, reference `rate_class=light_duty`) | 0.000236092280606479 |
| relative difference | 2.5e-12 (asserted < 1e-9) |

Pass. The part_121 pooled rate agrees with plan 3's preset 2 (0.000128).

## Step 5: Browser check

Done by the controller after this report (forest plot with the dashed rule at 1, tables without page-level horizontal
scroll, warnings shown).

## Rulings applied after the first run

1. Preset 10 is filtered to `country: ['US']` (numbers above), and its note says so.
2. `logistic-regression` now warns when the Pearson dispersion exceeds 1.5 (the limit shared with `poisson-regression`).
   Preset 9 shows the warning (8.46). Preset 10 does not (0.92). The intervals are not rescaled; a quasi-binomial or
   scaled-SE option is a Later item in the carry-over.
