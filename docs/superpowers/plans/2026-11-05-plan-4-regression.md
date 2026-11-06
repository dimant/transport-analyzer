# Plan 4: Regression ("control for vehicle class") Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Milestone 8. Add `poisson-regression` (rate ratios with an exposure offset, Poisson or negative binomial) and `logistic-regression` (odds ratios for case-only attributes) to the browser stats library. Add presets that answer "air vs road, controlling for vehicle class" and "which vehicle attributes predict an occupant death".

**Architecture:** The server keeps counting. It gains two things:
- a rate join that puts Part 121 and road vehicle classes in one table;
- one vehicle measure, so the logistic outcome comes from full-population counts.

The browser fits GLMs by IRLS on grouped rows (one row per covariate cell), using hand-rolled small dense matrices. Shared parts: design-matrix building, the GLM fitter, the result shape and a forest-plot render spec. Each method module is thin.

**Tech Stack:** As in plan 3: TypeScript 6 strict, Vitest 5, Svelte 5, Observable Plot 0.6.18; .NET 10 and xUnit for the two server changes. No new dependencies.

**Spec:** `docs/superpowers/specs/2026-11-04-experiment-platform-design.md`. Section 7 covers modules 4 and 5, section 8 milestone 8, and section 9 says the fitting algorithm is left to the builder.

## Global Constraints

- `web/src/stats/` imports nothing outside itself (enforced by `stats-imports.test.ts`).
- Only attributes with an exposure breakdown (vehicle class, regulation class, mode, year) enter `poisson-regression`, so its covariates are categorical only. `logistic-regression` takes categorical and continuous covariates.
- "Every rate shows its interval and its denominator source": every ratio has a Wald interval at the chosen confidence, and the Poisson summary names the exposure sources and provenance of the rows it used.
- Negative binomial is a `family` parameter. Overdispersion is detected and reported as a warning; it never switches the family silently.
- Nothing a fit cannot estimate is shown as a number. Not-estimable and aliased terms show `—` with a note saying why.
- Exact dependency versions, no new packages. Work on `main`; never push without being asked; never `docker compose down -v`.
- Commit trailers as given in the session's attribution reminder.

## Review Focus

1. **A covariate level with no events, or where all or none had the outcome.** Examples: a year with zero Part 121 deaths, or `crew_count=0`. The level is listed as not estimable, its rows are left out, and the other terms still fit. Pinned by Task 5 `buildDesign_dropsLevelWithoutEvents` and Task 8 `poisson_zeroEventLevel_notEstimable`.
2. **Collinear covariates.** `maintenance_regime` is derived from `regulation_class`; `mode` is determined by `rate_class`. The redundant column is reported as aliased, with no crash and no singular-matrix NaNs elsewhere. Pinned by Task 4 `cholesky_flagsDependentColumn` and Task 8 `poisson_aliasedCovariate_reported`.
3. **Separation with a continuous covariate** (outcome all 0 below some age, all 1 above). The fit warns that it did not converge or that estimates are unstable; it does not print huge odds ratios as if they were fine. Pinned by Task 9 `logistic_separation_warns`.
4. **Real-scale magnitudes**: exposures near 5e12 passenger-miles and counts near 25,000 next to counts of 0–3. Results match the small-scale case exactly. Pinned by Task 8 `poisson_realScale_matchesSmallScale`.
5. **Unusable rows**: missing or zero exposure, zero trials, successes above trials, missing continuous values. They are dropped, counted and named in a warning. If no rows are left, the result is a warning and not a thrown error. Pinned by Task 8 `poisson_dropsUnusableRows` and Task 9 `logistic_dropsUnusableRows`.

---

### Task 1: Rate join puts Part 121 beside road vehicle classes

`/api/rates?metric=passenger_miles&groupBy=vehicle_class,regulation_class` today matches road classes, because each one finds a `(vehicle_class, null)` exposure row. `airliner/part_121` is unmatched, because grouping by class forbids classless keys. The rule exists to stop every class merging into the mode total. A classless key that has a regulation class, such as `(null, part_121)`, is a legitimate roll-up over vehicle class within that regulation class, as long as every numerator cell of that regulation class rolls up with it.

**Files:**
- Modify: `src/TransportAnalyzer.Api/Query/RateJoiner.cs` (`Match`, the guard, the doc comment)
- Test: `tests/TransportAnalyzer.Tests/RateJoinerTests.cs`
- Modify: `README.md`, the `/api/rates` section

