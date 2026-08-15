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
