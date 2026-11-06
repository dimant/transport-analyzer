# Transport Analyzer

Transport Analyzer ingests public transport safety data into one shared schema and serves a query API over it. It
loads US highway fatal crashes from NHTSA FARS, aviation accidents and incidents from the NTSB database, passenger-miles
and vehicle-miles from the BTS National Transportation Statistics tables, and general-aviation flight hours from the FAA
General Aviation and Part 135 Activity Survey. You can count events and casualties by any whitelisted dimension and pair
them with exposure to get rate numerators and denominators. The design is in
[docs/superpowers/specs/2026-11-04-experiment-platform-design.md](docs/superpowers/specs/2026-11-04-experiment-platform-design.md).

## Prerequisites

Docker with Compose. To run the tests you also need the .NET 10 SDK.

## Start

```sh
docker compose up -d --build
curl localhost:8080/api/health
```

On startup the app migrates the database and loads `data/vocabulary.csv` and `data/mappings/*.csv`.

## Get the data

| Source id | Kind | Mode | What it supplies | How it is fetched |
|---|---|---|---|---|
| `fars` | events | highway | NHTSA FARS fatal crashes, 2019–2023 by default | downloaded by the app |
| `ntsb` | events | aviation | NTSB aviation accidents and incidents, 2008 on | downloaded by the app |
| `bts-passenger-miles` | exposure | highway, aviation (Part 121) | `passenger_miles` | xlsx dropped in `data/inbox/` by hand |
| `bts-vehicle-miles` | exposure | highway | `vehicle_miles` | xlsx dropped in `data/inbox/` by hand |
| `faa-ga-survey` | exposure | aviation (general aviation) | `flight_hours` | downloaded by the app |

**FARS** is downloaded by the app from static.nhtsa.gov.

**NTSB** (`ntsb`, aviation accidents and incidents from 2008 on) is downloaded by the app from data.ntsb.gov as
`avall.zip`, an Access database read with mdbtools (installed in the Docker image; `mdb-tables` and `mdb-export` must be
on `PATH` when running outside Docker). Every run downloads and stages the whole snapshot; `?years=` limits which
years are written to events. NTSB `occurred_on` is the UTC date of the occurrence (NTSB records most event times in
UTC), while FARS `occurred_on` is the local date, so an evening accident in US local time can be stored under the next
day.

**BTS** tables have to be downloaded by hand, because bts.gov blocks automated clients. Save the xlsx download into the
source's drop folder. The file name does not matter; the newest `*.xlsx` in the folder is used.

| Source id | Table | Sheet read | Page | Drop folder |
|---|---|---|---|---|
| `bts-passenger-miles` | NTS Table 1-40 U.S. Passenger-Miles | `1-40` | https://www.bts.gov/content/us-passenger-miles | `data/inbox/bts-passenger-miles/` |
| `bts-vehicle-miles` | NTS Table 1-35 U.S. Vehicle-Miles | `1-35` | https://www.bts.gov/content/us-vehicle-miles | `data/inbox/bts-vehicle-miles/` |

Files under `data/inbox/` are ignored by git.

**FAA General Aviation Survey** (`faa-ga-survey`) supplies flight hours (`flight_hours`) for general aviation, by aircraft
type (Table 1.3) and by use (Table 1.4, from which Part 91 / 137 / 133 hours are derived). The app downloads the
chapter 1 xlsx from faa.gov. The URL changes every survey year (both the folder and the file name), so the app cannot
find a new edition by itself. When the FAA publishes a new survey year on
https://www.faa.gov/data_research/aviation_data_statistics/general_aviation, copy the link of its chapter 1 xlsx
(`...GASurveyCh1....xlsx`) into `Sources:faa-ga-survey:Url` in `src/TransportAnalyzer.Api/appsettings.json` and rebuild
(`docker compose up -d --build`), or set `Sources__faa-ga-survey__Url` in the `app` service's `environment` in
`docker-compose.yml`. Then ingest `faa-ga-survey` again. The CY2024 edition's Tables 1.3 and 1.4 cover 2013–2024.

## Ingest

