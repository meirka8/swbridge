using Xunit;

namespace SwBridge.Tests;

public class ComLifetimeTests
{
    [Fact]
    public void Release_NullOrManagedObject_DoesNotThrow()
    {
        ComLifetime.Release(null);
        ComLifetime.Release(new object());
        ComLifetime.Release("not a com object");
    }
}
