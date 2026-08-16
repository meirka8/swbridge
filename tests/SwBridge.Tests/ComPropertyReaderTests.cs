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

    private sealed class DangerousProbe
    {
        public bool WasCalled { get; private set; }

        public int ExitApp()
        {
            WasCalled = true;
            return 0;
        }
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

    [Fact]
    public void TryGetPropertyStrict_ReadsExistingProperty()
    {
        Assert.True(ComPropertyReader.TryGetPropertyStrict(new Probe(), "Depth", out var value));
        Assert.Equal(12.5, value);
    }

    [Fact]
    public void TryGetPropertyStrict_IsCaseInsensitive()
    {
        Assert.True(ComPropertyReader.TryGetPropertyStrict(new Probe(), "depth", out var value));
        Assert.Equal(12.5, value);
    }

    [Fact]
    public void TryGetPropertyStrict_NullObject_ReturnsFalse()
    {
        Assert.False(ComPropertyReader.TryGetPropertyStrict(null, "Depth", out var value));
        Assert.Null(value);
    }

    [Fact]
    public void TryGetPropertyStrict_MethodName_FailsWithoutInvokingIt()
    {
        // C1 regression: unlike TryGetMember/TryGetProperty, the strict reader
        // must never fall back to DISPATCH_METHOD.
        var probe = new DangerousProbe();

        var ok = ComPropertyReader.TryGetPropertyStrict(probe, "ExitApp", out var value);

        Assert.False(ok);
        Assert.Null(value);
        Assert.False(probe.WasCalled);
    }

    [Fact]
    public void TryGetProperty_MethodName_DoesInvokeIt_ContrastWithStrict()
    {
        // Documents the existing, deliberate behavior of the combined-flag
        // reader (used by the feature-definition read path) so the contrast
        // with TryGetPropertyStrict above is explicit and tested, not assumed.
        var probe = new DangerousProbe();

        var ok = ComPropertyReader.TryGetProperty(probe, "ExitApp", out var value);

        Assert.True(ok);
        Assert.Equal(0, value);
        Assert.True(probe.WasCalled);
    }
}
