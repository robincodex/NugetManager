# Themed and Localized NuGet UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver the approved native theme/language controls, segmented tabs, multi-source browsing, complete loading indicators and source-correct package link menus.

**Architecture:** Keep the SDK authoritative and the existing controller/transaction pipeline. Add explicit source-set identity, language-neutral message values and a small user preference store; bind the existing MewUI window to these rather than recreating the application on theme/language changes.

**Tech Stack:** .NET 10, Native AOT win-x64, Aprillz.MewUI 0.22.1, xUnit, existing SDK fixtures.

**Spec:** `docs\superpowers\specs\2026-11-04-themed-localized-ui-design.md`

## Global Constraints

- Continue in the current host-managed worktree; do not request another.
- No dependency or MewUI version changes without evidence and approval.
- Preserve installed inventory, source mapping/credentials, central version semantics, transaction/recovery and preview consent.
- Simplified Chinese, English, Korean and Russian; theme and language default to system and persist per user, not per workspace.
- Default all enabled sources; one selected source takes 30, multiple sources take 10 each, empty selection sends no requests.
- Use actual ProgressRing, SegmentedControl and ContextMenu controls, not text glyph substitutes.
- No reflection-based JSON or broad trimming roots for new types.
- No merge/push without a new user decision; commits include the existing Copilot coauthor trailer.

## Review Focus

- Source config changes while a subset query is in flight: no newly disabled feed reappears; explicit empty never becomes all (Task 2).
- Switching language/theme while background work completes: new and existing messages update without losing query, page, selections or consent (Tasks 1/4).
- Right-clicking an unselected duplicate ID: URL identity is the clicked source/version, not the current details pane (Tasks 3/4).
- Hidden/removed waiting controls and cancelled superseded requests: no persistent ring or animation clock (Task 4).
- Corrupt/locked preference files and malicious repository URLs: explicit failure feedback, no false persisted-success state or shell interpretation (Tasks 1/3).

## File Map

- Create `NugetManager\Presentation\AppPreferences.cs`: preference records and AOT-safe atomic file persistence.
- Create `NugetManager\Presentation\AppLocalization.cs`: explicit locale resolution and keyed immutable messages; no MewUI dependency.
- Create `NugetManager\Presentation\AppTheme.cs`: semantic palette from actual MewUI theme, shared by window/dialogs.
- Create `NugetManager\Domain\SourceSelection.cs`: all/subset/empty identity and normalization.
- Create `NugetManager\Presentation\PackageLinkMenu.cs`: clicked package identity, generation-safe menu loading and URI opening.
- Modify domain/operation message models and their producers as listed in Task 1; never translate package data or SDK output.
- Modify catalog/controller/window/metadata/Program and directly related tests as assigned below.

### Task 1: Preference persistence and live four-language presentation messages

**Files:** create `AppPreferences.cs`, `AppLocalization.cs`; modify `Program.cs`, `PresentationModel.cs`, `WorkspaceController.cs`, `OperationDialog.cs`, `MainWindow.cs`, `Domain\PackageModels.cs`, `Domain\WorkspaceModels.cs`, `Operations\OperationModels.cs`, `PackageOperations.cs`, `FileTransaction.cs`, `RecoveryCoordinator.cs`, `Packages\NuGetConfigReader.cs`, `PackageCatalog.cs`, `PackageMetadataReader.cs`, `PopularPackageReader.cs`, `Sdk\SdkClient.cs`, `SdkClient.Core.cs`, `ProcessRunner.cs`, `Workspaces\WorkspaceDiscovery.cs`; create `NugetManager.Tests\AppLocalizationTests.cs`, `AppPreferencesTests.cs`; adjust existing exact-message assertions only to explicitly format the intended locale.

