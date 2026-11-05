# Transport Analyzer

Transport Analyzer ingests public transport safety data into one shared schema and serves a query API over it. This
first slice loads US highway fatal crashes from NHTSA FARS and exposure (passenger-miles, vehicle-miles) from the BTS
National Transportation Statistics tables. You can count events and casualties by any whitelisted dimension and pair
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

**FARS** is downloaded by the app from static.nhtsa.gov.

**BTS** tables have to be downloaded by hand, because bts.gov blocks automated clients. Save the xlsx download into the
source's drop folder. The file name does not matter; the newest `*.xlsx` in the folder is used.

| Source id | Table | Sheet read | Page | Drop folder |
|---|---|---|---|---|
| `bts-passenger-miles` | NTS Table 1-40 U.S. Passenger-Miles | `1-40` | https://www.bts.gov/content/us-passenger-miles | `data/inbox/bts-passenger-miles/` |
| `bts-vehicle-miles` | NTS Table 1-35 U.S. Vehicle-Miles | `1-35` | https://www.bts.gov/content/us-vehicle-miles | `data/inbox/bts-vehicle-miles/` |

Files under `data/inbox/` are ignored by git.

## Ingest

```sh
curl -X POST localhost:8080/api/admin/ingest/fars                    # FARS 2019-2023
curl -X POST 'localhost:8080/api/admin/ingest/fars?years=2019,2020'  # chosen years
curl -X POST localhost:8080/api/admin/ingest/bts-passenger-miles
curl -X POST localhost:8080/api/admin/ingest/bts-vehicle-miles
```

Each call returns `202` with `{"runId": ...}` and runs in the background. Poll the run log until the status is
`succeeded` or `failed`:

```sh
curl 'localhost:8080/api/admin/runs'                       # newest first; optional sourceId=..., limit=... (default 50)
```

A run records its raw files, row counts, counts of `unknown` values per harmonized attribute (`unknown:<attribute>`),
source values that had no mapping, and the error if it failed. Starting a source that already has a run in progress
returns `409` with the id of that run. A re-ingest replaces the rows of the years it covers.

### Changing vocabulary or mappings

`data/` (vocabulary and mappings) is baked into the image. After editing `data/vocabulary.csv` or
`data/mappings/*.csv`, rebuild with `docker compose up -d --build` and re-ingest the affected sources.

## Stopping and data

- `docker compose down` keeps the database (`pgdata` volume) and the downloaded raw files (`rawdata` volume).
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
| `GET /api/rates` | `measure` (`occupant_fatalities` or `societal_fatalities`), `metric`, `fromYear`, `toYear` (all required); `groupBy` (`vehicle_class` and/or `regulation_class`); `exposureSource` | `curl 'localhost:8080/api/rates?measure=occupant_fatalities&metric=vehicle_miles&fromYear=2019&toYear=2022&groupBy=vehicle_class'` |

Dimensions for `groupBy` and `f.` are `source_id`, `mode`, `event_type`, `severity`, `state`, `country`,
`operation_class`, `phase` and, at vehicle grain only, `vehicle_class`, `vehicle_class_parent`, `operator_type`,
`regulation_class`, `maintenance_regime`, `occupants`, `crew_count`, `vehicle_age_years`, `year_built`, `make`.
`/api/rates` returns numerators and denominators, not computed rates; see `join_level` for how each pair was matched.

## Tests

```sh
dotnet test
```

The tests start PostgreSQL 17 through Testcontainers, so Docker must be running.

## Verification

Real-data checks of this slice are in
[docs/verification/plan-1.md](docs/verification/plan-1.md).