**Interfaces:**
- Produces: when grouped by vehicle class, a cell may now match `(null, rc)` or `(null, pr)`, after every keyed candidate and never `(null, null)`. Its row has `VehicleClass = null` and `join_level` `regulation_class` (or `regulation_class_parent`).
- **Guard:** a classless key `(null, X)` reached this way is valid only if every numerator cell in the same mode and year whose regulation class is `X`, or whose regulation parent is `X`, also matched `(null, X)`. Otherwise every cell matched to `(null, X)` becomes unmatched with reason `mixed_granularity`. Without this, unknown-class Part 91 aircraft would be divided by all Part 91 hours while light aircraft use general-aviation hours.

- [ ] **Step 1: Write the failing tests** in `RateJoinerTests.cs`, using the file's existing helpers:
  - `Join_GroupedByClass_Part121FallsBackToRegulationClass`: numerators `(aviation, 2022, airliner, part_121)=3`, `(aviation, 2022, turboprop, part_121)=1`, `(highway, 2022, bus, road_commercial)=19`. Exposure `(aviation, 2022, null, part_121)` and `(highway, 2022, bus, null)`. Both groupings on. Expect two rows: `(aviation, null, part_121)` with numerator 4 and join level `regulation_class`, and `(highway, bus, null)` with numerator 19. Nothing unmatched.
  - `Join_GroupedByClass_ClasslessRegulationKeyBesideClassedSibling_IsMixedGranularity`: numerators `(aviation, 2022, light_aircraft, part_91)=10` and `(aviation, 2022, unknown, part_91)=2`. Exposure `(light_aircraft, general_aviation)` and `(null, part_91)`. Regulation parent `part_91→general_aviation`. Expect one row, light_aircraft/general_aviation with 10. The unknown cell (2) is unmatched with `mixed_granularity`.
  - The existing `Join_GroupedByClass_DoesNotFallBackToModeTotal` stays green unchanged: `(null, null)` is still never used under a class grouping.
- [ ] **Step 2: Run** `dotnet test --filter RateJoinerTests`. Expected: the two new tests FAIL, with the airliner reported unmatched as `no_exposure`.
- [ ] **Step 3: Implement.** In `Match`, skip `cvc is null` only when `crc is null` as well. Add the guard next to the existing mixed-granularity guard. Update the method's doc comment and the candidate-order list.
- [ ] **Step 4: Run** `dotnet test`. Expected: all pass, including `Join_ConservesNumerator` (numerator plus unmatched equals input).
- [ ] **Step 5: Live check and README.** Rebuild with `docker compose up -d --build`. Then call `curl -s 'localhost:8080/api/rates?measure=occupant_fatalities&metric=passenger_miles&fromYear=2019&toYear=2023&groupBy=vehicle_class,regulation_class'`. The response must contain 5 `aviation` rows with `regulation_class: part_121`, null `vehicle_class` and join level `regulation_class`, plus the 20 highway rows. In README:
  - replace the sentence that says Part 121 against road needs `groupBy=regulation_class` with one saying both groupings now return Part 121 beside the road vehicle classes;
  - document the new `mixed_granularity` case.
- [ ] **Step 6: Commit:** `feat(api): rates grouped by class fall back to a regulation-class total, guarded`

### Task 2: Vehicle measure for the logistic outcome, and a `rate_class` column

**Files:**
- Modify: `src/TransportAnalyzer.Api/Query/AggregateQuery.cs` (`VehicleMeasures`)
- Test: `tests/TransportAnalyzer.Tests/AggregateTests.cs`
- Modify: `web/src/api/columns.ts`, `web/src/api/client.ts`
- Test: `web/src/api/client.test.ts`, `web/src/experiments/presets.test.ts` (column constants)

**Interfaces:**
- Produces, server: a vehicle-grain measure `vehicles_with_occupant_fatality` = `count(*) filter (where v.occupant_fatalities > 0)`, appended after `nonoccupant_minor_injuries`.
- Produces, client:
  - `columnType('vehicles_with_occupant_fatality') === 'count'`.
  - Every `rates` table gains a categorical column `rate_class`, inserted after `regulation_class`. Its value is the non-null of `vehicle_class` and `regulation_class` joined with `' · '`, or `mode` when both are null. Examples: `light_duty`, `part_121`, `light_aircraft · general_aviation`.
  - `COLUMN_HELP` entries for both columns.

- [ ] **Step 1: Write the failing tests.**
  - `AggregateTests`: update the vehicle-grain column list assertion to end with `"vehicles_with_occupant_fatality"`. Add `Aggregate_VehiclesWithOccupantFatality_CountsVehiclesNotDeaths`: one vehicle with 2 occupant deaths, one with 0, and one with 1 in the same group give `vehicles = 3` and `vehicles_with_occupant_fatality = 2`.
  - `client.test.ts`: `rates_addsRateClass`. A rates response with rows `(highway, 2022, light_duty, null, …)`, `(aviation, 2022, null, part_121, …)` and `(highway, 2022, null, null, …)` gives a `rate_class` column of type `categorical` with values `['light_duty', 'part_121', 'highway']`. The column sits at index `columns.indexOf('regulation_class') + 1`.
  - `presets.test.ts`: add `rate_class` to `RATES_COLUMNS` after `regulation_class`, and `vehicles_with_occupant_fatality` to the end of `VEHICLE_COUNTS`.
