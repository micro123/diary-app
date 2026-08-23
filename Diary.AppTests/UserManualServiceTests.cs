using Diary.App.Services;

namespace Diary.AppTests;

[TestClass]
public sealed class UserManualServiceTests
{
    [TestMethod]
    public void IsReleaseBuild_MatchesCompilationConfiguration()
    {
#if DEBUG
        Assert.IsFalse(UserManualService.IsReleaseBuild);
#else
        Assert.IsTrue(UserManualService.IsReleaseBuild);
#endif
    }

    [TestMethod]
    public void ResolveDocumentPath_PrefersHtmlAndFallsBackToPdf()
    {
        var root = Directory.CreateTempSubdirectory("diary-user-manual-");
        try
        {
            var manualDirectory = Directory.CreateDirectory(Path.Combine(root.FullName, "Docs", "UserManual"));
            var pdfPath = Path.Combine(manualDirectory.FullName, UserManualService.PdfFileName);
            File.WriteAllText(pdfPath, "pdf");
            Assert.AreEqual(pdfPath, UserManualService.ResolveDocumentPath(root.FullName));

            var htmlPath = Path.Combine(manualDirectory.FullName, UserManualService.HtmlFileName);
            File.WriteAllText(htmlPath, "html");
            Assert.AreEqual(htmlPath, UserManualService.ResolveDocumentPath(root.FullName));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [TestMethod]
    public void ResolveDocumentPath_ReturnsNullWhenManualIsMissing()
    {
        var root = Directory.CreateTempSubdirectory("diary-user-manual-");
        try
        {
            Assert.IsNull(UserManualService.ResolveDocumentPath(root.FullName));
        }
        finally
        {
            root.Delete(true);
        }
    }
}
