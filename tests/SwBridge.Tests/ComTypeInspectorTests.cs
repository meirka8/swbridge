using System.Linq;
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
}
