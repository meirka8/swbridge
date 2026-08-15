// Live smoke test for SwBridge: attaches to a running SolidWorks instance,
// lists open documents, and dumps the feature tree of the active document.
// No MCP involved — this is plain library usage.
//
// By default this sample is entirely read-only. Pass --new-part to also run
// the write-side demo (SwDispatcher/DocumentManager.NewPart/ComPath/ComInvoker/
// DocumentStateProbes/ResultConverters) — it creates a scratch part, sketches
// and closes it again, and touches nothing else open in the session.
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using SolidWorks.Interop.sldworks;
using SwBridge;

var runNewPartDemo = args.Any(a => string.Equals(a, "--new-part", StringComparison.OrdinalIgnoreCase));
var positionalArgs = args.Where(a => !string.Equals(a, "--new-part", StringComparison.OrdinalIgnoreCase)).ToArray();

var connection = new SwConnection();
var documents = new DocumentManager(connection);

if (!connection.IsConnected)
{
    Console.Error.WriteLine("SolidWorks is not running (or has no registered instance). Open SolidWorks and retry.");
    return 1;
}

// Example schema: which members to read per feature type. In a real consumer
// this comes from configuration; SwBridge itself has no opinion about it.
// Extrusion entries verified live against SolidWorks 2024.
var schema = new Dictionary<string, IReadOnlyList<PropertySpec>>(StringComparer.OrdinalIgnoreCase)
{
    ["Extrusion"] = new[]
    {
        new PropertySpec("Depth", "GetDepth", new object?[] { true }),
        new PropertySpec("DraftAngle", "GetDraftAngle", new object?[] { true }),
        new PropertySpec("DraftOutward", "GetDraftOutward", new object?[] { true }),
        PropertySpec.Bare("BothDirections"),
        PropertySpec.Bare("ReverseDirection"),
    },
    ["Fillet"] = new[] { PropertySpec.Bare("DefaultRadius"), PropertySpec.Bare("OverflowType") },
    ["CirPattern"] = new[] { PropertySpec.Bare("TotalInstances"), PropertySpec.Bare("D1TotalAngle") },
};
IReadOnlyList<PropertySpec>? Lookup(string typeName) => schema.TryGetValue(typeName, out var props) ? props : null;

var json = new JsonSerializerOptions { WriteIndented = true };

// Optional: open a model from disk first (pass its path as the first argument).
if (positionalArgs.Length > 0)
{
    var opened = documents.OpenDocument(Path.GetFullPath(positionalArgs[0]));
    Console.WriteLine($"Opened: {opened.Info.Title}");
}

Console.WriteLine("Open documents:");
Console.WriteLine(JsonSerializer.Serialize(documents.ListOpenDocuments(), json));

var active = documents.GetActiveDocument();
if (active == null)
{
    Console.WriteLine("No active document.");
    return 0;
}

Console.WriteLine($"\nActive document: {active.Info.Title} ({active.Info.Type})");
Console.WriteLine("\nFeatures:");
Console.WriteLine(JsonSerializer.Serialize(active.GetFeatures(Lookup), json));

if (active.Info.Type == SwDocumentType.Part)
{
    Console.WriteLine("\nPart info:");
    Console.WriteLine(JsonSerializer.Serialize(active.GetPartInfo(Lookup), json));
}

// Discovery demo: find "Boss-Extrude1", or else the first Extrusion feature,
// and dump every member its definition object actually exposes. This is the
// mechanism a schema-enrichment consumer would use to find real member names
// instead of guessing them.
var features = active.GetFeatures();
var target = features.FirstOrDefault(f => string.Equals(f.Name, "Boss-Extrude1", StringComparison.OrdinalIgnoreCase))
    ?? features.FirstOrDefault(f => string.Equals(f.TypeName, "Extrusion", StringComparison.OrdinalIgnoreCase));

if (target != null)
{
    Console.WriteLine($"\nDiscovered members of '{target.Name}' [{target.TypeName}] definition (combined, as SwDocument exposes it):");
    Console.WriteLine(JsonSerializer.Serialize(active.DescribeFeatureDefinition(target.Name), json));

    // Show the two tiers explicitly, working from the raw definition object:
    // 1) the ITypeInfo attempt (expected empty for a feature definition — see CLAUDE.md), then
    // 2) the interop-assembly cast-probing fallback that recovers its real members.
    if (active.Model.FeatureManager.GetFeatures(false) is object[] rawFeatures)
    {
        var rawFeature = rawFeatures
            .Cast<Feature>()
            .FirstOrDefault(f => string.Equals(f.Name, target.Name, StringComparison.OrdinalIgnoreCase));
        var definition = rawFeature?.GetDefinition();

        Console.WriteLine($"\nITypeInfo attempt member count: {ComTypeInspector.DescribeMembers(definition).Count}");

        bool FeatureDataFilter(Type t) => t.Name.Contains("FeatureData", StringComparison.OrdinalIgnoreCase);
        var matchedInterfaces = ComTypeInspector.FindImplementedInteropInterfaces(definition, FeatureDataFilter);
        Console.WriteLine($"Interop-assembly matched interfaces: {string.Join(", ", matchedInterfaces.Select(t => t.Name))}");

        var interopMembers = ComTypeInspector.DescribeMembersViaInterop(definition, FeatureDataFilter);
        Console.WriteLine($"Interop-assembly member count: {interopMembers.Count}");
        Console.WriteLine("First 30 members via interop fallback:");
        Console.WriteLine(JsonSerializer.Serialize(interopMembers.Take(30), json));

        ComLifetime.Release(definition);
        foreach (var f in rawFeatures)
        {
            ComLifetime.Release(f);
        }
    }
}
else
{
    Console.WriteLine("\nNo Boss-Extrude1 / Extrusion feature found for the discovery demo.");
}

