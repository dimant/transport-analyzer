# Plan 2: Aviation (NTSB, FAA flight hours), sample endpoint, schedules — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add NTSB aviation accidents and FAA flight-hour exposure so `/api/rates` can compare airline, charter and private flying with road travel, plus a sample endpoint and monthly schedules.

**Architecture:** Same adapter pipeline as plan 1. The NTSB adapter downloads `avall.zip`, exports every MDB table with `mdb-export` (mdbtools, installed in the container) through an `IMdbExporter` seam, stages them with `CsvStager`, and normalizes aircraft into `vehicle` rows with set-based SQL. A new FAA GA Survey adapter parses Chapter 1 Tables 1.3 and 1.4 into `flight_hours` exposure. `RateJoiner` gains a regulation-class parent fallback and a mixed-granularity guard; `/api/rates` is scoped to US events. Quartz.NET schedules ingests from config.

**Tech Stack:** as plan 1, plus mdbtools (Debian package, container only), Quartz.NET (`Quartz.Extensions.Hosting`), `Xunit.SkippableFact`.

**Spec:** `docs/superpowers/specs/2026-11-04-experiment-platform-design.md`. Inputs: `docs/superpowers/plans/2026-11-04-plan-1-carryover.md` (plan 2 items), `docs/research/2026-11-04-ntsb-faa-recon.md` (verified facts about the real NTSB and FAA files — read the sections your task touches).

## Global Constraints

Plan 1's Global Constraints all still apply (net10.0, Postgres 17 without PostGIS, single user, nothing discarded from sources, `source_payload` = main record, upsert by `(source_id, source_key)`, mappings in `data/` not code, unknown values counted, the event casualty invariant, never spread a denominator, whitelisted query dimensions). In addition:

- Work directly on `main` (user decision). Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- `docker-compose.yml` keeps `8080:8080`; admin endpoints need no token unless `Admin__Token` is set (user decision).
- Never work around a site that blocks automated clients (no User-Agent changes). bts.gov blocks; NTSB and faa.gov were verified reachable on 2026-11-04.
- Downloaded files are untrusted data: never execute anything from them; never commit them.
- JSON object keys camelCase; tabular `columns` keep snake_case whitelist names; data keys (e.g. `unknown:vehicle_class`) are opaque strings.
- NTSB `occurred_time` stays null: `ev_time` is labelled UTC but unverified, and `event.occurred_time` is defined as local time. `ev_time`/`ev_tmzn` stay in `source_payload`.
- Country codes in `event.country`: FARS writes `US`; NTSB `USA` is normalized to `US`, other NTSB codes are kept as given.

## Review Focus

1. **Midair with ground casualties**: with more than one aircraft, ground casualties cannot be tied to one aircraft and must land in `event.unattributed_*`; with exactly one aircraft they are that aircraft's `nonoccupant_*`. The invariant must hold. Test: `Normalize_MidairGroundFatality_IsUnattributed` and `Normalize_SingleAircraftGroundFatality_IsNonoccupant` (Task 3).
2. **Foreign NTSB events in rates**: every exposure source is US-only, so `/api/rates` numerators must exclude non-US events. Test: `Rates_ExcludeNonUsEvents` (Task 6).
3. **Mixed granularity**: a numerator that rolls up to a parent exposure key while a sibling matched its own child key exactly would get a denominator that also covers the sibling. It must be reported unmatched with reason `mixed_granularity`. Test: `Join_RollUpBesideExactSibling_IsMixedGranularity` (Task 5).
4. **Narratives with embedded CRLF and doubled quotes** stage as one row each. Test: `Stage_MultilineNarrative_IsOneRow` (Task 3).
5. **Scheduled Part 135 (commuter)** is not in the FAA survey, which covers on-demand Part 135 only; it must never be divided by on-demand hours. Test: `Normalize_Part135Scheduled_HasOwnRegulationClass` (Task 3) and `Join_Part135Scheduled_HasNoParent` (Task 5).

---

## File Structure

```
Dockerfile                                         + apt-get install mdbtools in runtime stage
data/vocabulary.csv                                + regulation_class general_aviation (parent of part_91, part_135, part_137, part_133), part_133, part_135_scheduled; vehicle_class experimental
data/mappings/ntsb.csv                             vocabulary_map rules (5-col header; source_value may be a SQL LIKE pattern)
data/mappings/faa-ga-survey.csv                    table,row_label,occurrence,mode,vehicle_class,regulation_class,provenance
src/TransportAnalyzer.Infrastructure/RawStore.cs   ms-resolution run directories
src/TransportAnalyzer.Ingestion/IngestQueue.cs     stale-run timeout
src/TransportAnalyzer.Ingestion/Fars/fars-normalize.sql   empty-year guard
src/TransportAnalyzer.Ingestion/Ntsb/IMdbExporter.cs      seam + MdbToolsExporter (process)
src/TransportAnalyzer.Ingestion/Ntsb/NtsbAdapter.cs
src/TransportAnalyzer.Ingestion/Ntsb/ntsb-normalize.sql   embedded resource
src/TransportAnalyzer.Ingestion/Faa/FaaGaSurveyAdapter.cs
src/TransportAnalyzer.Api/Query/RateJoiner.cs      rc parent fallback, mixed-granularity guard
src/TransportAnalyzer.Api/Endpoints/ExposureEndpoints.cs  US scope, flight_hours default
src/TransportAnalyzer.Api/Query/SampleQuery.cs, Endpoints/EventEndpoints.cs   /api/events/sample
src/TransportAnalyzer.Api/Scheduling/IngestJob.cs  Quartz job
tests/TransportAnalyzer.Tests/Fixtures/ntsb/*.csv  hand-built mdb-export-shaped fixture
docs/verification/plan-2.md
README.md
```

