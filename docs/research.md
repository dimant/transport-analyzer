# Transportation Safety Analysis App

Design document, draft v0.1 (November 2026)

## 1. Goal

Build a web app that brings public US transportation safety data (aviation, road, rail, transit) into one place. It should let a user answer questions like:

- What is the full incident history of a specific aircraft, operator or carrier? (for example, N722BD and its operator)
- How do accident and fatality rates compare across modes, normalized by exposure (flight hours, vehicle miles, passenger miles)?
- How do charter (Part 135) and private (Part 91) operations compare with scheduled airlines (Part 121)?
- Where and when do accidents cluster, by geography, weather, time of day or phase of operation?
- What do the narratives say? Full-text search across investigation narratives and probable causes.

Non-goals for v1: real-time flight tracking, predictive risk scoring, and non-US data.

## 2. Data sources

| Mode | Source | What it contains | Format and access | Update cadence | Notes |
|---|---|---|---|---|---|
| Aviation | NTSB aviation database (`avall.zip`, `Pre2008.zip`, `PRE1982.zip`) | Investigated civil aviation accidents and selected incidents, 1962 to present | Access MDB files | Monthly full, weekly change files | NTSB moves bulk data to its Enterprise API on April 5, 2027. Build the adapter behind an interface so the source can be swapped. |
| Aviation (all modes) | NTSB CAROL | Investigations across aviation, highway, marine, pipeline, rail, hazmat | JSON or CSV export, public API | Continuous | Only cases NTSB investigated |
| Aviation | FAA incident data (ASIAS) | Incidents not investigated by NTSB | Downloads | Varies | Verify current access path |
| Aviation | NASA ASRS | Voluntary pilot and controller safety reports | Web query with CSV export | Ongoing | De-identified, no tail numbers. Useful for pattern and narrative analysis. |
| Aviation | FAA Service Difficulty Reports (SDRS) | Mechanical problems and defects | Downloads | Ongoing | Joins by make, model and sometimes N-number |
| Aviation | FAA Aircraft Registry | Every N-number with owner, airframe, engine | CSV bulk download | Daily | Primary key for aircraft entity resolution |
| Aviation | OpenSky Network | Historical ADS-B tracks | Research API | Ongoing | Optional, for reconstructing flight paths |
| Road | NHTSA FARS | Census of fatal crashes on public roads, 1975 to present | CSV or SAS zips, JSON CrashAPI | Annual, with lag | Person, vehicle and crash tables |
| Road | NHTSA CRSS | Weighted sample of all police-reported crashes | CSV or SAS zips | Annual | Results are estimates, so weights must be applied |
| Road (commercial) | FMCSA MCMIS / SAFER | Truck and bus carrier crashes, inspections, safety ratings | Downloads | Monthly | Primary source for bus and truck operators |
| Road | NHTSA recalls, complaints, vPIC | Vehicle defects and VIN decoding | JSON APIs | Ongoing | Enrichment |
| Rail | FRA Safety Data | Train accidents (Form 6180.54), grade crossings, casualties | data.transportation.gov (Socrata API) | Monthly | |
| Transit | FTA National Transit Database | Major and non-major safety and security events, 2014 to present | data.transportation.gov (Socrata API) | Monthly, about 90 day lag | Buses, subway, light rail, commuter rail |
| Pipeline and hazmat | PHMSA | Incidents | Downloads | Ongoing | Later phase |
| Marine | USCG MISLE | Marine casualties | Downloads | Ongoing | Later phase |
| Exposure | BTS, FHWA, FAA GA Survey | Passenger-miles, vehicle miles traveled, flight hours by operation type | Downloads | Annual | Required for any rate comparison |

## 3. Architecture

