# Changelog

All notable changes to this project are documented here, one line per released version.
Format loosely follows [Keep a Changelog](https://keepachangelog.com/); this project does not yet
follow strict SemVer guarantees (pre-1.0).

## 0.6.0 — 2026-08-16

Add selection identity readback (`SelectionInspector.GetSelection`, closing the "which edge/face
was actually selected" gap `DocumentStateProbes.GetSelectionCount` alone cannot answer) and
`ComTypeInspector.DescribeAllMembers`, a union-of-both-discovery-mechanisms entry point for
objects (like a document root) where the `ITypeInfo` and interop-assembly probes each see a
different, non-overlapping slice of the real member surface.

## 0.5.0 — 2026-08-16

Fix a round of review findings: `ComPath` now walks segments through a strictly
property-get-only reader instead of one that could silently invoke a zero-argument method;
`SwDispatcher` gained a Windows message pump on its STA thread, a per-call timeout
(`SwDispatchTimeoutException`), and a bounded `Dispose`; `ResultConverters` gained an
`ownsReference` flag so converting a shared COM reference no longer silently disconnects it for
every other holder; assorted RCW-lifetime and ambiguous-document-name fixes.

## 0.4.0 — 2026-08-16

Add the STA dispatcher (`SwDispatcher`, all COM work serialized onto one dedicated thread) and
the generic write surface: `ComInvoker` (single-dispatch-flag COM invocation), `ComPath`
(dotted, read-only path resolution), result converters (`FeatureRef`/`SketchSegmentRef`),
`DocumentStateProbes`, and `DocumentManager.NewPart`. Migrates the existing read path onto the
same dispatcher.

## 0.3.0 — 2026-08-16

Add `ComTypeInspector.FindImplementedInteropInterfaces`/`DescribeMembersViaInterop`: an
interop-assembly cast-probing fallback for discovering a late-bound COM object's real members
when its own type information (`ITypeInfo`) reports none — the common case for SolidWorks
feature-definition objects.

## 0.2.0 — 2026-08-16

Add `ComTypeInspector.DescribeMembers`: discovers a late-bound COM object's real members via
`IDispatch::GetTypeInfo`, falling back to `IProvideClassInfo` for objects (like SolidWorks
document coclasses) that report no embedded type information of their own.

## 0.1.0 — 2026-08-15

Initial release: an MIT-licensed, MCP-agnostic .NET abstraction over the SolidWorks COM API —
lazy connection/reconnection (`SwConnection`), document enumeration/resolution
(`DocumentManager`), and generic, schema-driven feature-tree reading (`ModelInspector`,
`ComPropertyReader`, `PropertySpec`).
