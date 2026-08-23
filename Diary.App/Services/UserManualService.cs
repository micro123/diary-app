using Diary.Utils;

namespace Diary.App.Services;

[DiAutoRegister(singleton: true)]
public sealed class UserManualService
{
    internal const string HtmlFileName = "DiaryApp-User-Manual.html";
    internal const string PdfFileName = "DiaryApp-User-Manual.pdf";

    public static bool IsReleaseBuild
    {
        get
        {
#if DEBUG
            return false;
#else
            return true;
#endif
        }
    }

    public string? DocumentPath => ResolveDocumentPath(AppContext.BaseDirectory);

    public bool IsMenuVisible => IsReleaseBuild && DocumentPath is not null;

    public void Open()
    {
        var path = DocumentPath ?? throw new FileNotFoundException(
            "发布包中未找到用户手册，请重新解压完整安装包。");
        ProcUtils.OpenFileCrossPlatform(path);
    }

    internal static string? ResolveDocumentPath(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        var manualDirectory = Path.Combine(baseDirectory, "Docs", "UserManual");
        var htmlPath = Path.Combine(manualDirectory, HtmlFileName);
        if (File.Exists(htmlPath))
            return htmlPath;
        var pdfPath = Path.Combine(manualDirectory, PdfFileName);
        return File.Exists(pdfPath) ? pdfPath : null;
    }
}