**Interfaces:**
- `UiLanguage { System, SimplifiedChinese, English, Korean, Russian }`, `UiTheme { System, Light, Dark }`.
- `AppPreferences(UiTheme Theme, UiLanguage Language)`; `AppPreferenceStore(string path).LoadAsync(CancellationToken)` and `.SaveAsync(AppPreferences, CancellationToken)`; missing file returns defaults, other failures propagate to the UI boundary.
- `TextKey` enum, immutable `UiMessage(TextKey Key, IReadOnlyList<string> Arguments)`; `AppLocalization.ResolveLanguage(UiLanguage, CultureInfo)` and `Format(UiMessage, UiLanguage, CultureInfo)`; resources cover every TextKey in all four languages. Resolve zh/ko/ru to their resources and unsupported cultures to English.
- Generated status/notice/diagnostic/progress/confirmation/recovery messages retain key + arguments, not already localized strings. External text is retained as a literal payload and formatted under a localized prefix. Keep types independent of MewUI; use explicit localizer at display time, not mutable global culture in worker threads.
- Later UI uses preference changes to invalidate text render keys; domain/operation control flow remains unchanged.

- [ ] Add failing `SystemLanguageResolvesAllSupportedLanguages`, `EveryKeyHasMatchingFormatArguments`, `ExistingMessagesCanRenderInAllFourLanguages`, `FormattingDoesNotTranslateLiteralSdkOutput`; assert zh/en/ko/ru status strings, en fallback for unsupported culture, unchanged package IDs/paths and complete resources (no silent missing-key fallback). Add `PreferencesRoundTrip` for all four languages, `MissingPreferencesUseSystemDefaults`, `CorruptPreferencesAreReported`, `FailedWritePreservesOriginalFile` in isolated directories.
- [ ] Run `dotnet test NugetManager.Tests\NugetManager.Tests.csproj --filter "FullyQualifiedName~AppLocalizationTests|FullyQualifiedName~AppPreferencesTests"`; expected red for absent interfaces, then assertion red for missing behavior.
- [ ] Implement typed resource/message and preference interfaces. Source-generate preference JSON; save through a sibling temporary file and atomic replacement, clean owned temporary file on failure. Convert application-generated text at its producer, including blocked operations and cached details origins; preserve SDK/OS/package data as literals. Surface preference failures; no silent catch.
- [ ] Wire startup load and error reporting, retaining default startup only with an explicit readable warning if loading fails. Do not add selectors yet. Run targeted tests; expected all pass.
- [ ] Run existing tests covering changed producer paths and commit `feat: add persistent preferences and localized messages`.

### Task 2: Explicit multi-source browsing and controller cache identity

**Files:** create `Domain\SourceSelection.cs`; modify `Packages\PackageCatalog.cs`, `Presentation\WorkspaceController.cs`, `Domain\PackageModels.cs`, `NugetManager.Tests\SourceBrowseTests.cs`, `SourceControllerTests.cs`, `WorkspaceControllerTests.cs`, `PackageCatalogTests.cs`, `PackageWorkflowTests.cs`.

**Interfaces:**
- `SourceSelection.All`; `SourceSelection.Subset(IEnumerable<string> names)` (including empty); `Resolve(IReadOnlyList<NuGetSource>)` returns enabled canonical names; equality/cache key is order-insensitive and case-insensitive, retaining all intent.
- Replace catalog search/browse optional single name with `SourceSelection` and controller `SelectedSourceName` with `SelectedSources`. Keep versions/details single concrete source identity.
- `SelectSourcesAsync(SourceSelection selection, CancellationToken ct, string? query = null)`.
- `BrowseAsync` uses resolved source count for 30/10, zero calls for empty. Search preserves per-source groups/diagnostics and existing per-source 20 paging; add `SearchPage.Sources` grouping without duplicating hit storage, or a derived grouping projection. Update all call sites/fakes coherently, remove obsolete single-selection adapters.
- Cache identity `(workspace, normalized resolved source set, prerelease)`; config refresh clears cache and reconciles all/subset intent.

