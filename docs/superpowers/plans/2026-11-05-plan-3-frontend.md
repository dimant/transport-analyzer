# Plan 3: Frontend shell, stats library (crosstab, rate, heatmap), first experiment — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A browser app, served by the ASP.NET host at `/`, that queries `/api/events/aggregate`, `/api/events/sample` and `/api/rates`, runs a method from a framework-free TypeScript statistics library on the resulting table (crosstab, rate with Garwood intervals, heatmap), and renders the result. Built-in experiments answer the first question: airlines vs road per passenger-mile, and general aviation by aircraft class per flight hour.

**Architecture:** `web/` is a Vite + Svelte 5 + TypeScript app. `web/src/stats/` is plain TypeScript with no imports outside itself: a columnar `Table`, the `Method` contract, distributions, and one module per method. Each method returns plain-data `RenderSpec`s. `web/src/render/` turns those into Observable Plot figures and HTML tables. `web/src/api/` builds query URLs and turns responses into `Table`s. Svelte components in `web/src/ui/` generate the method panel from each method's declared inputs and params. `vite build` writes into `src/TransportAnalyzer.Api/wwwroot/`, which the existing `UseDefaultFiles`/`UseStaticFiles` serve. The Dockerfile gains a Node build stage.

**Tech Stack:** Node 22, Vite, Svelte 5 (runes), TypeScript (strict), `@observablehq/plot`, Vitest with jsdom, `@testing-library/svelte`, `svelte-check`. No other runtime dependencies.

**Spec:** `docs/superpowers/specs/2026-11-04-experiment-platform-design.md` sections 6–8 (milestones 6 and 7). Inputs: `docs/superpowers/plans/2026-11-04-plan-1-carryover.md` (the plan 3 and "Decided by the user" items), `README.md` "Query API" (response shapes, `join_level` and `unmatched.reason` values), `docs/verification/plan-1.md` and `plan-2.md` (reference numbers).

## Global Constraints

Plans 1 and 2 Global Constraints still apply to anything touched on the .NET side. In addition:

- Work directly on `main` (user decision). Commit messages end with the session's `Co-Authored-By` / `Claude-Session` trailers.
- `web/src/stats/**` imports nothing outside `web/src/stats/` (no Svelte, no Plot, no DOM types used). A test enforces this.
- The server does the counting; the browser does the statistics. No new server endpoints in this plan.
- Every rate shown has its interval, its `exposure_source` and its `provenance` beside it (spec anti-goal). A rate row with a null interval is a bug.
- Garwood interval: `lo = Gamma⁻¹(α/2; k)`, `hi = Gamma⁻¹(1−α/2; k+1)` (shape k, scale 1), `lo = 0` when `k = 0`; rate interval = count interval × `per / denominator`. The denominator is treated as fixed.
- `/api/rates` denominators are in raw units (passenger-miles, vehicle-miles, hours), not millions.
- Null category values display as `(none)`, never `null` or an empty string.
- Exact versions in `web/package.json` (`npm install -E`); `web/package-lock.json` committed; `node_modules` and the built `wwwroot/` never committed.
- `localStorage` access is wrapped in try/catch; the app works with storage unavailable.
- Part 121 rates include all-cargo flights (user decision 2026-11-05). The Part 121 presets say so in their note.

## Review Focus

1. **Zero deaths.** Part 121 has 0 occupant deaths in 2020–2023. The rate must be 0 with a positive upper bound (k = 0 → hi = 3.689 × per / denominator at 95%), and on a log-scale plot the row must still be drawn. Tests: `rate_zeroNumerator_hasUpperBound` (Task 5), `intervals_logScale_zeroRate_isClampedNotDropped` (Task 8).
2. **Rates spanning four orders of magnitude** (0.000128 vs 0.618) on one plot. Log scale is the default for rate plots. Test: `rate_render_defaultsToLogScale` (Task 5).
3. **Null categories.** `/api/rates` rows joined at `mode` level have null `vehicle_class` / `regulation_class`; aggregate groups can be null. They show as `(none)` and group together. Tests: `fromRows_nullCategorical_isNull`, `crosstab_nullCategory_labelledNone`, `rate_nullGroup_labelledNone` (Tasks 2, 4, 5).
4. **Stale bindings.** Changing the query changes the columns; a saved experiment can name columns that no longer exist. The method panel reports the missing column and does not run; nothing throws. Tests: `validateBindings_missingColumn_reportsError`, `validateBindings_wrongType_reportsError` (Task 2).
5. **Server errors and broken storage.** A 400 from the API (e.g. `groupBy=make` at event grain) shows the server's message; corrupt or throwing `localStorage` leaves the presets usable. Tests: `runQuery_400_throwsApiErrorWithBody` (Task 7), `loadExperiments_corruptJson_returnsEmpty`, `loadExperiments_throwingStorage_returnsEmpty` (Task 10).

---

## File Structure

