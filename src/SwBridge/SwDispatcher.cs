using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

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
/// exception-propagation and reentrancy semantics are unit-testable without
/// SolidWorks installed.
/// </remarks>
public sealed class SwDispatcher : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private volatile bool _disposed;

    /// <summary>
    /// Starts the dedicated STA thread. The thread is a background thread — it
    /// does not by itself keep the process alive — and sits idle until work is
    /// queued via <see cref="Run{T}(Func{T})"/>.
    /// </summary>
    public SwDispatcher()
    {
        _thread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "SwBridge.SwDispatcher",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void WorkerLoop()
    {
        foreach (var unitOfWork in _queue.GetConsumingEnumerable())
        {
            unitOfWork();
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the dispatcher thread and blocks the
    /// calling thread until it completes, returning its result. An exception
    /// thrown by <paramref name="work"/> is rethrown here, on the calling
    /// thread, with its original type and stack trace preserved.
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
    /// instead of queueing it. Because the dispatcher never runs two units of
    /// work concurrently by construction, inline execution produces the same
    /// total ordering a fresh dispatch from an idle queue would have — this is
    /// deliberately a composition mechanism, not merely a deadlock workaround,
    /// and it is what lets every public SwBridge method dispatch independently
    /// without every caller having to know whether it is already "inside" a
    /// dispatch.
    /// </para>
    /// </remarks>
    /// <param name="work">The work to run. Must not be null.</param>
    /// <exception cref="ObjectDisposedException">The dispatcher has been disposed.</exception>
    public T Run<T>(Func<T> work)
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

        using var done = new ManualResetEventSlim(false);
        var result = default(T)!;
        ExceptionDispatchInfo? failure = null;

        try
        {
            _queue.Add(() =>
            {
                try
                {
                    result = work();
                }
                catch (Exception ex)
                {
                    failure = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    done.Set();
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

        done.Wait();
        failure?.Throw();
        return result;
    }

    /// <summary>Runs <paramref name="work"/> on the dispatcher thread; see <see cref="Run{T}(Func{T})"/>.</summary>
    public void Run(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        Run<object?>(() =>
        {
            work();
            return null;
        });
    }

    /// <summary>
    /// Stops accepting new work and joins the dispatcher thread once any
    /// in-flight unit of work finishes. Not safe to call concurrently with a
    /// <see cref="Run{T}(Func{T})"/> call racing to queue new work — that is an
    /// accepted limitation of this first shape (see ADR 0003's note that the
    /// dispatcher's disposal semantics deserve review before wider use); callers
    /// should stop issuing work before disposing.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.CompleteAdding();

        if (Thread.CurrentThread != _thread)
        {
            _thread.Join();
        }

        _queue.Dispose();
    }
}
