using Xunit;

namespace SwBridge.Tests;

// SwConnection's attach attempt is designed to fail gracefully (return false)
// rather than throw when no SolidWorks instance is registered, so this much is
// testable regardless of whether SolidWorks is installed/running on the
// machine running the tests.
public class SwConnectionTests
{
    [Fact]
    public void IsConnected_DoesNotThrow_RegardlessOfSolidWorksState()
    {
        using var connection = new SwConnection();

        var isConnected = connection.IsConnected;

        // No assertion on the value itself — it legitimately depends on whether
        // SolidWorks happens to be running on this machine. The point is that
        // the dispatcher-routed attach attempt completes without throwing.
        _ = isConnected;
    }

    [Fact]
    public void Dispose_IsSafeToCall()
    {
        var connection = new SwConnection();
        connection.Dispose();
        connection.Dispose(); // idempotent, via the underlying dispatcher
    }
}