---

### Task 1: Carry-over hardening

**Files:**
- Modify: `src/TransportAnalyzer.Infrastructure/RawStore.cs`, `src/TransportAnalyzer.Ingestion/IngestQueue.cs`, `src/TransportAnalyzer.Ingestion/Fars/fars-normalize.sql` (or `FarsAdapter.cs`)
- Test: existing `IngestRunnerTests.cs`, `FarsAdapterTests.cs`, new `RawStoreTests.cs`

**Interfaces:**
- Produces: `RawStore` directory name `{yyyyMMdd'T'HHmmssfff}`; `IngestQueue` option `Ingest:StaleRunAfter` (TimeSpan, default `06:00:00`).

- [ ] **Step 1: Write failing tests:**

```csharp
[Fact] public async Task RawStore_TwoSavesSameSecond_DoNotCollide()
// two SaveAsync calls for the same source and file name in a tight loop both succeed with different paths

[Fact] public async Task Fars_RequestedYearWithNoAccidentRows_FailsRunAndKeepsEvents()
// ingest the clean 2022 fixture; ingest again with a 2022 zip whose accident.csv has a header only
// → run status "failed", error mentions "2022" and "no accident rows"; the 3 fixture events still exist

[Fact] public async Task AdminIngest_StaleRunningRow_DoesNotBlock()
// insert a `running` row for a stub source with started_at = now - 7h; POST ingest → 202 (not 409);
// the stale row is now `failed` with error "stale: exceeded Ingest:StaleRunAfter"
```

- [ ] **Step 2: Run** `dotnet test --filter "RawStore_TwoSaves|RequestedYearWithNoAccident|StaleRunningRow"` — expected FAIL.
- [ ] **Step 3: Implement.** The empty-year guard runs before the stale-crash delete and raises an exception naming each requested year with zero staged accident rows. The stale check runs inside `TryEnqueueAsync` under the existing lock.
- [ ] **Step 4: Run** the three tests, then the full suite — expected PASS.
- [ ] **Step 5: Commit** `fix: raw store ms dirs, FARS empty-year guard, stale-run timeout`.

---

### Task 2: Vocabulary additions, MDB exporter seam, mdbtools in the container

**Files:**
- Modify: `data/vocabulary.csv`, `Dockerfile`
- Create: `src/TransportAnalyzer.Ingestion/Ntsb/IMdbExporter.cs`
- Test: `tests/TransportAnalyzer.Tests/MdbExporterTests.cs`, `SchemaTests.cs`

**Interfaces:**
- Produces:

```csharp
public interface IMdbExporter
{
    Task<IReadOnlyList<string>> ListTablesAsync(string mdbPath, CancellationToken ct);
    Task<Stream> ExportAsync(string mdbPath, string table, CancellationToken ct);   // mdb-export default dialect
}
public sealed class MdbToolsExporter(string mdbTablesPath = "mdb-tables", string mdbExportPath = "mdb-export") : IMdbExporter
```

  `MdbToolsExporter` runs `mdb-tables -1 <path>` and `mdb-export <path> <table>` with `ProcessStartInfo.ArgumentList` (no shell), streams stdout, and throws `InvalidOperationException` with exit code and stderr on non-zero exit. `ExportAsync` returns a stream that surfaces a non-zero exit as an exception when read to the end.
- Vocabulary rows (descriptions one sentence each):
  - `regulation_class`: `general_aviation` (no parent: Part 91, on-demand Part 135, 137 and 133 together, as the FAA GA and Part 135 Activity Survey counts them); `part_91`, `part_135`, `part_137` get parent `general_aviation`; new `part_133` (parent `general_aviation`); new `part_135_scheduled` (no parent: scheduled commuter, not in the FAA survey).
  - `vehicle_class`: new `experimental` (amateur-built and other experimental aircraft).
- Dockerfile runtime stage: `apt-get update && apt-get install -y --no-install-recommends mdbtools && rm -rf /var/lib/apt/lists/*`.

- [ ] **Step 1: Write failing tests:**

