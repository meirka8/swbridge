using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SwBridge;

/// <summary>
/// One currently-selected entity — never the live COM object.
/// </summary>
/// <param name="Type">
/// The <c>swSelectType_e</c> name (e.g. <c>swSelEDGES</c>, <c>swSelFACES</c>,
/// <c>swSelDATUMPLANES</c>), or <c>Unknown(n)</c> if the reported code is not a
/// defined enum member.
/// </param>
/// <param name="Descriptor">
/// Best-effort, human-meaningful identity for the entity — enough for someone
/// reading a transcript to tell *which* edge/face/vertex/named object this is,
/// without ever holding the live COM reference. Null when nothing useful could
/// be read; this is never a reason to fail the whole selection read.
/// </param>
public sealed record SelectionInfo(string Type, string? Descriptor);

/// <summary>
/// Reads the identity of what is currently selected in a document — not just
/// how many things (<see cref="DocumentStateProbes.GetSelectionCount"/>), but
/// what they plausibly are. This exists specifically to close the "silent
/// wrong-edge" failure mode: a selection-driven write (a fillet, a chamfer, a
/// draft) can succeed against the wrong entity with no COM error and no
/// indication in a transcript of what was actually selected — the only way to
/// catch it today is an independent geometry check (e.g. a hand-computed mass)
/// after the fact.
/// </summary>
/// <remarks>
/// Takes an already-resolved <see cref="ModelDoc2"/> and does no dispatching
/// itself — callers run this inside a <see cref="SwDispatcher.Run{T}(Func{T})"/>
/// call, like every other COM touch in this library. Every intermediate COM
/// object reached along the way (the selection manager, each selected object,
/// the curve/surface/vertex objects read off it) is released before returning;
/// nothing here ever hands back a live reference. Decoding is genuinely
/// best-effort: an entity kind this method does not know how to describe
/// yields <see cref="SelectionInfo.Descriptor"/> null with
/// <see cref="SelectionInfo.Type"/> still populated — a description failure is
/// never a reason to fail the whole read, since the read itself is a safety
/// mechanism and must not become one more thing to work around.
/// </remarks>
public static class SelectionInspector
{
    /// <summary>
    /// Reads every currently selected entity in <paramref name="doc"/>, in
    /// selection order.
    /// </summary>
    public static IReadOnlyList<SelectionInfo> GetSelection(ModelDoc2 doc)
    {
        var results = new List<SelectionInfo>();
        var selectionManager = doc.SelectionManager;
        try
        {
            if (selectionManager is not SelectionMgr manager)
            {
                return results;
            }

            var count = manager.GetSelectedObjectCount2(-1);

            // SelectionMgr's Index-and-Mark overloads (…3/…6, as opposed to the
            // older AtIndex-only ones) are 1-based — verified live against a
            // known single selection (index 0 returned nothing usable, index 1
            // returned the selected edge).
            for (var i = 1; i <= count; i++)
            {
                var typeCode = manager.GetSelectedObjectType3(i, -1);
                var typeName = DescribeSelectType(typeCode);

                object? selected = null;
                string? descriptor = null;
                try
                {
                    selected = manager.GetSelectedObject6(i, -1);
                    descriptor = BuildDescriptor(selected);
                }
                catch (Exception)
                {
                    // Best-effort: an undecodable selected object is Type-only, never a failure.
                }
                finally
                {
                    ComLifetime.Release(selected);
                }

                results.Add(new SelectionInfo(typeName, descriptor));
            }
        }
        finally
        {
            ComLifetime.Release(selectionManager);
        }

        return results;
    }

    /// <summary>Maps a <c>swSelectType_e</c> code to its enum name, best-effort.</summary>
    internal static string DescribeSelectType(int code) =>
        Enum.IsDefined(typeof(swSelectType_e), code) ? ((swSelectType_e)code).ToString() : $"Unknown({code})";

