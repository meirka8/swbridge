using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SwBridge;

/// <summary>A feature-tree entry.</summary>
/// <param name="Name">Feature name as shown in the tree, e.g. <c>Boss-Extrude1</c>.</param>
/// <param name="TypeName">Type name from <c>IFeature.GetTypeName2()</c>, e.g. <c>Extrusion</c>, <c>Fillet</c>, <c>CirPattern</c>.</param>
/// <param name="Properties">
/// Values read off the feature definition by reflection, keyed by
/// <see cref="PropertySpec.Name"/>. Null when the caller supplied no property
/// lookup, or the lookup had no entry for this feature type.
/// </param>
public sealed record FeatureInfo(string Name, string TypeName, IReadOnlyDictionary<string, object?>? Properties);

/// <summary>A point in model space (meters).</summary>
public sealed record Point3(double X, double Y, double Z);

/// <summary>Axis-aligned bounding box in model space (meters).</summary>
public sealed record BoundingBox(Point3 Min, Point3 Max);

/// <summary>Summary of a part document.</summary>
public sealed record PartInfo(
    string Path,
    string Title,
    double Mass,
    IReadOnlyList<FeatureInfo> Features,
    BoundingBox? BoundingBox);

/// <summary>
/// Read-only inspection of SolidWorks documents. Feature properties are read
/// generically by reflection — the caller supplies which members to read per
/// feature type via <c>propertyLookup</c>, so no feature type is hardcoded here.
/// </summary>
public static class ModelInspector
{
    /// <summary>
    /// Walks the feature tree. For each feature, <paramref name="propertyLookup"/>
    /// is asked (by feature type name) which members to read off the feature
    /// definition; a null return leaves <see cref="FeatureInfo.Properties"/> null.
    /// </summary>
    public static IReadOnlyList<FeatureInfo> GetFeatures(
        ModelDoc2 doc,
        Func<string, IReadOnlyList<PropertySpec>?>? propertyLookup = null)
    {
        var featureList = new List<FeatureInfo>();
        if (doc.FeatureManager.GetFeatures(false) is not object[] features)
        {
            return featureList;
        }

        foreach (var featureObject in features)
        {
            var swFeature = (Feature)featureObject;
            try
            {
                var typeName = swFeature.GetTypeName2();

                IReadOnlyDictionary<string, object?>? properties = null;
                var specs = propertyLookup?.Invoke(typeName);
                if (specs != null)
                {
                    var definition = swFeature.GetDefinition();
                    try
                    {
                        var values = new Dictionary<string, object?>();
                        foreach (var spec in specs)
                        {
                            var args = spec.Args is { Count: > 0 } ? spec.Args.ToArray() : null;
                            if (ComPropertyReader.TryGetMember(definition, spec.Member, args, out var value))
                            {
                                values[spec.Name] = value;
                            }
                        }
                        properties = values;
                    }
                    finally
                    {
                        ComLifetime.Release(definition);
                    }
                }

                featureList.Add(new FeatureInfo(swFeature.Name, typeName, properties));
            }
            finally
            {
                ComLifetime.Release(swFeature);
            }
        }

        return featureList;
    }

    /// <summary>
    /// Summarizes a part document: mass, feature tree, bounding box.
    /// Returns null when the document is not a part or has no solid bodies.
    /// </summary>
    public static PartInfo? GetPartInfo(
        ModelDoc2 doc,
        Func<string, IReadOnlyList<PropertySpec>?>? propertyLookup = null)
    {
        if (doc.GetType() != (int)swDocumentTypes_e.swDocPART)
        {
            return null;
        }

        var part = (PartDoc)doc;
        if (part.GetBodies2((int)swBodyType_e.swSolidBody, true) is not object[] bodies ||
            bodies.Length == 0 ||
            bodies[0] is not Body2 body)
        {
            return null;
        }

        return new PartInfo(
            doc.GetPathName(),
            doc.GetTitle(),
            GetMass(doc),
            GetFeatures(doc, propertyLookup),
            GetBoundingBox(body));
    }

    /// <summary>Mass in kilograms, from the document's mass properties.</summary>
    public static double GetMass(ModelDoc2 doc) =>
        doc.Extension.CreateMassProperty().Mass;

    /// <summary>Axis-aligned bounding box of a body, or null if unavailable.</summary>
    public static BoundingBox? GetBoundingBox(Body2 body)
    {
        if (body.GetBodyBox() is not double[] box || box.Length < 6)
        {
            return null;
        }

        return new BoundingBox(
            new Point3(box[0], box[1], box[2]),
            new Point3(box[3], box[4], box[5]));
    }

    /// <summary>
    /// Finds a feature by name (case-insensitive) and discovers the members its
    /// definition object exposes: first via <see cref="ComTypeInspector.DescribeMembers(object?)"/>
    /// (reading the definition's own type information), and if that finds
    /// nothing, via <see cref="ComTypeInspector.DescribeMembersViaInterop(object?, Func{Type, bool}?)"/>
    /// (probing which <c>*FeatureData*</c> interop interface the definition
    /// implements and reflecting that instead). Use this to find the real
    /// member names/signatures for a feature type before writing a
    /// <see cref="PropertySpec"/> for it, instead of guessing.
    /// </summary>
    /// <remarks>
    /// Verified live against SolidWorks 2024: feature-definition objects are not
    /// introspectable via <c>ITypeInfo</c> — SolidWorks does not publish runtime
    /// type information for these internal objects (see <see cref="ComTypeInspector"/>
    /// remarks) — but the interop-assembly fallback recovers their real members
    /// (e.g. <c>GetDepth</c>, <c>BothDirections</c> on an Extrusion's
    /// <c>IExtrudeFeatureData2</c>). A non-null but empty result means neither
    /// mechanism found anything; member names known from documentation or prior
    /// use can still be read directly via <see cref="ComPropertyReader"/>.
    /// </remarks>
    /// <param name="doc">Document to search.</param>
    /// <param name="featureName">Feature name as shown in the tree, e.g. <c>Boss-Extrude1</c>.</param>
    /// <returns>
    /// The discovered members, or null when no feature with that name exists or
    /// its definition could not be obtained (e.g. <c>GetDefinition()</c> returned null).
    /// </returns>
    public static IReadOnlyList<ComMemberInfo>? DescribeFeatureDefinition(ModelDoc2 doc, string featureName)
    {
        if (doc.FeatureManager.GetFeatures(false) is not object[] features)
        {
            return null;
        }

        foreach (var featureObject in features)
        {
            var swFeature = (Feature)featureObject;
            try
            {
                if (!string.Equals(swFeature.Name, featureName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var definition = swFeature.GetDefinition();
                try
                {
                    if (definition == null)
                    {
                        return null;
                    }

                    var members = ComTypeInspector.DescribeMembers(definition);
                    return members.Count > 0
                        ? members
                        : ComTypeInspector.DescribeMembersViaInterop(definition, ComTypeInspector.FeatureDataFilter);
                }
                finally
                {
                    ComLifetime.Release(definition);
                }
            }
            finally
            {
                ComLifetime.Release(swFeature);
            }
        }

        return null;
    }
}