```
.dockerignore                         new: web/node_modules, **/bin, **/obj, .superpowers, data/inbox/**
.gitignore                            + web/node_modules/, src/TransportAnalyzer.Api/wwwroot/
.github/workflows/ci.yml              + job web: npm ci, npm run check, npm test, npm run build
Dockerfile                            + node:22-bookworm-slim stage building web/ into wwwroot
src/TransportAnalyzer.Api/wwwroot/index.html   deleted (now build output)
data/vocabulary.csv                   real descriptions
web/package.json, package-lock.json, vite.config.ts, tsconfig.json, svelte.config.js, index.html
web/src/main.ts, App.svelte, app.css
web/src/stats/table.ts                Table, Column, ColumnType, fromRows, column, groupKey, categoryLabel, naturalCompare
web/src/stats/method.ts               Method, InputSpec, ParamSpec, Bindings, RenderSpec types; validateBindings, defaultParams
web/src/stats/distributions.ts        regularizedGammaP, gammaQuantile, garwood
web/src/stats/crosstab.ts             crosstab method, crosstabCells
web/src/stats/rate.ts                 rate method
web/src/stats/heatmap.ts              heatmap method
web/src/stats/registry.ts             methods list, methodById
web/src/stats/*.test.ts               unit tests; stats-imports.test.ts (no framework imports)
web/src/api/types.ts                  Query, Source, Vocabulary, QueryResult, Notice
web/src/api/client.ts                 buildUrl, runQuery, getSources, getVocabulary, ApiError
web/src/api/columns.ts                COLUMN_TYPES, EVENT_DIMENSIONS, VEHICLE_DIMENSIONS, VOCAB_ATTRIBUTE, COLUMN_HELP
web/src/api/glossary.ts               buildGlossary
web/src/render/format.ts              formatValue
web/src/render/plot.ts                renderPlot (HeatmapSpec | IntervalSpec → Element)
web/src/ui/QueryPanel.svelte, DataPreview.svelte, MethodPanel.svelte, ResultPanel.svelte, TableView.svelte, ExperimentBar.svelte
web/src/experiments/storage.ts        loadExperiments, saveExperiment, deleteExperiment
web/src/experiments/presets.ts        PRESETS
web/src/experiments/presets.live.test.ts   runs presets against a live API when TA_API is set
docs/verification/plan-3.md
README.md                             + Frontend section
```

---

### Task 1: Web scaffold and build integration

**Files:**
- Create: `web/` (package.json, vite.config.ts, tsconfig.json, svelte.config.js, index.html, src/main.ts, src/App.svelte, src/app.css, src/smoke.test.ts), `.dockerignore`
- Modify: `.gitignore`, `Dockerfile`, `.github/workflows/ci.yml`, `README.md`
- Delete: `src/TransportAnalyzer.Api/wwwroot/index.html`

**Interfaces:**
- Produces: npm scripts `dev` (Vite, proxies `/api` to `http://localhost:8080`), `build` (outDir `../src/TransportAnalyzer.Api/wwwroot`, `emptyOutDir: true`), `test` (`vitest run`), `check` (`svelte-check --fail-on-warnings`). `App.svelte` renders `<main id="app-root"><h1>Transport Analyzer</h1></main>` for now. Vitest default environment `jsdom`.

- [ ] **Step 1:** Scaffold with Vite's `svelte-ts` template into `web/`, then `npm install -E -D vitest jsdom @testing-library/svelte svelte-check` and `npm install -E @observablehq/plot`. Remove template demo files.
- [ ] **Step 2: Write `web/src/smoke.test.ts`:** `App_rendersTitle` — renders `App` with `@testing-library/svelte` and asserts `getByRole('heading', {name: 'Transport Analyzer'})` exists.
- [ ] **Step 3:** Run `npm test` (PASS), `npm run check` (0 errors, 0 warnings), `npm run build`. Expected: `src/TransportAnalyzer.Api/wwwroot/index.html` exists and references a hashed `assets/*.js`.
- [ ] **Step 4:** Ignore `web/node_modules/` and `src/TransportAnalyzer.Api/wwwroot/`; delete the placeholder `index.html` with `git rm`. Add `.dockerignore`. In the Dockerfile add a first stage `FROM node:22-bookworm-slim AS web` that copies `web/package*.json`, runs `npm ci`, copies `web/`, runs `npm run build` (its output lands in `/src/src/TransportAnalyzer.Api/wwwroot`); in the sdk stage `COPY --from=web /src/src/TransportAnalyzer.Api/wwwroot src/TransportAnalyzer.Api/wwwroot` after `COPY . .`. Add CI job `web` (`actions/setup-node@v4`, node 22, `working-directory: web`, cache npm): `npm ci`, `npm run check`, `npm test`, `npm run build`.
- [ ] **Step 5: Verify:** `docker compose up -d --build`, then `curl -s localhost:8080/ | grep -c 'assets/'` ≥ 1 and `curl -s localhost:8080/api/health` → `{"status":"ok"}`. `dotnet test` still 203 passed / 1 skipped.
- [ ] **Step 6:** README: "Frontend" section — `cd web && npm ci && npm run dev` (needs the API on 8080), `npm run build` writes into the API's `wwwroot`, Docker builds it automatically. Commit.

### Task 2: Stats core — table and method contract

**Files:**
- Create: `web/src/stats/table.ts`, `web/src/stats/method.ts`, `web/src/stats/table.test.ts`, `web/src/stats/method.test.ts`, `web/src/stats/stats-imports.test.ts`

**Interfaces:**
- Produces (`table.ts`):
  ```ts
  export type ColumnType = 'categorical' | 'count' | 'continuous' | 'exposure' | 'weight';
  export type CategoricalColumn = { name: string; type: 'categorical'; values: (string | null)[] };
  export type NumericColumn = { name: string; type: Exclude<ColumnType, 'categorical'>; values: Float64Array }; // NaN = null
  export type Column = CategoricalColumn | NumericColumn;
  export interface Table { columns: Column[]; length: number; meta: Record<string, string> }
  export function fromRows(columns: string[], rows: unknown[][], types: Record<string, ColumnType>, meta?: Record<string, string>): Table;
  export function column(t: Table, name: string): Column;            // throws Error(`no column "${name}"`)
  export function categoryLabel(v: string | null): string;           // null → '(none)'
  export function groupKey(t: Table, names: string[], row: number): string; // labels joined with ' · '
  export function naturalCompare(a: string, b: string): number;      // numeric when both parse as numbers, else localeCompare; '(none)' last
  ```
  Columns missing from `types` are `categorical`. Categorical cells: `null`/`undefined` → `null`, anything else `String(v)`. Numeric cells: `null`/non-finite → `NaN`.