    private static string? BuildDescriptor(object? selected)
    {
        try
        {
            switch (selected)
            {
                case Edge edge:
                    return DescribeEdge(edge);
                case Face2 face:
                    return DescribeFace(face);
                case Vertex vertex:
                    return DescribeVertex(vertex);
                case Feature feature:
                    return FormatNamedDescriptor(feature.Name);
                case null:
                    return null;
                default:
                    // Sketches, components, and anything else this method does not
                    // specifically decode: fall back to a bare Name if it has one
                    // (many named SolidWorks objects expose it, e.g. a plane's
                    // owning Feature already handled above, but this catches
                    // whatever else responds to it) — otherwise Type-only.
                    return ComPropertyReader.TryGetProperty(selected, "Name", out var nameValue) &&
                           nameValue is string name && !string.IsNullOrEmpty(name)
                        ? FormatNamedDescriptor(name)
                        : null;
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? DescribeEdge(Edge edge)
    {
        object? curveObject = null;
        Vertex? startVertex = null;
        Vertex? endVertex = null;
        try
        {
            curveObject = edge.GetCurve();
            if (curveObject is not Curve curve)
            {
                return null;
            }

            var kind = DescribeCurveKind(curve);
            double? length = null;
            Point3? midpoint = null;

            // Chord length/midpoint from the edge's vertices. Exact for a
            // straight edge (the common, load-bearing case for fillets and
            // chamfers); a chord approximation — not the true arc length or a
            // curve-parameter midpoint — for a curved one, which is still
            // enough to plausibly identify which edge this is. Deliberately
            // not using IEdge.GetCurveParams2()/ICurve.GetLength2/Evaluate2:
            // verified live that GetCurveParams2()'s returned array is not the
            // simple (startParam, endParam) pair its name suggests (its first
            // six values matched the start/end vertex coordinates instead),
            // and feeding it to GetLength2/Evaluate2 produced nonsense
            // (a negative length, a midpoint off the edge entirely) — so this
            // uses only the two calls whose contract was confirmed live. A
            // closed edge with no distinct start/end vertex (e.g. a full
            // circle) yields kind-only, an accepted degradation for a
            // best-effort descriptor.
            if (edge.GetStartVertex() is Vertex sv && edge.GetEndVertex() is Vertex ev)
            {
                startVertex = sv;
                endVertex = ev;
                if (sv.GetPoint() is double[] { Length: >= 3 } p1 && ev.GetPoint() is double[] { Length: >= 3 } p2)
                {
                    midpoint = new Point3((p1[0] + p2[0]) / 2, (p1[1] + p2[1]) / 2, (p1[2] + p2[2]) / 2);
                    length = Distance(p1, p2);
                }
            }

            return FormatEdgeDescriptor(kind, length, midpoint);
        }
        finally
        {
            ComLifetime.Release(curveObject);
            ComLifetime.Release(startVertex);
            ComLifetime.Release(endVertex);
        }
    }

    private static string? DescribeFace(Face2 face)
    {
        object? surfaceObject = null;
        try
        {
            surfaceObject = face.GetSurface();
            var kind = surfaceObject is Surface surface ? DescribeSurfaceKind(surface) : "Face";

            double? area = null;
            try
            {
                area = face.GetArea();
            }
            catch (Exception)
            {
                area = null;
            }

            Point3? center = null;
            try
            {
                if (face.GetBox() is double[] { Length: >= 6 } box)
                {
                    center = new Point3((box[0] + box[3]) / 2, (box[1] + box[4]) / 2, (box[2] + box[5]) / 2);
                }
            }
            catch (Exception)
            {
                center = null;
            }

            return FormatFaceDescriptor(kind, area, center);
        }
        finally
        {
            ComLifetime.Release(surfaceObject);
        }
    }

    private static string? DescribeVertex(Vertex vertex) =>
        vertex.GetPoint() is double[] { Length: >= 3 } p ? FormatVertexDescriptor(new Point3(p[0], p[1], p[2])) : null;

    private static string DescribeCurveKind(Curve curve)
    {
        try
        {
            if (curve.IsLine())
            {
                return "Line";
            }
        }
        catch (Exception)
        {
            // fall through to the next candidate kind
        }

        try
        {
            if (curve.IsCircle())
            {
                return "Circle";
            }
        }
        catch (Exception)
        {
        }

        try
        {
            if (curve.IsEllipse())
            {
                return "Ellipse";
            }
        }
        catch (Exception)
        {
        }

        try
        {
            if (curve.IsBcurve())
            {
                return "BCurve";
            }
        }
        catch (Exception)
        {
        }

        return "Curve";
    }

    private static string DescribeSurfaceKind(Surface surface)
    {
        try
        {
            if (surface.IsPlane())
            {
                return "Planar";
            }
        }
        catch (Exception)
        {
        }

        try
        {
            if (surface.IsCylinder())
            {
                return "Cylindrical";
            }
        }
        catch (Exception)
        {
        }

        try
        {
            if (surface.IsCone())
            {
                return "Conical";
            }
        }
        catch (Exception)
        {
        }

        try
        {
            if (surface.IsSphere())
            {
                return "Spherical";
            }
        }
        catch (Exception)
        {
        }

        try
        {
            if (surface.IsTorus())
            {
                return "Toroidal";
            }
        }
        catch (Exception)
        {
        }

        return "Surface";
    }

    private static double Distance(double[] a, double[] b)
    {
        var dx = a[0] - b[0];
        var dy = a[1] - b[1];
        var dz = a[2] - b[2];
        return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    // --- Pure formatting — no COM, unit-tested directly with fake values. ---
    // All coordinates/lengths/areas are SI (meters / square meters), matching
    // the rest of SwBridge; converting for display is a presentation choice
    // that belongs above this library.

    /// <summary>Formats an edge descriptor from already-extracted values. Pure; no COM.</summary>
    internal static string FormatEdgeDescriptor(string curveKind, double? length, Point3? midpoint)
    {
        var sb = new StringBuilder(curveKind).Append(" edge");
        if (length is { } l)
        {
            sb.Append(", length=").Append(l.ToString("G6")).Append(" m");
        }

        if (midpoint is { } m)
        {
            sb.Append(", midpoint=(").Append(m.X.ToString("G6")).Append(", ")
                .Append(m.Y.ToString("G6")).Append(", ").Append(m.Z.ToString("G6")).Append(") m");
        }

        return sb.ToString();
    }

    /// <summary>Formats a face descriptor from already-extracted values. Pure; no COM.</summary>
    internal static string FormatFaceDescriptor(string surfaceKind, double? area, Point3? approximateCenter)
    {
        var sb = new StringBuilder(surfaceKind).Append(" face");
        if (area is { } a)
        {
            sb.Append(", area=").Append(a.ToString("G6")).Append(" m^2");
        }

        if (approximateCenter is { } c)
        {
            sb.Append(", center~(").Append(c.X.ToString("G6")).Append(", ")
                .Append(c.Y.ToString("G6")).Append(", ").Append(c.Z.ToString("G6")).Append(") m");
        }

        return sb.ToString();
    }

    /// <summary>Formats a vertex descriptor from its point. Pure; no COM.</summary>
    internal static string FormatVertexDescriptor(Point3 point) =>
        $"Vertex at ({point.X:G6}, {point.Y:G6}, {point.Z:G6}) m";

    /// <summary>Formats a descriptor for anything identified only by name (a feature, a plane, ...). Pure; no COM.</summary>
    internal static string FormatNamedDescriptor(string name) => $"'{name}'";
}