- [ ] **Step 2: Run** `dotnet test --filter AggregateTests` and `cd web && npx vitest run src/api`. Expected: the new tests FAIL.
- [ ] **Step 3: Implement** the measure, the `COUNTS` entry and the column insertion in `runQuery`'s rates branch. Add help texts:
  - `rate_class`: "The class the exposure row is defined for: the vehicle class, the regulation class, both, or the mode total when neither applies."
  - `vehicles_with_occupant_fatality`: "Vehicles in which at least one occupant died."
- [ ] **Step 4: Run** `dotnet test` and `cd web && npm test`. Expected: all pass.
- [ ] **Step 5: Commit:** `feat: vehicles_with_occupant_fatality measure and rate_class column`

### Task 3: Normal and digamma functions

**Files:**
- Modify: `web/src/stats/distributions.ts`
- Test: `web/src/stats/distributions.test.ts`

**Interfaces:**
- Produces:
  - `normalQuantile(p: number): number`, Wichura AS241, `RangeError` outside (0, 1).
  - `normalTwoSidedP(z: number): number`, computed as `Q(0.5, z²/2)` from the existing `gammaPQ`, so that tail values keep their precision.
  - `digamma(x: number): number` and `trigamma(x: number): number` for x > 0, using recurrence up to x ≥ 6 and then the asymptotic series.
  - `logGamma` is now exported.

- [ ] **Step 1: Write the failing tests** (reference values from scipy):
  - `normalQuantile(0.975)` ≈ `1.959963984540054` (1e-12), `(0.995)` ≈ `2.5758293035489004` (1e-12), `(1e-10)` ≈ `-6.361340902404056` (1e-9).
  - `normalTwoSidedP(1.959963984540054)` ≈ `0.05` (1e-12).
  - `normalTwoSidedP(10)` relative error to `1.5239706048320948e-23` below 1e-6.
  - `normalTwoSidedP(0) === 1`.
  - `digamma(1)` ≈ `-0.5772156649015329`, `(0.5)` ≈ `-1.9635100260214235`, `(7.3)` ≈ `1.917820335637986` (1e-12).
  - `trigamma(1)` ≈ `1.6449340668482266`, `(0.5)` ≈ `4.93480220054468`, `(7.3)` ≈ `0.14679576813142703` (1e-10).
- [ ] **Step 2: Run** `npx vitest run src/stats/distributions`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit:** `feat(stats): normal quantile, two-sided p, digamma, trigamma`

### Task 4: Small dense linear algebra with aliasing

**Files:**
- Create: `web/src/stats/linalg.ts`
- Test: `web/src/stats/linalg.test.ts`

**Interfaces:**
- Produces:
  - `interface Mat { rows: number; cols: number; data: Float64Array }` (row-major) and `matrix(rows: number, cols: number): Mat`.
  - `weightedCrossprod(X: Mat, w: Float64Array): Mat`, which computes XᵀWX.
  - `weightedXty(X: Mat, w: Float64Array, z: Float64Array): Float64Array`, which computes XᵀWz.
  - `cholesky(A: Mat, tol = 1e-9): { L: Mat; aliased: boolean[] }`. Column j is aliased when its remaining pivot is ≤ `tol × A[j][j]`, or when A[j][j] is 0. An aliased column's row and column of L are zero, and later columns ignore it.
  - `choleskySolve(c, b): Float64Array`: entries of aliased columns are 0.
  - `choleskyInverse(c): Mat`: rows and columns of aliased entries are NaN.

- [ ] **Step 1: Write the failing tests:**
  - `cholesky_solvesSpd`: A = [[4,2,0.4],[2,5,1],[0.4,1,3]] and b = [1,2,3]. A·x reproduces b to 1e-12, and `aliased` is all false.
  - `cholesky_flagsDependentColumn`: X has 3 columns, with column 3 = column 1 + column 2 (5 rows, non-trivial values), and A = XᵀX. `aliased` is `[false, false, true]`. `choleskyInverse` returns NaN in row and column 3. Its top-left 2×2 block times A's top-left 2×2 block is the identity to 1e-10.
  - `weightedCrossprod_matchesNaive` against a hand-computed 3×2 example.
- [ ] **Step 2: Run** `npx vitest run src/stats/linalg`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit:** `feat(stats): small dense Cholesky with aliased-column detection`

### Task 5: Design matrix from a grouped table

**Files:**
- Create: `web/src/stats/design.ts`
- Test: `web/src/stats/design.test.ts`