- Produces (`method.ts`):
  ```ts
  export interface InputSpec { key: string; label: string; types: ColumnType[]; multiple?: boolean; optional?: boolean }
  export type ParamSpec =
    | { key: string; label: string; kind: 'number'; default: number }
    | { key: string; label: string; kind: 'boolean'; default: boolean }
    | { key: string; label: string; kind: 'select'; options: string[]; default: string };
  export type Bindings = Record<string, string | string[] | undefined>;
  export type Cell = string | number | null;
  export interface TableSpec { kind: 'table'; columns: { key: string; label: string; format: 'integer' | 'number' | 'rate' | 'percent' | 'text' }[]; rows: Record<string, Cell>[] }
  export interface HeatmapSpec { kind: 'heatmap'; x: string; y: string; fill: string; fillLabel: string; logScale: boolean; data: Record<string, Cell>[] }
  export interface IntervalSpec { kind: 'intervals'; x: string; y: string; lo: string; hi: string; series?: string; yLabel: string; logScale: boolean; data: Record<string, Cell>[] }
  export type RenderSpec = TableSpec | HeatmapSpec | IntervalSpec;
  export interface Method<P extends Record<string, unknown> = Record<string, unknown>, R = unknown> {
    id: string; name: string; description: string;
    inputs: InputSpec[]; params: ParamSpec[];
    run(table: Table, bindings: Bindings, params: P): R;
    render(result: R): RenderSpec[];
  }
  export function defaultParams(m: Method): Record<string, unknown>;
  export function validateBindings(m: Method, table: Table, b: Bindings): string[]; // [] when valid
  ```
  `validateBindings` messages: `"<label>: choose a column"` (required input unbound or empty array), `"<label>: column \"x\" is not in the table"`, `"<label>: column \"x\" is <type>, needs one of <types>"`, `"<label>: takes one column"` (array bound to a non-multiple input).

- [ ] **Step 1: Write failing tests:**
  - `fromRows_typesColumns` — `fromRows(['mode','numerator'], [['aviation', 3], ['highway', 5]], {numerator: 'count'})`: `mode` categorical `['aviation','highway']`, `numerator` `Float64Array [3,5]`, `length` 2.
  - `fromRows_nullCategorical_isNull` — `[null]` and `[undefined]` → `null`; `categoryLabel(null)` → `'(none)'`; year `2022` → `'2022'`.
  - `fromRows_nullNumeric_isNaN`.
  - `column_unknown_throws` — message `no column "x"`.
  - `naturalCompare_sortsNumbersNumerically` — `['10','9','(none)','2']` sorts to `['2','9','10','(none)']`; `['b','a']` → `['a','b']`.
  - `validateBindings_missingColumn_reportsError`, `validateBindings_wrongType_reportsError`, `validateBindings_requiredUnbound_reportsError`, `validateBindings_optionalUnbound_ok`, `validateBindings_arrayForSingle_reportsError` — use a fake method with inputs `{key:'n', label:'Numerator', types:['count']}`, `{key:'g', label:'Groups', types:['categorical'], multiple:true, optional:true}`; assert the exact messages above.
  - `defaultParams_collectsDefaults`.
  - `stats_hasNoFrameworkImports` (`stats-imports.test.ts`) — reads every non-test `web/src/stats/*.ts` with `node:fs` and fails if any `import`/`export ... from '<x>'` has `x` not starting with `./`. Test files (`*.test.ts`) may also import `vitest` and `node:*`.
- [ ] **Step 2:** Run `npm test` — the new tests fail.
- [ ] **Step 3:** Implement `table.ts` and `method.ts`.
- [ ] **Step 4:** `npm test`, `npm run check` pass. Commit.

### Task 3: Distributions and the Garwood interval

**Files:**
- Create: `web/src/stats/distributions.ts`, `web/src/stats/distributions.test.ts`

**Interfaces:**
- Produces: `regularizedGammaP(a: number, x: number): number`; `gammaQuantile(p: number, shape: number): number` (scale 1; `p` in (0,1)); `garwood(k: number, confidence = 0.95): { lo: number; hi: number }`. `garwood` throws `RangeError` for negative or non-integer `k` and for `confidence` outside (0,1).

- [ ] **Step 1: Write failing tests** (relative tolerance 1e-6 unless noted; reference values from `scipy.stats.chi2.ppf(q, 2k)/2`):
  - `garwood_95` — k=0 → {0, 3.688879}; k=1 → {0.025318, 5.571643}; k=4 → {1.089865, 10.241589}; k=10 → {4.795389, 18.390356}; k=100 → {81.363991, 121.626794}.
  - `garwood_90` — k=3, confidence 0.9 → {0.817691, 7.753657}.
  - `garwood_large_k` — k=33679 (2022 road deaths): `lo < 33679 < hi`, and `hi − lo` within 1% of `2 × 1.959964 × √33679`.
  - `gammaQuantile_invertsP` — for shapes 0.5, 1, 7.5, 250 and p 0.001, 0.5, 0.999: `regularizedGammaP(shape, gammaQuantile(p, shape))` within 1e-9 of p.
  - `regularizedGammaP_exponential` — shape 1: equals `1 − e^{−x}` for x 0.1, 1, 10.
  - `garwood_rejectsBadInput` — k = −1, k = 1.5, confidence 1 throw `RangeError`.
- [ ] **Step 2:** Run — fails.
- [ ] **Step 3:** Implement: `regularizedGammaP` by the series for `x < a + 1` and Lentz continued fraction for Q otherwise, with a Lanczos `logGamma`; `gammaQuantile` by a Wilson–Hilferty start refined with Newton steps on `P`, falling back to bisection when a step leaves the bracket.
- [ ] **Step 4:** Tests pass. Commit.

