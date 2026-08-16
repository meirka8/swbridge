using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace SwBridge;

/// <summary>
/// Owns a single dedicated STA thread and marshals arbitrary work onto it,
/// blocking the caller until the work completes. This is the mechanism behind
/// <c>ADR 0003</c>'s COM-confinement rule: every COM touch in SwBridge — the
/// existing read path (<see cref="SwConnection"/>, <see cref="DocumentManager"/>,
/// the paths reached via <see cref="SwDocument"/>) and the write surface
/// (<see cref="ComInvoker"/>, <see cref="ComPath"/>) alike — runs inside a
/// <see cref="Run{T}(Func{T})"/> call on this thread, so operations against one
/// SolidWorks session are serialized by construction rather than by convention.
/// </summary>
/// <remarks>
/// This type has no COM dependency itself — it dispatches arbitrary
/// <see cref="Func{T}"/>/<see cref="Action"/> delegates — so its ordering,
/// exception-propagation, reentrancy and timeout semantics are unit-testable
/// without SolidWorks installed.
/// </remarks>
public sealed class SwDispatcher : IDisposable
{
    /// <summary>
    /// Default timeout applied by <see cref="Run{T}(Func{T})"/>/<see cref="Run(Action)"/>
    /// when no per-call timeout is given. <c>ADR 0003</c> names this need explicitly
    /// ("if a rebuild ever blocks a health check for minutes, the answer is a
    /// timeout on <c>Run</c>") without naming a value; 120 seconds is generous
    /// enough not to fire on a legitimately long rebuild while still bounding a
    /// wedged modal dialog to a reportable failure instead of a permanent hang.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Default bound for <see cref="Dispose()"/>'s thread join; see <see cref="Dispose(TimeSpan)"/>.</summary>
    public static readonly TimeSpan DefaultDisposeTimeout = TimeSpan.FromSeconds(5);

    // How often the idle wait below re-checks the work queue while pumping
    // Windows messages in between. Small enough that a queued call starts
    // promptly; not zero, so this is a bounded wait each cycle, not a spin loop.
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromMilliseconds(10);

    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private readonly TimeSpan _defaultTimeout;
    private volatile bool _disposed;
    private volatile bool _fullyStopped;

    /// <summary>Starts the dedicated STA thread with <see cref="DefaultTimeout"/>. See <see cref="SwDispatcher(TimeSpan)"/>.</summary>
    public SwDispatcher() : this(DefaultTimeout)
    {
    }

