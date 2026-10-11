# UERegister 26.10.10.1

UERegister is a Windows command-line utility for registering custom Unreal Engine builds for the current user. Supports UE4/UE5 release, Debug and DebugGame editors, and uncompiled source trees with engine version metadata. Engine registration needs neither administrator rights nor an Epic Launcher installation. Association repair requests administrator elevation.

## Usage

Extract the portable ZIP and run `UERegister.exe` from a terminal. Requires 64-bit Windows and .NET Framework 4.8. Registrations are stored in `HKCU\Software\Epic Games\Unreal Engine\Builds`.

```powershell
UERegister list
UERegister ls --json
UERegister update A:\Dev W:\UnrealEngine --dry-run
UERegister update A:\Dev W:\UnrealEngine
UERegister clean --dry-run
UERegister remove "OldEngineAlias" --yes --non-interactive
UERegister backup before-changes.json
UERegister restore before-changes.json --dry-run
UERegister restore before-changes.json --yes --non-interactive
```

Normal commands show the application header. Update displays `Scanning <folder>` on one line using carriage returns, then `Done.` and the current build list. Lists align Name or GUID, Type and Location using the longest name and type in the buffered rows; `list --json` omits the header and table for scripting. Dry runs finish with the actual, unchanged registrations.

Short aliases: `l`/`ls` (list), `u` (update), `c` (clean), `f` (fullscan), `r` (remove). `fullscan` updates then cleans. With no search roots, update/fullscan use the current directory. Invalid supplied roots fail; they never cause a fallback to the current directory. Use `--` before a positional argument starting with `--`.

Scanning includes the root itself and nested folders, skips junctions/symlinks and generated directories, deduplicates overlapping paths, and stops below discovered engines. A `do.not.scan.ued` file excludes a subtree. Inaccessible directories produce warnings and a nonzero result; fullscan skips cleanup when scanning is incomplete.

## Stable project associations

- `UERegister.key` contains a friendly ID or GUID. `UE4Register.key` is the legacy fallback. Empty or multiline IDs are errors.
- Without a key file, an existing registration for that path is reused. A genuinely new engine receives a GUID, saved to `UERegister.key` before its registry entry is written.
- Existing aliases are preserved because `.uproject` files may still reference them. Use `remove <id>` to remove an alias explicitly.
- Changing the name in `UERegister.key` and running `u` registers the new name even when that engine path is already registered. The old name remains as a compatibility alias; repeated updates with the same name make no changes.
- A key pointing at a different engine is a collision. `--replace` deliberately moves that ID; two engines requesting the same ID in one scan always fail.
- Epic Launcher engines listed in `LauncherInstalled.dat` are excluded from update. List includes them and UEFN. A malformed manifest aborts the operation rather than risking custom registration of retail builds.
- Version detection reads `Engine/Build/Build.version`, falling back to `Version.h`. Editor executable variants and version metadata follow the same rules as UEDesktop; the utilities remain independently buildable.

## File associations

`UERegister a` (or `UERegister Associations`) repairs `.uproject` icons, double-click opening and context-menu actions using Epic's UnrealVersionSelector `/fileassociations` command. Discovery checks Launcher installations (including the install-directory registry value), registered engines and the current directory. No Launcher is needed when a source engine contains the selector.

```powershell
UERegister a --dry-run
UERegister a
UERegister associations W:\UnrealEngine
UERegister a "W:\UnrealEngine\Engine\Binaries\Win64\UnrealVersionSelector-Win64-Shipping.exe"
```

Windows may show a UAC prompt. Cancelling it aborts cleanly. Noninteractive mode requires an already elevated terminal; `--dry-run` never starts the selector. If the selector is missing, build it from the source engine root with `Engine\Build\BatchFiles\Build.bat UnrealVersionSelector Win64 Shipping`, then supply that engine root. Keep that engine installation in place because Windows associations refer to the selector there.

This command delegates shell registration to Epic; the engine-registration JSON backups do not back up Windows file associations.

## Cleanup and recovery

Cleanup distinguishes Ready, SourceOnly, Missing, Unavailable and Invalid. Only Missing entries on an accessible drive/share are offered for removal. Offline drives, inaccessible roots, uncompiled source trees, Debug-only builds, and ambiguous existing folders are preserved. Removal deletes registrations, never engine files. A missing engine may have been temporarily renamed; review cleanup output before accepting it.

Every registry mutation first saves a new JSON snapshot under `%LOCALAPPDATA%\UERegister\Backups`. Explicit `backup` never overwrites a file. `restore` merges the saved IDs, preserving unrelated current entries; conflicting IDs require `--replace`. To undo a newly added ID, remove it explicitly. Original engine key files are not removed by registry restore.

`--dry-run` performs no writes, including key files and automatic backups. `--non-interactive` prohibits prompts; removal, cleanup and restore require `--yes` to approve changes. Redirected input automatically enables noninteractive mode. EOF and empty responses cancel interactive prompts. Update is an explicit write command and does not prompt, except that collisions fail without `--replace`.

Exit codes: **0** success or interactive cancellation, **1** failed/incomplete operation, **2** invalid arguments or missing noninteractive confirmation. A write failure can leave earlier successfully applied entries in place; the printed backup path provides recovery. Unsupported non-string registry values stop operations rather than being coerced or lost.

## Build and test

Install Visual Studio Build Tools with the .NET desktop workload and .NET Framework 4.8 targeting pack.

```powershell
msbuild UERegister.sln /t:Rebuild /p:Configuration=Debug
.\Tests\bin\Debug\RegressionTests.exe
.\Build-Release.ps1
```

Release builds automatically run regression tests and package the exact executable that was built. The standalone script rebuilds Release, tests it, and creates `Releases\26.10.10.1\UERegister-26.10.10.1-x64-portable.zip` plus `SHA256SUMS.txt`. Rebuilding a version replaces its package only after validation. Use `/p:SkipReleasePackaging=true` for compile-only builds. `-MSBuildPath` and `-OutputRoot` override script defaults.

Tests use temporary engine folders and an in-memory registry. They do not edit your engine registrations. GitHub Actions builds and packages on Windows; it does not publish releases. Packages are unsigned.

Original source is MIT licensed; see LICENSE and THIRD-PARTY-NOTICES.md.

After association repair, UERegister verifies Windows can resolve all four project actions. A text-editor default (including Windows `UserChoiceLatest`) can still mask Epic's registration; this is reported as an incomplete repair, with guidance to choose the `.uproject` default in Windows Settings.