### Task 4: `crosstab`

**Files:**
- Create: `web/src/stats/crosstab.ts`, `web/src/stats/crosstab.test.ts`, `web/src/stats/registry.ts`

**Interfaces:**
- Consumes: Task 2 types.
- Produces:
  ```ts
  export interface CrosstabCells { rowLabels: string[]; colLabels: string[]; cells: number[][]; rowTotals: number[]; colTotals: number[]; total: number }
  export function crosstabCells(t: Table, row: string, col: string | undefined, value: string | undefined): CrosstabCells;
  export const crosstab: Method<{ normalize: 'none' | 'row' | 'column' | 'total'; margins: boolean }, CrosstabCells & { normalize: string; margins: boolean }>;
  ```
  Inputs: `row` (categorical), `col` (categorical, optional), `value` (count | continuous | exposure, optional; unbound → each row counts 1). NaN values are skipped. Labels via `categoryLabel`, sorted with `naturalCompare`. With `col` unbound, `colLabels = ['total']`. Params: `normalize` select default `'none'`, `margins` boolean default `true`. `render` → one `TableSpec`: first column `row` label (format text), one column per colLabel, plus `Total` column and a `Total` row when `margins`; format `integer` for `none` with a count value, `number` for continuous, `percent` otherwise.
  `registry.ts`: `export const methods: Method<any, any>[]` and `export function methodById(id: string): Method<any, any> | undefined`. Tasks 5 and 6 append to `methods`.

- [ ] **Step 1: Write failing tests** on a 5-row fixture `mode/vehicle_class/occupant_fatalities`: `[aviation, airliner, 3], [aviation, helicopter, 101], [highway, bus, 19], [highway, bus, 1], [highway, null, 7]`:
  - `crosstab_sumsValueByRowAndColumn` — row mode, col vehicle_class: highway/bus = 20, aviation/helicopter = 101, total 131.
  - `crosstab_noValue_countsRows` — highway/bus = 2.
  - `crosstab_nullCategory_labelledNone` — `colLabels` contains `'(none)'`, last.
  - `crosstab_normalizeRow_rowsSumToOne`.
  - `crosstab_render_marginsAddTotalRowAndColumn` — TableSpec has a last row whose first cell is `'Total'` and a `Total` column; with `margins: false`, neither.
  - `registry_findsCrosstab` — `methodById('crosstab')` is `crosstab`.
- [ ] **Step 2–4:** Run (fail), implement, run (pass). Commit.

### Task 5: `rate`

**Files:**
- Create: `web/src/stats/rate.ts`, `web/src/stats/rate.test.ts`; Modify: `web/src/stats/registry.ts`

**Interfaces:**
- Consumes: `garwood` (Task 3), Task 2 types.
- Produces:
  ```ts
  export interface RateRow { key: string; groups: Record<string, string>; numerator: number; denominator: number; rate: number | null; lo: number | null; hi: number | null; carry: Record<string, string> }
  export interface RateResult { groupColumns: string[]; per: number; confidence: number; logScale: boolean; numeratorLabel: string; denominatorLabel: string; rows: RateRow[]; warnings: string[] }
  export const rate: Method<{ per: number; confidence: number; logScale: boolean }, RateResult>;
  ```
  Inputs: `numerator` (count), `denominator` (exposure), `groups` (categorical, multiple, optional), `carry` (categorical, multiple, optional). Rows with the same group key are pooled: numerators and denominators summed (pooling over years is choosing groups without `year`). A `carry` column keeps its value when all pooled rows agree, else the distinct values sorted and joined with `' + '`. `rate = numerator / denominator × per`; `lo/hi = garwood(numerator, confidence) × per / denominator`. Rows with a NaN numerator or denominator are skipped and counted in a warning `"<n> rows skipped: missing numerator or denominator"`; a pooled denominator ≤ 0 gives `rate/lo/hi = null` and a warning naming the group. A non-integer numerator gives a warning and is rounded for the interval only. Params: `per` number default `100000000`, `confidence` number default `0.95`, `logScale` boolean default `true`. `numeratorLabel` / `denominatorLabel` come from `table.meta.measure` / `table.meta.metric` when present, else the bound column names. Rows sorted by group labels with `naturalCompare`.
  `render` → `[TableSpec, IntervalSpec]`. Table columns: each group column (text), `numerator` (integer), `denominator` (number), `rate`, `lo`, `hi` (rate), each carry column (text). IntervalSpec: when a group column is named `year` or `period`, `x` is that column and `series` is the remaining groups' joined label (omitted when none); otherwise `x` is the joined label of all groups (`'all'` with no groups). `yLabel = "<numeratorLabel> per <formatPer(per)> <denominatorLabel>"`, where `export function formatPer(per: number): string` gives `1e3 → '1k'`, `1e5 → '100k'`, `1e6 → '1M'`, `1e8 → '100M'`, `1e9 → '1B'`, otherwise `per.toLocaleString('en-US')`.

