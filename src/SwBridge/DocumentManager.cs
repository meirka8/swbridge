using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SwBridge;

/// <summary>
/// Enumerates and resolves open documents on a <see cref="SwConnection"/>.
/// All methods throw <see cref="SwNotRunningException"/> when SolidWorks is not
/// reachable, so callers can distinguish "SolidWorks not running" from
/// "no documents open".
/// </summary>
/// <remarks>
/// Every method runs on <see cref="SwConnection.Dispatcher"/> (per <c>ADR
/// 0003</c>) — the same STA thread <see cref="SwConnection"/> attaches on and
/// every <see cref="SwDocument"/> this class hands out reads through, so a
/// document enumerated here is never read from a second thread by SwBridge's
/// own code.
/// </remarks>
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
    public IReadOnlyList<DocumentInfo> ListOpenDocuments() =>
        _connection.Dispatcher.Run(() =>
        {
            var docs = GetOpenDocumentsUnsynchronized();
            var infos = new List<DocumentInfo>(docs.Count);
            foreach (var doc in docs)
            {
                infos.Add(doc.Info);
            }
            return (IReadOnlyList<DocumentInfo>)infos;
        });

    /// <summary>Returns every open document.</summary>
    /// <exception cref="SwNotRunningException"/>
    public IReadOnlyList<SwDocument> GetOpenDocuments() =>
        _connection.Dispatcher.Run(() => (IReadOnlyList<SwDocument>)GetOpenDocumentsUnsynchronized());

    // Assumes it is already running on _connection.Dispatcher's thread — every
    // public method wraps a call to this in exactly one Run, and the dispatcher's
    // reentrancy guard makes composing it into a larger unit of work safe too.
    private List<SwDocument> GetOpenDocumentsUnsynchronized()
    {
        var app = _connection.GetApp();
        var result = new List<SwDocument>();
        if (app.GetDocuments() is object[] docs)
        {
            foreach (var docObject in docs)
            {
                if (docObject is ModelDoc2 model)
                {
                    result.Add(new SwDocument(model, _connection.Dispatcher));
                }
            }
        }
        return result;
    }

    /// <summary>Returns the document currently active in the SolidWorks window, or null if none.</summary>
    /// <exception cref="SwNotRunningException"/>
    public SwDocument? GetActiveDocument() =>
        _connection.Dispatcher.Run(() =>
        {
            var app = _connection.GetApp();
            return app.ActiveDoc is ModelDoc2 model ? new SwDocument(model, _connection.Dispatcher) : null;
        });

    /// <summary>
    /// Resolves an open document by name. Matches (case-insensitively) the window
    /// title, the file name with or without extension, or the full path.
    /// Returns null when nothing matches.
    /// </summary>
    /// <exception cref="SwNotRunningException"/>
    public SwDocument? Resolve(string documentName) =>
        _connection.Dispatcher.Run(() =>
        {
            foreach (var doc in GetOpenDocumentsUnsynchronized())
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
        });

    /// <summary>
    /// Opens a document from disk (silently, no dialogs). The document type is
    /// inferred from the file extension (.SLDPRT/.SLDASM/.SLDDRW).
    /// </summary>
    /// <exception cref="SwNotRunningException"/>
    /// <exception cref="SwBridgeException">The file could not be opened.</exception>
    public SwDocument OpenDocument(string path) =>
        _connection.Dispatcher.Run(() =>
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
                ? new SwDocument(doc, _connection.Dispatcher)
                : throw new SwBridgeException($"SolidWorks failed to open '{path}' (errors={errors}, warnings={warnings}).");
        });

    /// <summary>
    /// Creates a new part document from a template — the seed step every
    /// downstream write operation builds on (<c>ADR 0001</c>, SwBridge piece 6).
    /// </summary>
    /// <param name="templatePath">
    /// Path to a <c>.prtdot</c> template. When omitted, uses SolidWorks' own
    /// configured default part template
    /// (<c>swUserPreferenceStringValue_e.swDefaultTemplatePart</c>) — the
    /// pattern verified live in <c>docs/seed-verification.md</c> §4.11.
    /// </param>
    /// <exception cref="SwNotRunningException"/>
    /// <exception cref="SwBridgeException">
    /// No template path was resolved, or SolidWorks failed to create the document.
    /// </exception>
    public SwDocument NewPart(string? templatePath = null) =>
        _connection.Dispatcher.Run(() =>
        {
            var app = _connection.GetApp();
            var template = templatePath;
            if (string.IsNullOrEmpty(template))
            {
                template = app.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
            }

            if (string.IsNullOrEmpty(template))
            {
                throw new SwBridgeException(
                    "No part template path was supplied, and SolidWorks has no default part template configured " +
                    "(swUserPreferenceStringValue_e.swDefaultTemplatePart is empty).");
            }

            var model = app.NewDocument(template, 0, 0, 0);
            return model is ModelDoc2 doc
                ? new SwDocument(doc, _connection.Dispatcher)
                : throw new SwBridgeException($"SolidWorks failed to create a new part from template '{template}'.");
        });

    private static bool Matches(string requested, string candidate) =>
        !string.IsNullOrEmpty(candidate) &&
        string.Equals(requested, candidate, StringComparison.OrdinalIgnoreCase);
}
