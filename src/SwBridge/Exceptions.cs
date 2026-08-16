namespace SwBridge;

/// <summary>Base exception for all SwBridge failures.</summary>
public class SwBridgeException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public SwBridgeException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public SwBridgeException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Thrown when no running SolidWorks instance can be attached to.</summary>
public sealed class SwNotRunningException : SwBridgeException
{
    /// <summary>Creates the exception with its standard message.</summary>
    public SwNotRunningException()
        : base("No running SolidWorks instance found. SwBridge attaches to an already-open SolidWorks session; it does not launch one.") { }
}

/// <summary>
/// Thrown by <see cref="SwDispatcher.Run{T}(Func{T}, TimeSpan)"/> when a dispatched
/// call does not complete within its timeout. SwBridge cannot abort a unit of work
/// already running on the dispatcher's STA thread — it is still running there, and
/// will eventually complete or fail on its own — this exception only stops the
/// calling thread from waiting forever. The most common live cause is a modal
/// SolidWorks dialog (a rebuild-error prompt, a missing-reference dialog, a
/// licence check) blocking the underlying out-of-process COM call.
/// </summary>
public sealed class SwDispatchTimeoutException : SwBridgeException
{
    /// <summary>The timeout that elapsed while waiting for the dispatched call.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>Creates the exception for the given timeout.</summary>
    public SwDispatchTimeoutException(TimeSpan timeout)
        : base($"A dispatched SolidWorks COM call did not complete within {timeout}. It is still running " +
               "on the dispatcher thread and cannot be aborted — SolidWorks may be showing a modal dialog " +
               "or performing a long rebuild; check the SolidWorks window.")
    {
        Timeout = timeout;
    }
}
