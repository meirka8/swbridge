using SolidWorks.Interop.swconst;
using Xunit;

namespace SwBridge.Tests;

// ResultConverters reads members via ComPropertyReader/ComPropertyReader-style
// reflection, which behaves the same for plain .NET objects as for COM RCWs,
// so DTO shape conversion is fully testable with fakes, without SolidWorks.
// The H4 ownership-flag behavior specifically needs a real, disconnectable RCW
// to prove anything (Marshal.ReleaseComObject is a no-op on a plain object), so
// those tests use "Scripting.FileSystemObject" — a built-in Windows automation
// object, distinct from SolidWorks — skipping gracefully if unavailable.
public class ConvertersTests
{
    private static object? CreateFileSystemObject()
    {
        var type = Type.GetTypeFromProgID("Scripting.FileSystemObject");
        return type == null ? null : Activator.CreateInstance(type);
    }

    private sealed class FakeFeature
    {
        public string Name => "Boss-Extrude1";
        public string GetTypeName2() => "Extrusion";
    }

    private sealed class FakeFeatureWithoutName
    {
        public string GetTypeName2() => "Extrusion";
    }

    private sealed class FakeSketchSegment
    {
        public int GetID() => 7;
        public new int GetType() => (int)swSketchSegments_e.swSketchLINE;
    }

    // Matches what ISketchSegment.GetID() actually returns live, despite its
    // interop signature declaring a bare object return — see Converters.cs.
    private sealed class FakeSketchSegmentArrayId
    {
        public int[] GetID() => new[] { 11, 22 };
        public new int GetType() => (int)swSketchSegments_e.swSketchARC;
    }

    private sealed class FakeSketchSegmentUnknownType
    {
        public int GetID() => 8;
        public new int GetType() => 999999;
    }

    [Fact]
    public void ToFeatureRef_Null_ReturnsNull()
    {
        Assert.Null(ResultConverters.ToFeatureRef(null));
    }

    [Fact]
    public void ToFeatureRef_ReadsNameAndTypeName()
    {
        var result = ResultConverters.ToFeatureRef(new FakeFeature());

        Assert.NotNull(result);
        Assert.Equal("Boss-Extrude1", result!.Name);
        Assert.Equal("Extrusion", result.TypeName);
    }

    [Fact]
    public void ToFeatureRef_MissingName_ReturnsNull()
    {
        Assert.Null(ResultConverters.ToFeatureRef(new FakeFeatureWithoutName()));
    }

    [Fact]
    public void ToSketchSegmentRef_Null_ReturnsNull()
    {
        Assert.Null(ResultConverters.ToSketchSegmentRef(null));
    }

    [Fact]
    public void ToSketchSegmentRef_ReadsIdAndHumanReadableSegmentType()
    {
        var result = ResultConverters.ToSketchSegmentRef(new FakeSketchSegment());

        Assert.NotNull(result);
        Assert.Equal(7, result!.Id);
        Assert.Equal(nameof(swSketchSegments_e.swSketchLINE), result.SegmentType);
    }

    [Fact]
    public void ToSketchSegmentRef_ArrayId_UsesFirstElement()
    {
        var result = ResultConverters.ToSketchSegmentRef(new FakeSketchSegmentArrayId());

        Assert.NotNull(result);
        Assert.Equal(11, result!.Id);
        Assert.Equal(nameof(swSketchSegments_e.swSketchARC), result.SegmentType);
    }

    [Fact]
    public void ToSketchSegmentRef_UndefinedEnumValue_ReportsUnknownWithValue()
    {
        var result = ResultConverters.ToSketchSegmentRef(new FakeSketchSegmentUnknownType());

        Assert.NotNull(result);
        Assert.Equal("Unknown(999999)", result!.SegmentType);
    }

    [Fact]
    public void ToSketchSegmentRefs_Null_ReturnsEmpty()
    {
        Assert.Empty(ResultConverters.ToSketchSegmentRefs(null));
    }

    [Fact]
    public void ToSketchSegmentRefs_DropsUnconvertibleEntriesButKeepsGoodOnes()
    {
        var segments = new object?[] { new FakeSketchSegment(), null, new FakeSketchSegment() };

        var results = ResultConverters.ToSketchSegmentRefs(segments);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(7, r.Id));
    }

    // --- H4: ownership flag ------------------------------------------------

    [Fact]
    public void ToFeatureRef_OwnsReferenceFalse_LeavesSharedRcwUsableAfterward()
    {
        var fso = CreateFileSystemObject();
        if (fso == null)
        {
            return; // ProgID unavailable on this machine — skip gracefully.
        }

        try
        {
            // FileSystemObject has neither Name nor GetTypeName2, so conversion
            // itself returns null — the point is what happens to fso regardless.
            ResultConverters.ToFeatureRef(fso, ownsReference: false);

            Assert.True(ComPropertyReader.TryGetProperty(fso, "Drives", out _),
                "the shared RCW must still be usable when ownsReference is false");
        }
        finally
        {
            ComLifetime.Release(fso);
        }
    }

    [Fact]
    public void ToFeatureRef_OwnsReferenceTrueDefault_ReleasesRcwAfterward()
    {
        var fso = CreateFileSystemObject();
        if (fso == null)
        {
            return; // ProgID unavailable on this machine — skip gracefully.
        }

        ResultConverters.ToFeatureRef(fso); // default ownsReference: true

        Assert.False(ComPropertyReader.TryGetProperty(fso, "Drives", out _),
            "the RCW must be disconnected (and therefore unusable) once released");
    }

    [Fact]
    public void ToSketchSegmentRef_OwnsReferenceFalse_LeavesSharedRcwUsableAfterward()
    {
        var fso = CreateFileSystemObject();
        if (fso == null)
        {
            return; // ProgID unavailable on this machine — skip gracefully.
        }

        try
        {
            ResultConverters.ToSketchSegmentRef(fso, ownsReference: false);

            Assert.True(ComPropertyReader.TryGetProperty(fso, "Drives", out _),
                "the shared RCW must still be usable when ownsReference is false");
        }
        finally
        {
            ComLifetime.Release(fso);
        }
    }

    [Fact]
    public void ToSketchSegmentRef_OwnsReferenceTrueDefault_ReleasesRcwAfterward()
    {
        var fso = CreateFileSystemObject();
        if (fso == null)
        {
            return; // ProgID unavailable on this machine — skip gracefully.
        }

        ResultConverters.ToSketchSegmentRef(fso); // default ownsReference: true

        Assert.False(ComPropertyReader.TryGetProperty(fso, "Drives", out _),
            "the RCW must be disconnected (and therefore unusable) once released");
    }
}