- [ ] **Step 1: Write failing tests** with a fixture shaped like `/api/rates` rows (`mode, year, regulation_class, numerator, denominator, join_level, exposure_source, provenance`):
  - `rate_computesRateAndGarwoodInterval` — numerator 4, denominator 3.1e12, per 1e8: rate `4/3.1e12×1e8`, lo `1.089865×1e8/3.1e12`, hi `10.241589×1e8/3.1e12` (rel 1e-6).
  - `rate_zeroNumerator_hasUpperBound` — numerator 0: rate 0, lo 0, hi `3.688879 × per / denominator`.
  - `rate_poolsRowsWithSameGroupKey` — groups `[mode, regulation_class]` over 5 years: numerator and denominator are the sums; the Part 121 2019–2023 check: numerators `[4,0,0,0,0]`, denominators `[1.1e12, 0.5e12, 0.7e12, 0.71e12, 0.12e12]` pools to 4 / 3.13e12.
  - `rate_carryColumns_joinDistinctValues` — `join_level` `regulation_class` in all rows stays; `provenance` `measured` + `survey_estimate` becomes `'measured + survey_estimate'`.
  - `rate_nullGroup_labelledNone` — highway row with null `regulation_class` has `groups.regulation_class === '(none)'`.
  - `rate_zeroDenominator_nullRateWithWarning`.
  - `rate_missingValues_skippedWithWarning`.
  - `rate_render_yearIsXAxis` — groups `[mode, regulation_class, year]`: IntervalSpec `x === 'year'`, `series` present, data rows have the series label `'aviation · part_121'`.
  - `rate_render_defaultsToLogScale` — `defaultParams(rate).logScale === true` and the spec carries it.
  - `rate_usesTableMetaLabels` — `meta {measure: 'occupant_fatalities', metric: 'passenger_miles'}`, per 1e8 → `yLabel === 'occupant_fatalities per 100M passenger_miles'`.
- [ ] **Step 2–4:** Run (fail), implement, run (pass). Add to registry. Commit.

### Task 6: `heatmap`

**Files:**
- Create: `web/src/stats/heatmap.ts`, `web/src/stats/heatmap.test.ts`; Modify: `web/src/stats/registry.ts`

**Interfaces:**
- Consumes: `crosstabCells` (Task 4).
- Produces: `export const heatmap: Method<{ per: number; normalize: 'none' | 'row' | 'column'; logScale: boolean }, HeatmapResult>` with `HeatmapResult { x: string; y: string; fillLabel: string; logScale: boolean; cells: { x: string; y: string; value: number | null }[] }`. Inputs: `x` (categorical), `y` (categorical), `value` (count | continuous | exposure, optional; unbound counts rows), `denominator` (exposure, optional). With `denominator` bound, cell value = Σvalue / Σdenominator × `per` (null when Σdenominator ≤ 0) and `normalize` is ignored. Params: `per` default `100000000`, `normalize` default `'none'`, `logScale` default `false`. Only cells with at least one source row are emitted. `render` → `[HeatmapSpec]`, `fill: 'value'`, `fillLabel` = value column name, or `"<value> per <per> <denominator>"` for rates.

- [ ] **Step 1: Write failing tests:**
  - `heatmap_countCells` — x period, y vehicle_class, value occupant_fatalities: cell (2022, helicopter) = 101; no cell for a pair absent from the input.
  - `heatmap_rateCells` — two rows for (2022, light_duty) numerators 10 and 20, denominators 1e9 and 2e9, per 1e8 → value 1.
  - `heatmap_zeroDenominator_isNull`.
  - `heatmap_normalizeRow_rowSumsToOne`.
  - `heatmap_render_spec` — kind `'heatmap'`, `x`/`y` are the bound column names, `fillLabel` correct for both modes.
- [ ] **Step 2–4:** Run (fail), implement, run (pass). Add to registry. Commit.

### Task 7: API client, column metadata, glossary

**Files:**
- Create: `web/src/api/types.ts`, `client.ts`, `columns.ts`, `glossary.ts`, and `client.test.ts`, `glossary.test.ts`

**Interfaces:**
- Consumes: `fromRows`, `Table`, `ColumnType` (Task 2).
- Produces (`types.ts`):
  ```ts
  export type Filters = Record<string, string[]>;
  export type Query =
    | { endpoint: 'aggregate'; from: string; to: string; grain: 'event' | 'vehicle'; bucket: 'none' | 'year' | 'quarter' | 'month'; groupBy: string[]; filters: Filters }
    | { endpoint: 'sample'; from: string; to: string; n: number; seed: number; filters: Filters }
    | { endpoint: 'rates'; measure: 'occupant_fatalities' | 'societal_fatalities'; metric: 'passenger_miles' | 'vehicle_miles' | 'flight_hours'; fromYear: number; toYear: number; groupBy: ('vehicle_class' | 'regulation_class')[]; exposureSource?: string };
  export interface Source { sourceId: string; name: string; mode: string; kind: string; url: string; definitions: string; caveats: string; dataCurrentThrough: string | null; lastRun: { status: string; finishedAt: string | null } | null }
  export interface VocabEntry { code: string; label: string; description: string; parentCode: string | null }
  export type Vocabulary = Record<string, VocabEntry[]>;
  export interface Notice { kind: 'scope' | 'unmatched' | 'unattributed' | 'sample'; text: string; table?: Table }
  export interface QueryResult { table: Table; notices: Notice[] }
  ```
