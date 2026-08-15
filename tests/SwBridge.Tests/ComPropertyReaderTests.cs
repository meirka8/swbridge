using Xunit;

namespace SwBridge.Tests;

// InvokeMember behaves the same for plain .NET objects as for COM RCWs,
// so these run without SolidWorks installed.
public class ComPropertyReaderTests
{
    private sealed class Probe
    {
        public double Depth { get; set; } = 12.5;
        public string Name { get; set; } = "Boss-Extrude1";
        public double GetDepth(bool forward) => forward ? 12.5 : 0.0;
    }

    [Fact]
    public void TryGetProperty_ReadsExistingProperty()
    {
        Assert.True(ComPropertyReader.TryGetProperty(new Probe(), "Depth", out var value));
        Assert.Equal(12.5, value);
    }

    [Fact]
    public void TryGetProperty_IsCaseInsensitive()
    {
        Assert.True(ComPropertyReader.TryGetProperty(new Probe(), "depth", out var value));
        Assert.Equal(12.5, value);
    }

    [Fact]
    public void TryGetProperty_MissingProperty_ReturnsFalse()
    {
        Assert.False(ComPropertyReader.TryGetProperty(new Probe(), "DoesNotExist", out var value));
        Assert.Null(value);
    }

    [Fact]
    public void TryGetProperty_NullObject_ReturnsFalse()
    {
        Assert.False(ComPropertyReader.TryGetProperty(null, "Depth", out var value));
        Assert.Null(value);
    }

    [Fact]
    public void GetProperty_ReturnsNullForMissing()
    {
        Assert.Null(ComPropertyReader.GetProperty(new Probe(), "Nope"));
        Assert.Equal("Boss-Extrude1", ComPropertyReader.GetProperty(new Probe(), "name"));
    }

    [Fact]
    public void TryGetMember_InvokesAccessorMethodWithArgs()
    {
        Assert.True(ComPropertyReader.TryGetMember(new Probe(), "GetDepth", new object?[] { true }, out var value));
        Assert.Equal(12.5, value);

        Assert.True(ComPropertyReader.TryGetMember(new Probe(), "getdepth", new object?[] { false }, out var reverse));
        Assert.Equal(0.0, reverse);
    }

    [Fact]
    public void TryGetMember_WrongArgCount_ReturnsFalse()
    {
        Assert.False(ComPropertyReader.TryGetMember(new Probe(), "GetDepth", null, out _));
        Assert.False(ComPropertyReader.TryGetMember(new Probe(), "Depth", new object?[] { true, false }, out _));
    }
}