```csharp
[Fact] public void Vocabulary_Part91ParentIsGeneralAviation()       // and part_135, part_137, part_133; part_135_scheduled parent null
[SkippableFact] public async Task MdbToolsExporter_MissingFile_ThrowsWithStderr()
// Skip.IfNot(mdb-export on PATH); ListTablesAsync("/nonexistent.mdb") throws InvalidOperationException containing the exit code
[Fact] public async Task MdbToolsExporter_MissingBinary_ThrowsClearError()
// new MdbToolsExporter("definitely-not-a-binary") → InvalidOperationException mentioning "mdb-tables" and "mdbtools"
```

- [ ] **Step 2: Run** — expected FAIL.
- [ ] **Step 3: Implement**, add `Xunit.SkippableFact` to the test project.
- [ ] **Step 4: Run** — expected PASS (the skippable test reports skipped on hosts without mdbtools).
- [ ] **Step 5: Verify the container**: `docker compose build app && docker compose run --rm --entrypoint mdb-tables app --version` prints a version.
- [ ] **Step 6: Commit** `feat: vocabulary for aviation regulation classes, MDB exporter seam, mdbtools in image`.

---

### Task 3: NTSB adapter

**Files:**
- Create: `src/TransportAnalyzer.Ingestion/Ntsb/NtsbAdapter.cs`, `NtsbOptions.cs`, `Ntsb/ntsb-normalize.sql` (embedded), `data/mappings/ntsb.csv`, `tests/TransportAnalyzer.Tests/Fixtures/ntsb/{events,aircraft,engines,injury,narratives}.csv`, `FakeMdbExporter.cs`
- Modify: `src/TransportAnalyzer.Api/Program.cs` (registration), `README.md` (source list)
- Test: `tests/TransportAnalyzer.Tests/NtsbAdapterTests.cs`

**Interfaces:**
- Consumes: `ISourceAdapter`, `IRawStore`, `CsvStager`, `IMdbExporter` (Task 2), `vocabulary_map`.
- Produces: `NtsbAdapter(HttpClient http, IMdbExporter mdb, IOptions<NtsbOptions> o)`, `SourceId = "ntsb"`, `Mode = "aviation"`, `Kind = "events"`. `NtsbOptions { string Url = "https://data.ntsb.gov/avdata/FileDirectory/DownloadFile?fileID=C%3A%5Cavdata%5Cavall.zip"; string Snapshot = "avall"; }`. Descriptor `Definitions`: "Accident: an occurrence associated with aircraft operation in which a person is fatally or seriously injured or the aircraft receives substantial damage (49 CFR 830.2). Incident: an occurrence other than an accident that affects or could affect safety." `Caveats`: "Only occurrences the NTSB investigated or recorded. avall covers 2008 onwards. Final reports take 12–24 months, so recent years are incomplete and revised. Scheduled Part 135 (commuter) is not covered by FAA survey exposure."
- Fetch: download the zip into the raw store. Stage: extract `*.mdb` to a temp directory, `ListTablesAsync`, and stage every table except names starting with `MSys` into `stg_ntsb.{lowercased name, non-[a-z0-9_] → _}` with extra column `snapshot` = `Snapshot` and `replaceWhereColumn = "snapshot"` (avall is a full reload). Delete the temp directory in `finally`. Normalize: `ntsb-normalize.sql` with `@years int[]` (null = all years present) and `@run_id`.
- `source_key`: event = `ev_id`; vehicle `vehicle_no` = `Aircraft_Key`.

**Normalize rules:**
- Event: `mode='aviation'`; `event_type` = `accident` for `ACC`, `incident` for `INC`; `occurred_on = make_date(ev_year, ev_month, day)` with day = `split_part(ev_date, '/', 2)::int` (mdb-export writes `MM/DD/YY HH:MM:SS`); `occurred_time` null; `latitude`/`longitude` from `dec_latitude`/`dec_longitude` (null when blank or outside ±90/±180); `state` = `ev_state` (blank → null); `country` = `US` for `USA`, else `ev_country` (blank → null); `severity` from `ev_highest_injury` (`FATL` fatal, `SERS` serious, `MINR` minor, `NONE` none, other/blank unknown); `narrative` = `narr_accf` of the lowest `Aircraft_Key` narrative row, falling back to `narr_accp` when blank or equal to `import`; `probable_cause` = `narr_cause` likewise; `operation_class` = the `regulation_class` of aircraft 1; `ground_fatalities` = `inj_f_grnd`.
- Aircraft occupants: per `(ev_id, Aircraft_Key)`, sum `inj_person_count` from `injury` by `injury_level` — `FATL` → `occupant_fatalities`, `SERS` → `occupant_serious_injuries`, `MINR` → `occupant_minor_injuries`. `TOTL` rows are ignored (unreliable, see recon). `occupants` = sum over `FATL, SERS, MINR, NONE` (null when the aircraft has no injury rows). `crew_count` = the same sum restricted to categories `Flig` and `Cabi` (null when none).
- Ground casualties (`inj_f_grnd`, `inj_s_grnd`, `inj_m_grnd`): if the event has exactly one aircraft, they go to that aircraft's `nonoccupant_*`; otherwise to `event.unattributed_*`.
- Event totals: `fatalities` = Σ aircraft `occupant_fatalities` + ground fatal; `serious_injuries` and `minor_injuries` likewise. The plan-1 invariant then holds by construction.
- Aircraft fields: `registration` = `regis_no`; `make`/`model` = `acft_make`/`acft_model` (trimmed); `year_built` = `acft_year` when between 1900 and `ev_year`, else null; `vehicle_age_years` = `ev_year - year_built`; `damage` = `damage` (blank → null).
- Derived matching columns (computed in SQL, all values trimmed, blank → empty string):
  - `homebuilt` (as is)
  - `vehicle_profile` = `acft_category:eng_type:far_part`, with `eng_type` from the lowest `eng_no` engine row
  - `reg_profile` = `far_part:oper_sched`
  - `use_profile` = `far_part:type_fly`
