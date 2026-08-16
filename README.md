# SwBridge

A small, MIT-licensed .NET library for automating **SOLIDWORKS** through its COM API. Not
affiliated with or endorsed by Dassault Systèmes SolidWorks Corporation; SOLIDWORKS is their
registered trademark.

SwBridge attaches to an already-running SOLIDWORKS instance (it never launches one), resolves open documents, and reads the feature tree **generically**: instead of compiling a class per feature type, you describe per feature type which members to read — bare properties (`BothDirections`) or accessor methods with arguments (`GetDepth(true)`) — and SwBridge pulls them off the late-bound feature definition by reflection. New feature types need data, not code.

SwBridge is deliberately unopinionated infrastructure — it knows nothing about MCP, AI, licensing, or any particular product. It is usable from any .NET application that wants to script SOLIDWORKS.

> **This library modifies CAD data.** From version 0.4.0 onward it can drive destructive writes —
> creating and deleting features, editing sketches, saving files — against a live document, with
> **no undo beyond SOLIDWORKS' own undo stack and no automatic rollback** if a multi-step
> operation fails partway through (see "The dispatcher" and ADR 0002 in `docs/adr/` for why that
> is a deliberate choice, not an oversight). The only warranty is MIT's ("AS IS", no warranty of
> any kind) — there is no additional guarantee that a write does the right thing to the right
> document. **Test against copies, not documents you cannot afford to lose,** until you have
> verified a given call sequence against your own SOLIDWORKS version and documents.

This project is maintained on a best-effort basis; no support or response time is guaranteed —
see [SECURITY.md](SECURITY.md) for how to report a vulnerability privately, and
[CONTRIBUTING.md](CONTRIBUTING.md) for issues and pull requests.

## Requirements

- Windows, .NET 8 (`net8.0-windows`)
- SOLIDWORKS installed and running. SwBridge binds to the SolidWorks 2024 interop assemblies
  (`SolidWorks.Interop.sldworks`/`swconst` 32.1.0) but is late-bound almost everywhere it touches
  SOLIDWORKS (`ComPropertyReader`, `ComInvoker`, `ComPath`, `ComTypeInspector`), so it is
  *expected* to work against SOLIDWORKS 2021 and later. It has been **live-tested only against
  SOLIDWORKS 2026 SP3.0** — earlier versions are untested, not unsupported; reports of what does
  or doesn't work on your version are welcome as an issue.

