# Source Browse and MewUI Status Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add source-aware initial browsing, honest popular-package results, and native MewUI loading/icon presentation.

**Architecture:** Extend the existing catalog and controller rather than introducing a second UI workflow. Preserve source identities in grouped browse results, use verified popular-source capabilities with explicit SDK ordinary-browse fallback, and keep installed inventory independent of the selected browse source.

**Tech Stack:** .NET 10, win-x64 Native AOT, Aprillz.MewUI 0.22.1, SDK CLI JSON, source-generated System.Text.Json.

**Spec:** [Approved design](../specs/2026-11-04-source-browse-ui-design.md).

## Global Constraints

- Default 全部源: each enabled configured source contributes at most 10 initial items; single source: at most 30.
- These limits apply to empty-query initial browsing, not keyword-search pagination.
- No source-config or credential edits; installed inventory remains complete.
- Duplicate package IDs across sources remain separately selectable.
- 热门 means verified download-popularity capability; otherwise label 普通浏览.
- Keep Native AOT, existing transaction/recovery behavior, and stale-response generation checks.

## Review Focus

- Same ID on two feeds: source-specific result, version, and details must remain distinguishable (Tasks 1–2).
- Source removed/disabled on refresh: reset selection to 全部源, invalidate obsolete cache (Task 2).
- Slow source response after selection changes: old data never replaces new results (Task 2).
- Private/local source lacking popularity or failing authentication: ordinary browse or explicit source diagnostic, not fake popularity (Task 1).
- Loading animation during search, details, update check, and writes: independent lifetimes and retained cancellation (Task 3).

## File Map

- Modify `NugetManager\Domain\PackageModels.cs`: source groups and initial browse types.
- Modify `NugetManager\Packages\PackageCatalog.cs`: source selection, browse limits, grouped results.
- Create `NugetManager\Packages\PopularPackageReader.cs`: only verified anonymous popularity capabilities.
- Modify `NugetManager\Sdk\SdkJsonContext.cs`: popularity payload types and source-generation registrations.
- Modify `NugetManager\Presentation\WorkspaceController.cs`: source selection, cache, grouped state, source-aware details.
- Modify `NugetManager\Presentation\MainWindow.cs`: source picker, ProgressRing, icons, grouped browse view.
- Modify `NugetManager\Program.cs`: composition for popular reader.
- Modify existing test helpers/interfaces as required in `NugetManager.Tests`; add catalog/controller/UI-state regression tests.
- Modify `README.md`: source behavior, ordinary/popular distinction.

### Task 1: Source-aware catalog and verified initial browse

**Files:** Domain models, catalog, popular reader, JSON context, Program; `PackageCatalogTests.cs`, `PackageMetadataReaderTests.cs`, and new `PopularPackageReaderTests.cs`.

**Interfaces:**
- Add `BrowseKind { Popular, Ordinary }`.
- Add `BrowseSourcePage(NuGetSource Source, BrowseKind Kind, SearchPage Page)` and `BrowsePage(IReadOnlyList<BrowseSourcePage> Sources)`.
- Add `IPackageCatalog.BrowseAsync(Workspace workspace, string? sourceName, bool includePrerelease, CancellationToken ct) -> Task<BrowsePage>`.
- Extend `IPackageCatalog.SearchAsync` and `GetVersionsAsync` with `string? sourceName` before the cancellation token; `null` means all configured sources.
- Add `IPopularPackageReader.ReadAsync(NuGetSource source, int take, bool includePrerelease, CancellationToken ct) -> Task<PopularReadResult>`.
- `PopularReadResult` includes explicit `Supported`, items, and diagnostics. Unsupported is a capability decision, not a suppressed network error.

- [ ] Verify SDK empty-query/source behavior using an isolated two-feed fixture. Inspect official/public implementation evidence for download ordering; record supported source capability in code documentation. If no capability can be verified, explain the limitation before labeling any result 热门.
- [ ] Write failing tests: `AllSourcesBrowseTakesTenPerSource`, `SingleSourceBrowseTakesThirty`, `SameIdAcrossSourcesIsPreserved`, `UnsupportedPopularityUsesOrdinaryBrowse`, `FailedSourceDoesNotHideSuccessfulSource`, `ExplicitSourceRestrictsSearchAndVersions`, and `PopularityDoesNotLeakCredentials`.
  Assert each page count <= 10 or <= 30, source identity retained, precise Kind, diagnostics retained, and no request to unselected feeds.
- [ ] Run `dotnet test NugetManager.Tests\NugetManager.Tests.csproj --filter "FullyQualifiedName~PackageCatalogTests|FullyQualifiedName~PopularPackageReaderTests"` and confirm meaningful failure before implementation.
- [ ] Implement source enumeration through `NuGetConfigReader.Load`, bounded per-source requests, explicit diagnostics, and no cross-source deduplication. Ordinary initial browsing uses SDK with empty query and exact take count. Never sort a small arbitrary sample and call it global popularity. Do not downgrade network/auth failures into unsupported capability silently.
- [ ] Register HTTP JSON source-generated DTOs; preserve existing composition patterns and fake interface implementations.
- [ ] Run the targeted command plus real-SDK local-feed integration tests; confirm passing.
- [ ] Commit explicit Task 1 files with `feat: browse packages by configured source`.