- Attribute matching: join `vocabulary_map` for `source_id='ntsb'` on `(attribute, source_column)` with `derived_value LIKE source_value`, lowest `priority` wins, no match → `unknown`, counted in `Unmapped` as `{attribute}:{column}={value}` on the attribute's last-resort column.
- Re-ingest: delete vehicles of the affected events before insert; delete `ntsb` events of the requested years absent from the staged events (same pattern as FARS R25, including the empty-year guard from Task 1: a requested year with zero staged events fails the run).

**Mapping file `data/mappings/ntsb.csv`** (header `attribute,source_column,source_value,code,priority`; the builder drafts the full list from the recon value counts and the user reviews it):
- `vehicle_class`: p1 `homebuilt` = `Y` → `experimental`; p2 `vehicle_profile` `HELI:%` and `GYRO:%` → `helicopter`; p3 `AIR:TF:121`, `AIR:TJ:121`, `AIR:TF:129`, `AIR:TJ:129` → `airliner`; p4 `AIR:TF:%`, `AIR:TJ:%`, `AIR:GTFN:%` → `business_jet`; p5 `AIR:TP:%` → `turboprop`; p6 `AIR:REC:%`, `AIR:ELEC:%` → `light_aircraft`; p7 `GLI:%`, `BALL:%`, `BLIM:%`, `WSFT:%`, `PPAR:%`, `ULTR:%`, `PLFT:%`, `RCKT:%` → `other`. Anything else (blank category, `UNK`, `AIR` with blank engine) stays unmatched → `unknown`.
- `regulation_class` on `reg_profile`: `091:%`, `091K:%` → `part_91`; `121:%` → `part_121`; `135:SCHD` (p1) → `part_135_scheduled`; `135:%` (p2) → `part_135`; `137:%` → `part_137`; `133:%` → `part_133`; `129:%`, `125:%`, `NUSN:%`, `NUSC:%`, `PUB%`, `ARMF:%`, `107:%`, `103:%`, `437:%` → `other`; `:%` and `UNK:%` → `unknown`.
- `operator_type` on `use_profile`: `PUB%`, `ARMF:%`, `%:PUB%`, `%:FIRF` → `government` (p1); `121:%`, `135:%`, `129:%`, `125:%`, `133:%`, `137:%`, `NUSC:%`, `091K:%` → `commercial` (p2); `091:PERS`, `091:`, `NUSN:%` → `private` (p3); `091:%` other purposes (`INST`, `BUS`, `EXEC`, `AAPL`, `AOBV`, `OWRK`, `EXLD`, `FERY`, `POSI`, `SKYD`, `BANT`, `GLDT`, `ASHO`, `FLTS`, `ADRP`) → `commercial` (p4).
- `maintenance_regime` on `reg_profile`: `091:%`, `091K:%`, `121:%`, `135:%`, `137:%`, `133:%`, `125:%` → `mandated_periodic`; everything else → `unknown` explicitly.

**Fixture (`Fixtures/ntsb/`)**, mdb-export shaped (quoted text, `MM/DD/YY 00:00:00` dates, blank = NULL), all events in 2022, columns limited to what the rules use plus `ntsb_no` for passthrough:
- E1 `20220101X00001` ACC, 01/15/22, `USA`, `CA`, one aircraft: `091`, `PERS`, `AIR`, engine `REC`, homebuilt `N`, `acft_year` 1978; injury Flig FATL 1, Pass SERS 1, Pass NONE 1, Flig TOTL 9 (must be ignored); `inj_f_grnd` blank; narratives `narr_accf` contains a CRLF and a doubled quote, `narr_cause` "Loss of control.".
- E2 `20220202X00002` INC, 02/02/22, `USA`, one aircraft: `121`, `SCHD`, `AIR`, `TF`; injury Flig NONE 2, Cabi NONE 3, Pass NONE 120.
- E3 `20220303X00003` ACC, 03/03/22, `USA` midair: AC1 `091`, `INST`, `AIR`, `REC`, homebuilt `Y`, Flig FATL 1; AC2 `135`, `NSCH`, `AIR`, `TP`, Flig FATL 1, Pass MINR 2; `inj_f_grnd` 1.
- E4 `20220404X00004` ACC, 04/04/22, `BR`, one aircraft: `NUSC`, `AIR`, `TF`, Flig FATL 2; `inj_f_grnd` 2.
- E5 `20220505X00005` ACC, 05/05/22, `USA`, one aircraft: `135`, `SCHD`, `AIR`, `TP`, Pass FATL 1.
- E6 `20220606X00006` ACC, 06/06/22, `USA`, one aircraft: far_part blank, `type_fly` `ZZZZ` (a code no rule covers), `HELI`, `TS`, Flig SERS 1.

