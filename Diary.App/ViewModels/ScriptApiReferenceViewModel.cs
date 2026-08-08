using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.GUIBase.ViewModels;
using Diary.Utils;

namespace Diary.App.ViewModels;

public enum ScriptApiReferenceBlockKind
{
    Heading,
    Paragraph,
    Code,
}

public sealed record ScriptApiReferenceBlock(
    string Text,
    ScriptApiReferenceBlockKind Kind,
    int Level = 0)
{
    public bool IsHeading => Kind == ScriptApiReferenceBlockKind.Heading;
    public bool IsParagraph => Kind == ScriptApiReferenceBlockKind.Paragraph;
    public bool IsCode => Kind == ScriptApiReferenceBlockKind.Code;
    public double FontSize => Level <= 1 ? 20 : 16;
    public FontWeight FontWeight => Level <= 1 ? FontWeight.Bold : FontWeight.SemiBold;
}

public partial class ScriptApiReferenceViewModel : ViewModelBase
{
    private readonly string _docsRoot;

    public ScriptApiReferenceViewModel(string? docsRoot = null)
    {
        _docsRoot = docsRoot ?? Path.Combine(AppContext.BaseDirectory, "Docs", "ScriptApi");
        LoadReference();
    }

    public IReadOnlyList<string> Languages { get; } = ["C#", "Lua", "Python"];
    public ObservableCollection<ScriptApiReferenceBlock> Blocks { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyPropertyChangedFor(nameof(DocumentPath))]
    private string _selectedLanguage = "C#";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoReference))]
    private bool _hasReference;

    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private string _error = string.Empty;

    public string Title => $"{SelectedLanguage} API Reference";
    public string DocumentPath => Path.Combine(_docsRoot, GetDocumentFileName(SelectedLanguage));
    public bool HasNoReference => !HasReference;
    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    partial void OnSelectedLanguageChanged(string value) => LoadReference();

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    [RelayCommand]
    private void OpenReference()
    {
        if (!HasReference)
            return;
        try
        {
            ProcUtils.OpenFileCrossPlatform(DocumentPath);
            Status = $"已打开 {Title}";
        }
        catch (Exception exception)
        {
            Error = $"无法打开 API Reference：{exception.Message}";
        }
    }

    private void LoadReference()
    {
        Blocks.Clear();
        Error = string.Empty;
        var path = DocumentPath;
        if (!File.Exists(path))
        {
            HasReference = false;
            Status = $"未找到 {Path.GetFileName(path)}";
            return;
        }

        try
        {
            foreach (var block in ParseMarkdown(File.ReadAllText(path)))
                Blocks.Add(block);
            HasReference = Blocks.Count > 0;
            Status = HasReference ? $"已加载 {Title} · {Blocks.Count} 个内容区块" : $"{Title} 暂无内容";
        }
        catch (Exception exception)
        {
            HasReference = false;
            Status = "API Reference 加载失败";
            Error = $"无法读取 API Reference：{exception.Message}";
        }
    }

    private static string GetDocumentFileName(string language) => language switch
    {
        "Lua" => "Lua.md",
        "Python" => "Python.md",
        _ => "CSharp.md",
    };

    private static IReadOnlyList<ScriptApiReferenceBlock> ParseMarkdown(string markdown)
    {
        var blocks = new List<ScriptApiReferenceBlock>();
        var paragraph = new StringBuilder();
        var code = new StringBuilder();
        var inCode = false;

        foreach (var line in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (inCode)
                {
                    AddCodeBlock(blocks, code);
                    code.Clear();
                    inCode = false;
                }
                else
                {
                    AddParagraph(blocks, paragraph);
                    inCode = true;
                }
                continue;
            }

            if (inCode)
            {
                code.AppendLine(line);
                continue;
            }

            if (line.StartsWith('#'))
            {
                AddParagraph(blocks, paragraph);
                var level = line.TakeWhile(character => character == '#').Count();
                blocks.Add(new ScriptApiReferenceBlock(
                    line[level..].Trim(),
                    ScriptApiReferenceBlockKind.Heading,
                    level));
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                AddParagraph(blocks, paragraph);
                continue;
            }

            if (paragraph.Length > 0)
                paragraph.AppendLine();
            paragraph.Append(line);
        }

        if (inCode)
            AddCodeBlock(blocks, code);
        AddParagraph(blocks, paragraph);
        return blocks;
    }

    private static void AddParagraph(List<ScriptApiReferenceBlock> blocks, StringBuilder paragraph)
    {
        var text = paragraph.ToString().Trim();
        if (text.Length > 0)
            blocks.Add(new ScriptApiReferenceBlock(text, ScriptApiReferenceBlockKind.Paragraph));
        paragraph.Clear();
    }

    private static void AddCodeBlock(List<ScriptApiReferenceBlock> blocks, StringBuilder code)
    {
        var text = code.ToString().TrimEnd();
        if (text.Length > 0)
            blocks.Add(new ScriptApiReferenceBlock(text, ScriptApiReferenceBlockKind.Code));
    }
}
