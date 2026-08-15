using SolidWorks.Interop.swconst;

namespace SwBridge;

/// <summary>A serializable reference to a feature — never the live COM object.</summary>
/// <param name="Name">Feature name as shown in the tree, e.g. <c>Boss-Extrude1</c>.</param>
/// <param name="TypeName">Type name from <c>IFeature.GetTypeName2()</c>, or null if unreadable.</param>
public sealed record FeatureRef(string Name, string? TypeName);

/// <summary>A serializable reference to a sketch segment — never the live COM object.</summary>
/// <param name="Id">
/// The first element of the segment's <c>ISketchSegment.GetID()</c> return.
/// Verified live: despite its interop signature returning bare <c>object</c>,
/// <c>GetID()</c> actually returns an <c>int[]</c> (a composite identifier, not
/// a single scalar) — this is that array's primary/first element.
/// </param>
/// <param name="SegmentType">
/// Human-readable name of the segment's <c>swSketchSegments_e</c> value (e.g.
/// <c>swSketchLINE</c>), or <c>Unknown(n)</c> if the value is not a defined enum member.
/// </param>
public sealed record SketchSegmentRef(int Id, string SegmentType);

/// <summary>
/// Converts raw COM returns from write operations into the plain, serializable
/// DTOs <c>ADR 0001</c> declares as the write surface's <c>returns</c> shapes —
/// a live <c>IFeature</c> or <c>ISketchSegment</c> must never reach a caller
/// outside SwBridge's dispatch. Each converter here releases the RCW it is
/// given after extracting the DTO (per <c>ADR 0003</c>: "the RCW released on
/// the way out"); do not use the input object after conversion.
/// </summary>
public static class ResultConverters
{
    /// <summary>
    /// Converts a live feature object (e.g. the return of
    /// <c>IFeatureManager.FeatureExtrusion3</c>) to a <see cref="FeatureRef"/>,
    /// releasing it. Returns null for a null input or one whose <c>Name</c>
    /// could not be read (most commonly: the creation call itself returned
    /// null/nothing, meaning no feature was actually created — see
    /// <c>docs/adr/0002-write-verification-and-no-auto-rollback.md</c>).
    /// </summary>
    public static FeatureRef? ToFeatureRef(object? feature)
    {
        if (feature == null)
        {
            return null;
        }

        try
        {
            if (!ComPropertyReader.TryGetProperty(feature, "Name", out var nameValue) || nameValue is not string name)
            {
                return null;
            }

            var typeName = ComPropertyReader.TryGetMember(feature, "GetTypeName2", null, out var typeValue)
                ? typeValue as string
                : null;

            return new FeatureRef(name, typeName);
        }
        finally
        {
            ComLifetime.Release(feature);
        }
    }

    /// <summary>
    /// Converts a live sketch segment object (e.g. the return of
    /// <c>ISketchManager.CreateCircleByRadius</c>) to a <see cref="SketchSegmentRef"/>,
    /// releasing it. Returns null for a null input or one whose ID could not be read.
    /// </summary>
    public static SketchSegmentRef? ToSketchSegmentRef(object? segment)
    {
        if (segment == null)
        {
            return null;
        }

        try
        {
            if (!ComPropertyReader.TryGetMember(segment, "GetID", null, out var idValue))
            {
                return null;
            }

            // GetID()'s interop signature says bare object, but verified live it
            // actually returns int[] (a composite id) — accept a bare int too in
            // case a future SolidWorks version (or a different segment kind)
            // shapes it differently.
            int? id = idValue switch
            {
                int i => i,
                int[] { Length: > 0 } ids => ids[0],
                _ => null,
            };

            if (id == null)
            {
                return null;
            }

            var segmentType = ComPropertyReader.TryGetMember(segment, "GetType", null, out var typeValue) && typeValue is int vt
                ? DescribeSegmentType(vt)
                : "Unknown";

            return new SketchSegmentRef(id.Value, segmentType);
        }
        finally
        {
            ComLifetime.Release(segment);
        }
    }

    /// <summary>
    /// Converts a sequence of live sketch segment objects (e.g. from
    /// <c>ISketch.GetSketchSegments()</c>) to <see cref="SketchSegmentRef"/>s,
    /// releasing each one. Entries that fail to convert are dropped rather than
    /// aborting the whole sequence.
    /// </summary>
    public static IReadOnlyList<SketchSegmentRef> ToSketchSegmentRefs(IEnumerable<object?>? segments)
    {
        if (segments == null)
        {
            return Array.Empty<SketchSegmentRef>();
        }

        var results = new List<SketchSegmentRef>();
        foreach (var segment in segments)
        {
            var converted = ToSketchSegmentRef(segment);
            if (converted != null)
            {
                results.Add(converted);
            }
        }

        return results;
    }

    private static string DescribeSegmentType(int vt) =>
        Enum.IsDefined(typeof(swSketchSegments_e), vt) ? ((swSketchSegments_e)vt).ToString() : $"Unknown({vt})";
}
