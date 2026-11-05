# Transportation Safety Experiment Platform: design

Date: 2026-11-04. Supersedes the feature plan in `docs/research.md` sections 3, 6 and 9. The source catalogue (section 2), ingestion layers (section 4), normalized model (section 5) and data caveats (section 7) of that document still apply except where this one says otherwise.

## 1. Intent

A personal workbench for testing "common knowledge" about transportation safety against public US data, and seeing what happens when the data is sliced in ways the common knowledge doesn't account for.

The first question it must answer: **is air travel safer than driving, and how does the answer change when you control for attributes of the vehicle and operation** (size, occupancy, operator type, regulation, maintenance regime, crew, vehicle age)? The slices after that are not known yet, and that is a design constraint: the system must keep every dimension the sources offer and let the grouping be chosen at query time.

The server is a data service. The browser is the lab.

## 2. Goals, non-goals, anti-goals

**Goals**

- Pull data from public sources on a schedule (roughly monthly), keep the raw files, and load them into Postgres through source-specific adapters.
- Expose one query API that returns filtered, grouped counts over a time range, a random sample of matching rows, and per-source metadata.
- A frontend that is a thin shell around a growing TypeScript library of statistical methods. Adding a method means adding a module; the UI should need little or no change.
- Make exposure (passenger-miles, vehicle-miles, flight hours) a first-class dataset, since every rate comparison depends on it.
- Show uncertainty: confidence intervals on every rate, source definitions and "data current through" next to every number.

**Non-goals**

- Spatial analysis. Keep latitude and longitude as plain columns so nothing is lost, but no PostGIS, no map view, no spatial methods.
- Real-time anything. Monthly is fine.
- Multi-user features: accounts, sharing, saved queries for others. Single user.
- Non-US data, news linking, predictive risk scoring.
- Entity resolution of individual aircraft or operators (the N722BD use case from research.md). The data model keeps registration numbers so this can come later, but no work goes into it in v1.

**Anti-goals** (would make this a failure even if it works)

- Dropping columns during ingestion because they aren't needed yet. Every source record is kept whole in `source_payload`.
- Treating weighted samples (CRSS, FAA GA Survey) as censuses.
- Showing a rate without its interval or its denominator's source.
- A frontend framework that ends up owning the analysis code. The statistics library must have no UI or framework dependency.

## 3. Constraints and decisions already made

| Decision | Choice | Who decided |
|---|---|---|
| Server | ASP.NET Core (.NET 9 or later), minimal APIs | user |
| Database | PostgreSQL, no PostGIS | user (Postgres), Claude (no PostGIS, follows from no spatial) |
| Scheduling | Quartz.NET inside the ASP.NET host, monthly cron per source, manual trigger endpoint | Claude |
| Frontend | TypeScript, Vite, Svelte, served as static files by the ASP.NET app | Claude |
| Plots | Observable Plot | Claude |
| Analysis code runs | in the browser | user |
| Statistics library | plain TypeScript, one module per method, no framework dependency | user (plain TS, accumulated module by module), Claude (interface below) |
| Hosting | Docker Compose: one app container, one Postgres container | Claude |
| Repo layout | single repository, `src/` for .NET projects, `web/` for the frontend | Claude |

## 4. Data sources for v1

Three source families are enough for the first question. The rest of the catalogue in `docs/research.md` section 2 comes later through the same adapter contract.

| Source | Role | Notes |
|---|---|---|
| NTSB aviation database (`avall.zip`, pre-2008 archives) | aviation events, with aircraft attributes and operation class (Part 91/121/135) | Access MDB. Read with `mdbtools` in the container. NTSB moves to an Enterprise API in April 2027; adapter sits behind the interface so it can be swapped. |
| NHTSA FARS | fatal road crashes with vehicle body type, occupants, driver attributes | Annual CSV zips. Codebooks change by year; keep the codebook year on every record. |
| Exposure: BTS (passenger-miles by mode), FHWA (VMT by vehicle class), FAA GA Survey and Part 121/135 activity (flight hours by operation type) | denominators | Annual tables, often spreadsheets. Adapter for each, loading into the `exposure` table. Record the source and whether the figure is a survey estimate. |

CRSS (non-fatal crashes, weighted sample) is the natural fourth source and the first test of the survey-weights module. It is in scope for the first follow-up, not v1.

## 5. Ingestion

Layers are as in `docs/research.md` section 4: raw (file as fetched, with checksum and fetch time, never mutated), staged (one schema per source, source-shaped), normalized (shared model). Each adapter implements `ISourceAdapter` as sketched there.

Every run is idempotent. A run record (`ingest_run`: source, started, finished, status, row counts, raw file checksums) is written per run and exposed through the API.

### Harmonized vehicle and operation attributes

This is the central ingestion deliverable for the first question. Each source codes these attributes differently; each adapter maps them into a shared vocabulary. The mapping tables are data, not code, so they can be reviewed and corrected:

| Attribute | Shared vocabulary (initial) | FARS source | NTSB source |
|---|---|---|---|
| `vehicle_class` | motorcycle, passenger_car, light_truck, bus, heavy_truck, light_aircraft, turboprop, business_jet, airliner, helicopter, other | BODY_TYP | aircraft category, engine count, weight class, make/model |
| `operator_type` | private, commercial, government, unknown | vehicle use / owner codes | FAR part, purpose of flight |
| `regulation_class` | road_private, road_commercial, part_91, part_121, part_135, part_137, other | registration / carrier fields | far_part |
| `occupants` | integer | person count per vehicle | injury totals per aircraft |
| `crew_count` | integer or null | always 1 driver | crew on board |
| `vehicle_age_years` | integer or null | MOD_YEAR vs crash year | year built vs event date |
| `maintenance_regime` | none, state_inspection, mandated_periodic, unknown | derived from vehicle use and state | derived from FAR part |

The shared vocabularies are enumerations stored in a `vocabulary` table (attribute, code, label, description) so the frontend can show definitions. Unmapped source values map to `unknown` and are counted; a high unknown rate on any attribute is reported in the run record.

### Normalized model

As in `docs/research.md` section 5 with these changes:

- `event.location geography(point)` becomes `latitude double precision null, longitude double precision null`.
- `vehicle` gains the harmonized attribute columns above, plus `source_payload jsonb` of its own.
- `exposure` gains `provenance enum (measured, survey_estimate, derived)`, `standard_error double precision null`, `assumptions jsonb null` (for derived rows), and `vehicle_class`, `regulation_class` so it can be joined to events on the same vocabulary.
- `event_operator` and `operator` are created but no adapter populates `operator` beyond a name in v1.

Exposure must be joinable to events on `(mode, year, vehicle_class, regulation_class)` for the first question to work. Where a source only provides exposure at a coarser level (for example VMT by vehicle class but not by operator type), the row is stored at that level and the API reports the join level it used.

### Casualty attribution (user decision)

Casualties are stored per vehicle, not only per event, so that two views of "is flying safer than driving" are both available and chosen at query time:

- **Occupant risk:** deaths of people on board a given vehicle. `vehicle.occupant_fatalities`, `vehicle.occupant_serious_injuries`, `vehicle.occupant_minor_injuries`.
- **Societal risk:** occupant deaths plus non-occupants (pedestrians, cyclists, people on the ground) attributed to the striking vehicle. `vehicle.nonoccupant_fatalities` and so on, derived from the FARS `person` table (`PER_TYP`, `STR_VEH`) and from NTSB ground-casualty fields.

`event.fatalities` remains the event total and must equal the sum over vehicles. This requires ingesting the FARS `person` table in v1 and a `person` staging table; the normalized model does not get a `person` table in v1, only the per-vehicle sums. Occupant risk is the default for the first experiment.

### Vocabulary hierarchy (Claude's call)

The `vocabulary` table gets a nullable `parent_code` so coarse exposure can be joined to fine event codes. Example: FHWA stopped splitting passenger cars from light trucks in 2007 and now reports "light duty", so `passenger_car` and `light_truck` both have parent `light_duty`, and the rate join rolls the numerator up to `light_duty` when that is the finest level the denominator has.

### Re-ingestion (Claude's call)

Normalized rows are upserted by `(source_id, source_key)` and stamped with the `ingest_run_id` that last wrote them. There is no row versioning in v1; the raw file archive is the history, and a reprocess from raw is how a past state is reconstructed. The "keep history so the UI can show what changed" item in `docs/research.md` section 7 is deferred.

### Scope of "kept whole" (Claude's call)

The anti-goal means no source table or column is discarded. Every table in a source (FARS has about twenty, the NTSB MDB has findings, sequence of events, engines, crew) is staged in that source's schema and stays queryable there. `source_payload` on `event` and `vehicle` holds the main source record only, not nested child tables.

## 6. API

Small surface, built around one idea: the server does the counting, the browser does the statistics.

| Endpoint | Purpose |
|---|---|
| `GET /api/sources` | list of sources with "data current through", last run, definitions, caveats |
| `GET /api/vocabulary` | all attribute enumerations with labels and definitions |
| `GET /api/events/aggregate` | counts (events, fatalities, injuries) grouped by any set of event or vehicle dimensions, filtered by any dimension, over a time range, with time bucket (year, quarter, month). Returns a flat table. |
| `GET /api/events/sample` | random sample of matching events joined to their vehicles, flat rows, `n` capped (default 10,000, hard cap 100,000). Seeded for reproducibility. |
| `GET /api/exposure` | exposure rows filtered by mode, year range, vehicle class, regulation class, metric |
| `GET /api/rates` | convenience: aggregate joined to exposure on the finest common level, returns numerator, denominator, join level and source. The browser computes the rate and interval. The denominator is never spread down to a finer level than its source provides; the numerator is rolled up to the join level instead. |
| `POST /api/admin/ingest/{source}` | trigger a run now |
| `GET /api/admin/runs` | run history |

Dimensions and filters are whitelisted column names, not free SQL. Responses are JSON for v1. Dapper for the aggregate queries, EF Core for the normalized model and migrations.

