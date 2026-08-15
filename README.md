# SwBridge

A small, MIT-licensed .NET library for automating **SolidWorks** through its COM API.

SwBridge attaches to an already-running SolidWorks instance (it never launches one), resolves open documents, and reads the feature tree **generically**: instead of compiling a class per feature type, you describe per feature type which members to read — bare properties (`BothDirections`) or accessor methods with arguments (`GetDepth(true)`) — and SwBridge pulls them off the late-bound feature definition by reflection. New feature types need data, not code.

SwBridge is deliberately unopinionated infrastructure — it knows nothing about MCP, AI, licensing, or any particular product. It is usable from any .NET application that wants to script SolidWorks.

## Requirements

- Windows, .NET 8 (`net8.0-windows`)
- SolidWorks 2021 or later, installed and running

## Usage

```csharp
using SwBridge;

var connection = new SwConnection();          // lazy — attaches on first use, re-attaches if SW restarts
var documents = new DocumentManager(connection);

// Enumerate what's open
foreach (var info in documents.ListOpenDocuments())
    Console.WriteLine($"{info.Title} ({info.Type}) {info.Path}");

// Resolve a document by title, file name, or path
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
var segmentRef = ResultConverters.ToSketchSegmentRef(circle.Value); // {Id, SegmentType} — the live ISketchSegment is released here

var segmentCountBefore = DocumentStateProbes.GetSketchSegmentCount(doc.Model);
```

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
- **`ComPath`** resolves an open, dotted, read-only property path (`"Extension.SelectionManager"`)
  from an application or document root — the same reflection `ComPropertyReader`
  uses, one hop per segment — so a target does not need a closed enum of known
  managers; a bad path is a runtime `ComPathResult` failure, not a compile-time one.
- **`ResultConverters`** turn a live COM return (a feature, a sketch segment) into a
  plain, serializable DTO (`FeatureRef`, `SketchSegmentRef`) and release the RCW on
  the way out — a live interface pointer should never survive past the call that produced it.
- **`DocumentStateProbes`** are the cheap, targeted reads (feature count, sketch mode,
  sketch segment count, selection count, rebuild state) a caller uses to check a write
  actually did something, since SolidWorks' return values alone are not trustworthy.
- **`SwDispatcher`** (see below) is what makes composing these safe: everything above
  runs on one dedicated STA thread per `SwConnection`, so a write step and a read
  step never interleave.

This mirrors the asymmetry deliberately: read failures are absorbed (`ComPropertyReader`
returns "absent"), write failures are surfaced (`ComInvoker` returns a typed outcome
you must check). Treating an unchecked `InvokeOutcome` as success is exactly the mistake
this shape exists to make hard to make silently.

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

## Design notes

- **Lazy connection & reconnection** — `SwConnection` resolves the running instance on first call and detects a dead COM link (SolidWorks closed/restarted), re-attaching transparently.
- **Schema-as-data feature reading** — `ModelInspector.GetFeatures` takes a `Func<string, IReadOnlyList<string>?>` so the set of understood feature types is the caller's data, not this library's code.
- **No console output** — the library never writes to stdout/stderr; failures surface as return values or typed exceptions (`SwNotRunningException`, `SwBridgeException`).
- **Errors as absence** — an unreadable feature property is reported as absent rather than throwing; COM property probing is inherently best-effort.
- **Discovery, not guessing** — `ComTypeInspector` reads a late-bound COM object's real member list from its type information, so schema entries can be found rather than assumed.

## Building

```bash
dotnet build
dotnet test
dotnet run --project samples/SwBridge.Sample   # requires SolidWorks running
```

## License

[MIT](LICENSE). SolidWorks is a registered trademark of Dassault Systèmes SolidWorks Corporation. This project is not affiliated with or endorsed by Dassault Systèmes.