- Produces (`client.ts`): `buildUrl(q: Query): string` (path + query string, relative `/api/...`; empty `groupBy` and empty filter lists omitted; filters as `f.<dim>=a,b`; `groupBy` comma-joined); `class ApiError extends Error { status: number; body: string }`; `runQuery(q: Query, fetchFn = fetch): Promise<QueryResult>`; `getSources(fetchFn = fetch): Promise<Source[]>`; `getVocabulary(fetchFn = fetch): Promise<Vocabulary>`. A non-2xx response throws `ApiError` whose `message` is the JSON body's `error` field (the API returns `{"error": "...", "parameter": "..."}` on 400), else the response text. `runQuery` sets `table.meta` to `{endpoint, measure, metric}` (rates) or `{endpoint, grain}` (aggregate/sample). Notices: rates → `scope` ("US events only"), `unmatched` (when non-empty; its own `Table` with columns `mode, year, vehicle_class, regulation_class, numerator, reason`), `unattributed` (when non-empty; `mode, year, fatalities`); sample → `sample` ("n of total matching rows, seed s").
- Produces (`columns.ts`): `COLUMN_TYPES: Record<string, ColumnType>` — `count`: `vehicles, events, numerator`, every `*_fatalities`, `*_injuries`, `fatalities`; `exposure`: `denominator, value`; `continuous`: `occupants, crew_count, vehicle_age_years, year_built, standard_error`; everything else categorical. `EVENT_DIMENSIONS` and `VEHICLE_DIMENSIONS` (the README "Query API" lists; vehicle = event + vehicle-only). `VOCAB_ATTRIBUTE: Record<string, string>` (`mode, vehicle_class, operator_type, regulation_class, maintenance_regime` → themselves, `vehicle_class_parent` → `vehicle_class`). `COLUMN_HELP: Record<string, string>` — one sentence each for `numerator`, `denominator`, `join_level` (lists the README values), `exposure_source`, `provenance`, `period`, `rate`, `lo`, `hi`.
- Produces (`glossary.ts`): `buildGlossary(vocab: Vocabulary, sources: Source[]): { describe(column: string, value: string | null): string | undefined; help(column: string): string | undefined }`. `describe`: vocabulary columns → `"<label>: <description>"`; `source_id` / `exposure_source` → `"<name>. <definitions> Caveats: <caveats> Data current through <date>."`; others undefined. `help` → `COLUMN_HELP`.

- [ ] **Step 1: Write failing tests** with a fake `fetchFn`:
  - `buildUrl_aggregate` — `{grain:'vehicle', from:'2022-01-01', to:'2022-12-31', bucket:'year', groupBy:['vehicle_class'], filters:{mode:['aviation'], country:['US']}}` → `/api/events/aggregate?grain=vehicle&from=2022-01-01&to=2022-12-31&bucket=year&groupBy=vehicle_class&f.mode=aviation&f.country=US`.
  - `buildUrl_rates_omitsEmptyGroupBy`, `buildUrl_sample`.
  - `runQuery_rates_buildsTableAndNotices` — fixture `web/src/api/fixtures/rates-2022.json` (write it by hand):
    `{"measure":"occupant_fatalities","metric":"passenger_miles","scope":{"country":"US"},"columns":["mode","year","vehicle_class","regulation_class","numerator","denominator","join_level","exposure_source","provenance"],"rows":[["aviation",2022,null,"part_121",0,708960150712,"regulation_class","bts-passenger-miles","measured"],["highway",2022,null,null,33679,5027965006953.494,"mode","bts-passenger-miles","survey_estimate"]],"unmatched":[{"mode":"aviation","year":2022,"vehicleClass":null,"regulationClass":"part_91","numerator":295,"reason":"no_exposure"}],"unattributed":[{"mode":"highway","year":2022,"fatalities":48}]}`.
    Table has 2 rows, `numerator` is `count`, `denominator` is `exposure`, `year` categorical `'2022'`; notices kinds `['scope','unmatched','unattributed']`, the unmatched table has `regulation_class` `'part_91'`; meta `metric === 'passenger_miles'`.
  - `runQuery_400_throwsApiErrorWithBody` — 400 with `{"error":"dimension 'make' needs grain=vehicle","parameter":"groupBy"}` → `ApiError`, `status 400`, `message "dimension 'make' needs grain=vehicle"`.
  - `glossary_describesVocabAndSources` — `describe('vehicle_class_parent','light_duty')` uses the vehicle_class entry; `describe('exposure_source','bts-passenger-miles')` contains the definitions and date; `describe('make','Subaru')` undefined.
- [ ] **Step 2–4:** Run (fail), implement, run (pass). Commit.

### Task 8: Render layer

**Files:**
- Create: `web/src/render/format.ts`, `plot.ts`, `format.test.ts`, `plot.test.ts`, `web/src/ui/TableView.svelte`, `web/src/ui/TableView.test.ts`

**Interfaces:**
- Consumes: `RenderSpec` types (Task 2), glossary (Task 7).
- Produces: `formatValue(v: Cell, format: TableSpec['columns'][number]['format']): string` — null/NaN → `'—'`; `integer` → `en-US` grouping, no decimals; `number` → `en-US` grouping, at most 1 decimal; `rate` → 3 significant digits without exponent for |v| ≥ 1e-6 (`0.000128`, `0.618`, `26.3`, `1,180`), exponent below; `percent` → one decimal and `%`; `text` → `String(v)`.
  `renderPlot(spec: HeatmapSpec | IntervalSpec, opts: { width: number }): Element` using Observable Plot. Heatmap: `Plot.cell` with fill = value, `Plot.text` with the formatted value, color scale `log` when `logScale` (null cells drawn as a hatched/grey cell, not dropped), legend on. Intervals: `Plot.ruleX` from lo to hi plus `Plot.dot` at the rate, x categorical (`point`/`band` scale), color by `series`, y `log` when `logScale`. On a log scale, values ≤ 0 (rate 0, lo 0) are clamped to one tenth of the smallest positive `lo/y/hi` in the data, and clamped dots are drawn hollow; a text note under the plot says `"values of 0 drawn at the floor"`. Every mark has a `title` channel with the formatted row.
  `TableView.svelte` props: `spec: TableSpec`, `glossary?` — renders a `<table>` with `title` attributes: header `title` from `glossary.help(key)`, cell `title` from `glossary.describe(key, value)`. Numeric cells right-aligned.
- [ ] **Step 1: Write failing tests:**
  - `formatValue_cases` — the examples above, plus `formatValue(null,'rate') === '—'`.
  - `heatmap_rendersOneCellPerDatum` — 6 data rows → the SVG contains 6 cell `rect`s (query `[aria-label="cell"] rect`).
  - `intervals_rendersRuleAndDotPerRow`.
  - `intervals_logScale_zeroRate_isClampedNotDropped` — rows with y 0 and 0.618: two dots rendered, and the note text present.
  - `TableView_showsHelpAndDescriptions` — header `numerator` has a `title`; a `vehicle_class` cell has the vocabulary description as `title`.
