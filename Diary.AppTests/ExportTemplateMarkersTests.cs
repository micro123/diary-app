using Diary.ScriptHost;

namespace Diary.AppTests;

[TestClass]
public sealed class ExportTemplateMarkersTests
{
    [TestMethod]
    public void Parse_RecognizesScalarRowAndColumnMarkers()
    {
        var markers = ExportTemplateMarkers.Parse(
            "{{title}} {{items.name}} {{items.hours|column}} {{items|matrix}}");

        Assert.AreEqual(4, markers.Count);
        Assert.AreEqual(ExportTemplateMarkerDirection.Scalar, markers[0].Direction);
        Assert.AreEqual(ExportTemplateMarkerDirection.Row, markers[1].Direction);
        Assert.AreEqual("items", markers[1].Collection);
        Assert.AreEqual("name", markers[1].Field);
        Assert.AreEqual(ExportTemplateMarkerDirection.Column, markers[2].Direction);
        Assert.AreEqual(ExportTemplateMarkerDirection.Matrix, markers[3].Direction);
        Assert.AreEqual("items", markers[3].Collection);
    }

    [TestMethod]
    public void CreateTemplateName_CreatesStableSafeNameForChineseFileName()
    {
        var first = ExportTemplateMarkers.CreateTemplateName("加班记录.xlsx");
        var second = ExportTemplateMarkers.CreateTemplateName("加班记录.xlsx");

        Assert.AreEqual(first, second);
        StringAssert.StartsWith(first, "template_");
        Assert.AreEqual(17, first.Length);
    }
}