```sh
curl -X POST localhost:8080/api/admin/ingest/fars                    # FARS 2019-2023
curl -X POST 'localhost:8080/api/admin/ingest/fars?years=2019,2020'  # chosen years
curl -X POST localhost:8080/api/admin/ingest/ntsb                    # NTSB, every year in avall (2008 on)
curl -X POST localhost:8080/api/admin/ingest/bts-passenger-miles
curl -X POST localhost:8080/api/admin/ingest/bts-vehicle-miles
curl -X POST localhost:8080/api/admin/ingest/faa-ga-survey          # FAA GA survey flight hours
```

Each call returns `202` with `{"runId": ...}` and runs in the background. Poll the run log until the status is
`succeeded` or `failed`:

```sh
curl 'localhost:8080/api/admin/runs'                       # newest first; optional sourceId=..., limit=... (default 50)
```

A run records its raw files, row counts, counts of `unknown` values per harmonized attribute (`unknown:<attribute>`),
source values that had no mapping, and the error if it failed. Starting a source that already has a run in progress
returns `409` with the id of that run. A re-ingest replaces the rows of the years it covers.

### Schedules

The app re-ingests some sources on its own. Schedules are Quartz cron expressions (seconds, minutes, hours,
day-of-month, month, day-of-week) and are evaluated in **UTC**:

| Source id | Setting `Sources:{id}:Schedule` | Runs |
|---|---|---|
| `fars` | `0 0 3 1 * ?` | 03:00 UTC on the 1st of every month |
| `ntsb` | `0 0 4 2 * ?` | 04:00 UTC on the 2nd of every month |
| `faa-ga-survey` | `0 0 5 1 3 ?` | 05:00 UTC on 1 March every year |

The BTS sources have no schedule because their files are dropped in by hand. A scheduled run is the same as
`POST /api/admin/ingest/{id}` without `years`, and it is skipped if a run of that source is already in progress.

What the default schedules fetch: the monthly FARS run re-ingests the same fixed default years, 2019–2023.
When NHTSA releases a new year, set `Sources:fars:DefaultYears` (e.g. `Sources__fars__DefaultYears__0: "2019"`, ... in
`docker-compose.yml`, or a `"DefaultYears": [2019, ..., 2024]` array in `appsettings.json`) or run an explicit
`POST /api/admin/ingest/fars?years=2024`.
The FAA URL is pinned to the CY2024 edition, so the yearly `faa-ga-survey` run fetches that same file again until the
URL is updated (see above). Unchanged downloads do not take extra space in the raw store (see "Stopping and data"). The
startup log lists each schedule with its next fire time (`docker compose logs app | grep "Scheduled ingest"`).

To change a schedule, edit `Sources:{id}:Schedule` in `src/TransportAnalyzer.Api/appsettings.json` and rebuild, or set
the environment variable in the `app` service's `environment` in `docker-compose.yml`, for example
`Sources__fars__Schedule: "0 0 3 15 * ?"`. An empty value (`Sources__fars__Schedule: ""`) turns off that one source's
schedule. `Scheduling__Enabled: "false"` turns off all schedules. An invalid cron expression stops the app at startup
with an error naming the setting.

### Changing vocabulary or mappings

`data/` (vocabulary and mappings) is baked into the image. After editing `data/vocabulary.csv` or
`data/mappings/*.csv`, rebuild with `docker compose up -d --build` and re-ingest the affected sources.

## Stopping and data

- `docker compose down` keeps the database (`pgdata` volume) and the downloaded raw files (`rawdata` volume).
- Raw files: a download identical (same sha256) to the same-named file of the source's last successful run is not stored
  twice; the run records the existing file. Downloads that differ accumulate under the `rawdata` volume and are never
  deleted by the app.
- `docker compose down -v` deletes both volumes: everything is ingested again from scratch on the next start.

The files in `data/inbox/` are on your disk and are kept either way.

## Security

Port 8080 is published on all network interfaces. The admin endpoints (`/api/admin/*`) need no token by default. To
require one, set `Admin__Token` in the `app` service's `environment` in `docker-compose.yml`; requests must then send
the header `X-Admin-Token: <token>`.

## Query API

All responses are JSON with camelCase keys. Tabular results have `columns` (snake_case names) and `rows` (arrays).

