# CLAUDE.md

`SwBridge` is a fully open-source (MIT) .NET abstraction of the SolidWorks COM API. It is **MCP-agnostic**: this repo must contain zero MCP, licensing, tier, or product code. If a change would be useless to someone building a non-MCP SolidWorks tool, it belongs in the `swmcp` repo instead. See the workspace-level `../CLAUDE.md` for the full boundary rules.

## Commands

```bash
dotnet build SwBridge.sln
dotnet test SwBridge.sln
dotnet run --project samples/SwBridge.Sample    # live smoke test — requires SolidWorks running
dotnet pack src/SwBridge/SwBridge.csproj -c Release -o ../localnuget
```

The pack output feeds `swmcp`'s local NuGet source (`../localnuget` at the workspace root) during development; the published package on nuget.org is the eventual real dependency channel.

## Architecture

- `SwConnection` — lazy attach to the running `SldWorks.Application` via `oleaut32!GetActiveObject`; liveness-checks the cached RCW on every access and re-attaches if SolidWorks was closed/restarted. Never launches SolidWorks.
- `DocumentManager` — enumerate/resolve open documents; throws `SwNotRunningException` (rather than returning empty) so callers can distinguish "no SolidWorks" from "no documents".
- `SwDocument` — wrapper so consumers rarely touch interop types; raw `ModelDoc2` stays exposed via `.Model`.
- `ModelInspector` — generic feature-tree reading. The central rule: **no feature type is hardcoded**. Callers pass a `Func<string, IReadOnlyList<string>?>` mapping feature type name → property names, and properties are read off `IFeature.GetDefinition()` by reflection (`ComPropertyReader`).
- `ComPropertyReader` — `Type.InvokeMember(..., GetProperty | IgnoreCase)` against late-bound COM objects; unreadable properties are reported as absent, never thrown.

## Rules

- **No console output from library code** — consumers include stdio-based servers where stdout is a protocol stream.
- Public API avoids leaking interop types where practical; where they appear (`SwDocument.Model`, `ModelInspector` parameters) it is deliberate, for advanced consumers.
- Tests in `tests/SwBridge.Tests` must run **without** SolidWorks installed. Anything needing a live instance goes in `samples/` (currently `SwBridge.Sample`, which doubles as the manual smoke test).
- Interop packages: `SolidWorks.Interop.SldWorks` / `swconst` 32.1.0 (SW 2024); supported floor is SolidWorks 2021+.
