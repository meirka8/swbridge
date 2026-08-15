using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SwBridge;

/// <summary>
/// Wrapper around an open SolidWorks document. Exposes typed inspection methods
/// so most consumers never need to touch the interop types directly; the raw
/// <see cref="Model"/> remains available for advanced use.
/// </summary>
/// <remarks>
/// Every public method runs on the owning <see cref="SwConnection"/>'s
/// <see cref="SwDispatcher"/> (per <c>ADR 0003</c>), the same thread
/// <see cref="DocumentManager"/> used to produce this <see cref="SwDocument"/>
/// in the first place — so calling these from inside another SwBridge dispatch
/// (e.g. while <see cref="DocumentManager"/> is composing a multi-document read)
/// is safe and does not deadlock; see the dispatcher's reentrancy guard.
/// <see cref="Model"/> itself is a raw escape hatch and is not thread-guarded —
/// using it directly from a thread other than the dispatcher's is the
/// cross-apartment risk ADR 0003 describes.
/// </remarks>
public sealed class SwDocument
{
    private readonly SwDispatcher _dispatcher;

    /// <summary>The underlying interop document object.</summary>
    public ModelDoc2 Model { get; }

    internal SwDocument(ModelDoc2 model, SwDispatcher dispatcher)
    {
        Model = model;
        _dispatcher = dispatcher;
    }

    /// <summary>Identity of this document (title, path, kind).</summary>
    public DocumentInfo Info => _dispatcher.Run(() => new DocumentInfo(
        Model.GetTitle(),
        Model.GetPathName(),
        (swDocumentTypes_e)Model.GetType() switch
        {
            swDocumentTypes_e.swDocPART => SwDocumentType.Part,
            swDocumentTypes_e.swDocASSEMBLY => SwDocumentType.Assembly,
            swDocumentTypes_e.swDocDRAWING => SwDocumentType.Drawing,
            _ => SwDocumentType.Unknown,
        }));

    /// <inheritdoc cref="ModelInspector.GetFeatures"/>
    public IReadOnlyList<FeatureInfo> GetFeatures(Func<string, IReadOnlyList<PropertySpec>?>? propertyLookup = null) =>
        _dispatcher.Run(() => ModelInspector.GetFeatures(Model, propertyLookup));

    /// <inheritdoc cref="ModelInspector.GetPartInfo"/>
    public PartInfo? GetPartInfo(Func<string, IReadOnlyList<PropertySpec>?>? propertyLookup = null) =>
        _dispatcher.Run(() => ModelInspector.GetPartInfo(Model, propertyLookup));

    /// <inheritdoc cref="ModelInspector.DescribeFeatureDefinition"/>
    public IReadOnlyList<ComMemberInfo>? DescribeFeatureDefinition(string featureName) =>
        _dispatcher.Run(() => ModelInspector.DescribeFeatureDefinition(Model, featureName));
}