    /// <summary>
    /// Starts the dedicated STA thread. The thread is a background thread — it
    /// does not by itself keep the process alive — and sits idle, pumping
    /// Windows messages, until work is queued via <see cref="Run{T}(Func{T})"/>.
    /// </summary>
    /// <param name="defaultTimeout">
    /// Timeout used by the overloads of <see cref="Run{T}(Func{T})"/>/<see cref="Run(Action)"/>
    /// that do not take an explicit per-call timeout.
    /// </param>
    public SwDispatcher(TimeSpan defaultTimeout)
    {
        _defaultTimeout = defaultTimeout;
        _thread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "SwBridge.SwDispatcher",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>
    /// Whether the dispatcher thread has fully stopped after <see cref="Dispose()"/>.
    /// False both before disposal and if disposal's bounded join timed out while
    /// a unit of work was still running — see <see cref="Dispose(TimeSpan)"/>.
    /// </summary>
    public bool IsFullyStopped => _fullyStopped;

    // An STA apartment must pump: COM delivers a cross-apartment call into this
    // apartment as a window message (posted to the hidden OLE window this
    // thread's message queue owns). Calling a Win32 message function is also
    // what lazily creates that message queue in the first place. Without a pump,
    // any RCW created on this thread that is later touched from a different
    // thread/apartment (see ADR 0003's escape-hatch consumers, or a converter
    // bug that lets a live object leak) blocks that other thread forever, and
    // because this thread never services anything else meanwhile, the whole
    // dispatcher — every other queued Run call — is wedged behind it too.
    //
    // The loop below waits on the work queue with a short timeout rather than
    // blocking on it indefinitely, so it returns to PumpMessages() every
    // ~10ms when idle. This is a bounded wait each cycle (BlockingCollection.TryTake
    // blocks on a real wait handle for the timeout, not a spin loop) — not a
    // busy-spin — and it adds no latency to real work: TryTake returns the
    // moment an item is queued, pump or no pump.
    private void WorkerLoop()
    {
        while (true)
        {
            if (_queue.TryTake(out var unitOfWork, IdlePollInterval))
            {
                unitOfWork();
                continue;
            }

            if (_queue.IsCompleted)
            {
                return;
            }

            PumpMessages();
        }
    }

    private static void PumpMessages()
    {
        while (NativeMethods.PeekMessage(out var msg, IntPtr.Zero, 0, 0, NativeMethods.PM_REMOVE))
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the dispatcher thread using <see cref="DefaultTimeout"/>
    /// (or the timeout passed to <see cref="SwDispatcher(TimeSpan)"/>). See the
    /// <see cref="Run{T}(Func{T}, TimeSpan)"/> overload for the full contract.
    /// </summary>
    public T Run<T>(Func<T> work) => Run(work, _defaultTimeout);

    /// <summary>
    /// Runs <paramref name="work"/> on the dispatcher thread and blocks the
    /// calling thread until it completes or <paramref name="timeout"/> elapses,
    /// returning its result. An exception thrown by <paramref name="work"/> is
    /// rethrown here, on the calling thread, with its original type and message
    /// preserved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Reentrancy guard.</b> If <paramref name="work"/> is itself running on
    /// the dispatcher thread when it calls <see cref="Run{T}(Func{T})"/> again
    /// (directly, or by calling another SwBridge method that dispatches
    /// internally — e.g. <see cref="DocumentManager"/> composing several
    /// <see cref="SwConnection"/>/<see cref="SwDocument"/> calls inside one unit
    /// of work), queueing the nested call and blocking on it would deadlock:
    /// the only thread that could ever service the queue is this one, and it is
    /// the one waiting. The dispatcher detects this (comparing against its own
    /// thread) and executes the nested call immediately and synchronously
    /// instead of queueing it — untimed, since there is nothing to wait for.
    /// Because the dispatcher never runs two units of work concurrently by
    /// construction, inline execution produces the same total ordering a fresh
    /// dispatch from an idle queue would have — this is deliberately a
    /// composition mechanism, not merely a deadlock workaround, and it is what
    /// lets every public SwBridge method dispatch independently without every
    /// caller having to know whether it is already "inside" a dispatch.
    /// </para>
    /// <para>
    /// <b>Timeout.</b> If <paramref name="work"/> has not completed within
    /// <paramref name="timeout"/>, this throws <see cref="SwDispatchTimeoutException"/>
    /// on the calling thread. The unit of work itself is <em>not</em> aborted —
    /// there is no safe way to cancel an in-flight COM call — it keeps running
    /// on the dispatcher thread and will eventually set its result or exception,
    /// which is simply discarded at that point. Until it does, every other
    /// queued <see cref="Run{T}(Func{T})"/> call — from any caller — blocks
    /// behind it; the timeout bounds how long <em>this</em> call waits, not how
    /// long the dispatcher stays busy.
    /// </para>
    /// </remarks>
    /// <param name="work">The work to run. Must not be null.</param>
    /// <param name="timeout">How long to wait before throwing <see cref="SwDispatchTimeoutException"/>.</param>
    /// <exception cref="ObjectDisposedException">The dispatcher has been disposed.</exception>
    /// <exception cref="SwDispatchTimeoutException"><paramref name="work"/> did not complete within <paramref name="timeout"/>.</exception>
    public T Run<T>(Func<T> work, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (Thread.CurrentThread == _thread)
        {
            return work();
        }

        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SwDispatcher));
        }

