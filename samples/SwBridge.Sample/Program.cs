// Live smoke test for SwBridge: attaches to a running SolidWorks instance,
// lists open documents, and dumps the feature tree of the active document.
// No MCP involved — this is plain library usage.
//
// By default this sample is entirely read-only. Pass --new-part to also run
// the write-side demo (SwDispatcher/DocumentManager.NewPart/ComPath/ComInvoker/
// DocumentStateProbes/ResultConverters) — it creates a scratch part, sketches
// and closes it again, and touches nothing else open in the session. Pass
// --selection-demo to run the SelectionInspector demo (also a scratch part).
// Pass --document-discovery to run ComTypeInspector.DescribeAllMembers against
// the active document's root (read-only, but an unfiltered interop probe —
// off by default to keep an ordinary run fast).
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwBridge;

bool HasFlag(string flag) => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

var runNewPartDemo = HasFlag("--new-part");
var runSelectionDemo = HasFlag("--selection-demo");
var runDocumentDiscovery = HasFlag("--document-discovery");
var positionalArgs = args
    .Where(a => !a.Equals("--new-part", StringComparison.OrdinalIgnoreCase) &&
                !a.Equals("--selection-demo", StringComparison.OrdinalIgnoreCase) &&
                !a.Equals("--document-discovery", StringComparison.OrdinalIgnoreCase))
    .ToArray();

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
// Document-root discovery demo (opt-in via --document-discovery): the gap the
// UAT report found — describe_com_members on a document root sees only the
// ~175 members its IProvideClassInfo/coclass default interface declares
// (ComTypeInspector.DescribeMembers), and misses IModelDoc2 members like
// EditRebuild3/SaveAs3/EditUndo2/ClearSelection2 entirely, because those are
// not on that default interface. DescribeAllMembers unions that with the
// interop-assembly probe, which finds them immediately (IModelDoc2 is just
// another interface the live object answers a QueryInterface for).
// Read-only; off by default only because the unfiltered probe here queries
// the whole interop assembly and is not fast.
// ---------------------------------------------------------------------------
if (runDocumentDiscovery)
{
    Console.WriteLine("\n=== --document-discovery demo (ComTypeInspector.DescribeAllMembers) ===");

    // Targets Part2 specifically (not whatever happens to be "active" — the
    // SolidWorks session may be shared with another process/agent) so this
    // demo is deterministic regardless of what else is going on in the window.
    var discoveryDoc = documents.Resolve("Part2.SLDPRT") ?? active;
    Console.WriteLine($"Discovery target: {discoveryDoc.Info.Title}");

    var viaTypeInfoOnly = ComTypeInspector.DescribeMembers(discoveryDoc.Model);
    Console.WriteLine($"DescribeMembers (ITypeInfo/IProvideClassInfo only) member count: {viaTypeInfoOnly.Count}");

    var all = ComTypeInspector.DescribeAllMembers(discoveryDoc.Model);
    Console.WriteLine($"DescribeAllMembers (union with unfiltered interop probe) member count: {all.Count}");

    string[] previouslyInvisible = { "EditRebuild3", "SaveAs3", "EditUndo2", "ClearSelection2" };
    var found = previouslyInvisible
        .Select(name => all.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)))
        .Where(m => m != null)
        .ToList();

    Console.WriteLine($"Previously-invisible members found ({found.Count}/{previouslyInvisible.Length}):");
    Console.WriteLine(JsonSerializer.Serialize(found, json));
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

// ---------------------------------------------------------------------------
// Selection-identity demo (opt-in via --selection-demo): SelectionInspector.
// Builds a scratch box, picks one vertical edge via Extension.SelectByRay (the
// reliable, view-independent pick — docs/uat-ladder-report.md's remediation
// for the wrong-edge gap), and prints what SelectionInspector reports was
// actually selected. This is the mechanism that closes the "silent
// wrong-edge" failure: a caller compares the printed descriptor against what
// it meant to pick, instead of trusting selectionCount alone.
// ---------------------------------------------------------------------------
if (runSelectionDemo)
{
    Console.WriteLine("\n=== --selection-demo (selection identity readback) ===");

    var scratch = documents.NewPart();
    var scratchTitle = scratch.Info.Title;
    Console.WriteLine($"Created scratch part: {scratchTitle}");
    var doc = scratch.Model;

    try
    {
        const double half = 0.05;  // 100 mm square box
        const double depth = 0.02; // 20 mm tall

        doc.Extension.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null, 0);
        doc.SketchManager.InsertSketch(true);
        doc.SketchManager.CreateCornerRectangle(-half, -half, 0, half, half, 0);
        doc.SketchManager.InsertSketch(true);
        var boss = doc.FeatureManager.FeatureExtrusion3(true, false, false,
            (int)swEndConditions_e.swEndCondBlind, 0, depth, 0,
            false, false, false, false, 0, 0,
            false, false, false, false, true, true, true,
            (int)swStartConditions_e.swStartSketchPlane, 0, false);
        Console.WriteLine($"Built scratch box: {(boss == null ? "FAILED" : "OK")}, feature count={DocumentStateProbes.GetFeatureCount(doc)}");

        // Aim diagonally inward at the (+half, +half) vertical corner edge,
        // from just outside it, at mid-height — side-on and close, per the
        // UAT report's tolerance note (a ray fired from further away along the
        // same line can pick a neighbouring edge instead).
        doc.ClearSelection2(true);
        var picked = doc.Extension.SelectByRay(
            half + 0.01, half + 0.01, depth / 2,
            -1.0, -1.0, 0.0,
            0.001, (int)swSelectType_e.swSelEDGES, false, 0, 0);
        Console.WriteLine($"SelectByRay -> {picked}, selection count={DocumentStateProbes.GetSelectionCount(doc)}");

        var selection = SelectionInspector.GetSelection(doc);
        Console.WriteLine("SelectionInspector.GetSelection:");
        Console.WriteLine(JsonSerializer.Serialize(selection, json));

        // Ground truth for the transcript: the picked edge should be a 20 mm
        // vertical edge at the (0.05, 0.05, *) corner, i.e. midpoint near
        // (0.05, 0.05, 0.01) m and length near 0.02 m.
    }
    finally
    {
        var app = connection.GetApp();
        app.CloseDoc(scratchTitle);
        Console.WriteLine($"Closed scratch document '{scratchTitle}'. Session otherwise untouched.");
    }
}

return 0;