- [ ] **Step 1: Write the fixture and `ntsb.csv`.**
- [ ] **Step 2: Write failing tests** (adapter built with a stub `HttpMessageHandler` serving any zip containing a dummy `avall.mdb`, and `FakeMdbExporter` listing the fixture files as tables and returning their bytes):

```csharp
[Fact] public async Task Normalize_SingleAircraft_CountsOccupantsAndIgnoresTotl()
// E1: fatalities 1, serious 1; vehicle: occupant_fatalities 1, occupant_serious 1, occupants 3, crew_count 1,
// vehicle_class light_aircraft, regulation_class part_91, operator_type private, maintenance_regime mandated_periodic,
// vehicle_age_years 44; event occurred_on 2022-01-15, occurred_time null, country "US", state "CA", severity from ev_highest_injury

[Fact] public async Task Normalize_Incident_IsIncidentWithNoCasualties()
// E2: event_type incident, fatalities 0; vehicle airliner, part_121, commercial, occupants 125, crew_count 5

[Fact] public async Task Normalize_MidairGroundFatality_IsUnattributed()
// E3: fatalities 3, minor 2, unattributed_fatalities 1; AC1 experimental, AC2 turboprop part_135 commercial; nonoccupant 0 on both

[Fact] public async Task Normalize_SingleAircraftGroundFatality_IsNonoccupant()
// E4: country "BR", fatalities 4; AC1 nonoccupant_fatalities 2, occupant_fatalities 2, regulation_class other

[Fact] public async Task Normalize_Part135Scheduled_HasOwnRegulationClass()
// E5: regulation_class part_135_scheduled

[Fact] public async Task Normalize_BlankFarPartAndUnmappedUse_AreUnknown()
// E6: vehicle_class helicopter, regulation_class unknown (explicit ':%' rule, so NOT in Unmapped);
// operator_type unknown and run Unmapped contains "operator_type:use_profile=:ZZZZ"; run row_counts has unknown:regulation_class >= 1

[Fact] public async Task Stage_MultilineNarrative_IsOneRow()
// stg_ntsb.narratives has exactly one row for E1, narr_accf contains "\r\n" and a single '"'; event.narrative equals it

[Fact] public async Task Normalize_EventInvariantHolds()        // same SQL as the FARS invariant test, for source 'ntsb'
[Fact] public async Task Ingest_Twice_IsIdempotent()            // 6 events, 7 vehicles after two runs
[Fact] public async Task Ingest_EventDroppedFromSnapshot_IsDeleted()   // re-ingest without E6 → E6 gone, others stay
[Fact] public async Task Mappings_AllCodesExistInVocabulary()   // as R6, for source 'ntsb'
```

- [ ] **Step 3: Run** — expected FAIL.
- [ ] **Step 4: Implement.**
- [ ] **Step 5: Run** — expected PASS; full suite pristine.
- [ ] **Step 6: Commit** `feat: NTSB aviation adapter with per-aircraft casualty attribution`.

---

### Task 4: FAA GA Survey flight-hours adapter

**Files:**
- Create: `src/TransportAnalyzer.Ingestion/Faa/FaaGaSurveyAdapter.cs`, `Faa/FaaGaSurveyOptions.cs`, `data/mappings/faa-ga-survey.csv`
- Modify: `src/TransportAnalyzer.Api/Program.cs`, `src/TransportAnalyzer.Api/appsettings.json`
- Test: `tests/TransportAnalyzer.Tests/FaaGaSurveyAdapterTests.cs`

**Interfaces:**
- Consumes: `ISourceAdapter`, `IRawStore`, `ExposureRow` (plan 1 R10).
- Produces: `FaaGaSurveyAdapter(HttpClient http, IOptions<FaaGaSurveyOptions> o, string dataRoot)`, `SourceId = "faa-ga-survey"`, `Mode = "aviation"`, `Kind = "exposure"`. `FaaGaSurveyOptions { string Url = "https://www.faa.gov/data_research/aviation_data_statistics/general_aviation/cy2024/2024GASurveyCh1.xlsx"; }` (the URL pattern changes every survey year; README says to update it). Caveats: "Survey estimates. Covers Part 91, on-demand Part 135, 137 and 133 flying only — not airlines (Part 121) and not scheduled Part 135. Part 91 / 137 hours are derived by subtracting use categories (see assumptions)."
- Metric `flight_hours`, unit multiplier 1,000 (tables are in thousands of hours). Years are the header columns (2024 … 2013, newest first).
- **Table 1.3** (sheet `1.3`, by aircraft type): data rows alternate a value row and a `% Std. Error` row. Labels repeat (`Piston: Total` appears under fixed-wing piston and under rotorcraft), so the mapping keys on `(row_label, occurrence)` — occurrence counts identical labels top to bottom, starting at 1. Footnote digits glued to labels (`Experimental: Total1`) are stripped. `standard_error` = value × pct / 100. Rows (all `regulation_class = general_aviation`, provenance `survey_estimate`):
  - `Piston: Total`#1 → `light_aircraft`
  - `Turboprop: Total`#1 → `turboprop`
  - `Turbojet: Total`#1 → `business_jet`
  - `Rotorcraft: Total`#1 → `helicopter`
  - `Experimental: Total`#1 → `experimental`
  - `Gliders`#1 and `Lighter-than-air`#1 → `other`, summed; the SE is the square root of the sum of squared absolute SEs
  - `All Aircraft`#1 → vehicle_class null
  - `Special Light-sport` is not mapped and is recorded in the run's `Unmapped` as `exposure:row_label=Special Light-sport`, since NTSB has no light-sport flag.