| Endpoint | Parameters | Example |
|---|---|---|
| `GET /api/sources` | none | `curl localhost:8080/api/sources` |
| `GET /api/vocabulary` | none | `curl localhost:8080/api/vocabulary` |
| `GET /api/events/aggregate` | `from`, `to` (ISO dates, required); `grain` (`event` or `vehicle`); `bucket` (`none`, `year`, `quarter`, `month`); `groupBy` (comma list); `f.<dimension>=a,b` filters | `curl 'localhost:8080/api/events/aggregate?grain=vehicle&from=2022-01-01&to=2022-12-31&bucket=year&groupBy=vehicle_class'` |
| `GET /api/exposure` | `metric` (required: `passenger_miles`, `vehicle_miles`, `flight_hours`); `mode`, `fromYear`, `toYear`, `vehicleClass`, `regulationClass`, `sourceId` | `curl 'localhost:8080/api/exposure?metric=vehicle_miles&mode=highway&fromYear=2019&toYear=2022'` |
| `GET /api/events/sample` | `from`, `to` (required); `n` (rows, default 10000, at most 100000); `seed` (integer, default 0); `f.<dimension>=a,b` filters | `curl 'localhost:8080/api/events/sample?from=2022-01-01&to=2022-12-31&f.mode=aviation&f.country=US&n=100&seed=42'` |
| `GET /api/rates` | `measure` (`occupant_fatalities` or `societal_fatalities`), `metric`, `fromYear`, `toYear` (all required); `groupBy` (`vehicle_class` and/or `regulation_class`); `exposureSource` (default: `bts-passenger-miles`, `bts-vehicle-miles` or `faa-ga-survey` for `flight_hours`) | `curl 'localhost:8080/api/rates?measure=occupant_fatalities&metric=vehicle_miles&fromYear=2019&toYear=2022&groupBy=vehicle_class'` |