```
                 +-----------------------------+
                 |  Web UI (Blazor or React)   |
                 +--------------+--------------+
                                |
                 +--------------v--------------+
                 |   ASP.NET Core API (REST)   |
                 +--------------+--------------+
                                |
        +-----------------------+-----------------------+
        |                                               |
+-------v---------+                           +---------v---------+
|  PostgreSQL     |  <-- normalized model     |  Search index     |
|  + PostGIS      |                           |  (Postgres FTS    |
|                 |                           |   to start)       |
+-------^---------+                           +---------^---------+
        |                                               |
+-------+-----------------------------------------------+---------+
|  Ingestion workers (.NET BackgroundService / Quartz.NET)        |
|  one adapter per source: fetch -> raw -> staged -> normalized   |
+-----------------------------------------------------------------+
```

### Stack

- **Backend:** .NET 9 or later, ASP.NET Core minimal APIs, EF Core for the normalized model, Dapper for heavy analytical queries.
- **Database:** PostgreSQL with PostGIS for geography. Consider DuckDB as a local analytics engine over Parquet extracts if aggregate queries get slow.
- **Ingestion:** one worker service per source family, scheduled with Quartz.NET. Every run is idempotent and keyed on source record ID plus source version.
- **Frontend:** Blazor keeps the stack all C#. React with a charting library (ECharts or Observable Plot) is the alternative if richer visuals matter more.
- **Maps:** MapLibre GL with vector tiles.
- **Hosting:** containers (Docker Compose locally, any container host later).

## 4. Ingestion pipeline

Each source adapter implements the same contract:

```csharp
public interface ISourceAdapter
{
    string SourceId { get; }                      // "ntsb-aviation", "fars", "fra-6180-54"
    Task<FetchResult> FetchAsync(DateTimeOffset? since, CancellationToken ct);
    IAsyncEnumerable<RawRecord> ReadAsync(FetchResult fetch, CancellationToken ct);
    StagedRecord Stage(RawRecord raw);            // typed, source-shaped, validated
    IEnumerable<NormalizedEvent> Normalize(StagedRecord staged);
}
```

Layers:

1. **Raw:** keep the original file or response as fetched (zip, MDB, JSON page), with a checksum and fetch timestamp. Never mutate it. This allows reprocessing when normalization logic changes.
2. **Staged:** source-shaped tables, one schema per source (`stg_ntsb.events`, `stg_ntsb.aircraft`, `stg_fars.accident`, and so on). Light typing and validation only.
3. **Normalized:** the shared cross-mode model described below.

Source-specific notes:

- **NTSB MDB:** on Windows, read with `System.Data.OleDb` and the ACE provider. In Linux containers, export with `mdbtools` (`mdb-tables`, `mdb-export`) to CSV, then bulk load with `COPY`. Apply the weekly change files incrementally.
- **Socrata (FRA, NTD):** page with `$limit` and `$offset`, filter incrementally with `$where=:updated_at > '...'`, and use an app token.
- **FARS and CRSS:** annual zips. Load each year as a unit, since codebooks change between years. Keep the codebook version on each record.

## 5. Normalized data model (v1)

```
event
  event_id          uuid  pk
  source_id         text            -- which adapter
  source_key        text            -- NTSB event id, FARS st_case+year, etc.
  mode              enum            -- aviation, highway, rail, transit, marine, pipeline
  event_type        enum            -- accident, incident, report
  occurred_at       timestamptz
  location          geography(point)
  state, country    text
  severity          enum            -- fatal, serious, minor, none, unknown
  fatalities        int
  serious_injuries  int
  minor_injuries    int
  ground_fatalities int null        -- third parties: bystanders, residents
  operation_class   text null       -- Part 91, 135, 121; FMCSA carrier type; etc.
  phase             text null       -- cruise, approach, takeoff, en route
  weather_summary   text null
  narrative         text null
  probable_cause    text null
  investigation_status text null
  source_payload    jsonb           -- everything else, untouched

vehicle               -- aircraft, train, bus, car involved in an event
  vehicle_id        uuid pk
  event_id          fk
  registration      text null       -- N-number, VIN, reporting mark
  make, model       text
  year_built        int null
  damage            text null

operator              -- airline, charter operator, railroad, motor carrier, transit agency
  operator_id       uuid pk
  name              text
  identifiers       jsonb           -- FAA certificate no., USDOT no., NTD id, aliases

event_operator      -- many to many with role (operator, owner, maintainer)

exposure
  mode, segment, year, metric (flight_hours, vmt, passenger_miles), value, source
```