- [ ] Add failing cases `EmptySelectionDoesNotQuerySdk`, `SubsetOnlyQueriesChosenSources`, `OneSourceThirtyMultipleTen`, `SourceOrderDoesNotChangeCacheIdentity`, `AllSelectionIncludesNewSourcesButSubsetDoesNot`, `RemovedSubsetCanBecomeEmpty`, `LateSubsetResponseCannotReplaceNewSelection`, `ChangingSourcesUsesPendingTextboxQuery`. Include real SDK three-source fixture for selecting two of three, duplicate IDs and paging.
- [ ] Run `dotnet test NugetManager.Tests\NugetManager.Tests.csproj --filter "FullyQualifiedName~SourceBrowseTests|FullyQualifiedName~SourceControllerTests"`; expected relevant failures.
- [ ] Implement selection resolution, SDK-per-source queries, diagnostics, pagination and controller state/cache. Do not infer all from empty. Installed data and single-source metadata identity remain unchanged.
- [ ] Run source/controller/workflow tests in one runner; expected all pass. Commit `feat: browse selected NuGet source sets`.

### Task 3: Repository metadata and source-correct link menu model

**Files:** modify `Domain\PackageModels.cs`, `Packages\PackageMetadataReader.cs`, test package builder `NugetManager.Tests\TestWorkspace.cs`; create `Presentation\PackageLinkMenu.cs`, `NugetManager.Tests\PackageLinkTests.cs`; extend `PackageMetadataReaderTests.cs`.

**Interfaces:**
- Add `PackageDetails.RepositoryUrl`, `PackageDetails.PackagePageUrl` optional Uri properties and concrete origin source identity needed for installed menu resolution; do not overwrite ProjectUrl.
- `PackageLinkIdentity(string WorkspaceDirectory, string PackageId, string Version, string? SourceName)`.
- `PackageLinks(Uri? NuGetPage, Uri? GitHubRepository)`.
- `PackageLinkResolver.Resolve(PackageLinkIdentity, PackageDetails?)` validates known origin and URLs; official nuget.org source recognition reused from existing verified source helper rather than duplicated.
- `IExternalLinkOpener.Open(Uri)` and production opener with `ProcessStartInfo.UseShellExecute=true`; only validated absolute HTTP(S), no shell command construction.
- Menu loading has its own generation/identity and cancellation, not selected detail-pane state; close/workspace change invalidates pending work.

- [ ] Add failing tests `RepositoryElementBeatsProjectHomepage`, `ProjectUrlGitHubFallback`, `NoRepositoryDisablesGitHub`, `PrivateSourceDoesNotInventPublicPage`, `KnownPublicOriginBuildsVersionPage`, `ClickedSourceDoesNotReuseOtherDetails`, `ClosedMenuIgnoresLateMetadata`, `OpenFailureIsReported`.
- [ ] Add URL table cases: github.com owner/repo, trailing .git, git HTTPS normalization, github.com.evil.example, userinfo, file/javascript protocols, malformed/SSH/scp and nonrepository URLs. Assert only valid repository root HTTP(S) links are enabled.
- [ ] Run metadata/link tests; expected red, including real local nuspec repository parsing.
- [ ] Implement nuspec extraction and validation shared across cached/local/remote paths. Treat metadata error versus missing URL distinctly. Keep selected-source cache provenance invariant and mapping/credential behavior.
- [ ] Run link and metadata tests; expected all pass. Commit `feat: resolve package page and GitHub repository links`.

### Task 4: Native theme, segmented tabs, source checklist, rings and context menus

**Files:** create `Presentation\AppTheme.cs`; modify `MainWindow.cs`, `OperationDialog.cs`, `PresentationModel.cs`, `Program.cs`; extend `MainWindowStateTests.cs`; create `NugetManager.Tests\NativeUiStateTests.cs`; update `README.md`.

**Interfaces:** consume Tasks 1–3; pure `BuildActivity` adds installed workspace/check states; semantic palette rebuilt from `ThemeManager.CurrentTheme`; native UI uses installed APIs verified against 0.22.1 XML/tagged source.

