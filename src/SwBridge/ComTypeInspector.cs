using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using SolidWorks.Interop.sldworks;

namespace SwBridge;

/// <summary>Kind of member discovered on a late-bound COM object.</summary>
public enum ComMemberKind
{
    /// <summary>A property getter (<c>INVOKE_PROPERTYGET</c>).</summary>
    PropertyGet,

    /// <summary>A property setter (<c>INVOKE_PROPERTYPUT</c> or <c>INVOKE_PROPERTYPUTREF</c>).</summary>
    PropertySet,

    /// <summary>An ordinary method (<c>INVOKE_FUNC</c>).</summary>
    Method,
}

/// <summary>
/// One member discovered on a late-bound COM object — the raw material for
/// building a <see cref="PropertySpec"/> without having to guess a member name.
/// </summary>
/// <param name="Name">Member name, as declared by the object's type library or interop interface.</param>
/// <param name="Kind">Whether this is a property getter, a property setter, or an ordinary method.</param>
/// <param name="ParamCount">Number of parameters the member takes (0 for a bare property get).</param>
/// <param name="ReturnType">
/// Best-effort human-readable name of the return value's type, or null when it
/// could not be determined. Its shape depends on which discovery path produced
/// the entry: <see cref="ComTypeInspector.DescribeMembers(object?)"/> (the
/// <c>ITypeInfo</c> path) reports a VARTYPE name (e.g. <c>VT_R8</c>,
/// <c>VT_BSTR</c>, <c>VT_DISPATCH</c>); <see cref="ComTypeInspector.DescribeMembersViaInterop(object?, Func{Type, bool}?)"/>
/// (the interop-assembly path) reports a CLR type name (e.g. <c>Double</c>,
/// <c>String</c>, <c>Void</c>) instead, since it comes from ordinary .NET
/// reflection rather than a VARTYPE. For <see cref="ComMemberKind.PropertySet"/>
/// this describes the setter's own return (normally void), not the value it accepts.
/// </param>
public sealed record ComMemberInfo(string Name, ComMemberKind Kind, int ParamCount, string? ReturnType);

/// <summary>
/// Discovers the members a late-bound COM object actually exposes, instead of
/// guessing member names. This is what lets a consumer find the real
/// property/method surface of an opaque object at runtime, and is the
/// foundation for enriching a schema (see <see cref="PropertySpec"/>) with
/// entries that are known to exist rather than assumed.
/// </summary>
/// <remarks>
/// <para>
/// Two independent discovery mechanisms are offered, because no single one
/// works for every SolidWorks COM object:
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// <see cref="DescribeMembers(object?)"/> reads the object's own type
/// information (<c>ITypeInfo</c>, reached via <c>IDispatch::GetTypeInfo</c> or,
/// when that reports none, the object's <c>IProvideClassInfo</c>). This works
/// for general COM automation objects and for creatable SolidWorks document
/// coclasses (e.g. <c>ModelDoc2</c>/<c>PartDoc</c>), but many internal SolidWorks
/// objects — notably <c>IFeature</c> and the feature-definition objects from
/// <c>IFeature.GetDefinition()</c> — implement neither and report no type
/// information at all; not every COM object supports runtime introspection,
/// since some expose IDispatch purely for invoking members by name (via
/// <c>GetIDsOfNames</c>/<c>Invoke</c>, the mechanism <see cref="ComPropertyReader"/>
/// uses). For such objects this returns an empty list — not itself proof the
/// object has no members, only that it does not support this mechanism.
/// </description>
/// </item>
/// <item>
/// <description>
/// <see cref="DescribeMembersViaInterop(object?, Func{Type, bool}?)"/> is the
/// fallback for exactly that case: it probes the object with a real COM
/// QueryInterface against every <c>[ComImport]</c> interface declared in the
/// referenced <c>SolidWorks.Interop.sldworks</c> assembly (e.g.
/// <c>IExtrudeFeatureData2</c>), and reflects whichever interfaces the object
/// actually implements. Verified live: this recovers <c>GetDepth</c> and the
/// rest of an Extrusion feature definition's real members when the
/// <c>ITypeInfo</c> path finds nothing.
/// </description>
/// </item>
/// </list>
/// </remarks>
public static class ComTypeInspector
{
    // Members every IDispatch-derived COM interface carries. Some type libraries
    // (dual interfaces) enumerate these alongside the object's own members; they
    // are never useful to a caller probing a feature definition, so they are
    // always excluded regardless of how they were declared.
    private static readonly HashSet<string> PlumbingMembers = new(StringComparer.OrdinalIgnoreCase)
    {
        "QueryInterface", "AddRef", "Release",
        "GetTypeInfoCount", "GetTypeInfo", "GetIDsOfNames", "Invoke",
    };

