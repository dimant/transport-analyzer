# Plan 1: Foundation, FARS, Exposure and Query API — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An ASP.NET Core app in Docker Compose that ingests NHTSA FARS and BTS exposure tables into Postgres and answers aggregate, exposure and rate queries over HTTP.

**Architecture:** One ASP.NET Core host (`Api`) runs minimal-API endpoints and an in-process ingest queue. Source adapters (`Ingestion`) fetch files into a filesystem raw store, stage every source table as text columns in a per-source Postgres schema, then normalize into shared `event` / `vehicle` / `exposure` tables with set-based SQL driven by data-file vocabulary mappings. Query endpoints use Dapper over whitelisted dimensions; the rate join is a pure C# function.

**Tech Stack:** .NET 10 (`net10.0`), ASP.NET Core minimal APIs, EF Core 10 + Npgsql (schema and migrations), Dapper (queries), Npgsql binary COPY (staging), ClosedXML (xlsx), CsvHelper (CSV), xUnit + Testcontainers.PostgreSql + `Microsoft.AspNetCore.Mvc.Testing`, Postgres 17, Docker Compose.

**Spec:** `docs/superpowers/specs/2026-11-04-experiment-platform-design.md` (background: `docs/research.md`).

**Plan sequence:** this is plan 1 of 4. Plan 2: NTSB adapter (mdbtools), FAA GA/Part 135 flight hours, `/api/events/sample`, Quartz schedules. Plan 3: Vite + Svelte shell, `web/src/stats` with `crosstab`, `rate`, `heatmap`, first experiment. Plan 4: `poisson-regression`, `logistic-regression`.

## Global Constraints

- Target `net10.0` (spec floor is ".NET 9 or later"; .NET 10 SDK 10.0.105 is installed).
- PostgreSQL 17, **no PostGIS**. Latitude and longitude are plain `double precision null` columns.
- Single user. No accounts. Admin endpoints require header `X-Admin-Token` only when config `Admin:Token` is non-empty.
- No source table or column is discarded: every table in a source file is staged, every column, as text.
- `source_payload` on `event` and `vehicle` holds the main source record only (`to_jsonb` of the staging row).
- Normalized rows are upserted by `(source_id, source_key)` and stamped with the `ingest_run_id` that last wrote them. No row versioning.
- Mappings from source values to shared vocabulary live in data files under `data/`, never in C# code. Unmapped source values become code `unknown` and are counted in the run record.
- `event` total casualties must equal the sum over its vehicles plus `event.unattributed_*` (see Task 5).
- The rate join never spreads a denominator down to a finer level than its source provides; the numerator is rolled up instead.
- Query dimensions and filters are whitelisted names, never interpolated free text.
- Repository layout: `src/` for .NET projects, `tests/` for .NET tests, `data/` for vocabulary and mappings, `web/` reserved for plan 3.

## Review Focus

1. **Non-occupant casualties with no known striking vehicle**, and people whose `VEH_NO` has no row in `vehicle.csv` (parked or working vehicles): they must land in `event.unattributed_*`, never vanish, and the event invariant must hold. Test: `Normalize_UnattributableCasualties_LandOnEvent` (Task 5).
2. **Older FARS years with uppercase file names and headers, and Windows-1252 bytes**: staging must accept both, with no mojibake and no missing tables. Tests: `Stage_UppercaseFileAndHeaders_Lowercased`, `Stage_Windows1252Bytes_Decoded` (Task 4).
3. **Ingesting the same year twice** must give identical counts, not doubled rows. Test: `Ingest_SameYearTwice_IsIdempotent` (Task 5).
4. **A rate cell with no matching exposure row** must come back in `unmatched` with its numerator, not dropped and not divided by zero. Test: `Join_NoExposure_ReportedUnmatched` (Task 9).
5. **Unknown dimension names or malformed filters in query strings** must return HTTP 400 naming the bad parameter, never reach SQL. Test: `Aggregate_UnknownDimension_Returns400` (Task 8).

---

## File Structure

```
TransportAnalyzer.sln
Directory.Build.props                      net10.0, nullable, warnings as errors
docker-compose.yml                         app + postgres:17, volume for data/raw
Dockerfile                                 multi-stage build of Api
.github/workflows/ci.yml                   dotnet test
data/
  vocabulary.csv                           attribute,code,label,description,parent_code
  mappings/fars.csv                        attribute,source_column,source_value,code,priority
  mappings/bts-passenger-miles.csv         row_label,mode,vehicle_class,regulation_class
  mappings/bts-vehicle-miles.csv           same columns
src/TransportAnalyzer.Domain/
  ISourceAdapter.cs                        adapter contract, SourceDescriptor, RawFile
  Exposure.cs, Vocabulary.cs               shared records
src/TransportAnalyzer.Infrastructure/
  AppDbContext.cs, Entities/*.cs           EF Core model for normalized tables
  Migrations/                              EF migrations
  RawStore.cs                              filesystem raw store with sha256
  CsvStager.cs                             generic CSV -> stg_<source>.<table> loader
  VocabularyLoader.cs                      loads data/*.csv into vocabulary / vocabulary_map
src/TransportAnalyzer.Ingestion/
  IngestRunner.cs                          runs an adapter, writes ingest_run
  IngestQueue.cs                           Channel + BackgroundService
  Fars/FarsAdapter.cs                      fetch + stage
  Fars/fars-normalize.sql                  embedded resource, set-based normalize
  Bts/BtsNtsTableAdapter.cs                xlsx parser for NTS tables 1-40 and 1-35
src/TransportAnalyzer.Api/
  Program.cs                               composition root
  Endpoints/AdminEndpoints.cs
  Endpoints/SourceEndpoints.cs             /api/sources, /api/vocabulary
  Endpoints/EventEndpoints.cs              /api/events/aggregate
  Endpoints/ExposureEndpoints.cs           /api/exposure, /api/rates
  Query/Dimensions.cs                      whitelist and SQL fragments
  Query/AggregateQuery.cs                  parse + build + run
  Query/RateJoiner.cs                      pure rate join
  wwwroot/index.html                       placeholder until plan 3
tests/TransportAnalyzer.Tests/
  PostgresFixture.cs                       Testcontainers, migrated DB per collection
  ApiFactory.cs                            WebApplicationFactory over PostgresFixture
  Fixtures/fars/2022/*.csv                 hand-built three-crash fixture
  *Tests.cs                                one file per task
```

