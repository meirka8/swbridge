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
in advance. `ComTypeInspector` finds that shape at runtime, for any late-bound
COM object, by reading its type information:

```csharp
var members = doc.DescribeFeatureDefinition("Boss-Extrude1");
foreach (var m in members ?? Array.Empty<ComMemberInfo>())
    Console.WriteLine($"{m.Name} [{m.Kind}] params={m.ParamCount} returns={m.ReturnType}");

// Works on any IDispatch-based COM object, not just SolidWorks:
var members2 = ComTypeInspector.DescribeMembers(someComObject);
```

Not every COM object publishes runtime type information — some SolidWorks
objects only support invoking members by name (the mechanism `ComPropertyReader`
uses), not enumerating them. `DescribeMembers` returns an empty list for those
rather than throwing; an empty result means "not introspectable," not
necessarily "no members."

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
