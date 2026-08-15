// Live smoke test for SwBridge: attaches to a running SolidWorks instance,
// lists open documents, and dumps the feature tree of the active document.
// No MCP involved — this is plain library usage.
using System.Linq;
using System.Text.Json;
using SolidWorks.Interop.sldworks;
using SwBridge;

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
if (args.Length > 0)
{
    var opened = documents.OpenDocument(Path.GetFullPath(args[0]));
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

return 0;