**Interfaces:**
- Consumes: `Table`, `column` and `naturalCompare` from `table.ts`; `Mat` and `matrix` from Task 4.
- Produces:
```ts
export interface DesignSpec { response: string; size: string; sizeKind: 'exposure' | 'trials'; covariates: string[] }
export interface DesignColumn { term: string; level: string | null } // '(intercept)'/null, a categorical level, or a continuous term with level null
export interface LevelInfo { term: string; level: string; events: number; size: number; status: 'reference' | 'estimated' | 'not_estimable' }
export interface Design {
  X: Mat; y: Float64Array; size: Float64Array; offset: Float64Array
  rows: number[]                     // table row indices used
  columns: DesignColumn[]
  levels: LevelInfo[]                // every categorical level seen, in covariate then naturalCompare order
  warnings: string[]
}
export function buildDesign(table: Table, spec: DesignSpec): Design
```
- **Rules.** The tests pin each one.
  - **Duplicate covariates:** listed covariates are deduplicated.
  - **Dropping rows:** a row is dropped if
    - its response or size is NaN;
    - its size is ≤ 0;
    - its response is < 0;
    - for `trials`, its response is greater than its size;
    - a continuous covariate is NaN.
    Each reason gets one warning with its count: `"3 rows dropped: missing or non-positive exposure"`, `"1 row dropped: successes above trials"`, `"2 rows dropped: missing vehicle_age_years"`.
  - **Null categories:** a categorical null is the level `(none)`.
  - **Not estimable:** a level is not estimable when its events total 0 (both kinds), or, for `trials`, when its events equal its size. Its rows are dropped, and the check is repeated until nothing changes. One warning per level:
    - exposure kind: `"vehicle_class=business_jet: no events; not estimable"`;
    - trials kind: `"crew_count=0: all or none had the outcome; not estimable"`.
  - **Reference level:** the level with the largest total size among the remaining rows. Ties go to the first level in `naturalCompare` order.
  - **One level left:** a categorical with only one remaining level contributes no column, with the warning `"<name> has one level left; not in the model"`.
  - **Offset:** `log(size)` for `exposure`, 0 for `trials`.
  - **Column order:** the intercept, then each covariate in binding order. A categorical gives one column per non-reference level, in `naturalCompare` order. A continuous covariate gives one column with level `null`.

- [ ] **Step 1: Write the failing tests:**
  - `buildDesign_referenceIsLargestSize`: levels A (size 1000) and B (size 1500). The reference is B, and there is one column `{term:'g', level:'A'}`.
  - `buildDesign_dropsLevelWithoutEvents`: A (10/1000), B (30/1500) and C (0/500). C is `not_estimable`, its row is not in `rows`, the warning reads `g=C: no events; not estimable`, and the columns are the intercept and A.
  - `buildDesign_dropsUnusableRowsWithCounts`: covers each drop reason with its exact warning text.
  - `buildDesign_nullCategoryIsALevel`, `buildDesign_singleLevelLeftOut`, `buildDesign_trialsAllOrNone_notEstimable`.
- [ ] **Step 2: Run** `npx vitest run src/stats/design`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit:** `feat(stats): design matrix with reference levels and estimability checks`

### Task 6: GLM fitting by IRLS (Poisson, binomial, negative binomial)

**Files:**
- Create: `web/src/stats/glm.ts`
- Test: `web/src/stats/glm.test.ts`

**Interfaces:**
- Consumes: Task 4 (`Mat`, `weightedCrossprod`, `weightedXty`, `cholesky*`); Task 3 (`digamma`, `trigamma`, `logGamma`).
- Produces:
```ts
export type Family = { kind: 'poisson' } | { kind: 'binomial' } | { kind: 'negative_binomial'; theta: number }
export interface GlmFit {
  beta: Float64Array; se: Float64Array; aliased: boolean[]  // beta/se NaN where aliased
  mu: Float64Array; deviance: number; pearson: number; logLik: number
  dfResidual: number; iterations: number; converged: boolean
}
export function fitGlm(X: Mat, y: Float64Array, offset: Float64Array, size: Float64Array, family: Family): GlmFit
export function fitNegativeBinomial(X: Mat, y: Float64Array, offset: Float64Array): GlmFit & { theta: number; thetaCapped: boolean }
```
- **Algorithm.**
  - Link: log for Poisson and negative binomial; logit for binomial, where `y` is the success count, `size` the trials, and μ = size·p.
  - Start: Poisson μ = y + 0.1; binomial p = (y + 0.5)/(size + 1).
  - Stopping: at most 50 iterations, stopping when |dev − devOld| / (|dev| + 0.1) < 1e-10.
  - Step-halving, up to 10 times, whenever the deviance increases or turns non-finite.
  - Clamping: logit η stays within ±30, and log-link η stays at or below 700.
  - SEs come from `choleskyInverse` of XᵀWX at convergence.
  - `dfResidual` = n − (number of non-aliased columns).
  - `size` is ignored by the log-link families.
