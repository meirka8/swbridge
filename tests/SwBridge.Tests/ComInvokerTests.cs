using Xunit;

namespace SwBridge.Tests;

// InvokeMember behaves the same for plain .NET objects as for COM RCWs (it is
// only the underlying dispatch mechanism that differs), so the single-flag
// discipline is fully testable without SolidWorks.
public class ComInvokerTests
{
    private sealed class Probe
    {
        public double Depth { get; set; } = 1.5;

        public double GetDepth(bool forward) => forward ? Depth : 0.0;
    }

    [Fact]
    public void InvokeMethod_CallsMethodWithArgs()
    {
        var outcome = ComInvoker.InvokeMethod(new Probe(), "GetDepth", new object?[] { true });

        Assert.True(outcome.Success);
        Assert.Equal(1.5, outcome.Value);
        Assert.Null(outcome.FailureDetail);
    }

    [Fact]
    public void GetProperty_ReadsBareProperty()
    {
        var outcome = ComInvoker.GetProperty(new Probe(), "Depth");

        Assert.True(outcome.Success);
        Assert.Equal(1.5, outcome.Value);
    }

    [Fact]
    public void SetProperty_WritesBareProperty()
    {
        var probe = new Probe();

        var outcome = ComInvoker.SetProperty(probe, "Depth", 9.0);

        Assert.True(outcome.Success);
        Assert.Equal(9.0, probe.Depth);
    }

    [Fact]
    public void GetProperty_NeverFallsBackToInvokingAMethod()
    {
        // Contrast with ComPropertyReader, which combines GetProperty|InvokeMethod.
        // ComInvoker.GetProperty must use GetProperty alone, so a method name
        // (not a property) fails rather than silently calling it.
        var outcome = ComInvoker.GetProperty(new Probe(), "GetDepth");

        Assert.False(outcome.Success);
        Assert.NotNull(outcome.FailureDetail);
    }

    [Fact]
    public void InvokeMethod_NeverFallsBackToReadingAProperty()
    {
        var outcome = ComInvoker.InvokeMethod(new Probe(), "Depth");

        Assert.False(outcome.Success);
        Assert.NotNull(outcome.FailureDetail);
    }

    [Fact]
    public void SetProperty_OnUnwritableMember_Fails()
    {
        // GetDepth is a method, not a settable property.
        var outcome = ComInvoker.SetProperty(new Probe(), "GetDepth", 1.0);

        Assert.False(outcome.Success);
    }

    [Fact]
    public void NullTarget_FailsForEveryEntryPoint()
    {
        Assert.False(ComInvoker.InvokeMethod(null, "GetDepth").Success);
        Assert.False(ComInvoker.GetProperty(null, "Depth").Success);
        Assert.False(ComInvoker.SetProperty(null, "Depth", 1.0).Success);
    }

    [Fact]
    public void MissingMember_FailsWithDetail_DoesNotThrow()
    {
        var outcome = ComInvoker.InvokeMethod(new Probe(), "DoesNotExist");

        Assert.False(outcome.Success);
        Assert.False(string.IsNullOrEmpty(outcome.FailureDetail));
    }

    [Fact]
    public void InvokeOutcome_FactoryMethods_SetExpectedShape()
    {
        var ok = InvokeOutcome.Ok(42);
        Assert.True(ok.Success);
        Assert.Equal(42, ok.Value);
        Assert.Null(ok.FailureDetail);

        var fail = InvokeOutcome.Fail("nope");
        Assert.False(fail.Success);
        Assert.Null(fail.Value);
        Assert.Equal("nope", fail.FailureDetail);
    }
}