- [ ] Inspect ProgressRing, SegmentedControl, ThemeManager/System notification, popup/checklist and ContextMenu APIs and sample patterns before binding; no guessed APIs or unnecessary package changes.
- [ ] Add failing tests `InstalledWorkspaceLoadingHasMiddleRing`, `CheckingWithExistingInventoryHasRing`, `CancelledAndFailedChecksStopRing`, `ThemeAndLanguageChangesPreserveWorkspaceSelectionAndPage`, `DarkPaletteHasReadableSelectedWarningAndDetailText`, `SourcePickerDisabledOnlyForInstalledOrConflictingOperation`, `RightClickDoesNotChangeBatchSelection`, `ContextMenuRetainsClickedSource`.
- [ ] Run `dotnet test NugetManager.Tests\NugetManager.Tests.csproj --filter "FullyQualifiedName~MainWindowStateTests|FullyQualifiedName~NativeUiStateTests"`; expected failures for missing state/control binding.
- [ ] Wire top theme/language selectors with preference save/error feedback; apply MewUI theme and invalidate only affected render caches. Bind all native strings via Task 1 resource service; do not rebuild controller or reset search when changing preferences.
- [ ] Replace tabs with SegmentedControl. Build source popup with select-all/indeterminate, individual checkboxes and explicit empty state. Submit current query alongside selection. Respect per-source grouped results and in-flight generations.
- [ ] Add independent middle workspace/check rings, stop inactive/hidden clocks. Wire source-aware native context menu to every package-row surface, retain disabled missing links, update enabled state only for current open menu identity.
- [ ] Theme all dynamic row/header/selection, text, glyph and dialog colors via AppTheme. Check actual controls and palette refresh rather than testing only upstream setters.
- [ ] Document preferences, all four supported languages, multi-source semantics and link restrictions in README. Run targeted tests; expected all pass. Commit `feat: deliver themed localized native package interactions`.

### Task 5: Full regression and final native verification

**Files:** only directly related tests/docs and fixes if verification reveals a regression; temporary native automation and fixtures belong to this session, not production.

- [ ] Run `dotnet test NugetManager.Tests\NugetManager.Tests.csproj`; expected all pass with no hidden skipped suites; report existing warnings separately.
- [ ] Run `dotnet publish NugetManager\NugetManager.csproj -c Release -r win-x64 -p:PublishAot=true`; expected exit 0, no new AOT/trim warnings.
- [ ] Launch the exact exe on an isolated three-source fixture. Verify first installed workspace/check loading, cancel/retry/terminal rings, light/dark/system, zh/en/ko/ru instant switch and preferences after restart, Korean font fallback and Russian long-label layout, segmented tabs, all/two/one/zero source selection, 10/30 limits, source-specific duplicate details, paging and config changes.
- [ ] Right-click a nonselected installed/browse row with/without repository. Verify menu disabled/enabled state, correct source/version, safe link opening with controlled HTTP(S) fixture, no changed upgrade checkboxes, and menu-close stale response isolation. Preserve original package files.
- [ ] Recheck upgrade preview/confirm/cancel and recovery dialog under light/dark themes and all four languages. Stop exact owned PID and verify no SDK children. Record any environment-blocked manual checks explicitly; clean only owned fixture paths.
- [ ] Run `git diff --check`, commit related verification fixes with regression tests. Perform one fresh-context whole-branch review; reproduce and fix important findings TDD, rerun full suite and publish if product changes.
- [ ] Ask user for integration choice; do not auto-merge.

## Self-review

All six requested surfaces have task owners. Tasks 1/2/3 publish explicit interfaces consumed by Task 4; Task 5 verifies final production behavior. Source-set identity remains separate from clicked package source. Locale resolution is explicit, preserving test parallelism and background messages. No transaction algorithm changes are planned. Unknown/private NuGet links remain disabled rather than guessed. Design remains authoritative if installed API details require a narrower implementation ruling.