- **Negative binomial (NB2, Var = μ + μ²/θ).**
  - Start from the Poisson fit with θ₀ = n / Σ((y/μ − 1)²).
  - Then alternate, up to 25 outer rounds:
    - Newton steps on the ML score for θ given μ, using `digamma` and `trigamma`, keeping θ > 0;
    - `fitGlm` with that θ.
  - Stop when |Δθ|/θ < 1e-8.
  - If θ exceeds 1e8, cap it there and set `thetaCapped`; the counts then show no overdispersion.

- [ ] **Step 1: Write the failing tests.** Tolerance is 1e-6 relative unless stated. Exact values come from closed forms; the others come from scipy maximum likelihood.
  - `poisson_oneFactor_closedForm`:
    - X = [[1,1],[1,0]], y = [10,30], offset = log([1000,1500]);
    - exp(β₁) = 0.5 and se(β₁) = √(1/10 + 1/30) = 0.36514837167011;
    - exp(β₀)·1000 = 20, deviance ≈ 0 (1e-8), dfResidual = 0, converged.
  - `poisson_independence2x2_closedForm`:
    - X = [[1,0,0],[1,0,1],[1,1,0],[1,1,1]], y = [10,20,30,40], offset = log(100) for every row;
    - exp(β₁) = 7/3 with se √(1/30 + 1/70), and exp(β₂) = 1.5 with se √(1/40 + 1/60);
    - μ = [12,18,28,42];
    - deviance = 2·[10 ln(10/12) + 20 ln(20/18) + 30 ln(30/28) + 40 ln(40/42)] = 0.8043486;
    - Pearson = 0.7936508.
  - `binomial_2x2_closedForm`:
    - X = [[1,1],[1,0]], y = [20,10], size = [100,200];
    - exp(β₁) = (20·190)/(80·10) = 4.75 and se = √(1/20 + 1/80 + 1/10 + 1/190) = 0.4095890.
  - `binomial_continuous_matchesScipy`:
    - X = [[1,x]] for x = 0…4, y = [5,8,12,15,22], size = 50 each;
    - exp(β₁) = 1.6001176, se = 0.1145253, β₀ = −2.1583223, deviance = 0.1729540 (all 1e-5).
  - `negativeBinomial_matchesScipy`:
    - X = [[1,1]×5, [1,0]×5], y = [2,9,0,14,5, 20,3,41,11,30], offset = log(10) for every row;
    - θ = 1.5561152 (1e-4 relative);
    - exp(β₁) = 0.2857143 (= mean ratio 4/14, 1e-6);
    - se(β₁) = 0.5476381 (1e-4);
    - logLik = −34.3738877 (1e-6).
    - The Poisson fit of the same data has se(β₁) = 0.2070197 and Pearson/df = 8.0178572.
  - `negativeBinomial_poissonData_capsTheta`: data that a Poisson model fits exactly gives `thetaCapped` true, and β equal to the Poisson β to 1e-8. Use X as in the 2×2 independence test, with the multiplicative counts y = [10,20,30,60] and offset log(100) for every row.
  - `glm_aliasedColumn_isNaN`: a column duplicating another gives `aliased[j]` true and `beta[j]` NaN; the other estimates equal the fit without that column.
- [ ] **Step 2: Run** `npx vitest run src/stats/glm`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit:** `feat(stats): IRLS GLM fitter (Poisson, binomial, negative binomial)`

### Task 7: Forest plot render spec

**Files:**
- Modify: `web/src/stats/method.ts` (add `ForestSpec` to `RenderSpec`)
- Modify: `web/src/render/plot.ts`, `web/src/ui/ResultPanel.svelte`
- Test: `web/src/render/plot.test.ts`, `web/src/ui/ResultPanel.test.ts`

**Interfaces:**
- Produces:
```ts
export interface ForestSpec {
  kind: 'forest'
  label: string; estimate: string; lo: string; hi: string // data keys
  xLabel: string          // e.g. 'rate ratio (95% CI)'
  caption?: string        // e.g. 'relative to vehicle_class=light_duty, year=2019'
  data: Record<string, Cell>[]
}
```
- **Rendering.** `renderPlot` accepts `ForestSpec`.
  - Layout: a horizontal forest. y is a band scale of `label` in data order; x is log-scaled; each row is a dot plus a rule from lo to hi.
  - A dashed vertical rule marks x = 1.
  - Ticks use `formatValue(v, 'rate')`.
  - Hover reads `label: est (lo – hi)`.
  - Rows whose estimate is null are not drawn. They are listed in a note: `"not estimated: <label>, <label>"`.
  - The caption goes under the figure, as for heatmaps.