- [ ] **Step 2–4:** Run (fail), implement, run (pass). Commit.

### Task 9: App shell, query panel, data preview

**Files:**
- Create: `web/src/ui/QueryPanel.svelte`, `DataPreview.svelte`, `QueryPanel.test.ts`; Modify: `web/src/App.svelte`, `web/src/app.css`

**Interfaces:**
- Consumes: Tasks 7, 8.
- Produces: `QueryPanel` props `{ query: Query; vocabulary: Vocabulary; sources: Source[]; onrun: (q: Query) => void }`. An endpoint selector (`aggregate`, `rates`, `sample`) switches the form; switching keeps `from/to` ↔ `fromYear/toYear` in sync where possible. Aggregate: date range, grain, bucket, groupBy (checkboxes from `EVENT_DIMENSIONS` or `VEHICLE_DIMENSIONS` by grain; switching to event grain drops vehicle-only choices), filters (add filter: dimension select; values multi-select from vocabulary when `VOCAB_ATTRIBUTE` has it, else a comma-separated text field). Rates: measure, metric, year range, groupBy (`vehicle_class`, `regulation_class`), optional exposure source (select of exposure-kind sources, blank = server default). Sample: date range, n (1–100000), seed, filters. `DataPreview` props `{ result: QueryResult; glossary }` shows row count, notices (unmatched/unattributed as collapsible `TableView`s), and the first 100 rows as a `TableView`. `App.svelte` loads sources and vocabulary on mount (error banner on failure), holds `query`, `result`, `error` in `$state`, and shows a sources strip (`name — data current through <date>`) in the header. Layout: one column under 900px, two columns (query+preview | method+result) above.
- [ ] **Step 1: Write failing tests** (fake data, `@testing-library/svelte`):
  - `QueryPanel_aggregate_emitsQuery` — choose vehicle grain, group by vehicle_class, filter mode = aviation, click Run → `onrun` called with the matching `Query`.
  - `QueryPanel_eventGrain_hidesVehicleDimensions` — `make` not offered at event grain.
  - `QueryPanel_rates_emitsQuery`.
  - `App_queryError_showsServerMessage` — `runQuery` rejecting with `ApiError(400, "dimension 'make' needs grain=vehicle")` shows that text in an alert region (`role="alert"`).
- [ ] **Step 2–4:** Run (fail), implement, run (pass). `npm run check` clean. Commit.

### Task 10: Method panel, result panel, experiments

**Files:**
- Create: `web/src/ui/MethodPanel.svelte`, `ResultPanel.svelte`, `ExperimentBar.svelte`, `web/src/experiments/storage.ts`, `presets.ts`, and tests `MethodPanel.test.ts`, `storage.test.ts`, `presets.test.ts`; Modify: `web/src/App.svelte`

**Interfaces:**
- Consumes: registry (Tasks 4–6), `validateBindings`, `defaultParams`, Tasks 7–9.
- Produces:
  ```ts
  export interface Experiment { version: 1; name: string; note?: string; query: Query; methodId: string; bindings: Bindings; params: Record<string, unknown> }
  export const STORAGE_KEY = 'transport-analyzer.experiments';
  export function loadExperiments(storage?: Storage): Experiment[];   // [] on missing, corrupt or throwing storage; drops entries without version 1
  export function saveExperiment(e: Experiment, storage?: Storage): boolean; // replaces same name; false when storage throws
  export function deleteExperiment(name: string, storage?: Storage): boolean;
  export const PRESETS: Experiment[];
  ```
  `MethodPanel` props `{ table: Table; methodId: string; bindings: Bindings; params: Record<string, unknown>; onchange }`: method select from `methods`; one control per `InputSpec` — a `<select>` (or checkbox list when `multiple`) listing only columns whose type is in `types`, plus "—" when optional; one control per `ParamSpec` by `kind`; `validateBindings` messages listed; Run disabled while there are messages. `ResultPanel` runs the method (catching exceptions into an error box), shows warnings, the experiment `note`, and each `RenderSpec` (`TableView` or `renderPlot` mounted in a `div`, re-rendered on width change). `ExperimentBar`: preset select, saved-experiment select, "Save as" (name input), Delete, "Export JSON" (download of the current experiment). Loading an experiment sets query, runs it, then applies method, bindings and params.
  `PRESETS` (rates presets bind `numerator`, `denominator`, `carry: ['join_level','exposure_source','provenance']`):
  1. `Airlines vs road — deaths per 100M passenger-miles, by year`: rates, `occupant_fatalities`, `passenger_miles`, 2019–2023, groupBy `['regulation_class']`; `rate`, groups `['mode','regulation_class','year']`, per `1e8`. Note: "Part 121 includes all-cargo flights (3 of the 4 deaths in 2019 were on Atlas Air 3591, a cargo flight). Airline passenger-miles are domestic. Road is every highway vehicle occupant."
  2. `Airlines vs road — 2019–2023 combined`: same query; groups `['mode','regulation_class']`; same note.
  3. `General aviation by aircraft class — deaths per 100k flight hours, 2022`: rates, `occupant_fatalities`, `flight_hours`, 2022–2022, groupBy `['vehicle_class','regulation_class']`; groups `['vehicle_class']`, per `1e5`. Note: "FAA survey hours cover Part 91, on-demand Part 135, Part 137 and Part 133 together (general aviation). Aircraft of unknown class are listed as unmatched."
  4. `Road by vehicle class — deaths per 100M vehicle-miles, by year`: rates, `occupant_fatalities`, `vehicle_miles`, 2019–2023, groupBy `['vehicle_class']`; groups `['vehicle_class','year']`, per `1e8`.
  5. `US aviation deaths by aircraft class and year (heatmap)`: aggregate, vehicle grain, 2019-01-01…2023-12-31, bucket year, groupBy `['vehicle_class']`, filters `{mode:['aviation'], country:['US']}`; `heatmap`, x `period`, y `vehicle_class`, value `occupant_fatalities`.
  6. `Road deaths by vehicle class and operator type (crosstab)`: aggregate, vehicle grain, 2022-01-01…2022-12-31, bucket none, groupBy `['vehicle_class','operator_type']`, filters `{mode:['highway']}`; `crosstab`, row `vehicle_class`, col `operator_type`, value `occupant_fatalities`.
