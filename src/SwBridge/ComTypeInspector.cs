using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

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
/// One member discovered on a late-bound COM object via its IDispatch type
/// information — the raw material for building a <see cref="PropertySpec"/>
/// without having to guess a member name.
/// </summary>
/// <param name="Name">Member name, as declared by the object's type library.</param>
/// <param name="Kind">Whether this is a property getter, a property setter, or an ordinary method.</param>
/// <param name="ParamCount">Number of parameters the member takes (0 for a bare property get).</param>
/// <param name="ReturnType">
/// Best-effort human-readable VARTYPE name of the return value (e.g. <c>VT_R8</c>,
/// <c>VT_BSTR</c>, <c>VT_DISPATCH</c>), or null when it could not be determined.
/// For <see cref="ComMemberKind.PropertySet"/> this describes the setter's own
/// return (normally <c>VT_VOID</c>), not the value it accepts.
/// </param>
public sealed record ComMemberInfo(string Name, ComMemberKind Kind, int ParamCount, string? ReturnType);

/// <summary>
/// Discovers the members a late-bound COM object actually exposes by reading
/// its type information (<c>ITypeInfo</c>, reached via <c>IDispatch::GetTypeInfo</c>
/// or, when that reports none, the object's <c>IProvideClassInfo</c>), instead of
/// guessing member names. This is what lets a consumer find the real
/// property/method surface of an opaque object at runtime, and is the
/// foundation for enriching a schema (see <see cref="PropertySpec"/>) with
/// entries that are known to exist rather than assumed.
/// </summary>
/// <remarks>
/// Not every COM object supports runtime introspection — some expose IDispatch
/// purely for invoking members by name (via <c>GetIDsOfNames</c>/<c>Invoke</c>,
/// the mechanism <see cref="ComPropertyReader"/> uses) without publishing a type
/// description at all. For such objects <see cref="DescribeMembers(object?)"/>
/// returns an empty list; that is not itself proof the object has no members,
/// only that it does not support this particular discovery mechanism.
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
}