---

### Task 1: Solution skeleton and container

**Files:**
- Create: `TransportAnalyzer.sln`, `Directory.Build.props`, the four `src/` projects, `tests/TransportAnalyzer.Tests`, `src/TransportAnalyzer.Api/Program.cs`, `src/TransportAnalyzer.Api/wwwroot/index.html`, `Dockerfile`, `docker-compose.yml`, `.github/workflows/ci.yml`, `.gitignore`, `.dockerignore`
- Test: `tests/TransportAnalyzer.Tests/HealthTests.cs`

**Interfaces:**
- Produces: `GET /api/health` → `200 {"status":"ok"}`. `public partial class Program` so tests can use `WebApplicationFactory<Program>`. Project references: Api → Ingestion → Infrastructure → Domain; Tests → Api.

- [ ] **Step 1: Create projects** with `dotnet new sln`, `classlib` ×3, `web` for Api, `xunit` for Tests; wire references; `Directory.Build.props` sets `TargetFramework=net10.0`, `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true`.
- [ ] **Step 2: Write the failing test**

```csharp
[Fact]
public async Task Health_ReturnsOk()
{
    await using var app = new WebApplicationFactory<Program>();
    var res = await app.CreateClient().GetFromJsonAsync<JsonElement>("/api/health");
    Assert.Equal("ok", res.GetProperty("status").GetString());
}
```

- [ ] **Step 3: Run** `dotnet test --filter Health_ReturnsOk` — expected FAIL (404).
- [ ] **Step 4: Implement** the endpoint in `Program.cs`, plus `UseDefaultFiles()` / `UseStaticFiles()` serving `wwwroot/index.html` (one line: "Transport Analyzer API. Frontend arrives in plan 3.").
- [ ] **Step 5: Run** the test — expected PASS.
- [ ] **Step 6: Container.** `Dockerfile`: `mcr.microsoft.com/dotnet/sdk:10.0` build stage, `mcr.microsoft.com/dotnet/aspnet:10.0` runtime, copies `data/` to `/app/data`. `docker-compose.yml`: service `db` (`postgres:17`, user/password/db `transport`, healthcheck `pg_isready`), service `app` (build `.`, port `8080:8080`, `ConnectionStrings__Default=Host=db;Database=transport;Username=transport;Password=transport`, `RawStore__Root=/data/raw`, volume `rawdata:/data/raw`, `depends_on: db: condition: service_healthy`).
- [ ] **Step 7: Verify** `docker compose up -d --build && curl -s localhost:8080/api/health` prints `{"status":"ok"}`; then `docker compose down`.
- [ ] **Step 8: CI.** `.github/workflows/ci.yml` on push: `actions/setup-dotnet` 10.0.x, `dotnet test` (ubuntu runners have Docker, so Testcontainers works).
- [ ] **Step 9: Commit** `feat: solution skeleton, health endpoint, compose`.

---

### Task 2: Normalized schema and vocabulary

**Files:**
- Create: `src/TransportAnalyzer.Infrastructure/AppDbContext.cs`, `Entities/{Event,Vehicle,Exposure,IngestRun,VocabularyEntry,VocabularyMapEntry,Operator,EventOperator}.cs`, `Migrations/*`, `VocabularyLoader.cs`, `data/vocabulary.csv`
- Create: `tests/TransportAnalyzer.Tests/PostgresFixture.cs`, `SchemaTests.cs`

