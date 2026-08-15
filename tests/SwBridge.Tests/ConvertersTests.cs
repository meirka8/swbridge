using SolidWorks.Interop.swconst;
using Xunit;

namespace SwBridge.Tests;

// ResultConverters reads members via ComPropertyReader/ComPropertyReader-style
// reflection, which behaves the same for plain .NET objects as for COM RCWs,
// so DTO conversion is fully testable with fakes, without SolidWorks. The one
// thing these fakes cannot exercise is ComLifetime.Release actually releasing
// an RCW (a plain object is a no-op for Marshal.ReleaseComObject, already
// covered by ComLifetimeTests) — only that it is safe to call, which it is here too.
public class ConvertersTests
{
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
}