Dimensions for `groupBy` and `f.` are `source_id`, `mode`, `event_type`, `severity`, `state`, `country`,
`operation_class`, `phase` and, at vehicle grain only, `vehicle_class`, `vehicle_class_parent`, `operator_type`,
`regulation_class`, `maintenance_regime`, `occupants`, `crew_count`, `vehicle_age_years`, `year_built`, `make`.
`/api/events/sample` returns vehicle rows in a random order that is fixed by `seed`. Each row starts with
`source_id`, `source_key` (the event's key in its source, e.g. a FARS `ST_CASE` with its year, or an NTSB `ev_id`)
and `vehicle_no`, which link it back to its source record, followed by the other dimensions, `occurred_on` and the
casualty counts. The order is computed from that key and the seed, so the same parameters return the same rows as long
as the data are the same, also after a re-ingest (scheduled or not) of an unchanged file. `total` is the number of rows
that match.

`/api/rates` returns numerators and denominators, not computed rates. Each row's `join_level` names the exposure key
the numerator was matched to:

- `mode`: the mode total (no vehicle or regulation class on the exposure row).
- `vehicle_class`: the numerator's own vehicle class.
- `vehicle_class_parent`: the parent of the numerator's vehicle class (e.g. a class rolled up to its group), because
  the exposure has no row for the class itself.
- `regulation_class`: the numerator's own regulation class. Grouped by vehicle class, this can be a row without a
  vehicle class (`vehicle_class: null`), e.g. Part 121, which the exposure files by regulation class only. The mode
  total is still never used under a vehicle-class grouping.
- `regulation_class_parent`: the parent of the numerator's regulation class.
- Combinations joined with `+` when the exposure row has both, e.g. `vehicle_class+regulation_class` or
  `vehicle_class+regulation_class_parent` (vehicle class part first).

Numerators with no usable exposure are listed in `unmatched` with a `reason`:

- `no_exposure`: no exposure row exists for the cell, its parents or the mode total (as allowed by the grouping).
- `mixed_granularity`: the cell would roll up to a parent exposure row while another cell matched a child of that parent
  exactly, so the parent's exposure would be counted beside part of itself; all cells matched to that parent are
  listed here instead. Also used when, grouped by vehicle class, a cell matched a classless regulation-class row
  (such as Part 121 passenger miles) but not every cell of that regulation class did: e.g. unknown-class Part 91
  aircraft would otherwise be divided by all Part 91 hours while light aircraft use general-aviation hours.

Casualties not attributed to any vehicle are in `unattributed`. Rates count **US events only** (`"scope": {"country": "US"}` in the response), because the BTS and FAA
exposure is national; `/api/events/aggregate` and `/api/events/sample` are not restricted.

Aviation rates by aircraft class need both dimensions:

```sh
curl 'localhost:8080/api/rates?measure=occupant_fatalities&metric=flight_hours&fromYear=2019&toYear=2023&groupBy=vehicle_class,regulation_class'
```

The FAA survey files its hours by aircraft type under the regulation class `general_aviation` only (Part 91, on-demand
Part 135, Part 137 and Part 133 together). With `groupBy=vehicle_class` alone, no aviation numerator finds a denominator
and every aviation class ends up in `unmatched`. With `groupBy=regulation_class`, `flight_hours` gives one row per Part
(`part_91`, `part_135`, `part_137`, `part_133`); Part 121 and scheduled Part 135 have no flight hours. Part 121
against road travel works with either grouping: `metric=passenger_miles&groupBy=vehicle_class,regulation_class` returns
Part 121 (one row, no vehicle class) beside the road vehicle classes, and `groupBy=regulation_class` returns it beside
the road total.

## Frontend

The browser UI lives in `web/` (Vite + Svelte 5 + TypeScript).

    cd web && npm ci && npm run dev    # dev server; needs the API on localhost:8080
    npm test                           # unit tests
    npm run build                      # writes into src/TransportAnalyzer.Api/wwwroot

`wwwroot/` is generated and not committed. `docker compose up -d --build` builds the frontend automatically.

### Experiments

An experiment is a query, a method, its column bindings and its parameters. Ten presets ship with the app; load one from
the experiment list:
- rates: airlines vs road by year and combined, GA by aircraft class, road by vehicle class;
- an aviation heatmap and a road crosstab;
- rate ratios (negative binomial): deaths per passenger-mile by vehicle class with airlines included, and GA by aircraft
  class per flight hour, both adjusted for year;
- odds ratios (logistic): did an occupant die, for road vehicles by class and vehicle age, and for aircraft by regulation
  class and aircraft class.

Your own experiments are saved in the browser's storage only (`localStorage`): they are not sent to the
server and do not follow you to another browser. Export an experiment as JSON to keep or share it (there is no import yet). The server counts; the browser does the statistics (rates with exact Garwood intervals, crosstabs, heatmaps,
Poisson / negative binomial and logistic regression).
Every rate is shown with its interval, its exposure source and its provenance.

Reading rate ratios and odds ratios: each ratio compares a level with its term's reference level (the one with the
largest exposure or most vehicles, named under the forest plot), with the other covariates held fixed; a numeric
covariate such as vehicle age gives the ratio per unit. 1 means no difference, and the interval (95% Wald by default)
shows how far the data pin it down; an interval that crosses the dashed line at 1 does not separate the level from the
reference. A level shown as "not estimable" had no events (or, for odds, all or none had the outcome) and was left out of
the fit; "aliased" means its column is a combination of other covariates, so the data cannot tell them apart. FARS covers
fatal crashes only, so road odds ratios are odds that someone aboard died given the vehicle was in a fatal crash, not
the risk of dying in any crash.

### Adding a method

A method is a module in `web/src/stats/` that implements `Method` (`web/src/stats/method.ts`): `id`, `name`, `inputs`,
`params`, `run(table, bindings, params)` and `render(result)`. Append it to `methods` in `web/src/stats/registry.ts`. The UI
builds its column pickers, parameter controls and result views from the method's declared inputs, params and render specs,
so no UI change is needed. `web/src/stats/` may not import anything outside itself (a test checks this).

To run the presets against a live API: `cd web && TA_API=http://localhost:8080 npm test -- presets.live` (skipped without `TA_API`).

## Tests

```sh
dotnet test
```

The tests start PostgreSQL 17 through Testcontainers, so Docker must be running.

## Verification

Real-data checks are in [docs/verification/plan-1.md](docs/verification/plan-1.md) (FARS and BTS) and
[docs/verification/plan-2.md](docs/verification/plan-2.md) (NTSB, FAA survey, aviation rates) and
[docs/verification/plan-3.md](docs/verification/plan-3.md) (browser app, presets).