**Interfaces:**
- Produces tables (snake_case via `UseSnakeCaseNamingConvention()` from `EFCore.NamingConventions`):
  - `event`: `event_id uuid pk`, `source_id text`, `source_key text`, unique `(source_id, source_key)`, `mode text`, `event_type text`, `occurred_on date not null`, `occurred_time time null` (local time as recorded; my call, replacing the spec's `occurred_at timestamptz` because sources record local time with unknown zone), `latitude`, `longitude double precision null`, `state text null`, `country text null`, `severity text`, `fatalities int`, `serious_injuries int`, `minor_injuries int`, `ground_fatalities int null`, `unattributed_fatalities int not null default 0`, `unattributed_serious_injuries int not null default 0`, `unattributed_minor_injuries int not null default 0`, `operation_class`, `phase`, `weather_summary`, `narrative`, `probable_cause`, `investigation_status text null`, `source_payload jsonb`, `ingest_run_id uuid`.
  - `vehicle`: `vehicle_id uuid pk`, `event_id uuid fk on delete cascade`, `vehicle_no int`, unique `(event_id, vehicle_no)`, `registration`, `make`, `model text null`, `year_built int null`, `damage text null`, `vehicle_class`, `operator_type`, `regulation_class`, `maintenance_regime text not null`, `occupants int null`, `crew_count int null`, `vehicle_age_years int null`, `occupant_fatalities`, `occupant_serious_injuries`, `occupant_minor_injuries`, `nonoccupant_fatalities`, `nonoccupant_serious_injuries`, `nonoccupant_minor_injuries int not null default 0`, `source_payload jsonb`, `ingest_run_id uuid`.
  - `exposure`: `exposure_id uuid pk`, `source_id`, `mode text`, `year int`, `vehicle_class text null`, `regulation_class text null`, `metric text` (`passenger_miles` | `vehicle_miles` | `flight_hours`), `value double precision`, `provenance text` (`measured` | `survey_estimate` | `derived`), `standard_error double precision null`, `assumptions jsonb null`, `ingest_run_id uuid`; unique `(source_id, mode, year, vehicle_class, regulation_class, metric) NULLS NOT DISTINCT` (raw SQL in the migration).
  - `ingest_run`: `ingest_run_id uuid pk`, `source_id`, `started_at`, `finished_at timestamptz null`, `status text` (`running` | `succeeded` | `failed`), `parameters jsonb`, `raw_files jsonb`, `row_counts jsonb`, `unmapped jsonb`, `error text null`.
  - `vocabulary`: pk `(attribute, code)`, `label`, `description`, `parent_code text null`.
  - `vocabulary_map`: pk `(source_id, attribute, source_column, source_value)`, `code`, `priority int`.
  - `operator` (`operator_id`, `name`, `identifiers jsonb`) and `event_operator` (`event_id`, `operator_id`, `role`): created, unused in v1.
- Produces: `VocabularyLoader.LoadAsync(NpgsqlConnection conn, string dataRoot, CancellationToken ct)`: truncates and reloads `vocabulary` from `data/vocabulary.csv` and `vocabulary_map` from every `data/mappings/*.csv` whose header is `attribute,source_column,source_value,code,priority` (file name minus extension is `source_id`). Called at app startup after `Database.MigrateAsync()`.
- Produces: `PostgresFixture : IAsyncLifetime` exposing `string ConnectionString`, starting `postgres:17`, migrating, loading vocabulary from the repo `data/` folder (copy `data/**` to test output via `<None Include="../../data/**" CopyToOutputDirectory="PreserveNewest" LinkBase="data" />`). xUnit collection `"db"` shares one fixture.

- [ ] **Step 1: Write `data/vocabulary.csv`** with these rows (labels and descriptions are one short sentence each, builder's wording):
  - `vehicle_class`: `light_duty` (no parent), `passenger_car`→`light_duty`, `light_truck`→`light_duty`, `motorcycle`, `bus`, `heavy_truck`, `light_aircraft`, `turboprop`, `business_jet`, `airliner`, `helicopter`, `other`, `unknown`
  - `operator_type`: `private`, `commercial`, `government`, `unknown`
  - `regulation_class`: `road_private`, `road_commercial`, `part_91`, `part_121`, `part_135`, `part_137`, `other`, `unknown`
  - `maintenance_regime`: `none`, `state_inspection`, `mandated_periodic`, `unknown`
  - `mode`: `aviation`, `highway`, `rail`, `transit`, `marine`, `pipeline`
- [ ] **Step 2: Write the failing tests** in `SchemaTests.cs` (collection `"db"`):

```csharp
[Fact] public async Task Migration_CreatesNormalizedTables()
// information_schema.tables contains event, vehicle, exposure, ingest_run, vocabulary, vocabulary_map, operator, event_operator

[Fact] public async Task Vocabulary_LightTruckParentIsLightDuty()
// SELECT parent_code FROM vocabulary WHERE attribute='vehicle_class' AND code='light_truck' == "light_duty"

[Fact] public async Task Exposure_NullKeysAreNotDistinct()
// inserting two rows with identical keys and vehicle_class NULL throws PostgresException SqlState 23505
```

- [ ] **Step 3: Run** `dotnet test --filter SchemaTests` — expected FAIL.
- [ ] **Step 4: Implement** entities, `AppDbContext`, `dotnet ef migrations add Initial`, the `NULLS NOT DISTINCT` index as `migrationBuilder.Sql`, `VocabularyLoader`, startup wiring in `Program.cs`.
- [ ] **Step 5: Run** — expected PASS.
- [ ] **Step 6: Commit** `feat: normalized schema, vocabulary and loader`.

---

### Task 3: Raw store, ingest runs and admin endpoints

**Files:**
- Create: `src/TransportAnalyzer.Domain/ISourceAdapter.cs`, `src/TransportAnalyzer.Infrastructure/RawStore.cs`, `src/TransportAnalyzer.Ingestion/IngestRunner.cs`, `IngestQueue.cs`, `src/TransportAnalyzer.Api/Endpoints/AdminEndpoints.cs`
- Test: `tests/TransportAnalyzer.Tests/IngestRunnerTests.cs`, `ApiFactory.cs`

**Interfaces:**
- Produces (Domain):

```csharp
public sealed record SourceDescriptor(string SourceId, string Name, string Mode, string Kind /* "events" | "exposure" */,
    string Url, string Definitions, string Caveats);
public sealed record RawFile(string Name, string Path, string Sha256, DateTimeOffset FetchedAt);
public sealed record IngestParameters(IReadOnlyList<int>? Years);
public sealed record NormalizeResult(IReadOnlyDictionary<string, long> RowCounts,
    IReadOnlyDictionary<string, long> Unmapped);   // key "attribute:source_value"

public interface ISourceAdapter
{
    SourceDescriptor Descriptor { get; }
    Task<IReadOnlyList<RawFile>> FetchAsync(IRawStore raw, IngestParameters p, CancellationToken ct);
    Task StageAsync(IReadOnlyList<RawFile> files, NpgsqlConnection conn, IngestParameters p, CancellationToken ct);
    Task<NormalizeResult> NormalizeAsync(NpgsqlConnection conn, Guid runId, IngestParameters p, CancellationToken ct);
}
public interface IRawStore
{
    Task<RawFile> SaveAsync(string sourceId, string name, Stream content, CancellationToken ct);
}
```

  This replaces the per-record contract sketched in `docs/research.md` §4 (my call): staging and normalizing are set-based because FARS person tables run to hundreds of thousands of rows a year.
- Produces: `RawStore(string root)` writes `{root}/{sourceId}/{yyyyMMdd'T'HHmmss}/{name}`, computes sha256 while writing, never overwrites.
- Produces: `IngestRunner.RunAsync(ISourceAdapter a, IngestParameters p, CancellationToken ct) : Task<Guid>`: inserts `ingest_run` (`running`), calls Fetch → Stage → Normalize in one connection, with Stage+Normalize inside one transaction; writes `succeeded` with `raw_files`, `row_counts`, `unmapped`, or `failed` with `error` (exception message plus type) and rolls back.
- Produces: `IngestQueue` (`Channel<(string sourceId, IngestParameters p, Guid runId)>` + `BackgroundService`), `Task<Guid> EnqueueAsync(string sourceId, IngestParameters p)` which pre-inserts the run row as `running` so the id is returned immediately.
- Produces endpoints: `POST /api/admin/ingest/{sourceId}?years=2019,2020` → `202 {"runId": "..."}`, `404` for unknown source; `GET /api/admin/runs?sourceId=&limit=50` → array of runs, newest first.
- Produces: `ApiFactory : WebApplicationFactory<Program>` taking `PostgresFixture`, overriding `ConnectionStrings:Default`, `RawStore:Root` (temp dir), and letting tests replace adapters via `ConfigureTestServices`.

- [ ] **Step 1: Write failing tests** with a `FakeAdapter` (writes one raw file, stages nothing, returns `RowCounts {"event": 0}`) and a `ThrowingAdapter` (`StageAsync` creates and inserts one row into `stg_test.marker`, then `NormalizeAsync` throws `InvalidOperationException("boom")`):

```csharp
[Fact] public async Task Run_Success_RecordsSucceededWithFiles()
// status == "succeeded", raw_files has 1 entry whose sha256 matches SHA256 of the bytes written, finished_at not null

[Fact] public async Task Run_Failure_RecordsFailedAndRollsBack()
// status == "failed", error contains the exception message, a row the adapter inserted in StageAsync is absent

[Fact] public async Task AdminIngest_UnknownSource_Returns404()

[Fact] public async Task AdminIngest_WithTokenConfigured_RequiresHeader()
// Admin:Token = "t": no header → 401; header X-Admin-Token: t → 202
```

- [ ] **Step 2: Run** — expected FAIL.
- [ ] **Step 3: Implement** the types above; register adapters as `IEnumerable<ISourceAdapter>` keyed by `Descriptor.SourceId`.
- [ ] **Step 4: Run** — expected PASS.
- [ ] **Step 5: Commit** `feat: raw store, ingest runner and admin endpoints`.

---

### Task 4: Generic CSV stager

**Files:**
- Create: `src/TransportAnalyzer.Infrastructure/CsvStager.cs`
- Test: `tests/TransportAnalyzer.Tests/CsvStagerTests.cs`

**Interfaces:**
- Produces: `CsvStager.StageAsync(NpgsqlConnection conn, string schema, string table, Stream csv, IReadOnlyDictionary<string,string> extraColumns, string replaceWhereColumn, CancellationToken ct) : Task<long>`
  - schema and table names: lowercased, must match `^[a-z][a-z0-9_]*$` or throw `ArgumentException`.
  - `CREATE SCHEMA IF NOT EXISTS`, `CREATE TABLE IF NOT EXISTS` with all columns `text`, then `ALTER TABLE ADD COLUMN IF NOT EXISTS` for any header column or extra column not present (FARS columns differ by year). Header names lowercased, trimmed, non-`[a-z0-9_]` replaced by `_`.
  - `DELETE FROM {schema}.{table} WHERE {replaceWhereColumn} = @value` using the value from `extraColumns[replaceWhereColumn]`, then binary `COPY` of all rows with extra columns appended. Returns rows copied. Runs on the caller's transaction.
  - Decoding: try strict UTF-8 (`throwOnInvalidBytes: true`) over the whole stream (buffer it); on `DecoderFallbackException` decode as Windows-1252 (`CodePagesEncodingProvider`). Strip a UTF-8 BOM.

- [ ] **Step 1: Write failing tests:**

```csharp
[Fact] public async Task Stage_CreatesTextTableAndCopiesRows()
// "ST_CASE,FATALS\n10001,1\n10002,2\n" with extra {data_year: "2022"} → 2 rows, columns st_case, fatals, data_year all text

[Fact] public async Task Stage_UppercaseFileAndHeaders_Lowercased()
// header "ST_CASE,Veh_No" → columns st_case, veh_no

[Fact] public async Task Stage_Windows1252Bytes_Decoded()
// bytes for "CITYNAME\nSAN JOS\xC9\n" → stored value "SAN JOSÉ"

[Fact] public async Task Stage_NewColumnInLaterYear_AddsColumn()
// stage 2021 with (a), then 2022 with (a,b) → 2021 rows have b NULL, 2022 rows have b set

[Fact] public async Task Stage_SameYearTwice_ReplacesRows()
// stage 2022 twice → row count for data_year=2022 equals one load

[Fact] public async Task Stage_InvalidTableName_Throws()
// table "x;drop" → ArgumentException
```

- [ ] **Step 2: Run** — expected FAIL.
- [ ] **Step 3: Implement** with CsvHelper for parsing (handles quoted fields) and `NpgsqlBinaryImporter`.
- [ ] **Step 4: Run** — expected PASS.
- [ ] **Step 5: Commit** `feat: generic CSV stager`.

---

### Task 5: FARS adapter

**Files:**
- Create: `src/TransportAnalyzer.Ingestion/Fars/FarsAdapter.cs`, `Fars/fars-normalize.sql` (embedded resource), `data/mappings/fars.csv`
- Create: `tests/TransportAnalyzer.Tests/Fixtures/fars/2022/{accident,vehicle,person}.csv`
- Test: `tests/TransportAnalyzer.Tests/FarsAdapterTests.cs`

**Interfaces:**
- Consumes: `ISourceAdapter`, `IRawStore`, `CsvStager`, `vocabulary_map` (Tasks 2–4).
- Produces: `FarsAdapter(HttpClient http, IOptions<FarsOptions> o)`, `SourceId = "fars"`, `Mode = "highway"`, `Kind = "events"`. `FarsOptions { string UrlTemplate = "https://static.nhtsa.gov/nhtsa/downloads/FARS/{year}/National/FARS{year}NationalCSV.zip"; int[] DefaultYears = [2019, 2020, 2021, 2022, 2023]; }`. Descriptor `Definitions`: "Fatal crash: a crash on a public trafficway involving a motor vehicle in transport that results in a death within 30 days." `Caveats`: "Census of fatal crashes only. Non-fatal crashes are not included. Annual release with a lag of about 18 months; preliminary years are revised."
- Fetch: one zip per year into the raw store. Stage: for every `*.csv` entry in the zip (any directory depth, any case) stage into `stg_fars.{lowercased file name without extension}` with extra column `data_year`. Normalize: run `fars-normalize.sql` with `@years int[]` and `@run_id uuid`.
- Produces in `event`/`vehicle`: `source_id='fars'`, event `source_key = '{year}-{st_case}'`, `mode='highway'`, `event_type='accident'`, `severity='fatal'`, vehicle `vehicle_no = veh_no`.

**Normalize rules (the SQL implements these; the builder writes it):**
- Before upsert: delete `vehicle` rows of the affected FARS events, so re-ingest does not leave stale vehicles. Events are upserted (`ON CONFLICT (source_id, source_key) DO UPDATE`).
- `occurred_on` from `year`, `month`, `day`; `occurred_time` from `hour`, `minute` only when `hour` between 0 and 23 and `minute` between 0 and 59, else null. `latitude`/`longitude` null when outside ±90 / ±180 (FARS uses 77.7777, 88.8888, 99.9999 and 777.7777 etc. as unknowns). `state` from `statename` when present, else the numeric `state` code as text. `country='US'`.
- Person `inj_sev`: `4` = fatal, `3` = serious, `1` or `2` = minor, everything else not counted. `6` (died prior to crash) is excluded.
- Occupant: person with `veh_no` > 0 that matches a staged vehicle row → counted on that vehicle's `occupant_*`.
- Non-occupant: person with `veh_no` = 0 and `str_veh` matching a staged vehicle → counted on that vehicle's `nonoccupant_*`.
- Anything else (non-occupant with `str_veh` 0 or unmatched, or occupant of a `veh_no` absent from `vehicle.csv`, such as parked vehicles in `parkwork.csv`) → `event.unattributed_*`.
- `event.fatalities` etc. are the person-level sums, so `fatalities = Σ vehicle.occupant_fatalities + Σ vehicle.nonoccupant_fatalities + unattributed_fatalities` holds by construction. Keep the accident file's own `fatals` in `source_payload`.
- Vehicle attributes: for each attribute, join `vocabulary_map` on `(source_id='fars', attribute, source_column, value of that column)` and take the lowest `priority`; no match → `unknown` and increment `Unmapped["{attribute}:{column}={value}"]`. `make` from `make` code text (name column if present), `model` likewise, `year_built` from `mod_year` unless 9998/9999, `vehicle_age_years = year - year_built` when both known and result ≥ 0, `occupants` from `numoccs` unless ≥ 97, `crew_count = 1`.
- `source_payload` = `to_jsonb` of the staged accident row for events and the staged vehicle row for vehicles.
- Return `RowCounts {"event": n, "vehicle": n}`.

**Mapping file `data/mappings/fars.csv`** (builder drafts from the FARS Analytical User's Manual for the years loaded; the user reviews it):
- `vehicle_class` on `body_typ`: 1–13 → `passenger_car`; 14–49 → `light_truck`; 50–59 → `bus`; 60–79 → `heavy_truck`; 80–89 → `motorcycle`; 90–97 → `other`. One row per code (generate the rows; do not write range logic). Verify each range against the manual before committing.
- `regulation_class`: priority 1 on `body_typ` 50–79 → `road_commercial`; priority 2 on `spec_use` non-zero commercial codes (taxi, transit, school bus, etc. per manual) → `road_commercial`; priority 3 on `spec_use` 0 → `road_private`.
- `operator_type`: same columns, `commercial` / `private`; government-owned `spec_use` codes (police, military, fire) → `government`.
- `maintenance_regime`: `body_typ` 50–79 → `mandated_periodic`; all others left unmapped (become `unknown`; do not count passenger cars as unmapped noise — map them explicitly to `unknown` so `Unmapped` only reports real gaps).

**Fixture (`Fixtures/fars/2022/`)**, three crashes, columns limited to what the rules use plus one extra column `cityname` to prove passthrough:
- Crash 10001, 2022-03-14 08:30: one vehicle (`body_typ` 4, `mod_year` 2015), driver `inj_sev` 4.
- Crash 10002, 2022-07-04 21:15: one vehicle (`body_typ` 31), driver `inj_sev` 0; a pedestrian (`veh_no` 0, `str_veh` 1, `inj_sev` 4).
- Crash 10003, 2022-11-20, `hour` 99, `minute` 99: vehicle 1 (`body_typ` 4), vehicle 2 (`body_typ` 66); veh 1 passenger `inj_sev` 4, veh 1 driver `inj_sev` 2, veh 2 driver `inj_sev` 3; a cyclist (`veh_no` 0, `str_veh` 0, `inj_sev` 4); a person with `veh_no` 3 (not in vehicle file) `inj_sev` 3.

- [ ] **Step 1: Recon.** Download the real `FARS2022NationalCSV.zip`, list its entries and the headers of `accident.csv`, `vehicle.csv`, `person.csv`. Confirm the column names used above (`st_case`, `veh_no`, `per_typ`, `inj_sev`, `str_veh`, `body_typ`, `mod_year`, `numoccs`, `spec_use`, `latitude`, `longitud`, `hour`, `minute`); note any differences in the test file header comment and adjust the rules to the real names. Check that 2019's zip opens the same way.
- [ ] **Step 2: Write the fixture and `fars.csv`** as specified.
- [ ] **Step 3: Write failing tests** (the test builds the zip in memory from the fixture folder and serves it through a stub `HttpMessageHandler`):

```csharp
[Fact] public async Task Normalize_SingleVehicleDriverKilled()
// event 2022-10001: fatalities 1; vehicle 1: vehicle_class passenger_car, occupant_fatalities 1, vehicle_age_years 7

[Fact] public async Task Normalize_PedestrianAttributedToStrikingVehicle()
// event 2022-10002: fatalities 1; vehicle 1: vehicle_class light_truck, occupant_fatalities 0, nonoccupant_fatalities 1

[Fact] public async Task Normalize_UnattributableCasualties_LandOnEvent()
// event 2022-10003: fatalities 2, serious_injuries 2, minor_injuries 1; unattributed_fatalities 1 (cyclist), unattributed_serious_injuries 1 (veh 3);
// vehicle 1 occupant_fatalities 1, occupant_minor_injuries 1; vehicle 2 vehicle_class heavy_truck, regulation_class road_commercial,
// maintenance_regime mandated_periodic, occupant_serious_injuries 1; occurred_time null

[Fact] public async Task Normalize_EventInvariantHoldsForAllEvents()
// SQL: no event where fatalities <> unattributed_fatalities + Σ vehicle occupant+nonoccupant fatalities (same for serious, minor)

[Fact] public async Task Stage_KeepsUnusedColumns()
// stg_fars.accident has column cityname with the fixture value; event.source_payload->>'cityname' equals it

[Fact] public async Task Ingest_SameYearTwice_IsIdempotent()
// run twice: event count 3, vehicle count 4, both runs succeeded, events stamped with the second run id
```

- [ ] **Step 4: Run** — expected FAIL.
- [ ] **Step 5: Implement** `FarsAdapter` and `fars-normalize.sql`.
- [ ] **Step 6: Run** — expected PASS.
- [ ] **Step 7: Commit** `feat: FARS adapter with per-vehicle casualty attribution`.

---

### Task 6: BTS National Transportation Statistics exposure adapter

**Files:**
- Create: `src/TransportAnalyzer.Ingestion/Bts/BtsNtsTableAdapter.cs`, `data/mappings/bts-passenger-miles.csv`, `data/mappings/bts-vehicle-miles.csv`
- Test: `tests/TransportAnalyzer.Tests/BtsNtsTableAdapterTests.cs`

**Interfaces:**
- Consumes: `ISourceAdapter`, `IRawStore` (Task 3).
- Produces: `BtsNtsTableAdapter(HttpClient http, BtsTableOptions o)` registered twice:
  - `bts-passenger-miles`: NTS Table 1-40 "U.S. Passenger-Miles", `metric = passenger_miles`, `unitMultiplier = 1_000_000` (table is in millions).
  - `bts-vehicle-miles`: NTS Table 1-35 "U.S. Vehicle-Miles", `metric = vehicle_miles`, `unitMultiplier = 1_000_000`.
  - `BtsTableOptions { string SourceId; string Url; string Metric; double UnitMultiplier; string MappingFile; }` bound from config `Sources:{SourceId}`; `Kind = "exposure"`, `Mode = "multi"`. Caveats: "Highway passenger-miles are BTS estimates from vehicle-miles and average occupancy. Light-duty vehicles are not split into cars and light trucks after 2006."
- Parsing (`internal static IEnumerable<ExposureRow> Parse(XLWorkbook wb, IReadOnlyList<RowMap> map, string metric, double unitMultiplier)`): first worksheet; header row = first row where ≥ 5 cells parse as integers 1950–2100; data rows matched by trimmed first-cell label (footnote markers like trailing `,a` or superscript digits stripped) against `row_label` in the mapping; numeric cells become rows; cells `U`, `N`, `R`, empty, or non-numeric are skipped. Labels not in the mapping are ignored (not every row is wanted). Raw xlsx is not staged as a table; `StageAsync` copies parsed rows to `stg_bts.{sourceId with - replaced by _}` (columns `row_label, year, value`) for traceability, and `NormalizeAsync` upserts `exposure` with `provenance` from the mapping file.
- Mapping columns: `row_label,mode,vehicle_class,regulation_class,provenance`. Required rows (labels to confirm in recon): air carrier → `aviation,,part_121,measured`; highway total → `highway,,,survey_estimate`; light duty short WB and long WB → both `highway,light_duty,,survey_estimate` (the adapter **sums** rows that map to the same key, since both light-duty rows roll up to `light_duty`); motorcycle → `highway,motorcycle,,survey_estimate`; bus → `highway,bus,,survey_estimate`; single-unit and combination truck → `highway,heavy_truck,,survey_estimate` (summed).

- [ ] **Step 1: Recon.** Find the current xlsx download URLs for NTS Tables 1-40 and 1-35 on bts.gov, put them in `appsettings.json` under `Sources:bts-passenger-miles:Url` and `Sources:bts-vehicle-miles:Url`, open each file and copy the exact row labels into the two mapping files.
- [ ] **Step 2: Write failing tests** (build a workbook in-memory with ClosedXML: title rows, header row `"", 2019, 2020, 2021`, rows `"Air carrier, domestic, all services", 1, 2, U` / `"Light duty vehicle, short wheel base", 10, 11, 12` / `"Light duty vehicle, long wheel base", 5, 5, 5` / `"Unwanted row", 9, 9, 9`):

```csharp
[Fact] public void Parse_FindsHeaderAndMultipliesUnits()
// air carrier 2019 value 1_000_000, mode aviation, regulation_class part_121

[Fact] public void Parse_SkipsNonNumericCells()
// no air carrier row for 2021

[Fact] public void Parse_SumsRowsMappedToSameKey()
// highway light_duty 2020 value 16_000_000

[Fact] public void Parse_IgnoresUnmappedLabels()
// no row with value 9_000_000

[Fact] public async Task Ingest_UpsertsExposureIdempotently()
// two runs → same row count, values unchanged
```

- [ ] **Step 3: Run** — expected FAIL.
- [ ] **Step 4: Implement.**
- [ ] **Step 5: Run** — expected PASS.
- [ ] **Step 6: Commit** `feat: BTS NTS exposure adapter`.

---

### Task 7: Sources and vocabulary endpoints

**Files:**
- Create: `src/TransportAnalyzer.Api/Endpoints/SourceEndpoints.cs`
- Test: `tests/TransportAnalyzer.Tests/SourceEndpointsTests.cs`

**Interfaces:**
- Consumes: registered adapters' `Descriptor`, `ingest_run`, `event`, `exposure`, `vocabulary`.
- Produces: `GET /api/sources` → array of `{ sourceId, name, mode, kind, url, definitions, caveats, lastRun: { runId, status, startedAt, finishedAt } | null, dataCurrentThrough: "YYYY-MM-DD" | null }`. `dataCurrentThrough` = `max(occurred_on)` for `events` sources, `"{max(year)}-12-31"` for `exposure` sources, `null` when no data.
- Produces: `GET /api/vocabulary` → `{ [attribute]: [{ code, label, description, parentCode }] }`.

- [ ] **Step 1: Write failing tests:**

```csharp
[Fact] public async Task Sources_ListsRegisteredAdaptersWithNullsBeforeIngest()
// contains fars, bts-passenger-miles, bts-vehicle-miles; lastRun null; dataCurrentThrough null

[Fact] public async Task Sources_AfterFarsFixtureIngest_ReportsCurrentThrough()
// fars dataCurrentThrough == "2022-11-20"; lastRun.status "succeeded"

[Fact] public async Task Vocabulary_ReturnsParentCodes()
// vehicle_class passenger_car parentCode "light_duty"
```

- [ ] **Step 2: Run** — expected FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — expected PASS.
- [ ] **Step 5: Commit** `feat: sources and vocabulary endpoints`.

---

### Task 8: Aggregate endpoint

**Files:**
- Create: `src/TransportAnalyzer.Api/Query/Dimensions.cs`, `Query/AggregateQuery.cs`, `Endpoints/EventEndpoints.cs`
- Test: `tests/TransportAnalyzer.Tests/AggregateTests.cs`

**Interfaces:**
- Produces: `GET /api/events/aggregate` with query parameters:
  - `grain` = `event` (default) | `vehicle`
  - `from`, `to`: ISO dates, inclusive, required
  - `bucket` = `none` (default) | `year` | `quarter` | `month`; output column `period` as `YYYY`, `YYYY-Qn`, `YYYY-MM`
  - `groupBy`: comma list of dimension names
  - `f.{dimension}`: comma list of allowed values (e.g. `f.vehicle_class=passenger_car,light_truck`)
- Dimensions (`Dimensions.cs`, a static dictionary name → SQL expression and grain):
  - event grain: `source_id`, `mode`, `event_type`, `severity`, `state`, `country`, `operation_class`, `phase`
  - vehicle grain: the above plus `vehicle_class`, `vehicle_class_parent` (`coalesce(vocabulary.parent_code, vehicle_class)` via a join), `operator_type`, `regulation_class`, `maintenance_regime`, `occupants`, `crew_count`, `vehicle_age_years`, `year_built`, `make`
- Measures (always all returned):
  - event grain: `events`, `fatalities`, `serious_injuries`, `minor_injuries`, `unattributed_fatalities`
  - vehicle grain: `vehicles`, `events` (`count(distinct event_id)`), `occupant_fatalities`, `occupant_serious_injuries`, `occupant_minor_injuries`, `nonoccupant_fatalities`, `nonoccupant_serious_injuries`, `nonoccupant_minor_injuries`
- Response: `{ grain, columns: string[], rows: (string|number|null)[][] }`; group columns first in `groupBy` order, then `period` if bucketed, then measures. Rows ordered by group columns.
- Errors → `400 { error, parameter }`: unknown dimension in `groupBy` or `f.*`; vehicle dimension with `grain=event`; missing or unparsable `from`/`to`; `from > to`; unknown `bucket`/`grain`.
- `AggregateQuery.Parse(IQueryCollection q) : Result<AggregateQuery, (string error, string parameter)>` and `AggregateQuery.ToSql() : (string sql, DynamicParameters p)` — filter values always bound as parameters (`= ANY(@f_vehicle_class)`), dimension SQL only from the whitelist.

- [ ] **Step 1: Write failing tests** (ingest the FARS fixture once in a class fixture):

```csharp
[Fact] public async Task Aggregate_EventGrainByYear()
// bucket=year, from=2022-01-01, to=2022-12-31 → one row: period "2022", events 3, fatalities 4

[Fact] public async Task Aggregate_VehicleGrainByClass()
// grain=vehicle, groupBy=vehicle_class → passenger_car: vehicles 2, occupant_fatalities 2; light_truck: nonoccupant_fatalities 1; heavy_truck: occupant_serious_injuries 1

[Fact] public async Task Aggregate_GroupByParentRollsUpLightDuty()
// grain=vehicle, groupBy=vehicle_class_parent → light_duty: vehicles 3

[Fact] public async Task Aggregate_FilterByValue()
// grain=vehicle, f.vehicle_class=heavy_truck → one vehicle

[Fact] public async Task Aggregate_UnknownDimension_Returns400()
// groupBy=vehicle_class;drop → 400, parameter "groupBy"; f.nope=1 → 400, parameter "f.nope"

[Fact] public async Task Aggregate_VehicleDimensionAtEventGrain_Returns400()
```

- [ ] **Step 2: Run** — expected FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — expected PASS.
- [ ] **Step 5: Commit** `feat: aggregate endpoint`.

---

### Task 9: Exposure and rates endpoints

**Files:**
- Create: `src/TransportAnalyzer.Api/Query/RateJoiner.cs`, `Endpoints/ExposureEndpoints.cs`
- Test: `tests/TransportAnalyzer.Tests/RateJoinerTests.cs`, `ExposureEndpointsTests.cs`

**Interfaces:**
- Produces: `GET /api/exposure?metric=&mode=&fromYear=&toYear=&vehicleClass=&regulationClass=` → `{ columns, rows }` with `source_id, mode, year, vehicle_class, regulation_class, metric, value, provenance, standard_error`. `metric` required.
- Produces (pure, `RateJoiner.cs`):

```csharp
public sealed record RateKey(string Mode, int Year, string? VehicleClass, string? RegulationClass);
public sealed record NumeratorCell(RateKey Key, long Value);
public sealed record ExposureCell(RateKey Key, double Value, string SourceId, string Provenance);
public sealed record RateRow(RateKey Key, long Numerator, double Denominator, string JoinLevel,
    string ExposureSource, string Provenance);
public sealed record RateResult(IReadOnlyList<RateRow> Rows, IReadOnlyList<NumeratorCell> Unmatched);

public static RateResult Join(IEnumerable<NumeratorCell> numerators, IEnumerable<ExposureCell> exposure,
    IReadOnlyDictionary<string, string?> vehicleClassParent, bool byVehicleClass, bool byRegulationClass);
```

  Algorithm: when `byVehicleClass` is false, set numerator and exposure `VehicleClass` to null before matching (same for regulation class); the numerator is then summed over the dropped dimension and the exposure must have its own null-key row. For each numerator cell try, in order, exposure keys `(vc, rc)`, `(vc, null)`, `(parent(vc), rc)`, `(parent(vc), null)`, `(null, null)`; the first key present wins. `JoinLevel` names the matched key shape: `"vehicle_class+regulation_class"`, `"vehicle_class"`, `"vehicle_class_parent+regulation_class"`, `"vehicle_class_parent"`, `"mode"`. Numerators resolving to the same exposure key are summed into one `RateRow` whose `Key` is the exposure key. Cells with no match go to `Unmatched`. Exposure is never divided across cells.
  When `byVehicleClass` is true, the `(null, null)` fallback is **not** used (it would silently merge classes into the mode total); such cells go to `Unmatched`. (My call.)
- Produces: `GET /api/rates?measure=&metric=&fromYear=&toYear=&groupBy=` where `measure` ∈ `occupant_fatalities` | `societal_fatalities` (`occupant + nonoccupant`, attributed casualties only), `metric` ∈ exposure metrics, `groupBy` ⊆ `{vehicle_class, regulation_class}` (mode and year are always grouped). Unattributed casualties cannot be assigned to a vehicle class, so they are never folded into a row; the response reports them per `(mode, year)` in `unattributed` so the UI can show how much the societal figure leaves out (my call). Response `{ measure, metric, columns, rows, unmatched, unattributed: [{ mode, year, fatalities }] }`; rows carry `mode, year, vehicle_class, regulation_class, numerator, denominator, join_level, exposure_source, provenance`. No rate is computed server-side.

- [ ] **Step 1: Write failing tests** for `RateJoiner`:

```csharp
[Fact] public void Join_ExactClassMatch()
// numerator (highway,2022,motorcycle) 5; exposure (highway,2022,motorcycle) 100 → row numerator 5, denominator 100, joinLevel "vehicle_class"

[Fact] public void Join_RollsNumeratorUpToParent()
// numerators passenger_car 3, light_truck 4; exposure light_duty 1000 → one row key light_duty, numerator 7, joinLevel "vehicle_class_parent"

[Fact] public void Join_NotGroupedByClass_UsesModeTotal()
// byVehicleClass false; numerators motorcycle 5, passenger_car 3; exposure (null,null) 2000 and motorcycle 100 → one row numerator 8, denominator 2000, joinLevel "mode"

[Fact] public void Join_NoExposure_ReportedUnmatched()
// numerator (highway,2023,bus) 2; exposure only for 2022 → rows empty, unmatched contains it with Value 2

[Fact] public void Join_GroupedByClass_DoesNotFallBackToModeTotal()
// byVehicleClass true; numerator bus 2; exposure only (null,null) → unmatched
```

  And endpoint tests (FARS fixture + seeded exposure rows inserted directly):

```csharp
[Fact] public async Task Rates_ByVehicleClass_ReturnsLightDutyRow()
// groupBy=vehicle_class, measure=occupant_fatalities, metric=passenger_miles, seeded light_duty 2022 → row light_duty numerator 2

[Fact] public async Task Rates_MissingMetric_Returns400()
```

- [ ] **Step 2: Run** — expected FAIL.
- [ ] **Step 3: Implement** `RateJoiner`, then the endpoints (numerator query at `(mode, year, vehicle_class, regulation_class)` via the Task 8 SQL builder, exposure via Dapper).
- [ ] **Step 4: Run** — expected PASS.
- [ ] **Step 5: Commit** `feat: exposure and rates endpoints`.

---

### Task 10: Real-data verification

**Files:**
- Create: `docs/verification/plan-1.md` (results table)

No new code unless a defect is found; a defect gets a failing test in the owning task's test file first.

- [ ] **Step 1:** `docker compose up -d --build`, then `curl -X POST localhost:8080/api/admin/ingest/fars`, `.../bts-passenger-miles`, `.../bts-vehicle-miles`; poll `GET /api/admin/runs` until all three are `succeeded`.
- [ ] **Step 2: Check FARS totals** with `GET /api/events/aggregate?from=2019-01-01&to=2023-12-31&bucket=year`. Expected `fatalities` per year, matching NHTSA final counts: 2019 36,355; 2020 39,007; 2021 43,230; 2022 42,514; 2023 40,901. Any year more than 0.1% off is a defect (check `inj_sev` and `died prior` handling first).
- [ ] **Step 3: Check unattributed share:** `unattributed_fatalities / fatalities` per year from the same query. Expect a small single-digit percentage; record the figure. Above 5% means the non-occupant attribution rule is missing a case.
- [ ] **Step 4: Check unmapped:** `GET /api/admin/runs` for the FARS run, list `unmapped` keys with counts above 100. Each is either added to `data/mappings/fars.csv` or recorded in `plan-1.md` as deliberately `unknown`.
- [ ] **Step 5: Check exposure:** `GET /api/exposure?metric=passenger_miles&fromYear=2019&toYear=2023` returns aviation `part_121` and highway `light_duty`, `motorcycle`, `bus`, `heavy_truck`, total rows for each year BTS has published.
- [ ] **Step 6: Check a rate by hand:** `GET /api/rates?measure=occupant_fatalities&metric=passenger_miles&fromYear=2022&toYear=2022&groupBy=vehicle_class`; for `motorcycle`, compute deaths per 100 million passenger-miles from `numerator / denominator` and record it next to the BTS-published motorcyclist rate for the same year in `plan-1.md`. Large disagreement (over 20%) is investigated and explained in the file.
- [ ] **Step 7: Commit** `docs: plan 1 real-data verification`.
