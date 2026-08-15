namespace SwBridge;

/// <summary>SolidWorks document kind.</summary>
public enum SwDocumentType
{
    /// <summary>Document type not recognized.</summary>
    Unknown = 0,
    /// <summary>Part document (.SLDPRT).</summary>
    Part,
    /// <summary>Assembly document (.SLDASM).</summary>
    Assembly,
    /// <summary>Drawing document (.SLDDRW).</summary>
    Drawing,
}

/// <summary>Identity of an open SolidWorks document.</summary>
/// <param name="Title">Window title, e.g. <c>Part2.SLDPRT</c> (unsaved documents have no path, only a title).</param>
/// <param name="Path">Full file path; empty string for unsaved documents.</param>
/// <param name="Type">Document kind.</param>
public sealed record DocumentInfo(string Title, string Path, SwDocumentType Type);
