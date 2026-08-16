using SolidWorks.Interop.sldworks;

namespace SwBridge;

/// <summary>
/// Cheap, targeted reads of document state — the vocabulary <c>ADR 0002</c>
/// builds write-step verification and precondition checks on (feature count,
/// sketch mode, selection count, sketch segment count, rebuild state). Useful
/// standalone too: reused by the same reads a verification step would take
/// before and after a write, but nothing here is specific to verification.
/// </summary>
/// <remarks>
/// Every probe here takes an already-resolved <see cref="ModelDoc2"/> and does
/// no dispatching itself — callers run these inside a <see cref="SwDispatcher.Run{T}(Func{T})"/>
/// call, typically bracketing a write (per <c>ADR 0002</c>: "snapshots the
/// relevant probes before the invoke and re-reads after, inside the same STA
/// dispatch"). Each probe releases the intermediate manager/sketch objects it
/// reaches through the document along the way (<c>FeatureManager</c>,
/// <c>SketchManager</c>, <c>ActiveSketch</c>, <c>SelectionManager</c>,
/// <c>Extension</c>) — a write operation calls several of these per step
/// (precondition + pre-snapshot + post-verify + result snapshot), and at
/// MCP-session timescales the unreleased native references add up enough that
/// SolidWorks may refuse to fully let go of a document after the user closes it.
/// </remarks>
public static class DocumentStateProbes
{
    /// <summary>
    /// Number of features in the tree (<c>IFeatureManager.GetFeatureCount</c>).
    /// The probe behind <c>ADR 0002</c>'s <c>featureCountIncreased(by)</c> check.
    /// </summary>
    /// <param name="doc">The document to read.</param>
    /// <param name="topLevelOnly">When true, counts only top-level features (excludes sub-features).</param>
    public static int GetFeatureCount(ModelDoc2 doc, bool topLevelOnly = false)
    {
        var featureManager = doc.FeatureManager;
        try
        {
            return featureManager.GetFeatureCount(topLevelOnly);
        }
        finally
        {
            ComLifetime.Release(featureManager);
        }
    }

    /// <summary>
    /// Whether the document currently has an active (being-edited) sketch. The
    /// probe behind <c>ADR 0001</c>'s <c>inSketchMode</c>/<c>notInSketchMode</c>
    /// preconditions and <c>ADR 0002</c>'s <c>sketchModeIs(bool)</c> check.
    /// </summary>
    /// <param name="doc">The document to read.</param>
    public static bool IsInSketchMode(ModelDoc2 doc)
    {
        var sketchManager = doc.SketchManager;
        try
        {
            var activeSketch = sketchManager.ActiveSketch;
            try
            {
                return activeSketch != null;
            }
            finally
            {
                ComLifetime.Release(activeSketch);
            }
        }
        finally
        {
            ComLifetime.Release(sketchManager);
        }
    }

    /// <summary>
    /// Number of segments in the active sketch, or 0 when there is none. The
    /// probe behind <c>ADR 0002</c>'s <c>sketchSegmentCountIncreased(by)</c> check.
    /// </summary>
    /// <param name="doc">The document to read.</param>
    public static int GetSketchSegmentCount(ModelDoc2 doc)
    {
        var sketchManager = doc.SketchManager;
        try
        {
            var sketch = sketchManager.ActiveSketch;
            if (sketch == null)
            {
                return 0;
            }

            try
            {
                return sketch.GetSketchSegments() is object[] segments ? segments.Length : 0;
            }
            finally
            {
                ComLifetime.Release(sketch);
            }
        }
        finally
        {
            ComLifetime.Release(sketchManager);
        }
    }

    /// <summary>
    /// Number of currently selected entities, across all selection marks. The
    /// probe behind <c>ADR 0001</c>'s <c>selectionCount(min, max)</c> precondition.
    /// </summary>
    /// <param name="doc">The document to read.</param>
    public static int GetSelectionCount(ModelDoc2 doc)
    {
        var selectionManager = doc.SelectionManager;
        try
        {
            return selectionManager is SelectionMgr manager ? manager.GetSelectedObjectCount2(-1) : 0;
        }
        finally
        {
            ComLifetime.Release(selectionManager);
        }
    }

    /// <summary>
    /// Whether the document has pending changes that have not been rebuilt yet
    /// (<c>IModelDocExtension.NeedsRebuild2</c>) — a cheap, passive read that
    /// does not itself trigger a rebuild. This says whether a rebuild is
    /// outstanding, not whether the <em>last</em> rebuild had errors; for that,
    /// see <see cref="RebuildSucceeded"/>.
    /// </summary>
    /// <param name="doc">The document to read.</param>
    public static bool NeedsRebuild(ModelDoc2 doc)
    {
        var extension = doc.Extension;
        try
        {
            return extension.NeedsRebuild2 != 0;
        }
        finally
        {
            ComLifetime.Release(extension);
        }
    }

    /// <summary>
    /// Forces a rebuild (<c>IModelDoc2.EditRebuild3</c>) and reports whether it
    /// completed without errors. Unlike the other probes here, this is not a
    /// passive read — SolidWorks exposes no cheap rebuild-error-count getter
    /// (see <c>docs/adr/0002-write-verification-and-no-auto-rollback.md</c>), so
    /// "are there rebuild errors" is answered the only way the API supports:
    /// by actually rebuilding and checking the result. This is the probe behind
    /// <c>ADR 0002</c>'s <c>noNewRebuildErrors</c> check.
    /// </summary>
    /// <param name="doc">The document to read.</param>
    public static bool RebuildSucceeded(ModelDoc2 doc) => doc.EditRebuild3();
}