### Entity resolution

This is the hardest part and the most valuable.

- **Aircraft:** join on N-number to the FAA Registry. Watch for re-registered N-numbers, since the same tail can belong to different airframes over time. Use serial number plus N-number plus date range.
- **Operators:** names vary ("Aurora Aviation" vs. "Upper Jets", a DBA). Match on FAA certificate number or USDOT number where present, with fuzzy name matching and a manual alias table as a fallback.
- **Cross-source links:** one real-world event can appear in NTSB, FAA incident data and the news. Link by (registration, date within a window, location within a radius) and store links in an `event_link` table rather than merging records.

## 6. Features

### Phase 1: foundation
- Ingest NTSB aviation (MDB plus weekly changes) and the FAA Registry.
- Event search: filters for date, mode, state, severity, operation class, make and model.
- Event detail page with narrative, probable cause and source link.
- **Aircraft and operator history pages:** a timeline of every event tied to a tail number or operator. This is the N722BD use case.

### Phase 2: multi-mode
- Add FARS, CRSS, FRA, NTD and FMCSA.
- Map view with clustering and heatmaps.
- Trend charts by mode, segment and year.

### Phase 3: rates and comparison
- Ingest exposure data.
- Rate calculator: accidents and fatalities per 100k flight hours, per 100M vehicle miles, per billion passenger-miles.
- Side-by-side segment comparison, such as Part 121 vs. 135 vs. 91, or transit bus vs. private car.
- Confidence intervals, since small segments have few events and noisy rates.

### Phase 4: narrative analysis
- Full-text search over narratives.
- Tagging narratives for themes (weather, turbulence, icing, maintenance, crew experience, pressure to complete the flight) with an LLM or classical classifier. Keep human-reviewable labels.
- Third-party harm view: ground fatalities and displaced residents, where recorded.

## 7. Data caveats to surface in the UI

- **Definitions differ by agency.** "Accident", "incident" and "serious injury" are not the same across NTSB, FRA, FTA and NHTSA. Show the source definition next to every number.
- **Lags.** NTSB final reports take 12 to 24 months, NTD has about a 90 day lag, and FARS is annual. Show "data current through" per source.
- **Coverage.** NTSB only covers what it investigates. ASRS is voluntary and self-selected. CRSS is a weighted sample, not a census.
- **Exposure uncertainty.** General aviation and charter flight hours come from surveys and estimates, so rates for those segments are less precise than airline rates.
- **Preliminary vs. final.** Fields like probable cause change as investigations progress. Keep history so the UI can show what changed.

## 8. Open questions

1. Blazor or React for the frontend?
2. Self-hosted Postgres, or a managed service?
3. Is the app single-user (personal analysis) or multi-user with saved queries and sharing?
4. How far back should history go in v1? (NTSB to 1962, FARS to 1975, NTD to 2014)
5. Should news articles be linked to events, and if so, from which sources?
6. Should the app include international data (EASA, ICAO, Aviation Safety Network) later?

## 9. First milestones

1. Repository skeleton: solution with `Api`, `Ingestion`, `Domain`, `Infrastructure`, `Web` projects, plus Docker Compose with Postgres and PostGIS.
2. NTSB adapter: download `avall.zip`, extract with mdbtools, stage tables, normalize into `event` and `vehicle`.
3. FAA Registry adapter and aircraft resolution by N-number.
4. Minimal API: `GET /events`, `GET /events/{id}`, `GET /aircraft/{registration}/history`.
5. Minimal UI: search, event detail, aircraft history timeline.
6. Validate end to end with N722BD: its prior events should appear as soon as they land in the source data.