        // TaskCompletionSource rather than a shared ManualResetEventSlim: on a
        // timeout this method returns while the queued unit of work is still
        // running and will still write its result/exception later. A disposable
        // wait handle would be unsafe to dispose here (the worker might still
        // touch it) and unsafe to leave undisposed indefinitely either; a TCS
        // has no disposal to get wrong and tolerates being completed after
        // everyone stopped waiting on it.
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            _queue.Add(() =>
            {
                try
                {
                    completion.TrySetResult(work());
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            });
        }
        catch (InvalidOperationException ex)
        {
            // The queue was completed (Dispose ran) between the disposed-check
            // above and this Add — a narrow race, reported the same way as an
            // already-observed disposal.
            throw new ObjectDisposedException(nameof(SwDispatcher), ex);
        }

        bool completedInTime;
        try
        {
            completedInTime = completion.Task.Wait(timeout);
        }
        catch (AggregateException)
        {
            // The work faulted within the timeout window; Task.Wait throws
            // rather than returning false in that case. Fall through to
            // GetAwaiter().GetResult() below, which unwraps to the single
            // original exception (type and message preserved) instead of
            // this wrapping AggregateException.
            completedInTime = true;
        }

        if (!completedInTime)
        {
            throw new SwDispatchTimeoutException(timeout);
        }

        return completion.Task.GetAwaiter().GetResult();
    }

    /// <summary>Runs <paramref name="work"/> on the dispatcher thread using the default timeout; see <see cref="Run{T}(Func{T})"/>.</summary>
    public void Run(Action work) => Run(work, _defaultTimeout);

    /// <summary>Runs <paramref name="work"/> on the dispatcher thread; see <see cref="Run{T}(Func{T}, TimeSpan)"/>.</summary>
    public void Run(Action work, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(work);
        Run<object?>(
            () =>
            {
                work();
                return null;
            },
            timeout);
    }

    /// <summary>Disposes with <see cref="DefaultDisposeTimeout"/>; see <see cref="Dispose(TimeSpan)"/>.</summary>
    public void Dispose() => Dispose(DefaultDisposeTimeout);

    /// <summary>
    /// Stops accepting new work and waits up to <paramref name="joinTimeout"/>
    /// for the dispatcher thread to finish any in-flight unit of work and exit.
    /// This bound exists so a wedged call (the same modal-dialog scenario
    /// <see cref="Run{T}(Func{T}, TimeSpan)"/>'s timeout guards against) cannot
    /// hang process shutdown — disposal itself cannot abort a running COM call
    /// any more than <c>Run</c> can. If the join times out, the background
    /// thread (<c>IsBackground = true</c>, so it will not itself keep the
    /// process alive) and its queue are left as they are rather than disposed
    /// out from under a thread that might still touch them; <see cref="IsFullyStopped"/>
    /// reports which outcome occurred. Not safe to call concurrently with a
    /// <see cref="Run{T}(Func{T})"/> call racing to queue new work — that is an
    /// accepted limitation of this first shape (see ADR 0003's note that the
    /// dispatcher's disposal semantics deserve review before wider use); callers
    /// should stop issuing work before disposing.
    /// </summary>
    public void Dispose(TimeSpan joinTimeout)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.CompleteAdding();

        if (Thread.CurrentThread == _thread)
        {
            // Disposing from within the dispatcher's own work — cannot join itself.
            return;
        }

        if (_thread.Join(joinTimeout))
        {
            _fullyStopped = true;
            _queue.Dispose();
        }
    }

    private static class NativeMethods
    {
        internal const uint PM_REMOVE = 0x0001;

        [DllImport("user32.dll")]
        internal static extern bool PeekMessage(out MSG msg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        [DllImport("user32.dll")]
        internal static extern bool TranslateMessage(ref MSG msg);

        [DllImport("user32.dll")]
        internal static extern IntPtr DispatchMessage(ref MSG msg);

        [StructLayout(LayoutKind.Sequential)]
        internal struct MSG
        {
            public IntPtr Hwnd;
            public uint Message;
            public IntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public POINT Pt;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            public int X;
            public int Y;
        }
    }
}