- **Table 1.4** (sheet `1.4`, by use, no SE rows), all vehicle_class null:
  - `On-Demand FAR Part 135 Use Total` → `part_135`, provenance `survey_estimate`
  - `part_137` = `Aerial Application Agriculture` + `Aerial Application Other`, provenance `derived`, `assumptions` `{"rule":"part_137 = aerial application (agriculture + other)"}`
  - `part_133` = `External Load`, provenance `derived`
  - `part_91` = `General Aviation Use Total` − `part_137` − `part_133`, provenance `derived`, `assumptions` naming the formula
- Mapping file columns: `table,row_label,occurrence,operation,mode,vehicle_class,regulation_class,provenance`, where `operation` is `value`, `add` (summed into the key) or `subtract`. Its header differs from vocabulary_map's, so the loader skips it (plan 1 R2).
- Normalize deletes this source's exposure rows for the requested years before upserting (plan 1 final-fix #3 pattern).

- [ ] **Step 1: Recon.** Download the CY2024 Ch1 xlsx to a scratch directory outside the repo. Confirm sheet names `1.3` and `1.4`, the header row, labels and occurrences against `docs/research/2026-11-04-ntsb-faa-recon.md` §2, and record any differences in the report.
- [ ] **Step 2: Write failing tests** on an in-memory workbook that mirrors the real layout: header row 2 with years newest first, alternating `% Std. Error` rows, a repeated `Piston: Total`, a footnote digit on `Experimental: Total1`, a decoy first sheet, and Table 1.4 rows.

```csharp
[Fact] public void Parse13_UsesFirstOccurrenceAndScalesUnits()      // Piston: Total#1 2024 = value*1000, class light_aircraft, rc general_aviation
[Fact] public void Parse13_StandardErrorIsAbsolute()                  // SE = value*1000*pct/100
[Fact] public void Parse13_OtherSumsGlidersAndLta_SeInQuadrature()
[Fact] public void Parse14_DerivesPart91AndPart137()                  // part_91 = GA total − ag − aerial other − external load; provenance derived; assumptions present
[Fact] public void Parse_SpecialLightSport_IsReportedUnmapped()
[Fact] public async Task Ingest_ReplacesSourceRowsForYears()          // re-ingest with a changed mapping removes the old key
```

- [ ] **Step 3: Run** — expected FAIL.
- [ ] **Step 4: Implement.**
- [ ] **Step 5: Run** — expected PASS. Then do one real ingest in the container and record the 2022 rows for `part_91`, `part_135`, `light_aircraft`/`general_aviation`, and `All Aircraft` in the report.
- [ ] **Step 6: Commit** `feat: FAA GA survey flight-hours exposure`.

---

### Task 5: RateJoiner — regulation-class parents and mixed-granularity guard

**Files:**
- Modify: `src/TransportAnalyzer.Api/Query/RateJoiner.cs`, `Endpoints/ExposureEndpoints.cs` (pass the regulation-class parent map and the new unmatched shape)
- Test: `tests/TransportAnalyzer.Tests/RateJoinerTests.cs`, `ExposureEndpointsTests.cs`

**Interfaces:**
- Changes (all callers updated):

```csharp
public sealed record UnmatchedCell(RateKey Key, long Value, string Reason);   // "no_exposure" | "mixed_granularity"
public sealed record RateResult(IReadOnlyList<RateRow> Rows, IReadOnlyList<UnmatchedCell> Unmatched);
public static RateResult Join(IEnumerable<NumeratorCell> numerators, IEnumerable<ExposureCell> exposure,
    IReadOnlyDictionary<string, string?> vehicleClassParent, IReadOnlyDictionary<string, string?> regulationClassParent,
    bool byVehicleClass, bool byRegulationClass);
```

  `/api/rates` `unmatched` entries gain `reason`.
