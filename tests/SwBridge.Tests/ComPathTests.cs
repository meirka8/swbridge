using Xunit;

namespace SwBridge.Tests;

// ComPath walks properties via ComPropertyReader, which behaves the same for
// plain .NET objects as for COM RCWs, so path parsing/resolution is fully
// testable without SolidWorks.
public class ComPathTests
{
    private sealed class Leaf
    {
        public string Value => "leaf-value";
    }

    private sealed class Middle
    {
        public Leaf Child { get; } = new();
        public object? NullThing => null;
    }

    private sealed class Root
    {
        public Middle Middle { get; } = new();
    }

    [Fact]
    public void Resolve_EmptyPath_ReturnsRootItself()
    {
        var root = new Root();

        var result = ComPath.Resolve(root, "");

        Assert.True(result.Success);
        Assert.Same(root, result.Value);
    }

    [Fact]
    public void Resolve_WhitespacePath_ReturnsRootItself()
    {
        var root = new Root();

        var result = ComPath.Resolve(root, "   ");

        Assert.True(result.Success);
        Assert.Same(root, result.Value);
    }

    [Fact]
    public void Resolve_SingleSegment_WalksOneHop()
    {
        var root = new Root();

        var result = ComPath.Resolve(root, "Middle");

        Assert.True(result.Success);
        Assert.Same(root.Middle, result.Value);
    }

    [Fact]
    public void Resolve_MultiSegment_WalksEveryHop()
    {
        var root = new Root();

        var result = ComPath.Resolve(root, "Middle.Child");

        Assert.True(result.Success);
        Assert.Same(root.Middle.Child, result.Value);
    }

    [Fact]
    public void Resolve_TrimsWhitespaceAroundSegments()
    {
        var root = new Root();

        var result = ComPath.Resolve(root, " Middle . Child ");

        Assert.True(result.Success);
        Assert.Same(root.Middle.Child, result.Value);
    }

    [Fact]
    public void Resolve_NullRoot_Fails()
    {
        var result = ComPath.Resolve(null, "Middle");

        Assert.False(result.Success);
        Assert.NotNull(result.FailureDetail);
    }

    [Fact]
    public void Resolve_UnknownSegment_FailsNamingTheSegment()
    {
        var root = new Root();

        var result = ComPath.Resolve(root, "Middle.DoesNotExist");

        Assert.False(result.Success);
        Assert.Equal("Middle.DoesNotExist", result.FailedSegment);
    }

    [Fact]
    public void Resolve_SegmentResolvesToNull_FailsAtThatSegment()
    {
        var root = new Root();

        var result = ComPath.Resolve(root, "Middle.NullThing.Value");

        Assert.False(result.Success);
        Assert.Equal("Middle.NullThing", result.FailedSegment);
    }

    [Fact]
    public void Resolve_EmptySegmentBetweenDots_Fails()
    {
        var root = new Root();

        var result = ComPath.Resolve(root, "Middle..Child");

        Assert.False(result.Success);
    }

    [Fact]
    public void ComPathResult_FactoryMethods_SetExpectedShape()
    {
        var ok = ComPathResult.Ok("value");
        Assert.True(ok.Success);
        Assert.Equal("value", ok.Value);

        var fail = ComPathResult.Fail("Segment", "detail");
        Assert.False(fail.Success);
        Assert.Equal("Segment", fail.FailedSegment);
        Assert.Equal("detail", fail.FailureDetail);
    }
}