    private const short VtArrayFlag = unchecked((short)0x2000);
    private const short VtByRefFlag = unchecked((short)0x4000);
    private const short VtTypeMask = 0x0FFF;

    /// <summary>
    /// Minimal projection of <c>IDispatch</c>, declared by hand because the BCL's
    /// <c>System.Runtime.InteropServices.ComTypes</c> namespace has no public
    /// <c>IDispatch</c> type. COM interop matches declared methods to vtable slots
    /// by order, so this deliberately stops after <c>GetTypeInfo</c> (the real
    /// interface continues with <c>GetIDsOfNames</c> and <c>Invoke</c>, neither of
    /// which this type needs) rather than declaring the whole interface.
    /// </summary>
    [ComImport]
    [Guid("00020400-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDispatchMinimal
    {
        void GetTypeInfoCount(out int typeInfoCount);

        void GetTypeInfo(int index, int lcid, out ITypeInfo typeInfo);
    }

    /// <summary>
    /// Enumerates the members a late-bound COM object exposes through its
    /// IDispatch type information: for each declared function, its name, whether
    /// it is a property getter/setter or an ordinary method, its parameter count,
    /// and a best-effort return type name.
    /// </summary>
    /// <remarks>
    /// A property with both a getter and a setter produces two entries — one
    /// <see cref="ComMemberKind.PropertyGet"/> and one <see cref="ComMemberKind.PropertySet"/> —
    /// sharing the same <see cref="ComMemberInfo.Name"/>; this is the chosen
    /// representation of get/set pairs. Overloaded setters (plain <c>PROPERTYPUT</c>
    /// and <c>PROPERTYPUTREF</c> declared for the same name) collapse into a single
    /// <see cref="ComMemberKind.PropertySet"/> entry. IUnknown/IDispatch plumbing
    /// members are never returned.
    /// </remarks>
    /// <param name="comObject">
    /// The late-bound COM object to inspect (e.g. a value returned by
    /// <c>IFeature.GetDefinition()</c>). Null, a plain .NET object with no COM
    /// identity, or a COM object that exposes no type information all yield an
    /// empty list — this method never throws for those cases.
    /// </param>
    public static IReadOnlyList<ComMemberInfo> DescribeMembers(object? comObject)
    {
        if (comObject == null)
        {
            return Array.Empty<ComMemberInfo>();
        }

        ITypeInfo? typeInfo = null;
        try
        {
            typeInfo = GetTypeInfo(comObject);
            return typeInfo == null ? Array.Empty<ComMemberInfo>() : DescribeMembers(typeInfo);
        }
        catch (Exception)
        {
            // Best-effort: any interop failure while probing type info is reported as "nothing discovered".
            return Array.Empty<ComMemberInfo>();
        }
        finally
        {
            ComLifetime.Release(typeInfo);
        }
    }

    [ComImport]
    [Guid("B196B283-BAB4-101A-B69C-00AA00341D07")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IProvideClassInfoMinimal
    {
        void GetClassInfo(out ITypeInfo typeInfo);
    }

    // Preference order: IProvideClassInfo before raw IDispatch::GetTypeInfo.
    // Verified live against SolidWorks 2024: creatable document coclasses (e.g.
    // ModelDoc2/PartDoc) answer IDispatch::GetTypeInfoCount() with 0 (no embedded
    // type info) despite being dual interfaces, but do implement the standard
    // IProvideClassInfo, which points at their coclass's default interface —
    // falling back to that recovers real member information (175 members
    // observed on a PartDoc). Some internal, non-creatable SolidWorks objects —
    // notably IFeature and the feature-definition objects returned by
    // IFeature.GetDefinition() — implement neither and are simply not
    // introspectable at runtime; DescribeMembers correctly returns an empty
    // list for those rather than throwing, since IDispatch::Invoke by name
    // (as used elsewhere by <see cref="ComPropertyReader"/>) still works on them
    // even though enumeration does not.
    private static ITypeInfo? GetTypeInfo(object comObject)
    {
        if (comObject is IProvideClassInfoMinimal pci)
        {
            var viaClassInfo = GetTypeInfoViaClassInfo(pci);
            if (viaClassInfo != null)
            {
                return viaClassInfo;
            }
        }

        if (comObject is IDispatchMinimal dispatch)
        {
            dispatch.GetTypeInfoCount(out var typeInfoCount);
            if (typeInfoCount > 0)
            {
                dispatch.GetTypeInfo(0, lcid: 0, out var typeInfo);
                return typeInfo;
            }
        }

        return null;
    }

    private static ITypeInfo? GetTypeInfoViaClassInfo(IProvideClassInfoMinimal pci)
    {
        ITypeInfo? classTypeInfo = null;
        var attrPtr = IntPtr.Zero;
        try
        {
            pci.GetClassInfo(out classTypeInfo!);
            if (classTypeInfo == null)
            {
                return null;
            }

            classTypeInfo.GetTypeAttr(out attrPtr);
            var attr = Marshal.PtrToStructure<TYPEATTR>(attrPtr);

            for (var i = 0; i < attr.cImplTypes; i++)
            {
                classTypeInfo.GetImplTypeFlags(i, out var flags);
                if ((flags & IMPLTYPEFLAGS.IMPLTYPEFLAG_FDEFAULT) == 0 ||
                    (flags & IMPLTYPEFLAGS.IMPLTYPEFLAG_FSOURCE) != 0)
                {
                    continue; // want the default *sink* interface, not the default event source
                }

                classTypeInfo.GetRefTypeOfImplType(i, out var href);
                classTypeInfo.GetRefTypeInfo(href, out var interfaceTypeInfo);
                return interfaceTypeInfo;
            }

            return null;
        }
        finally
        {
            if (attrPtr != IntPtr.Zero)
            {
                classTypeInfo!.ReleaseTypeAttr(attrPtr);
            }

            ComLifetime.Release(classTypeInfo);
        }
    }

    private static IReadOnlyList<ComMemberInfo> DescribeMembers(ITypeInfo typeInfo)
    {
        var results = new List<ComMemberInfo>();
        var seen = new HashSet<(string Name, ComMemberKind Kind)>();
        var names = new string[1];

        var typeAttrPtr = IntPtr.Zero;
        try
        {
            typeInfo.GetTypeAttr(out typeAttrPtr);
            var typeAttr = Marshal.PtrToStructure<TYPEATTR>(typeAttrPtr);

            for (var i = 0; i < typeAttr.cFuncs; i++)
            {
                var funcDescPtr = IntPtr.Zero;
                try
                {
                    typeInfo.GetFuncDesc(i, out funcDescPtr);
                    var funcDesc = Marshal.PtrToStructure<FUNCDESC>(funcDescPtr);

                    typeInfo.GetNames(funcDesc.memid, names, names.Length, out var nameCount);
                    if (nameCount == 0 || string.IsNullOrEmpty(names[0]))
                    {
                        continue;
                    }

                    var name = names[0];
                    if (PlumbingMembers.Contains(name))
                    {
                        continue;
                    }

                    var kind = funcDesc.invkind switch
                    {
                        INVOKEKIND.INVOKE_PROPERTYGET => ComMemberKind.PropertyGet,
                        INVOKEKIND.INVOKE_PROPERTYPUT or INVOKEKIND.INVOKE_PROPERTYPUTREF => ComMemberKind.PropertySet,
                        _ => ComMemberKind.Method,
                    };

                    if (!seen.Add((name, kind)))
                    {
                        continue;
                    }

                    results.Add(new ComMemberInfo(name, kind, funcDesc.cParams, DescribeVarType(funcDesc.elemdescFunc.tdesc.vt)));
                }
                finally
                {
                    if (funcDescPtr != IntPtr.Zero)
                    {
                        typeInfo.ReleaseFuncDesc(funcDescPtr);
                    }
                }
            }
        }
        finally
        {
            if (typeAttrPtr != IntPtr.Zero)
            {
                typeInfo.ReleaseTypeAttr(typeAttrPtr);
            }
        }

        return results;
    }

    private static string? DescribeVarType(short vt)
    {
        var baseType = (VarEnum)(vt & VtTypeMask);
        var name = Enum.IsDefined(typeof(VarEnum), baseType) ? baseType.ToString() : $"VT_{vt & VtTypeMask}";

        if ((vt & VtArrayFlag) != 0)
        {
            name += "[]";
        }

        if ((vt & VtByRefFlag) != 0)
        {
            name += "&";
        }

        return name;
    }

    /// <summary>
    /// A ready-made, filtered-by-default entry point for the common case: narrows
    /// <see cref="FindImplementedInteropInterfaces"/>/<see cref="DescribeMembersViaInterop"/>
    /// to interfaces whose name contains <c>"FeatureData"</c> — the convention
    /// SolidWorks' interop assembly uses for feature-definition interfaces
    /// (<c>IExtrudeFeatureData2</c>, <c>IChamferFeatureData2</c>, …). This cuts an
    /// unfiltered probe of a few thousand interfaces down to about a hundred, and
    /// is exactly what <see cref="ModelInspector.DescribeFeatureDefinition"/> uses
    /// internally. Pass this explicitly for any target that is plausibly a
    /// feature definition instead of duplicating the substring match or reaching
    /// for an unfiltered call — see the cost warning on <see cref="FindImplementedInteropInterfaces"/>.
    /// </summary>
    public static bool FeatureDataFilter(Type type) =>
        type.Name.Contains("FeatureData", StringComparison.OrdinalIgnoreCase);

    // Lazily enumerated once per process: every public [ComImport] interface
    // declared in the SolidWorks interop assembly. There are a few thousand of
    // these; building the list is the expensive part, not testing any one of
    // them, so it is cached rather than re-walked on every call.
    private static readonly Lazy<IReadOnlyList<Type>> InteropComImportInterfaces = new(() =>
        typeof(ISldWorks).Assembly.GetTypes()
            .Where(t => t.IsInterface && t.IsDefined(typeof(ComImportAttribute), inherit: false))
            .ToArray());

    /// <summary>
    /// Finds interfaces from the SolidWorks interop assembly
    /// (<c>SolidWorks.Interop.sldworks</c>) that <paramref name="comObject"/>
    /// actually implements, by attempting a real COM QueryInterface for each
    /// candidate interface's IID (via <see cref="Type.IsInstanceOfType"/>). This
    /// is how the interop-assembly fallback (see <see cref="DescribeMembersViaInterop"/>)
    /// identifies which concrete definition interface (e.g. <c>IExtrudeFeatureData2</c>)
    /// an otherwise-opaque <c>object</c> reference really implements.
    /// </summary>
    /// <param name="comObject">The COM object to probe. Null yields an empty list.</param>
    /// <param name="filter">
    /// Optional predicate over candidate interface <see cref="Type"/>s, applied
    /// before probing. The interop assembly declares several thousand interfaces;
    /// an unfiltered call (<c>filter: null</c>) probes all of them with a
    /// QueryInterface each. Each of those is a cross-process round trip to the
    /// SolidWorks executable, and — critically — this method does no dispatching
    /// of its own, so if a caller runs it inside a <see cref="SwDispatcher.Run{T}(Func{T})"/>
    /// call (as any COM-touching code must, per <c>ADR 0003</c>), an unfiltered
    /// probe monopolizes the dispatcher's single STA thread for its entire
    /// duration: every other queued operation against that SolidWorks session —
    /// reads and writes alike — waits behind it. Use <see cref="FeatureDataFilter"/>
    /// when the target is plausibly a feature definition (the common case), or
    /// supply a narrower predicate when more is known about the target; reach
    /// for an unfiltered call only for an occasional, deliberate, full discovery
    /// scan, not as a default.
    /// </param>
    public static IReadOnlyList<Type> FindImplementedInteropInterfaces(object? comObject, Func<Type, bool>? filter = null)
    {
        if (comObject == null)
        {
            return Array.Empty<Type>();
        }

        var matches = new List<Type>();
        foreach (var candidate in InteropComImportInterfaces.Value)
        {
            if (filter != null && !filter(candidate))
            {
                continue;
            }

            try
            {
                if (candidate.IsInstanceOfType(comObject))
                {
                    matches.Add(candidate);
                }
            }
            catch (Exception)
            {
                // A failed probe (e.g. InvalidCastException, COMException) just means "not this interface".
            }
        }

        return matches;
    }

    /// <summary>
    /// Discovers members via the interop-assembly cast-probing fallback: finds
    /// which SolidWorks interop interfaces <paramref name="comObject"/> implements
    /// (<see cref="FindImplementedInteropInterfaces"/>) and reflects their public
    /// members into the same <see cref="ComMemberInfo"/> shape used by
    /// <see cref="DescribeMembers(object?)"/>. Use this when that method returns
    /// an empty list — the object may still expose members, just not through
    /// <c>ITypeInfo</c>.
    /// </summary>
    /// <param name="comObject">The COM object to inspect. Null yields an empty list.</param>
    /// <param name="filter">See <see cref="FindImplementedInteropInterfaces"/>.</param>
    public static IReadOnlyList<ComMemberInfo> DescribeMembersViaInterop(object? comObject, Func<Type, bool>? filter = null)
    {
        var interfaces = FindImplementedInteropInterfaces(comObject, filter);
        if (interfaces.Count == 0)
        {
            return Array.Empty<ComMemberInfo>();
        }

        var results = new List<ComMemberInfo>();
        var seen = new HashSet<(string Name, ComMemberKind Kind, int ParamCount)>();
        foreach (var interfaceType in interfaces)
        {
            foreach (var member in DescribeInterfaceMembers(interfaceType))
            {
                if (seen.Add((member.Name, member.Kind, member.ParamCount)))
                {
                    results.Add(member);
                }
            }
        }

        return results;
    }

    /// <summary>
    /// The union of <see cref="DescribeMembers(object?)"/> and
    /// <see cref="DescribeMembersViaInterop(object?, Func{Type, bool}?)"/>,
    /// deduplicated by <c>(Name, Kind, ParamCount)</c>, instead of the
    /// either/or fallback <see cref="ModelInspector.DescribeFeatureDefinition"/>
    /// uses. The two mechanisms are not always redundant with each other on the
    /// same object: a document root (e.g. a live <c>ModelDoc2</c>/<c>PartDoc</c>)
    /// answers <see cref="DescribeMembers(object?)"/> with a real, non-empty list
    /// via <c>IProvideClassInfo</c> (its own coclass's default interface) —
    /// but that default interface does not include everything the object's
    /// <em>other</em> implemented interfaces declare. Concretely, a
    /// <c>PartDoc</c>'s coclass default interface does not surface
    /// <c>IModelDoc2</c> members like <c>EditRebuild3</c>, <c>SaveAs3</c>,
    /// <c>EditUndo2</c> or <c>ClearSelection2</c> — which the interop probe
    /// finds immediately, because <c>IModelDoc2</c> is just another
    /// <c>[ComImport]</c> interface the same live object answers <c>true</c>
    /// to a QueryInterface for. Neither existing method alone sees the full
    /// picture on an object like this; this one does, at the cost of always
    /// running both.
    /// </summary>
    /// <param name="comObject">The COM object to inspect. Null yields an empty list.</param>
    /// <param name="interopFilter">
    /// Forwarded to <see cref="DescribeMembersViaInterop(object?, Func{Type, bool}?)"/>/
    /// <see cref="FindImplementedInteropInterfaces"/>. Defaults to null —
    /// <em>unfiltered</em>, deliberately: this method does not impose an
    /// opinion about the tradeoff between completeness and probe cost (an
    /// unfiltered call QueryInterfaces the whole interop assembly — a few
    /// thousand cross-process calls — and blocks the calling dispatcher for the
    /// duration if run inside one). That policy call belongs to the caller, who
    /// knows what it is looking for and how expensive it is prepared to be; pass
    /// <see cref="FeatureDataFilter"/> or a narrower predicate of your own when
    /// you do not need a full scan.
    /// </param>
    public static IReadOnlyList<ComMemberInfo> DescribeAllMembers(object? comObject, Func<Type, bool>? interopFilter = null)
    {
        var viaTypeInfo = DescribeMembers(comObject);
        var viaInterop = DescribeMembersViaInterop(comObject, interopFilter);

        if (viaInterop.Count == 0)
        {
            return viaTypeInfo;
        }

        if (viaTypeInfo.Count == 0)
        {
            return viaInterop;
        }

        var seen = new HashSet<(string Name, ComMemberKind Kind, int ParamCount)>();
        var results = new List<ComMemberInfo>();
        foreach (var member in viaTypeInfo)
        {
            if (seen.Add((member.Name, member.Kind, member.ParamCount)))
            {
                results.Add(member);
            }
        }

        foreach (var member in viaInterop)
        {
            if (seen.Add((member.Name, member.Kind, member.ParamCount)))
            {
                results.Add(member);
            }
        }

        return results;
    }

    /// <summary>
    /// Reflects the public members of a single interop interface type into
    /// <see cref="ComMemberInfo"/> entries: property getters/setters from
    /// <see cref="Type.GetProperties()"/>, and ordinary methods from
    /// <see cref="Type.GetMethods()"/> (skipping the compiler-generated
    /// <c>get_</c>/<c>set_</c> accessor methods already covered via properties).
    /// Pure reflection over the <see cref="Type"/>'s metadata — no COM object and
    /// no SolidWorks instance are needed, which is why this is exposed
    /// internally: it can be unit-tested directly against an interop type like
    /// <c>typeof(IExtrudeFeatureData2)</c>.
    /// </summary>
    internal static IReadOnlyList<ComMemberInfo> DescribeInterfaceMembers(Type interfaceType)
    {
        var results = new List<ComMemberInfo>();

        foreach (var property in interfaceType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var getMethod = property.GetGetMethod();
            if (getMethod != null)
            {
                results.Add(new ComMemberInfo(property.Name, ComMemberKind.PropertyGet, getMethod.GetParameters().Length, getMethod.ReturnType.Name));
            }

            var setMethod = property.GetSetMethod();
            if (setMethod != null)
            {
                results.Add(new ComMemberInfo(property.Name, ComMemberKind.PropertySet, setMethod.GetParameters().Length, setMethod.ReturnType.Name));
            }
        }

        foreach (var method in interfaceType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (method.IsSpecialName)
            {
                continue; // get_/set_/add_/remove_ accessors — already covered above (properties) or not relevant (events)
            }

            results.Add(new ComMemberInfo(method.Name, ComMemberKind.Method, method.GetParameters().Length, method.ReturnType.Name));
        }

        return results;
    }
}
