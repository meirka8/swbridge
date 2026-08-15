using Xunit;

namespace SwBridge.Tests;

// SwDispatcher has no COM dependency — it marshals arbitrary delegates onto a
// dedicated thread — so its ordering, exception-propagation, reentrancy and
// disposal semantics are fully testable without SolidWorks.
public class SwDispatcherTests
{
    [Fact]
    public void Run_ReturnsResultFromDispatcherThread()
    {
        using var dispatcher = new SwDispatcher();

        var threadId = dispatcher.Run(() => Environment.CurrentManagedThreadId);

        Assert.NotEqual(Environment.CurrentManagedThreadId, threadId);
    }

    [Fact]
    public void Run_Action_Overload_ExecutesOnDispatcherThread()
    {
        using var dispatcher = new SwDispatcher();
        var ran = false;

        dispatcher.Run(() => ran = true);

        Assert.True(ran);
    }

    [Fact]
    public void Run_PropagatesException_WithOriginalTypeAndMessage()
    {
        using var dispatcher = new SwDispatcher();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            dispatcher.Run<int>(() => throw new InvalidOperationException("boom")));

        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public async Task Run_ConcurrentCallsFromMultipleThreads_NeverOverlap()
    {
        using var dispatcher = new SwDispatcher();
        var inProgress = 0;
        var overlapDetected = false;
        var tasks = new List<Task>();

        for (var i = 0; i < 20; i++)
        {
            tasks.Add(Task.Run(() => dispatcher.Run(() =>
            {
                if (Interlocked.Increment(ref inProgress) > 1)
                {
                    overlapDetected = true;
                }

                Thread.Sleep(5); // give a real chance for a race to show up
                Interlocked.Decrement(ref inProgress);
            })));
        }

        await Task.WhenAll(tasks);

        Assert.False(overlapDetected, "two units of work ran concurrently on the dispatcher");
    }

    [Fact]
    public async Task Run_Reentrant_ExecutesInlineWithoutDeadlocking()
    {
        using var dispatcher = new SwDispatcher();

        // Started from a thread-pool thread so the outer Run genuinely queues;
        // the inner Run then runs ON the dispatcher thread and must detect that
        // and execute inline rather than queueing (which would deadlock: the
        // only thread that could service the queue is the one now waiting).
        var task = Task.Run(() => dispatcher.Run(() => dispatcher.Run(() => 42)));

        var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))) == task;

        Assert.True(finished, "a reentrant Run call deadlocked instead of executing inline");
        Assert.Equal(42, await task);
    }

    [Fact]
    public void Run_Reentrant_PreservesOrdering()
    {
        using var dispatcher = new SwDispatcher();
        var order = new List<int>();

        dispatcher.Run(() =>
        {
            order.Add(1);
            dispatcher.Run(() => order.Add(2));
            order.Add(3);
        });

        Assert.Equal(new[] { 1, 2, 3 }, order);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var dispatcher = new SwDispatcher();
        dispatcher.Dispose();
        dispatcher.Dispose(); // must not throw
    }

    [Fact]
    public void Run_AfterDispose_ThrowsObjectDisposedException()
    {
        var dispatcher = new SwDispatcher();
        dispatcher.Dispose();

        Assert.Throws<ObjectDisposedException>(() => dispatcher.Run(() => 1));
    }

    [Fact]
    public void Run_NullWork_Throws()
    {
        using var dispatcher = new SwDispatcher();

        Assert.Throws<ArgumentNullException>(() => dispatcher.Run((Func<int>)null!));
        Assert.Throws<ArgumentNullException>(() => dispatcher.Run((Action)null!));
    }
}