### Task 2: Controller source state, initial browse and cache

**Files:** `WorkspaceController.cs`, controller tests and `PackageWorkflowTests.cs`.

**Interfaces:**
- Add `WorkspaceViewState.Sources`, `SelectedSourceName`, and `Browse` using Task 1 types.
- Add `SelectSourceAsync(string? sourceName, CancellationToken ct) -> Task`.
- Add `LoadBrowseAsync(bool refresh, CancellationToken ct) -> Task`.
- Existing `SearchAsync` keeps its UI signature and forwards selected source.
- Keep result identity `(sourceName, packageId)` through selected details/version state; reuse existing metadata source argument.

- [ ] Write failing tests: `FirstBrowseLoadsAllSources`, `SingleSourceReloadsThirty`, `EmptyQueryReturnsInitialBrowse`, `InstalledScopeUnaffectedBySource`, `LateOldSourceResponseIsIgnored`, `BrowseCacheKeysIncludeWorkspaceSourceAndPrerelease`, `RemovedSourceResetsSelection`, and `SamePackageFromDifferentSourcesLoadsCorrectVersions`.
  Assert default selection null, 10/30 counts through real catalog fixtures, installed lists unchanged, stale data excluded, no repeated initial request on tab return, and explicit refresh re-fetches.
- [ ] Run `dotnet test NugetManager.Tests\NugetManager.Tests.csproj --filter "FullyQualifiedName~WorkspaceControllerTests|FullyQualifiedName~PackageWorkflowTests"` and confirm red.
- [ ] Implement initial browse/cache, per-source grouped results, generation checks, request cancellation and selection invalidation. Key cache by workspace/config/source/prerelease; refresh or config change invalidates it. Preserve keyword-search pagination, with selected source applied to subsequent pages.
- [ ] Ensure selecting a source while a query is present reruns the query, not initial browsing. Empty query loads initial groups. Scope switching still clears upgrade selection.
- [ ] Run targeted tests and confirm green.
- [ ] Commit explicit Task 2 files with `feat: coordinate source selection and initial browse state`.

### Task 3: Native MewUI progress, icons and source selection

**Files:** `MainWindow.cs`, `MainWindowStateTests.cs`, `README.md`; `OperationDialog.cs` only if it contains actionable glyphs requiring replacement.

**Interfaces:** Consume Task 2 state/actions; preserve existing tab/scope/install/upgrade UI entry points.

- [ ] Inspect installed MewUI Core API and examples to determine exact `ProgressRing`/icon APIs, sizes and activity properties. Confirm icons are AOT-safe before introducing resources; no package-version upgrade unless unavoidable and approved.
- [ ] Write failing presentation tests covering independent `IsBusy`, `SearchLoading`, `DetailsLoading` flags and group labels/counts/source availability. Assert source selector disabled only in installed mode or conflicting workspace operations, not permanently during browse.
- [ ] Run `dotnet test NugetManager.Tests\NugetManager.Tests.csproj --filter FullyQualifiedName~MainWindowStateTests` and confirm red.
- [ ] Replace loading-only text presentation with MewUI ProgressRing plus retained description for workspace/search/details. Stop hidden/inactive rings. Replace actionable arrow/expand glyphs using actual MewUI icons; keep package data and diagnostic text untouched.
- [ ] Use a `Grid` for tab buttons on left and source picker on right. Wire source changes, initial browse on first entry, source groups, per-source errors/retry, and selected-source versions/details; retain existing installation/transaction semantics.
- [ ] Update README with all-source 10 / single-source 30 limits, installed independence, source grouping, and honest ordinary/popular labels.
- [ ] Run the targeted tests, then `dotnet test NugetManager.Tests\NugetManager.Tests.csproj`.
- [ ] Publish `dotnet publish NugetManager\NugetManager.csproj -c Release -r win-x64 -p:PublishAot=true`; require exit 0 and no new AOT/trim warnings.
- [ ] Launch that native exe on an isolated two-source fixture. Verify all-source per-group limits, single-source limit, keyword search/pagination, returning tabs/cache, source-specific details, missing/failed sources, all rings, icons, and existing batch confirmation/cancel. Verify no leftover SDK children; stop the exact launched process.
- [ ] Run `git diff --check`; commit explicit Task 3 files with `feat: show native progress and source-aware browse UI`.

## Self-review

The tasks cover all confirmed behaviors. Popularity protocol support remains an evidence-gated capability, not a fabricated standard parameter. No task changes installed source semantics, NuGet credentials, or rollback policy. All-source failure reporting and duplicate identities are preserved through catalog, controller and UI.