- Candidate order per numerator cell, with `pv` = parent of vc and `pr` = parent of rc. Candidates involving a dimension the query does not group by are skipped. The `(null, null)` candidate is skipped whenever grouped by vehicle class (plan 1 rule):
  `(vc, rc)`, `(vc, pr)`, `(vc, null)`, `(pv, rc)`, `(pv, pr)`, `(pv, null)`, `(null, rc)`, `(null, pr)`, `(null, null)`.
  `JoinLevel` is the matched key's parts joined by `+`, from `vehicle_class` | `vehicle_class_parent` and `regulation_class` | `regulation_class_parent`, or `mode` when both are null. This keeps plan 1's six level names unchanged.
- **Mixed-granularity guard** (closes the carry-over item): after matching, take every exposure key `P` that some cell reached through a parent fallback. If any other cell in the same `(mode, year)` matched exactly a key that is a child of `P` (same other dimension, child code whose parent is `P`'s code), then all cells that rolled up to `P` move to `Unmatched` with reason `mixed_granularity`, and `P`'s row is dropped. Cells matched exactly are unaffected. The same applies when the roll-up is on vehicle class.

- [ ] **Step 1: Write failing tests** (existing plan-1 joiner tests keep passing after the signature change):

```csharp
[Fact] public void Join_RegulationParent_RollsUpToGeneralAviation()
// grouped by both; numerators (light_aircraft, part_91) 3, (light_aircraft, part_135) 2; exposure (light_aircraft, general_aviation) 1000
// → one row numerator 5, joinLevel "vehicle_class+regulation_class_parent"
[Fact] public void Join_RollUpBesideExactSibling_IsMixedGranularity()
// grouped by regulation class; numerators (null, part_91) 3, (null, part_133) 1; exposure (null, part_91) 900, (null, general_aviation) 1000
// → row part_91 numerator 3; unmatched part_133 reason "mixed_granularity"; no general_aviation row
[Fact] public void Join_Part135Scheduled_HasNoParent()
// numerator (turboprop, part_135_scheduled) 1; exposure (turboprop, general_aviation) → unmatched reason "no_exposure"
[Fact] public void Join_Part121_NeverReachesGeneralAviation()
// numerator (airliner, part_121); exposure only (null, general_aviation) and (airliner, general_aviation) → unmatched "no_exposure"
[Fact] public void Join_VehicleClassMixedGranularity_IsGuarded()
// numerators (passenger_car, null) 2, (light_truck, null) 3; exposure (passenger_car, null) 50, (light_duty, null) 100
// → passenger_car row; light_truck unmatched mixed_granularity
```

- [ ] **Step 2: Run** — expected FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the joiner and endpoint tests, then the full suite — expected PASS.
- [ ] **Step 5: Commit** `feat: regulation-class roll-up and mixed-granularity guard in rates`.

---

### Task 6: `/api/rates` — US scope and flight-hours default

**Files:**
- Modify: `src/TransportAnalyzer.Api/Endpoints/ExposureEndpoints.cs`
- Test: `tests/TransportAnalyzer.Tests/ExposureEndpointsTests.cs`

**Interfaces:**
- Numerator and `unattributed` queries add the filter `country = US`. The response gains `"scope": { "country": "US" }`.
- Default `exposureSource` for `flight_hours` becomes `faa-ga-survey`; plan 1's 400 for a missing default is removed.
- Ingest the NTSB fixture (Task 3 helpers) in the class fixture. Seed `faa-ga-survey` rows directly after deleting that source's 2022 rows.

- [ ] **Step 1: Write failing tests:**

```csharp
[Fact] public async Task Rates_ExcludeNonUsEvents()
// measure=occupant_fatalities, metric=flight_hours, groupBy=vehicle_class,regulation_class, 2022; seeded (business_jet, general_aviation)
// → no numerator from E4 (BR); unmatched contains no 'other' row from E4; scope.country == "US"
[Fact] public async Task Rates_FlightHoursDefaultsToFaaSurvey()
// no exposureSource → rows have exposure_source "faa-ga-survey"
[Fact] public async Task Rates_GaByClass_RollsPart91And135IntoGeneralAviation()
// seeded (light_aircraft, general_aviation) and (turboprop, general_aviation) → light_aircraft numerator from E1 (1), turboprop from E3 AC2 (1)
// and E5 part_135_scheduled turboprop unmatched "no_exposure"
```

- [ ] **Step 2: Run** — expected FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — expected PASS.
- [ ] **Step 5: Commit** `feat: rates scoped to US events; flight-hours default source`.

---

### Task 7: `/api/events/sample`

**Files:**
- Create: `src/TransportAnalyzer.Api/Query/SampleQuery.cs`
- Modify: `src/TransportAnalyzer.Api/Endpoints/EventEndpoints.cs`
- Test: `tests/TransportAnalyzer.Tests/SampleTests.cs`

**Interfaces:**
- `GET /api/events/sample?from=&to=&n=&seed=&f.<dim>=…` → `{ columns, rows, total, n, seed }`.
  - One row per vehicle, joined to its event (vehicle grain).
  - Columns: every whitelisted dimension from `Dimensions.All` in their defined order, then `occurred_on`, then the vehicle casualty measures (`occupant_*`, `nonoccupant_*`).
  - Reuses `AggregateQuery`'s filter parsing and validation: same 400s, and the same `parameter` names.
  - `n` defaults to 10,000; `n` < 1 or > 100,000 → 400 with parameter `n`.
  - `seed` is an integer, default 0.
  - Sampling must be reproducible for the same seed and data: order by `md5(vehicle_id::text || @seed)`, then `limit @n`.
  - `total` is the count of matching vehicles.

- [ ] **Step 1: Write failing tests** (FARS fixture plus NTSB fixture):

```csharp
[Fact] public async Task Sample_SameSeed_SameRows()
[Fact] public async Task Sample_DifferentSeed_DifferentOrder()       // with n=all rows, row sets equal, order differs
[Fact] public async Task Sample_RespectsFiltersAndN()                // f.mode=aviation, n=3 → 3 rows, all aviation; total 7
[Fact] public async Task Sample_NOutOfRange_Returns400()             // n=0 and n=100001 → parameter "n"
[Fact] public async Task Sample_UnknownFilter_Returns400()           // same as aggregate
```

- [ ] **Step 2: Run** — expected FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — expected PASS.
- [ ] **Step 5: Commit** `feat: reproducible event sample endpoint`.

---

### Task 8: Quartz schedules

**Files:**
- Create: `src/TransportAnalyzer.Api/Scheduling/IngestJob.cs`, `Scheduling/ScheduleRegistration.cs`
- Modify: `src/TransportAnalyzer.Api/Program.cs`, `appsettings.json`
- Test: `tests/TransportAnalyzer.Tests/ScheduleTests.cs`

**Interfaces:**
- Config: `Sources:{sourceId}:Schedule` holds a Quartz cron expression and is optional. Sources without one are never scheduled. Defaults in `appsettings.json`:
  - `fars`: `0 0 3 1 * ?` (03:00 on the 1st of each month)
  - `ntsb`: `0 0 4 2 * ?`
  - `faa-ga-survey`: `0 0 5 1 3 ?` (yearly)
  - BTS sources: none, since BTS files are dropped in by hand.
- Each scheduled run calls `IngestQueue.TryEnqueueAsync(sourceId, new IngestParameters(null))`. A 409-equivalent result is logged at information level and skipped.
- An invalid cron expression fails startup with a message naming the source and the expression.
- Quartz is registered only inside the connection-string block. In tests, `ApiFactory` sets `Scheduling:Enabled=false` by default.

- [ ] **Step 1: Write failing tests:**

```csharp
[Fact] public void Registration_CreatesTriggerPerConfiguredSource()     // fars, ntsb, faa-ga-survey; no bts-*
[Fact] public void Registration_InvalidCron_ThrowsNamingSource()
[Fact] public async Task IngestJob_SourceAlreadyRunning_SkipsWithoutThrowing()
```

- [ ] **Step 2: Run** — expected FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — expected PASS.
- [ ] **Step 5: Commit** `feat: Quartz ingest schedules from config`.

---

### Task 9: README and real-data verification

**Files:**
- Modify: `README.md`
- Create: `docs/verification/plan-2.md`

No new code unless a defect is found. A defect gets a failing test in the owning task's test file first.

- [ ] **Step 1: README.** Add `ntsb` and `faa-ga-survey` to the source list. Explain schedules and how to change or disable them. Explain that the FAA URL changes every survey year. Explain that aviation rates by class need `groupBy=vehicle_class,regulation_class` (general-aviation exposure exists only under `general_aviation`). Add one `/api/events/sample` example.
- [ ] **Step 2: Ingest** `ntsb` (all years) and `faa-ga-survey` in the running stack. FARS 2019–2023 and BTS are already loaded or should be re-ingested. All runs `succeeded`.
- [ ] **Step 3: NTSB totals.** For each `ev_year` 2019–2023 and US events only, check that the API `fatalities` equals the staged `Σ inj_tot_f + Σ inj_f_grnd` (`psql` on `stg_ntsb.events`). Any mismatch is a defect.
- [ ] **Step 4: Unknowns.** Record the run's `unknown:*` counts and the `Unmapped` keys above 50 as proposed `ntsb.csv` additions in `plan-2.md`. Do not edit the mapping file; the user reviews it.
- [ ] **Step 5: Exposure.** Record `faa-ga-survey` 2019–2023 for `part_91`, `part_135`, `part_137`, and each vehicle class under `general_aviation`.
- [ ] **Step 6: The first question.** Record:
  - `/api/rates?measure=occupant_fatalities&metric=passenger_miles&fromYear=2019&toYear=2023&groupBy=regulation_class`, giving Part 121 against road per 100M passenger-miles, with `unmatched` and `unattributed`.
  - `metric=flight_hours&groupBy=vehicle_class,regulation_class`, giving GA rates per 100k flight hours by class.

  Compare the GA total fatal-accident rate to a published NTSB/FAA figure for the same year if one can be retrieved normally; otherwise record "not retrieved".
- [ ] **Step 7: Commit** `docs: plan 2 real-data verification`.