- [ ] **Step 1: Write the failing tests.**
  - `plot.test.ts` `forest_drawsEstimableRowsAndReferenceRule`: 3 rows, one with a null estimate. The SVG has 2 dots, one dashed rule, and the note text `not estimated: c`.
  - `ResultPanel.test.ts`: a method whose `render` returns a `ForestSpec` mounts a figure without throwing.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** `npm test`. Expected: PASS.
- [ ] **Step 5: Commit:** `feat(render): forest plot for ratio estimates`

### Task 8: `poisson-regression` method and the shared regression result

**Files:**
- Create: `web/src/stats/regression.ts` (shared result type and render)
- Create: `web/src/stats/poisson-regression.ts`
- Modify: `web/src/stats/registry.ts` (append)
- Test: `web/src/stats/poisson-regression.test.ts`

**Interfaces:**
- Consumes: `buildDesign` (Task 5), `fitGlm` and `fitNegativeBinomial` (Task 6), `normalQuantile` and `normalTwoSidedP` (Task 3), `ForestSpec` (Task 7).
- Produces:
```ts
// regression.ts
export interface RegressionTerm { term: string; level: string; estimate: number | null; lo: number | null; hi: number | null; p: number | null; events: number; size: number; note: string }
export interface RegressionResult {
  ratioName: 'rate ratio' | 'odds ratio'; eventsLabel: string; sizeLabel: string; confidence: number
  terms: RegressionTerm[]; reference: string           // 'vehicle_class=light_duty, year=2019'
  summary: { label: string; value: Cell }[]; warnings: string[]
}
export function termsFromFit(design: Design, fit: GlmFit, confidence: number): RegressionTerm[]
export function renderRegression(r: RegressionResult): [TableSpec, ForestSpec, TableSpec]
// poisson-regression.ts
export type PoissonParams = { family: 'poisson' | 'negative_binomial'; confidence: number; per: number }
export const poissonRegression: Method<PoissonParams, RegressionResult> // id 'poisson-regression'
```
- **Method.**
  - Name: "Poisson regression (rate ratios)".
  - Inputs:
    - `events` (count);
    - `exposure` (exposure);
    - `covariates` (categorical, multiple, required).
  - Params:
    - `family`: select `poisson` / `negative_binomial`, default `poisson`;
    - `confidence`: default 0.95;
    - `per`: default 1e8.
  - `defaultBindings`: `events = numerator`, `exposure = denominator`, and `covariates` = whichever of `['rate_class', 'year']` are categorical columns.
- **Terms.**
  - Every level in `design.levels` gives one term.
  - Reference level: estimate 1, lo/hi/p null, note `reference`.
  - Estimated level: exp(β) with exp(β ± z·se), the two-sided Wald p, and note `''`.
  - Not-estimable level: nulls, note `not estimable: no events`.
  - Aliased level: nulls, note `aliased with other covariates`.
- **Summary rows**, in this order:
  - `family` (with θ when negative binomial: `negative binomial (θ = 1.556)`);
  - `rows used`;
  - `parameters`;
  - `deviance`;
  - `residual df`;
  - `Pearson dispersion` (null when df = 0);
  - `AIC` (−2·logLik + 2·parameters, + 2 for θ);
  - `baseline rate per <formatPer(per)>`: exp(β₀)·per, the rate at all reference levels;
  - `converged`;
  - `exposure sources`: distinct `exposure_source (provenance)` over the used rows, joined with `' + '`, only when those columns exist.
- **Warnings**, after the design warnings:
  - Poisson family, df > 0, dispersion > 1.5: `Pearson dispersion ${d.toFixed(2)} exceeds 1.5: counts vary more than Poisson allows, so these intervals are too narrow. Try family negative_binomial.`
  - df = 0: `No residual degrees of freedom: the model is saturated, so overdispersion cannot be checked.`
  - Not converged: `The fit did not converge in 50 iterations; estimates are unreliable.`
  - `thetaCapped`: `No overdispersion found: negative binomial reduces to Poisson.`
  - No rows left: `No rows left to fit.` The method returns with empty terms and does not throw.
- **Render:**
  - a terms table with columns `term`, `level`, the ratio (format `rate`, labelled `ratioName`), `lo`, `hi`, `p` (`rate`), events (`integer`), size (`number`) and `note`;
  - a `ForestSpec` over the non-reference terms, labelled `term: level`, with `xLabel` `${ratioName} (${confidence*100}% CI)` and `caption` `relative to ${reference}`;
  - a summary table with columns `label` and `value`.

