using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SwBridge;

/// <summary>
/// Enumerates and resolves open documents on a <see cref="SwConnection"/>.
/// All methods throw <see cref="SwNotRunningException"/> when SolidWorks is not
/// reachable, so callers can distinguish "SolidWorks not running" from
/// "no documents open".
/// </summary>
public sealed class DocumentManager
{
    private readonly SwConnection _connection;

    /// <summary>Creates a manager over the given connection.</summary>
    public DocumentManager(SwConnection connection)
    {
        _connection = connection;
    }

    /// <summary>Lists the identity of every open document.</summary>
    /// <exception cref="SwNotRunningException"/>
    public IReadOnlyList<DocumentInfo> ListOpenDocuments()
    {
        var docs = GetOpenDocuments();
        var infos = new List<DocumentInfo>(docs.Count);
        foreach (var doc in docs)
        {
            infos.Add(doc.Info);
        }
        return infos;
    }

    /// <summary>Returns every open document.</summary>
    /// <exception cref="SwNotRunningException"/>
    public IReadOnlyList<SwDocument> GetOpenDocuments()
    {
        var app = _connection.GetApp();
        var result = new List<SwDocument>();
        if (app.GetDocuments() is object[] docs)
        {
            foreach (var docObject in docs)
            {
                if (docObject is ModelDoc2 model)
                {
                    result.Add(new SwDocument(model));
                }
            }
        }
        return result;
    }

    /// <summary>Returns the document currently active in the SolidWorks window, or null if none.</summary>
    /// <exception cref="SwNotRunningException"/>
    public SwDocument? GetActiveDocument()
    {
        var app = _connection.GetApp();
        return app.ActiveDoc is ModelDoc2 model ? new SwDocument(model) : null;
    }

    /// <summary>
    /// Resolves an open document by name. Matches (case-insensitively) the window
    /// title, the file name with or without extension, or the full path.
    /// Returns null when nothing matches.
    /// </summary>
    /// <exception cref="SwNotRunningException"/>
    public SwDocument? Resolve(string documentName)
    {
        foreach (var doc in GetOpenDocuments())
        {
            var info = doc.Info;
            if (Matches(documentName, info.Title) ||
                Matches(documentName, info.Path) ||
                Matches(documentName, System.IO.Path.GetFileName(info.Path)) ||
                Matches(documentName, System.IO.Path.GetFileNameWithoutExtension(info.Title)) ||
                Matches(documentName, System.IO.Path.GetFileNameWithoutExtension(info.Path)))
            {
                return doc;
            }
        }
        return null;
    }

    /// <summary>
    /// Opens a document from disk (silently, no dialogs). The document type is
    /// inferred from the file extension (.SLDPRT/.SLDASM/.SLDDRW).
    /// </summary>
    /// <exception cref="SwNotRunningException"/>
    /// <exception cref="SwBridgeException">The file could not be opened.</exception>
    public SwDocument OpenDocument(string path)
    {
        var app = _connection.GetApp();
        var type = System.IO.Path.GetExtension(path).ToUpperInvariant() switch
        {
            ".SLDPRT" => swDocumentTypes_e.swDocPART,
            ".SLDASM" => swDocumentTypes_e.swDocASSEMBLY,
            ".SLDDRW" => swDocumentTypes_e.swDocDRAWING,
            var ext => throw new SwBridgeException($"Unrecognized SolidWorks document extension '{ext}' for '{path}'."),
        };

        int errors = 0, warnings = 0;
        var model = app.OpenDoc6(
            path,
            (int)type,
            (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
            "",
            ref errors,
            ref warnings);

        return model is ModelDoc2 doc
            ? new SwDocument(doc)
            : throw new SwBridgeException($"SolidWorks failed to open '{path}' (errors={errors}, warnings={warnings}).");
    }

    private static bool Matches(string requested, string candidate) =>
        !string.IsNullOrEmpty(candidate) &&
        string.Equals(requested, candidate, StringComparison.OrdinalIgnoreCase);
}
