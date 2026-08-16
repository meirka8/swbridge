using SolidWorks.Interop.swconst;
using Xunit;

namespace SwBridge.Tests;

// SelectionInspector.GetSelection needs a live ISelectionMgr and is covered by
// the sample's --selection-demo live check instead. The pieces tested here —
// enum-name mapping and descriptor formatting — are pure functions over plain
// values (no COM), so they run without SolidWorks.
public class SelectionInspectorTests
{
    [Theory]
    [InlineData((int)swSelectType_e.swSelEDGES, "swSelEDGES")]
    [InlineData((int)swSelectType_e.swSelFACES, "swSelFACES")]
    [InlineData((int)swSelectType_e.swSelDATUMPLANES, "swSelDATUMPLANES")]
    [InlineData((int)swSelectType_e.swSelVERTICES, "swSelVERTICES")]
    public void DescribeSelectType_KnownCode_ReturnsEnumName(int code, string expected)
    {
        Assert.Equal(expected, SelectionInspector.DescribeSelectType(code));
    }

    [Fact]
    public void DescribeSelectType_UnknownCode_ReturnsUnknownWithValue()
    {
        Assert.Equal("Unknown(999999)", SelectionInspector.DescribeSelectType(999999));
    }

    [Fact]
    public void FormatEdgeDescriptor_WithLengthAndMidpoint_IncludesBoth()
    {
        var descriptor = SelectionInspector.FormatEdgeDescriptor("Line", 0.006, new Point3(0.04, 0.02, 0.003));

        Assert.StartsWith("Line edge", descriptor);
        Assert.Contains("length=0.006 m", descriptor);
        Assert.Contains("midpoint=(0.04, 0.02, 0.003) m", descriptor);
    }

    [Fact]
    public void FormatEdgeDescriptor_NoLengthOrMidpoint_StillNamesTheKind()
    {
        var descriptor = SelectionInspector.FormatEdgeDescriptor("Curve", null, null);

        Assert.Equal("Curve edge", descriptor);
    }

    [Fact]
    public void FormatEdgeDescriptor_LengthOnly_OmitsMidpoint()
    {
        var descriptor = SelectionInspector.FormatEdgeDescriptor("Circle", 0.1, null);

        Assert.Contains("length=0.1 m", descriptor);
        Assert.DoesNotContain("midpoint", descriptor);
    }

    [Fact]
    public void FormatFaceDescriptor_WithAreaAndCenter_IncludesBoth()
    {
        var descriptor = SelectionInspector.FormatFaceDescriptor("Planar", 0.0016, new Point3(0, 0, 0.05));

        Assert.StartsWith("Planar face", descriptor);
        Assert.Contains("area=0.0016 m^2", descriptor);
        Assert.Contains("center~(0, 0, 0.05) m", descriptor);
    }

    [Fact]
    public void FormatFaceDescriptor_NoAreaOrCenter_StillNamesTheKind()
    {
        var descriptor = SelectionInspector.FormatFaceDescriptor("Cylindrical", null, null);

        Assert.Equal("Cylindrical face", descriptor);
    }

    [Fact]
    public void FormatVertexDescriptor_FormatsPoint()
    {
        var descriptor = SelectionInspector.FormatVertexDescriptor(new Point3(0.01, -0.02, 0.03));

        Assert.Equal("Vertex at (0.01, -0.02, 0.03) m", descriptor);
    }

    [Fact]
    public void FormatNamedDescriptor_QuotesTheName()
    {
        Assert.Equal("'Boss-Extrude1'", SelectionInspector.FormatNamedDescriptor("Boss-Extrude1"));
    }
}