- [ ] **Step 1: Write the failing tests.** Build tables with `fromRows`, using the Task 6 data:
  - `poisson_oneFactor_rateRatio`:
    - rows g = A (10 / 1000) and B (30 / 1500), `per = 1000`;
    - term A: estimate 0.5, lo 0.24443036, hi 1.02278622, p 0.05766162 (1e-6);
    - term B: note `reference`;
    - `reference` = `g=B`, and the baseline rate is 20.
  - `poisson_zeroEventLevel_notEstimable`: adding C = 0 / 500 leaves A's numbers unchanged, gives C note `not estimable: no events` with null estimate, and adds the warning.
  - `poisson_aliasedCovariate_reported`: a second covariate `h` identical to `g` gives the h term the note `aliased with other covariates`; g's numbers are unchanged.
  - `poisson_overdispersion_warns`: on the Task 6 negative-binomial data with family `poisson`, the warning text starts `Pearson dispersion 8.02 exceeds 1.5`. With family `negative_binomial`, there is no such warning, the ratio is 0.2857143, lo is 0.0976746 and hi is 0.8357616 (1e-4), and the summary family reads `negative binomial (θ = 1.556)`.
  - `poisson_realScale_matchesSmallScale`: the one-factor data with counts ×1000 (10,000 and 30,000) and exposures ×1e9 gives estimate 0.5, and se is √(1/10000 + 1/30000) recovered from lo and hi.
  - `poisson_dropsUnusableRows`:
    - a row with denominator NaN and a row with 0 give the warning `2 rows dropped: missing or non-positive exposure`;
    - a table where every row is unusable gives the warnings `No rows left to fit.`, empty terms and no throw.
  - `poisson_summaryNamesExposureSources`: with `exposure_source` and `provenance` columns, the summary value is `bts-passenger-miles (measured) + bts-passenger-miles (survey_estimate)`.
  - `poisson_defaultBindings_usesRateClassAndYear`.
- [ ] **Step 2: Run** `npx vitest run src/stats/poisson`. Expected: FAIL.
- [ ] **Step 3: Implement,** and register the method after `heatmap`.
- [ ] **Step 4: Run** `npm test`. Expected: PASS. `MethodPanel` renders the new method's inputs and params with no UI change (the plan 3 contract).
- [ ] **Step 5: Commit:** `feat(stats): poisson-regression with negative binomial option`

### Task 9: `logistic-regression` method

**Files:**
- Create: `web/src/stats/logistic-regression.ts`
- Modify: `web/src/stats/registry.ts` (append)
- Test: `web/src/stats/logistic-regression.test.ts`

**Interfaces:**
- Consumes: Tasks 5, 6 and 8 (`termsFromFit`, `renderRegression`, `RegressionResult`).
- Produces: `export type LogisticParams = { confidence: number }`, plus `export const logisticRegression: Method<LogisticParams, RegressionResult>`, with id `logistic-regression`.
- **Method.**
  - Name: "Logistic regression (odds ratios)".
  - Inputs:
    - `outcome` (count), label "With the outcome";
    - `trials` (count), label "Out of";
    - `covariates` (categorical or continuous, multiple, required).
  - `defaultBindings`: `outcome = vehicles_with_occupant_fatality` and `trials = vehicles`, when present.
- **Terms.**
  - A continuous term has level `per unit`, with exp(β) as the odds ratio per one unit.
  - A not-estimable level has note `not estimable: all or none had the outcome`.
- **Summary rows:** the same as Poisson, without `baseline rate` and `exposure sources`, and with `baseline probability`. That is expit(β₀): the probability at all reference levels and continuous covariates at 0.
- **Extra warning:** when the fit did not converge, or any |β| exceeds 15 on a non-aliased column: `Estimates are unstable (possible separation: a covariate predicts the outcome almost perfectly). Do not read these odds ratios.`

- [ ] **Step 1: Write the failing tests:**
  - `logistic_2x2_oddsRatio`:
    - exposed = 20/100, unexposed = 10/200; the reference is unexposed (more trials);
    - estimate 4.75, lo 2.12838802, hi 10.60074532 (1e-5).
  - `logistic_continuous_perUnit`: the Task 6 continuous data gives term `x`, level `per unit`, estimate 1.6001176, lo 1.2784029 and hi 2.0027928 (1e-5).
  - `logistic_dropsUnusableRows`: successes above trials, zero trials and a NaN continuous value each produce their Task 5 warning text.
  - `logistic_separation_warns`: x = 0…5, size 20 each, successes = [0,0,0,20,20,20]. The warning starts `Estimates are unstable`.
  - `logistic_categoricalAllOrNone_notEstimable`: a level with 0 of 24 has note `not estimable: all or none had the outcome`.
