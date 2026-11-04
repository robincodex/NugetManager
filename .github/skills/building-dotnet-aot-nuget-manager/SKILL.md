---
name: building-dotnet-aot-nuget-manager
description: "Use when building or extending a Windows desktop NuGet package manager with .NET 10, Native AOT, Aprillz.MewUI, and the dotnet SDK CLI; especially for solution/project inventories, package upgrades, central package management, transactional rollback, and startup recovery."
---

# Building a .NET AOT NuGet Manager

## Overview

Use the SDK as authority for project evaluation and package operations; let the app coordinate scopes, previews, safe transactions, and recovery. Verify with real SDK fixtures and a published native executable.

## Procedure

1. **Define behavior.** Separate solution/project scopes and direct/transitive/implicit/outdated packages. Specify browse, install, uninstall, checked batch upgrades, cancellation, all-or-nothing behavior, and interrupted recovery.
2. **Inspect SDK and project semantics.** Respect `global.json`, target frameworks, `NuGet.Config`/source mapping, lock files, imports, and `Directory.Packages.props`. Use MSBuild evaluation to locate effective declarations; deserialize versioned JSON with source-generated `System.Text.Json` types and reject unknown output shapes.
3. **Build a fresh inventory.** Restore before querying: `--no-restore` can return stale `obj` assets after external project edits. Restore sequentially when projects share `obj` paths; then retain project, TFM, requested/resolved version, and direct/transitive kind from JSON. Use `--locked-mode` for existing lock files and don't create new ones during read-only loading. For .NET 10, use:

   ```powershell
   dotnet package list --project <project.csproj> --include-transitive --no-restore --format json --output-version 1
   dotnet package list --project <project.csproj> --outdated --no-restore --format json --output-version 1
   ```
4. **Plan safe writes.** Preview target versions, affected projects/files, major-version risks, and CPM impact. Do not flatten conditional, imported, or `VersionOverride` references; block unsafe edits. Use SDK add/remove for standard references and update a shared `PackageVersion` once.
5. **Make writes recoverable.** Journal original bytes and hashes for each expected project, central props, and existing lock file; serialize writes. After edits, restore and refresh. On failure/cancel, revert only files still provably owned by the operation, then restore original configuration. Persist a startup recovery manifest; never overwrite conflicts or call a partial rollback complete.
6. **Keep UI and errors safe.** Use a controller/view model and UI dispatcher. Guard the entire plan-preview-confirm-execute flow against repeated clicks, disable conflicting actions while busy, debounce search, cancel obsolete work where supported, and show actionable errors.
7. **Test the product.** Use TDD and real-SDK tests in isolated workspaces with local feeds/config. Cover multi-project/TFM, CPM, stale assets, cancellation/failure, corrupt manifests, and interrupted conflicts. Run all tests and `git diff --check`; publish via `dotnet publish <project> -c Release -r win-x64 -p:PublishAot=true`, launch that exact exe, exercise load/search/install/uninstall/cancel/batch/recovery, and verify files, UI status, AOT diagnostics, and child-process exit.
8. **Document limits.** Explain SDK/global.json and solution-selection rules, CPM effects, private-source detail limits, and that restore success is not a compile check.

## Quick Reference

| Concern | Safe default |
|---|---|
| Package data | SDK JSON + evaluated MSBuild declarations |
| Freshness | Restore first; then query with `--no-restore` |
| Shared versions | Preview all loaded affected projects and files |
| Batch mutation | Journal → confirm → write → restore → refresh |
| Conflict recovery | Preserve current file; retain actionable recovery record |
| AOT confidence | Publish and launch the win-x64 native executable |

## Common Mistakes

- Parsing localized CLI tables or silently accepting an unknown JSON shape.
- Trusting `package list --no-restore` without ensuring assets match current project inputs.
- Treating a package ID as one version across all projects and target frameworks.
- Assuming CPM changes only the currently selected project.
- Rolling back with Git commands, broad directory cleanup, or backups that overwrite later user edits.
- Claiming AOT support from a successful ordinary build or adding broad reflection roots to suppress warnings without evidence.
- Treating restore success as proof the project compiles.
