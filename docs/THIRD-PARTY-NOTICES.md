# Third-party notices

SwBridge (MIT-licensed, see [`LICENSE`](../LICENSE)) depends on the following third-party
packages. This file exists to make that dependency's provenance explicit — see also the
"Requirements" section of [`README.md`](../README.md).

## Runtime dependencies (shipped as `<dependency>` entries in the SwBridge NuGet package)

### SolidWorks.Interop.sldworks and SolidWorks.Interop.swconst (version 32.1.0)

- **What they are:** .NET interop assemblies for the SOLIDWORKS 2024 COM API. They contain no
  SolidWorks logic themselves — they are typed wrappers (interfaces, enums, coclass shims) over
  the COM interfaces SOLIDWORKS itself exposes, generated from SOLIDWORKS' own type library.
  Functionally identical copies ship inside every SOLIDWORKS installation, under
  `<install>\api\redist\`.
- **Published by:** `avidesk` on nuget.org — a third-party account, **not** Dassault Systèmes.
  The packages' own metadata sets `<owners>Dassault Systèmes</owners>`, which is misleading;
  Dassault Systèmes does not publish or maintain these NuGet packages.
- **License stated in the package:** **none.** Neither package's `.nuspec` declares a
  `<license>`/`<licenseUrl>` element or any other license grant. Restoring these packages via
  NuGet does not, by itself, grant you any rights to use, copy, or redistribute the DLLs inside
  them.
- **What actually grants rights to these DLLs:** a SOLIDWORKS software license. The DLLs
  themselves are Dassault Systèmes SolidWorks Corporation's copyrighted work (part of the
  SOLIDWORKS product), and using them is governed by your SOLIDWORKS license agreement, not by
  anything nuget.org or these package publishers state.
- **Redistribution permission:** every SOLIDWORKS installation ships a `redist.txt` file at
  `api\redist\redist.txt` (verified present, byte-checked, in both a 2021 and a 2026 install)
  stating: *"Subject to the license terms for the software, you may redistribute the following
  .DLL files unmodified"* — a list that includes both `SolidWorks.Interop.sldworks.dll` and
  `SolidWorks.Interop.swconst.dll`. So redistributing these two DLLs *unmodified*, subject to
  your SOLIDWORKS license terms, is something Dassault Systèmes has affirmatively permitted; it
  is the "no stated license on the NuGet package itself" gap above that this notice exists to
  flag, not a claim that redistribution is prohibited.
- **Integrity of the published copies:** the nuget.org copies were verified to be byte-identical
  (full public key and public key token match, strong-name bit set) to the official DLLs shipped
  inside a real SOLIDWORKS installation — a repackager cannot re-sign these with Dassault's
  signing key, so "unmodified" is a verifiable, not merely asserted, property of the packages as
  published at the time of this review.
- **SwBridge does not embed or redistribute these DLLs.** The published `SwBridge` NuGet package
  contains only `SwBridge.dll`, `SwBridge.xml`, and `README.md` — the two interop assemblies are
  ordinary transitive `<dependency>` entries that NuGet resolves separately, from nuget.org, at
  restore time on the consumer's machine.
- **Alternative if you would rather not depend on the community-republished copies:** SwBridge's
  build supports an `$(SolidWorksApiRedist)` MSBuild property that, when set to your own licensed
  SOLIDWORKS installation's `api\redist` folder, binds directly against your install's own copies
  of these two DLLs instead of restoring the NuGet packages above. See the README's Requirements
  section and the comment above the relevant `<ItemGroup>`s in
  [`src/SwBridge/SwBridge.csproj`](../src/SwBridge/SwBridge.csproj).

## Build/test-only dependencies (not shipped, not redistributed)

The following are referenced only by the test project (`tests/SwBridge.Tests`) or as build
tooling, and are **not** included in — nor a dependency of — the published `SwBridge` NuGet
package:

- `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio` — MIT-licensed test
  infrastructure, used only to build and run the repository's own test suite.
- `Microsoft.SourceLink.GitHub` — MIT-licensed build-time tooling (`PrivateAssets="All"`) that
  embeds source-debugging metadata into the package's symbol file (`.snupkg`); it produces no
  runtime dependency and ships nothing into `SwBridge.dll` itself.

None of the above are SOLIDWORKS-related and none carry the provenance questions described above.