- [ ] **Step 2: Run** `npx vitest run src/stats/logistic`. Expected: FAIL.
- [ ] **Step 3: Implement** and register the method after `poisson-regression`.
- [ ] **Step 4: Run** `npm test`. Expected: PASS.
- [ ] **Step 5: Commit:** `feat(stats): logistic-regression for case-only attributes`

### Task 10: Presets, live check, docs

**Files:**
- Modify: `web/src/experiments/presets.ts`, `web/src/experiments/presets.test.ts`, `web/src/experiments/presets.live.test.ts`
- Modify: `web/src/api/columns.ts` (`COLUMN_HELP`: `estimate`, `p`, `note`)
- Modify: `README.md` (Experiments: the count and the list; one paragraph on reading rate ratios and odds ratios)
- Create: `docs/verification/plan-4.md`
- Modify: `docs/superpowers/plans/2026-11-04-plan-1-carryover.md` (plan 4 items; move the remaining mixed-granularity gap note as Task 1 changes it)

**Interfaces:**
- Consumes: everything above.
- **Presets.** Four are appended, so there are 10. `logistic` means `logistic-regression`; `poisson` means `poisson-regression` with family `negative_binomial`.

  7. **Poisson.** Name: "Deaths per passenger-mile by vehicle class, airlines included, adjusted for year".
     - Query: rates, `passenger_miles`, 2019–2023, `groupBy: ['vehicle_class', 'regulation_class']`.
     - Covariates: `rate_class`, `year`. `per` = 1e8.
     - Note: `PART_121_NOTE` + " Rate ratios are relative to light-duty vehicles (cars and light trucks), the largest exposure. Compare part_121 with bus for the like-for-like large-vehicle comparison."
  8. **Poisson.** Name: "General aviation by aircraft class, adjusted for year — rate ratios per flight hour".
     - Query: rates, `flight_hours`, 2019–2023, `groupBy: ['vehicle_class', 'regulation_class']`.
     - Covariates: `vehicle_class`, `year`. `per` = 1e5.
     - Note: the existing GA note.
  9. **Logistic.** Name: "Road: did an occupant die? By vehicle class and vehicle age".
     - Query: aggregate, vehicle grain, 2019–2023, bucket `none`, `groupBy: ['vehicle_class', 'vehicle_age_years']`, `filters: { mode: ['highway'] }`.
     - Covariates: `vehicle_class`, `vehicle_age_years`.
     - Note: "FARS covers fatal crashes only: this is the odds that someone aboard died, given the vehicle was in a crash where someone died. Vehicle age enters as a straight line (odds ratio per year of age)."
  10. **Logistic.** Name: "Aviation accidents: did an occupant die? By regulation class and aircraft class".
      - Query: aggregate, vehicle grain, 2019–2023, `groupBy: ['regulation_class', 'vehicle_class']`, `filters: { mode: ['aviation'] }`.
      - Covariates: `regulation_class`, `vehicle_class`.
      - Note: "NTSB accidents only: the odds that someone aboard died, given the aircraft had an accident. Maintenance regime is derived from the FAR part, so it is left out (it would be aliased with regulation class)."

- [ ] **Step 1: Update the preset tests.**
  - The count becomes 10.
  - `presets_bindToColumnsTheirQueryReturns` covers the new four. Its `columnsOf` already handles vehicle-grain aggregates.
  - The rate-carry assertion stays scoped to `slice(0, 4)`.
  - Add `regression presets use negative_binomial for Poisson`.
- [ ] **Step 2: Implement** the presets and help texts. Run `npm test`. Expected: PASS.
- [ ] **Step 3: Live check.** With the stack rebuilt, run `cd web && TA_API=http://localhost:8080 npm test -- presets.live`. The live test, extended for regression presets, asserts for each new preset:
  - the result has at least one estimated term;
  - `converged` is true;
  - there is no `No rows left to fit.` warning.
  Expected: PASS.
- [ ] **Step 4: Write** `docs/verification/plan-4.md`. Record, for presets 7–10 on live data:
  - the reference level;
  - every estimated ratio with its interval;
  - the Pearson dispersion under `poisson`, and θ under `negative_binomial`, for presets 7 and 8;
  - the warnings shown.
  Also add one cross-check: preset 7 under `poisson` with only `rate_class` as covariate. Its part_121 rate ratio must equal the pooled Part 121 rate divided by the pooled light_duty rate from the rate method, to 1e-9 relative (a saturated one-factor model).
- [ ] **Step 5: Browser check** (the controller does this with the Chrome tools). For each new preset, load it at `http://localhost:8080/` and confirm:
  - the forest plot renders with the dashed rule at 1;
  - the tables fit without page-level horizontal scroll;
  - the warnings appear.
  Fix what is found in this task.
- [ ] **Step 6: README and carry-over.**
- [ ] **Step 7: Commit:** `feat: regression presets, live verification, docs`