// ---------------------------------------------------------------------------
// Write-side demo (opt-in via --new-part): DocumentManager.NewPart, then a
// sketch/circle/exit round trip driven entirely through ComPath (dotted,
// read-only target resolution) + ComInvoker (single-flag write dispatch) +
// DocumentStateProbes (cheap before/after reads) + ResultConverters (COM ->
// DTO). This is the SwBridge write surface ADR 0001 describes — nothing here
// is SolidWorks-feature-specific C#, it is all generic mechanism.
// ---------------------------------------------------------------------------
if (runNewPartDemo)
{
    Console.WriteLine("\n=== --new-part demo (write path) ===");

    var scratch = documents.NewPart();
    var scratchTitle = scratch.Info.Title; // captured once; reused below instead of re-reading Info later
    Console.WriteLine($"Created scratch part: {scratchTitle}");
    var doc = scratch.Model;

    try
    {
        Console.WriteLine($"Feature count before: {DocumentStateProbes.GetFeatureCount(doc)}");
        Console.WriteLine($"In sketch mode before: {DocumentStateProbes.IsInSketchMode(doc)}");

        var extensionPath = ComPath.Resolve(doc, "Extension");
        Console.WriteLine($"ComPath.Resolve(doc, \"Extension\") -> Success={extensionPath.Success}");

        // SelectByID2's 8th parameter is typed Callout (a COM interface), not
        // object — an early-bound call lets a literal `null` marshal as an
        // empty VT_DISPATCH automatically, but a late-bound Type.InvokeMember
        // call (as ComInvoker makes) has no compile-time parameter types to go
        // by, so a bare `null` marshals as VT_EMPTY and SolidWorks rejects it
        // with DISP_E_TYPEMISMATCH. DispatchWrapper(null) forces the VT_DISPATCH
        // shape explicitly — the standard late-bound-COM fix for this.
        var selectOutcome = ComInvoker.InvokeMethod(extensionPath.Value, "SelectByID2",
            new object?[] { "Front Plane", "PLANE", 0.0, 0.0, 0.0, false, 0, new DispatchWrapper(null), 0 });
        Console.WriteLine($"SelectByID2(\"Front Plane\") via ComInvoker -> Success={selectOutcome.Success} Value={selectOutcome.Value} Detail={selectOutcome.FailureDetail}");

        var sketchManagerPath = ComPath.Resolve(doc, "SketchManager");
        var insertOutcome = ComInvoker.InvokeMethod(sketchManagerPath.Value, "InsertSketch", new object?[] { true });
        Console.WriteLine($"InsertSketch(true) via ComInvoker -> Success={insertOutcome.Success} Detail={insertOutcome.FailureDetail}");
        Console.WriteLine($"In sketch mode after InsertSketch: {DocumentStateProbes.IsInSketchMode(doc)}");

        var circleOutcome = ComInvoker.InvokeMethod(sketchManagerPath.Value, "CreateCircleByRadius",
            new object?[] { 0.0, 0.0, 0.0, 0.01 });
        Console.WriteLine($"CreateCircleByRadius via ComInvoker -> Success={circleOutcome.Success} Detail={circleOutcome.FailureDetail}");
        var segmentRef = ResultConverters.ToSketchSegmentRef(circleOutcome.Value);
        Console.WriteLine($"ResultConverters.ToSketchSegmentRef -> {JsonSerializer.Serialize(segmentRef, json)}");
        Console.WriteLine($"Sketch segment count: {DocumentStateProbes.GetSketchSegmentCount(doc)}");

        var exitOutcome = ComInvoker.InvokeMethod(sketchManagerPath.Value, "InsertSketch", new object?[] { true }); // exit sketch
        Console.WriteLine($"InsertSketch(true) [exit] via ComInvoker -> Success={exitOutcome.Success} Detail={exitOutcome.FailureDetail}");
        Console.WriteLine($"In sketch mode after exit: {DocumentStateProbes.IsInSketchMode(doc)}");
        Console.WriteLine($"Feature count after: {DocumentStateProbes.GetFeatureCount(doc)}");
    }
    finally
    {
        var app = connection.GetApp();
        app.CloseDoc(scratchTitle);
        Console.WriteLine($"Closed scratch document '{scratchTitle}'. Session otherwise untouched.");
    }
}

return 0;
