using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SwBridge;

/// <summary>
/// Wrapper around an open SolidWorks document. Exposes typed inspection methods
/// so most consumers never need to touch the interop types directly; the raw
/// <see cref="Model"/> remains available for advanced use.
/// </summary>
public sealed class SwDocument
{
    /// <summary>The underlying interop document object.</summary>
    public ModelDoc2 Model { get; }

    internal SwDocument(ModelDoc2 model)
    {
        Model = model;
    }

    /// <summary>Identity of this document (title, path, kind).</summary>
    public DocumentInfo Info => new(
        Model.GetTitle(),
        Model.GetPathName(),
        (swDocumentTypes_e)Model.GetType() switch
        {
            swDocumentTypes_e.swDocPART => SwDocumentType.Part,
            swDocumentTypes_e.swDocASSEMBLY => SwDocumentType.Assembly,
            swDocumentTypes_e.swDocDRAWING => SwDocumentType.Drawing,
            _ => SwDocumentType.Unknown,
        });

    /// <inheritdoc cref="ModelInspector.GetFeatures"/>
    public IReadOnlyList<FeatureInfo> GetFeatures(Func<string, IReadOnlyList<PropertySpec>?>? propertyLookup = null) =>
        ModelInspector.GetFeatures(Model, propertyLookup);

    /// <inheritdoc cref="ModelInspector.GetPartInfo"/>
    public PartInfo? GetPartInfo(Func<string, IReadOnlyList<PropertySpec>?>? propertyLookup = null) =>
        ModelInspector.GetPartInfo(Model, propertyLookup);
}