- [ ] **Step 1: Write failing tests:**
  - `loadExperiments_corruptJson_returnsEmpty`, `loadExperiments_throwingStorage_returnsEmpty` (a `Storage` whose `getItem` throws), `saveExperiment_replacesSameName`, `deleteExperiment_removes`, `saveExperiment_throwingStorage_returnsFalse`.
  - `presets_bindToColumnsTheirQueryReturns` — for each preset, build a `Table` with the columns its endpoint returns (rates: the 9 rates columns; aggregate: groupBy + `period` when bucketed + the count columns) and assert `validateBindings` returns `[]` and `methodById(methodId)` exists.
  - `MethodPanel_offersOnlyMatchingColumnTypes` — with the rates table and `rate` selected, the Numerator select offers `numerator` and no categorical columns; Groups offers `mode`, `year`, `vehicle_class`, `regulation_class`, `join_level`, `exposure_source`, `provenance`.
  - `MethodPanel_staleBinding_disablesRun` — bindings naming a column not in the table: the validation message shows and Run is disabled.
- [ ] **Step 2–4:** Run (fail), implement, run (pass). `npm run check` clean. Commit.

### Task 11: Vocabulary descriptions

**Files:**
- Modify: `data/vocabulary.csv`; Test: the existing vocabulary loader test file in `tests/TransportAnalyzer.Tests` (add one test)

**Interfaces:**
- Produces: real `description` text for all 39 rows (codes and `parent_code` unchanged). Required content: `light_duty` — cars and light trucks combined, the way FHWA/BTS report vehicle-miles and passenger-miles since 2007; `passenger_car` / `light_truck` — counted separately in FARS but have no separate exposure after 2006, so rates roll up to `light_duty`; each aviation class names what the NTSB mapping puts in it (e.g. `light_aircraft`: piston and electric airplanes, plus Part 91 airplanes with no engine record; `airliner`: turbofan/turbojet airplanes under Part 121 or 129, plus Part 121/129 airplanes with no engine record); `general_aviation` — the FAA survey scope (Part 91, on-demand Part 135, Part 137, Part 133); `part_121` includes all-cargo operations; `part_135_scheduled` is commuter service, not in the FAA survey; `road_private` / `road_commercial` say how FARS operator use decides them; `unknown` / `other` say what lands there. One or two sentences each.
- [ ] **Step 1: Write failing test** `Vocabulary_DescriptionsAreNotPlaceholders` — load `data/vocabulary.csv`; no description contains `" as a shared "`; every description is at least 30 characters.
- [ ] **Step 2–4:** Run (fail), rewrite descriptions, `dotnet test` (pass). Commit.

### Task 12: End-to-end verification and docs

**Files:**
- Create: `web/src/experiments/presets.live.test.ts`, `docs/verification/plan-3.md`; Modify: `README.md`, `docs/superpowers/plans/2026-11-04-plan-1-carryover.md`

**Interfaces:**
- Consumes: everything above; the running Docker stack with all sources ingested.
- [ ] **Step 1: Write `presets.live.test.ts`:** skipped unless `process.env.TA_API` is set (e.g. `http://localhost:8080`); for each preset, `runQuery` against `TA_API` (prefix the relative URL), run the method with the preset bindings and params, and assert no exception, no warnings, and at least one row/cell. For preset 2 also assert the Part 121 pooled rate is within 1% of `0.000128` and road within 1% of `0.618` (plan-2 verification), and that both have `hi > rate`.
- [ ] **Step 2:** `docker compose up -d --build`, then `cd web && TA_API=http://localhost:8080 npm test -- presets.live` — PASS. Record the output.
- [ ] **Step 3:** Open `http://localhost:8080/` in a browser, load each preset, and capture a screenshot of presets 1, 3 and 5. Check: rates table shows interval, exposure source and provenance on every row; Part 121 zero-death years drawn hollow at the floor; hovering a vehicle class shows its description; stopping the app container (`docker compose stop app`) while the page is open and running a query shows an error banner, not a blank page.
- [ ] **Step 4: Write `docs/verification/plan-3.md`:** per preset, the numbers the browser shows (rate, lo, hi) next to the plan-1/plan-2 references: road 2019–2023 0.6180 and Part 121 0.000128 per 100M passenger-miles; 2022 motorcycle 26.32 per 100M VMT (NHTSA 26.30); GA 2022 per 100k hours (plan 2: business_jet 0.019, experimental 4.30, helicopter 1.15, light_aircraft 1.23, turboprop 1.20, other 10.5). Light aircraft and airliner values may differ from plan 2 because the four NTSB mapping rows accepted on 2026-11-05 (`AIR::091`, `AIR::121`, `AIR::129`, `091:UNK`) moved vehicles out of `unknown`; give the before/after numerator and explain any other difference with query output.
- [ ] **Step 5:** README: Frontend section gains "Experiments" (presets, saving in browser storage only, export JSON) and "Adding a method" (a module in `web/src/stats/` implementing `Method`, appended to `registry.ts`, no UI change). Carry-over doc: mark the plan 3 items done or move what remains (selecting nulls, zero-event exposure cells, NTSB UTC dates for future day/hour views) to "Later". Commit.
