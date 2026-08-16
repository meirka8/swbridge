using System.Linq;
using SolidWorks.Interop.sldworks;
using Xunit;

namespace SwBridge.Tests;

// ITypeInfo enumeration works on any IDispatch-based automation object, so
// "Scripting.FileSystemObject" (a built-in Windows automation object, distinct
// from SolidWorks) is used as a stand-in COM target. These run without
// SolidWorks installed. If the ProgID is unavailable on the machine running the
// tests, the COM-backed cases skip gracefully rather than fail.
public class ComTypeInspectorTests
{
    private static object? CreateFileSystemObject()
    {
        var type = Type.GetTypeFromProgID("Scripting.FileSystemObject");
        return type == null ? null : Activator.CreateInstance(type);
    }

    [Fact]
    public void DescribeMembers_NullObject_ReturnsEmpty()
    {
        Assert.Empty(ComTypeInspector.DescribeMembers(null));
    }

    [Fact]
    public void DescribeMembers_PlainDotNetObject_ReturnsEmptyWithoutThrowing()
    {
        Assert.Empty(ComTypeInspector.DescribeMembers(new object()));
        Assert.Empty(ComTypeInspector.DescribeMembers("just a string"));
    }

    [Fact]
    public void DescribeMembers_FileSystemObject_FindsKnownMembersAndExcludesPlumbing()
    {
        var fso = CreateFileSystemObject();
        if (fso == null)
        {
            return; // ProgID unavailable on this machine — skip gracefully.
        }

        try
        {
            var members = ComTypeInspector.DescribeMembers(fso);
            Assert.NotEmpty(members);

            var fileExists = Assert.Single(members, m => string.Equals(m.Name, "FileExists", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(ComMemberKind.Method, fileExists.Kind);
            Assert.Equal(1, fileExists.ParamCount);

            var drives = Assert.Single(members, m => string.Equals(m.Name, "Drives", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(ComMemberKind.PropertyGet, drives.Kind);

            string[] plumbing = { "QueryInterface", "AddRef", "Release", "GetTypeInfoCount", "GetTypeInfo", "GetIDsOfNames", "Invoke" };
            Assert.DoesNotContain(members, m => plumbing.Contains(m.Name, StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            ComLifetime.Release(fso);
        }
    }

    [Fact]
    public void FindImplementedInteropInterfaces_NullObject_ReturnsEmpty()
    {
        Assert.Empty(ComTypeInspector.FindImplementedInteropInterfaces(null));
    }

    [Fact]
    public void FindImplementedInteropInterfaces_NonSolidWorksComObject_MatchesNothingWithoutThrowing()
    {
        // A real COM object (so the QueryInterface probes actually run), but not
        // a SolidWorks one — none of the interop assembly's interfaces should match.
        var fso = CreateFileSystemObject();
        if (fso == null)
        {
            return; // ProgID unavailable on this machine — skip gracefully.
        }

        try
        {
            Assert.Empty(ComTypeInspector.FindImplementedInteropInterfaces(fso));
            Assert.Empty(ComTypeInspector.DescribeMembersViaInterop(fso));
        }
        finally
        {
            ComLifetime.Release(fso);
        }
    }

    [Fact]
    public void FindImplementedInteropInterfaces_FilterExcludesNonMatchingCandidates()
    {
        var fso = CreateFileSystemObject();
        if (fso == null)
        {
            return; // ProgID unavailable on this machine — skip gracefully.
        }

        try
        {
            // A filter that accepts nothing means nothing gets probed, let alone matched.
            Assert.Empty(ComTypeInspector.FindImplementedInteropInterfaces(fso, _ => false));
        }
        finally
        {
            ComLifetime.Release(fso);
        }
    }

    [Fact]
    public void DescribeInterfaceMembers_ExtrudeFeatureData2_FindsGetDepthAsMethod()
    {
        // Pure reflection over the interop Type's metadata — no COM object, no
        // SolidWorks instance required.
        var members = ComTypeInspector.DescribeInterfaceMembers(typeof(IExtrudeFeatureData2));

        var getDepth = Assert.Single(members, m => string.Equals(m.Name, "GetDepth", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ComMemberKind.Method, getDepth.Kind);
        Assert.Equal(1, getDepth.ParamCount);

        // Property accessor methods must not also show up as bare Method entries.
        Assert.DoesNotContain(members, m => m.Name.StartsWith("get_", StringComparison.Ordinal) || m.Name.StartsWith("set_", StringComparison.Ordinal));
    }

    // M9: a reusable, filtered-by-default entry point so callers do not need to
    // duplicate the "FeatureData" substring convention or reach for an
    // unfiltered (dispatcher-blocking) scan by default.
    [Fact]
    public void FeatureDataFilter_MatchesInterfacesNamedFeatureData()
    {
        Assert.True(ComTypeInspector.FeatureDataFilter(typeof(IExtrudeFeatureData2)));
        Assert.True(ComTypeInspector.FeatureDataFilter(typeof(IChamferFeatureData2)));
    }

    [Fact]
    public void FeatureDataFilter_ExcludesNonFeatureDataInterfaces()
    {
        Assert.False(ComTypeInspector.FeatureDataFilter(typeof(IFeature)));
        Assert.False(ComTypeInspector.FeatureDataFilter(typeof(ISldWorks)));
    }

    // --- DescribeAllMembers (Gap 3: discovery blind on the document root) ---
    // A genuine "both paths return real, partially-overlapping members" case
    // needs a live SolidWorks object (see the sample's discovery demo, run
    // against Part2's ModelDoc2, for the real EditRebuild3/SaveAs3/EditUndo2/
    // ClearSelection2 verification). What is testable without SolidWorks here
    // is the union/dedup mechanics themselves, using FileSystemObject as a
    // stand-in for "the ITypeInfo path finds something, the interop-assembly
    // path finds nothing" (FSO is a real COM object but implements none of
    // SolidWorks' interop interfaces).

    [Fact]
    public void DescribeAllMembers_NullObject_ReturnsEmpty()
    {
        Assert.Empty(ComTypeInspector.DescribeAllMembers(null));
    }

    [Fact]
    public void DescribeAllMembers_NonSolidWorksComObject_EqualsTypeInfoPathAlone()
    {
        var fso = CreateFileSystemObject();
        if (fso == null)
        {
            return; // ProgID unavailable on this machine — skip gracefully.
        }

        try
        {
            var viaTypeInfo = ComTypeInspector.DescribeMembers(fso);
            var viaAll = ComTypeInspector.DescribeAllMembers(fso);

            Assert.NotEmpty(viaTypeInfo); // sanity: FSO does support ITypeInfo
            Assert.Equal(viaTypeInfo.Count, viaAll.Count);
            Assert.Equal(
                viaTypeInfo.Select(m => (m.Name, m.Kind, m.ParamCount)).OrderBy(t => t.Name).ToList(),
                viaAll.Select(m => (m.Name, m.Kind, m.ParamCount)).OrderBy(t => t.Name).ToList());
        }
        finally
        {
            ComLifetime.Release(fso);
        }
    }

    [Fact]
    public void DescribeAllMembers_PlainDotNetObject_ReturnsEmptyWithoutThrowing()
    {
        Assert.Empty(ComTypeInspector.DescribeAllMembers(new object()));
    }

    [Fact]
    public void DescribeAllMembers_FilterIsForwardedToInteropPath()
    {
        var fso = CreateFileSystemObject();
        if (fso == null)
        {
            return; // ProgID unavailable on this machine — skip gracefully.
        }

        try
        {
            // A filter that matches nothing means the interop path contributes
            // nothing, but the ITypeInfo path's members must still come through.
            var members = ComTypeInspector.DescribeAllMembers(fso, _ => false);
            Assert.NotEmpty(members);
        }
        finally
        {
            ComLifetime.Release(fso);
        }
    }
}