**On the interop dependency**: the two `SolidWorks.Interop.*` NuGet packages above are
third-party re-publications (publisher `avidesk`, not Dassault Systèmes, despite their package
metadata's `<owners>` field) of DLLs that ship inside every SOLIDWORKS install. They carry **no
stated license of their own** — restoring them via NuGet is not a grant of rights; a SOLIDWORKS
license is what actually entitles you to use these DLLs, same as if you copied them out of your
own install by hand. Dassault's own `redist.txt` (shipped in every SOLIDWORKS install's
`api\redist\` folder) explicitly permits redistributing these DLLs unmodified, subject to the
software license terms, and the nuget.org copies are verified byte-identical to the official
ones. See [`docs/THIRD-PARTY-NOTICES.md`](docs/THIRD-PARTY-NOTICES.md) for the full disclosure.
If you would rather not depend on a community-republished copy, build against your own licensed
install instead:

```bash
dotnet build -p:SolidWorksApiRedist="C:\Program Files\Dassault Systemes\SOLIDWORKS 3DEXPERIENCE R2026x\SOLIDWORKS\api\redist"
```

Setting `$(SolidWorksApiRedist)` to your install's `api\redist` folder swaps the two
`PackageReference`s for direct `Reference`s against that folder's DLLs — see the comment above
the relevant `ItemGroup`s in `src/SwBridge/SwBridge.csproj`.

## Usage

```csharp
using SwBridge;

var connection = new SwConnection();          // lazy — attaches on first use, re-attaches if SW restarts
var documents = new DocumentManager(connection);

// Enumerate what's open
foreach (var info in documents.ListOpenDocuments())
    Console.WriteLine($"{info.Title} ({info.Type}) {info.Path}");

// Resolve a document by title, file name, or path. Throws SwBridgeException if
// the name matches more than one open document — see Design notes below.
var doc = documents.Resolve("Part2.SLDPRT") ?? documents.GetActiveDocument();

// Read the feature tree; you decide which members matter per feature type.
// Some values are bare properties, some hide behind accessor methods with args.
IReadOnlyList<PropertySpec>? Lookup(string typeName) => typeName switch
{
    "Extrusion" => new[]
    {
        new PropertySpec("Depth", "GetDepth", new object?[] { true }),
        PropertySpec.Bare("BothDirections"),
    },
    "Fillet" => new[] { PropertySpec.Bare("DefaultRadius") },
    _ => null,
};

foreach (var feature in doc!.GetFeatures(Lookup))
    Console.WriteLine($"{feature.Name} [{feature.TypeName}] {feature.Properties?.Count ?? 0} props");

// Part summary: mass, features, bounding box
var part = doc.GetPartInfo(Lookup);
```

A runnable version of this lives in [`samples/SwBridge.Sample`](samples/SwBridge.Sample).

## Discovering members instead of guessing them

Writing a `PropertySpec` above requires knowing the real member name and shape
in advance. `ComTypeInspector` finds that shape at runtime instead of guessing
it, via two independent mechanisms:

```csharp
var members = doc.DescribeFeatureDefinition("Boss-Extrude1");
foreach (var m in members ?? Array.Empty<ComMemberInfo>())
    Console.WriteLine($"{m.Name} [{m.Kind}] params={m.ParamCount} returns={m.ReturnType}");

// Works on any IDispatch-based COM object, not just SolidWorks:
var members2 = ComTypeInspector.DescribeMembers(someComObject);
```

1. **`ComTypeInspector.DescribeMembers`** reads the object's own type information
   (`ITypeInfo`, via `IDispatch::GetTypeInfo` or, when that reports none, the
   object's `IProvideClassInfo`). This works for general COM automation objects
   and for creatable SolidWorks document coclasses (`ModelDoc2`/`PartDoc`), but
   many internal SolidWorks objects — notably feature-definition objects from
   `IFeature.GetDefinition()` — publish none at all and return an empty list.
2. **`ComTypeInspector.DescribeMembersViaInterop`** is the fallback for exactly
   that case: it probes the object with a real COM QueryInterface against the
   `[ComImport]` interfaces declared in the referenced `SolidWorks.Interop.sldworks`
   assembly (e.g. `IExtrudeFeatureData2`), then reflects whichever interfaces the
   object actually implements — pass a `filter` (e.g. interface name containing
   `"FeatureData"`) to keep the probe count small, since an unfiltered call
   probes every interface in the assembly. `DescribeFeatureDefinition` already
   does this automatically when the `ITypeInfo` path finds nothing.

An empty result from `DescribeMembers` alone means "not introspectable via
`ITypeInfo`," not necessarily "no members" — try `DescribeMembersViaInterop`
next. Neither mechanism throws for objects it can't handle.

**These two do not always find the same members on the same object.** A
document root (a live `ModelDoc2`/`PartDoc`) answers `DescribeMembers` with a
real, non-empty list — its coclass's default interface, found via
`IProvideClassInfo` — but that default interface does not include everything
else the object implements. Concretely, it does not include `IModelDoc2`
members like `EditRebuild3`, `SaveAs3`, `EditUndo2` or `ClearSelection2`,
which `DescribeMembersViaInterop` finds immediately (`IModelDoc2` is just
another `[ComImport]` interface the same object answers a QueryInterface for).
Use `ComTypeInspector.DescribeAllMembers(comObject, interopFilter?)` for the
union of both, deduplicated by `(Name, Kind, ParamCount)`, when you need the
full picture rather than whichever mechanism happens to answer first:

```csharp
var everything = ComTypeInspector.DescribeAllMembers(doc.Model);
// finds EditRebuild3/SaveAs3/EditUndo2/ClearSelection2 on a document root,
// which DescribeMembers alone does not.
```

`interopFilter` defaults to `null` (unfiltered) — this method takes no
position on the completeness/cost tradeoff described above; pass
`ComTypeInspector.FeatureDataFilter` or your own predicate when you don't need
a full scan of the interop assembly.

## Writing to SolidWorks

Everything above is read-only and forgiving: an unreadable property is just
absent. Writing is not — a wrong call can modify the wrong customer's part —
so the write surface is deliberately narrower, more explicit, and lives behind
its own types rather than reusing the read-side ones.

```csharp
using System.Runtime.InteropServices; // DispatchWrapper — see the note below

var doc = documents.NewPart(); // or a template path; new document, dispatcher-routed like everything else

var extension = ComPath.Resolve(doc.Model, "Extension").Value;
ComInvoker.InvokeMethod(extension, "SelectByID2",
    new object?[] { "Front Plane", "PLANE", 0.0, 0.0, 0.0, false, 0, new DispatchWrapper(null), 0 });

var sketchManager = ComPath.Resolve(doc.Model, "SketchManager").Value;
ComInvoker.InvokeMethod(sketchManager, "InsertSketch", new object?[] { true });

var circle = ComInvoker.InvokeMethod(sketchManager, "CreateCircleByRadius", new object?[] { 0.0, 0.0, 0.0, 0.01 });
var segmentRef = ResultConverters.ToSketchSegmentRef(circle.Value); // {Id, SegmentType} — the live ISketchSegment is released here (fresh return value, the default ownsReference: true is correct)

var segmentCountBefore = DocumentStateProbes.GetSketchSegmentCount(doc.Model);
```

> **Ownership matters for converters.** `ToFeatureRef`/`ToSketchSegmentRef` default
> to releasing the object you pass in (`ownsReference: true`) — correct for a
> fresh return value like `circle.Value` above. Pass `ownsReference: false` for
> anything you resolved rather than just created — e.g. a `ComPath` hop that
> happens to land on `Extension.Document`, which is the *same COM identity* as
> `doc.Model`: converting it with the default would disconnect the document
> handle you (and every other `SwDocument` wrapping it) still need, for the rest
> of that RCW's lifetime. SwBridge cannot infer ownership from the object itself.

> Verified live: a `null` argument for a COM-interface-typed parameter (e.g.
> `SelectByID2`'s `Callout`) needs `new DispatchWrapper(null)`, not a bare `null`
> — an early-bound call lets the compiler marshal a literal `null` correctly,
> but `ComInvoker`'s late-bound `Type.InvokeMember` has no parameter types to go
> by, and a bare `null` marshals as `VT_EMPTY`, which SolidWorks rejects with
> `DISP_E_TYPEMISMATCH`.

- **`ComInvoker`** is the only write-dispatch door: `InvokeMethod`/`GetProperty`/`SetProperty`,
  each exactly one COM dispatch flag — never combined, unlike `ComPropertyReader`'s
  deliberately-combined read-only flags. Every call returns a typed `InvokeOutcome`
  (`Success`, `Value`, `FailureDetail`) instead of throwing for a member-level failure,
  because SolidWorks' write API reports most failures as `Nothing`/`False`, not exceptions.
- **`ComPath`** resolves an open, dotted, *strictly read-only* property path
  (`"Extension.SelectionManager"`) from an application or document root, one hop
  per segment — via a dedicated `DISPATCH_PROPERTYGET`-only reader
  (`ComPropertyReader.TryGetPropertyStrict`), never `ComPropertyReader`'s
  combined `GetProperty | InvokeMethod` flags used by the feature-definition read
  path. That distinction is load-bearing, not stylistic: with the combined flags,
  a path segment that happened to name a zero-argument COM method (`ExitApp`,
  `EditDelete`, …) would be silently *invoked* while merely being resolved. A
  target does not need a closed enum of known managers; a bad path is a runtime
  `ComPathResult` failure, not a compile-time one.
- **`ResultConverters`** turn a live COM return (a feature, a sketch segment) into
  a plain, serializable DTO (`FeatureRef`, `SketchSegmentRef`). Each takes an
  `ownsReference` flag (default `true`) that decides whether the RCW is released
  after conversion — see the ownership note above; getting this wrong
  (`Marshal.ReleaseComObject` on an object something else still holds) permanently
  disconnects that RCW for every other holder, not just the converter's caller.
- **`DocumentStateProbes`** are the cheap, targeted reads (feature count, sketch mode,
  sketch segment count, selection count, rebuild state) a caller uses to check a write
  actually did something, since SolidWorks' return values alone are not trustworthy.
  Every intermediate COM object each probe reaches through (`FeatureManager`,
  `SketchManager`, `ActiveSketch`, `SelectionManager`, `Extension`) is released
  before the probe returns.
- **`SwDispatcher`** (see below) is what makes composing these safe: everything above
  runs on one dedicated STA thread per `SwConnection`, so a write step and a read
  step never interleave.

This mirrors the asymmetry deliberately: read failures are absorbed (`ComPropertyReader`
returns "absent"), write failures are surfaced (`ComInvoker` returns a typed outcome
you must check). Treating an unchecked `InvokeOutcome` as success is exactly the mistake
this shape exists to make hard to make silently.

## Selection identity

A selection-driven write (a fillet, a chamfer, a draft) can succeed against
the *wrong* edge or face with no COM error at all — `selectionCount` tells you
*how many* things are selected, never *what*. `SelectionInspector.GetSelection`
closes that gap:

```csharp
var selection = SelectionInspector.GetSelection(doc.Model);
foreach (var s in selection)
    Console.WriteLine($"{s.Type}: {s.Descriptor}");
// swSelEDGES: Line edge, length=0.02 m, midpoint=(0.05, 0.05, 0.01) m
```

Each `SelectionInfo` carries the `swSelectType_e` name (`Type`) and a
best-effort, human-meaningful `Descriptor` — enough to tell *which* edge/face
this is from a transcript alone, without ever handing back the live COM
object:

- **Edges** — curve kind (`Line`/`Circle`/`Ellipse`/`BCurve`/`Curve`) plus
  chord length and chord midpoint from the edge's start/end vertices. Exact
  for a straight edge; a chord approximation (not the true arc length) for a
  curved one — still enough to identify which edge it is. Verified live
  against a known 20 mm box edge: `length=0.02 m, midpoint=(0.05, 0.05, 0.01) m`,
  matching ground truth exactly.
  (Deliberately *not* `IEdge.GetCurveParams2()` + `ICurve.GetLength2`/`Evaluate2`,
  despite that looking like the obvious API: verified live that
  `GetCurveParams2()`'s returned array is not the simple start/end-parameter
  pair its name suggests, and feeding it to `GetLength2`/`Evaluate2` produced
  nonsense — a negative length, a midpoint off the edge. Vertices were the
  mechanism that actually worked.)
- **Faces** — surface kind (`Planar`/`Cylindrical`/`Conical`/`Spherical`/`Toroidal`/`Surface`)
  plus area and an approximate center (the bounding-box midpoint).
- **Vertices** — the point.
- **Named entities** (features, planes, sketches, …) — the name.
- Anything else: `Type` only, `Descriptor` null — never a reason to fail the read.

Every intermediate COM object (the selection manager, the curve/surface/vertex
objects read off each selected entity) is released before returning; nothing
here ever hands back a live reference, and a description failure never fails
the whole read — the read itself is a safety mechanism and must not become one
more thing to work around.

## The dispatcher

`SwConnection` owns a dedicated STA thread (`SwDispatcher`) that every COM touch
in this library — the read path above and the write primitives — runs on,
blocking the calling thread for the result. This exists because SolidWorks
write calls are stateful (an active sketch, a selection list) in a way reads
never were: two calls interleaving on different threads corrupt each other's
preconditions. `SwDispatcher.Run`/`Run<T>` is public — a consumer composing
several of the write primitives above into one atomic step (e.g. select, then
invoke, then probe) should wrap them in a single `Run` call so nothing else can
interleave in the middle; calling `Run` again from inside that callback (or
from inside another SwBridge call that already dispatches) is safe and runs
inline rather than deadlocking.

The dispatcher's STA thread pumps Windows messages while idle (`PeekMessage`/
`TranslateMessage`/`DispatchMessage`, polled every ~10ms between work-queue
checks — a bounded wait each cycle, not a busy-spin). An STA apartment that
never pumps cannot service a COM call marshalled back into it from another
thread; without the pump, any RCW that ever leaked off the dispatcher and was
then touched from elsewhere would hang that other thread forever, and because
the dispatcher itself never services anything else meanwhile, every other
queued `Run` call would be wedged behind it too.

`Run` also takes a timeout — `DefaultTimeout` (120 seconds) unless a call
passes its own. SolidWorks can genuinely block a COM call indefinitely (a
modal rebuild-error dialog, a missing-reference prompt, a licence check), and
without a bound, that wedges the dispatcher exactly as above. On timeout, `Run`
throws `SwDispatchTimeoutException` on the *calling* thread; the unit of work
itself keeps running on the dispatcher (there is no safe way to abort an
in-flight COM call) and every call still queued behind it keeps waiting until
it finishes. `Dispose()` mirrors this: it joins the thread with a bounded
timeout (`DefaultDisposeTimeout`, 5 seconds) rather than indefinitely, so a
wedged call cannot hang process shutdown either; check `IsFullyStopped`
afterward if you need to know whether the join actually completed.

## Design notes

- **Lazy connection & reconnection** — `SwConnection` resolves the running instance on first call and detects a dead COM link (SolidWorks closed/restarted, or a disconnected RCW — `InvalidComObjectException` included), re-attaching transparently.
- **Ambiguous name resolution is a reportable error, not a coin flip** — `DocumentManager.Resolve` throws `SwBridgeException` when a name matches more than one open document (e.g. an unsaved scratch `Part2` and a saved `Part2.SLDPRT`) instead of silently returning whichever `ISldWorks.GetDocuments()` happened to enumerate first. This follows the write side's error philosophy ("worst case you modify the wrong customer's part") rather than the read side's ("worst case you read the wrong thing"), because a resolved document feeds both.
- **Schema-as-data feature reading** — `ModelInspector.GetFeatures` takes a `Func<string, IReadOnlyList<string>?>` so the set of understood feature types is the caller's data, not this library's code.
- **No console output** — the library never writes to stdout/stderr; failures surface as return values or typed exceptions (`SwNotRunningException`, `SwBridgeException`).
- **Errors as absence** — an unreadable feature property is reported as absent rather than throwing; COM property probing is inherently best-effort.
- **Discovery, not guessing** — `ComTypeInspector` reads a late-bound COM object's real member list from its type information, so schema entries can be found rather than assumed. `ComTypeInspector.FeatureDataFilter` is a ready-made, filtered-by-default predicate for the interop-assembly fallback — an unfiltered probe costs a QueryInterface per interface in the whole interop assembly (a few thousand) and, since it does no dispatching of its own, blocks the shared dispatcher for its entire duration if run inside a `Run` call. `DescribeAllMembers` unions the `ITypeInfo` and interop-assembly paths for objects (like a document root) where neither alone sees everything.
- **Selection is identifiable, not just countable** — `SelectionInspector.GetSelection` turns "2 things selected" into "this edge, that face", closing the silent-wrong-entity failure mode a selection-driven write cannot otherwise detect.

## Building

```bash
dotnet build
dotnet test
dotnet run --project samples/SwBridge.Sample   # requires SolidWorks running
```

## License

[MIT](LICENSE). SOLIDWORKS is a registered trademark of Dassault Systèmes SolidWorks Corporation. This project is not affiliated with or endorsed by Dassault Systèmes. See [`docs/THIRD-PARTY-NOTICES.md`](docs/THIRD-PARTY-NOTICES.md) for the interop dependencies' provenance.
