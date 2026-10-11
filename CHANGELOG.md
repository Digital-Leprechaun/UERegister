# Changelog

## 26.10.10.1

- Honor scan exclusion markers inside engine roots.
- Preserve positional arguments after `--` when input is redirected.
- Fall back to Version.h when Build.version has null, missing, invalid or oversized version fields.

## 26.9.16.7

- Verify effective Windows .uproject verbs after association repair and report incomplete repairs caused by per-user default overrides.

## 26.9.16.6

- Add a/associations to repair .uproject shell integration through UnrealVersionSelector, with automatic discovery, explicit path, dry-run and UAC cancellation handling.
- Separate commands from options in help and remove its exit-code explanation.

## 26.9.16.5

- Resolve engine directory spelling from disk and refresh existing registry paths, including aliases, when only casing or separators differ.

## 26.9.16.4

- Add a separator below list headers and two tabs between padded columns.

## 26.9.16.3

- Align list headers and rows using calculated name/type column widths, including long GUIDs. Preserve list ordering and JSON output.

## 26.9.16.2

- Restore the console header, add CR-only folder scan progress and completion output, and list builds after update.
- Simplify human-readable build lists to name, type and location separated by tabs; retain clean JSON output.

## 26.9.16.1

- Preserve existing IDs and aliases; persist new IDs and detect collisions.
- Scan roots and nested directories safely; reject invalid search paths.
- Exclude launcher installations and distinguish unavailable, source-only and missing engines.
- Add dry runs, JSON output, scriptable removal, noninteractive operation and meaningful exit codes.
- Back up registrations before mutations; provide explicit backup and merge restore commands.
- Separate discovery, registration planning, registry access and console handling with regression tests.
- Add MIT license, documentation, Windows CI and versioned unsigned ZIP packaging on Release builds.
- Align build prerequisites with .NET Framework 4.8; remove missing icon reference.