## 7. Frontend

A Svelte app with three parts:

1. **Query panel.** Pick source(s), time range, filters, grouping dimensions. Produces a table from `/aggregate`, `/sample` or `/rates`.
2. **Method panel.** Pick a method from the statistics library, bind table columns to the method's inputs, run it. Each method declares its inputs (which columns, of which types) so this panel is generated, not hand-written per method.
3. **Result panel.** Renders the method's result: table, plot (Observable Plot), or both. Every rate shows its interval and its denominator source. Every number shows the source definition on hover.

A query plus method plus bindings is a serializable "experiment" that can be saved to local storage and reloaded. No server-side saving in v1.

### Statistics library

Lives at `web/src/stats/`, plain TypeScript, no imports from Svelte or Plot, unit-tested on its own. Module contract (Claude's call, the builder may refine the types):

```ts
interface Method<P, R> {
  id: string;
  name: string;
  inputs: InputSpec[];           // columns required, with type (categorical | count | continuous | exposure | weight)
  params: ParamSpec<P>;          // method options with defaults
  run(table: Table, bindings: Bindings, params: P): R;
  render?: (result: R) => PlotSpec | TableSpec;   // optional default rendering
}
```

**Modules in v1** (in build order):

1. `crosstab`: counts by one or two categorical columns.
2. `rate`: events per exposure unit with Poisson (Garwood) confidence intervals; handles the numerator/denominator join level reported by `/rates`.
3. `heatmap`: cross-tab rendered as a colour matrix, count or rate.
4. `poisson-regression`: log-linear model with exposure offset, categorical covariates; returns rate ratios with intervals. This is "control for vehicle size". Negative binomial as a parameter if overdispersion is detected. Only attributes that have an exposure breakdown (vehicle class, regulation class, mode, year) can enter as rate covariates.
5. `logistic-regression`: case-only analysis for attributes with no exposure breakdown (vehicle age, occupancy, crew count, maintenance regime). Outcome is per-vehicle, for example fatal vs. non-fatal or occupant death vs. survival, covariates categorical. Returns odds ratios with intervals. Added to v1 because most of the attributes in section 1 can only be analysed this way.

**Planned next** (not in v1, but the module contract must not make them awkward): survey weights (needed for CRSS), categorical association (Cramér's V, mutual information), correspondence analysis and MCA (HOMALS), hierarchical Poisson, age-period-cohort, survival on vehicle age, change-point detection, PCA on derived rate matrices, clustering on MCA coordinates.

## 8. Milestones

1. Repository skeleton: .NET solution (`Api`, `Ingestion`, `Domain`, `Infrastructure`), Vite + Svelte app in `web/`, Docker Compose with Postgres, CI running .NET tests and `vitest`.
2. Exposure adapters and `exposure` table, since they are small and the first question is impossible without them.
3. FARS adapter for the most recent five years: raw, staged, normalized, with the vehicle attribute mapping.
4. NTSB adapter for the same years, same mapping.
5. API: `/sources`, `/vocabulary`, `/aggregate`, `/exposure`, `/rates`.
6. Frontend shell with query panel and `crosstab`, `rate`, `heatmap`.
7. First experiment end to end: occupant fatalities per billion passenger-miles, **Part 121 airlines vs. road**, by year, with intervals. Compare against published BTS figures as a correctness check. Private and charter aviation (Part 91, 135) are compared separately on a per-flight-hour basis in the same milestone, since no passenger-mile denominator exists for them (user decision). Derived GA passenger-miles (hours × assumed speed × assumed occupancy, flagged as derived with the assumptions stored) are a later addition, not v1.
8. `poisson-regression` and the "control for vehicle class" version of the same question; `logistic-regression` for the case-only attributes.
9. Quartz schedules and `/sample`, then extend history backwards.

## 9. Left to the builder

- Exact .NET project boundaries and folder layout within the conventions above.
- Choice of Poisson regression fitting algorithm (IRLS is the obvious one) and numerical linear algebra approach in TypeScript (hand-rolled small matrices or a dependency such as `ml-matrix`).
- Table representation in the browser (columnar typed arrays vs. row objects); columnar is suggested for the sample sizes involved.
- Exact FARS and NTSB field-to-vocabulary mappings. The builder drafts them from the codebooks; the user reviews them, since they encode the "attributes inherent to the mode" the whole project hinges on.
- Error handling, logging, and retry policy for downloads.
- Which FARS years the first load covers if five is too many to validate against; fewer is fine.

## 10. Decisions log

Settled with the user during design: single user; analysis runs in the browser; no spatial work; occupant and societal casualty views both stored, occupant default; first experiment is Part 121 vs. road on passenger-miles, GA on flight hours, derived GA passenger-miles deferred.

Claude's calls, open to revision: Svelte/Vite/Plot; Quartz in-process; Docker Compose; vocabulary `parent_code`; upsert without row versioning; `source_payload` holds the main record only; numerator rolled up rather than denominator spread down; logistic regression in v1.
