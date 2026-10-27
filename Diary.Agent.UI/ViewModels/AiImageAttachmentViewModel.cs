using Avalonia.Media.Imaging;
using Diary.Agent.Protocols;

namespace Diary.Agent.UI.ViewModels;

public sealed class AiImageAttachmentViewModel : IDisposable
{
    public AiImageAttachmentViewModel(AgentImageContent content)
    {
        Content = content;
        using var stream = new MemoryStream(content.Data, writable: false);
        Preview = new Bitmap(stream);
    }

    public AgentImageContent Content { get; }

    public Bitmap Preview { get; }

    public string FileName => Content.FileName;

    public string Details => $"{Content.PixelWidth}×{Content.PixelHeight} · {FormatSize(Content.Data.LongLength)}";

    public void Dispose() => Preview.Dispose();

    private static string FormatSize(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / (1024d * 1024d):0.0} MiB"
        : $"{Math.Max(1, bytes / 1024d):0.#} KiB";
}

internal static class AiImageAttachmentProcessor
{
    public const int MaximumImageCount = 4;
    public const int MaximumLongestEdge = 2048;
    public const long MaximumImageBytes = 10L * 1024 * 1024;
    public const long MaximumTotalBytes = 20L * 1024 * 1024;

    public static async ValueTask<AgentImageContent> FromStreamAsync(
        Stream source,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        await using var input = new MemoryStream();
        await source.CopyToAsync(input, cancellationToken);
        if (input.Length == 0 || input.Length > MaximumImageBytes)
            throw new InvalidDataException("图片文件必须大于 0 且不超过 10 MiB。");
        input.Position = 0;
        using var original = new Bitmap(input);
        return Normalize(original, fileName);
    }

    public static AgentImageContent FromBitmap(Bitmap bitmap, string fileName) => Normalize(bitmap, fileName);

    private static AgentImageContent Normalize(Bitmap bitmap, string fileName)
    {
        using var normalized = ResizeIfNeeded(bitmap);
        using var output = new MemoryStream();
        normalized.Save(output);
        if (output.Length > MaximumImageBytes)
            throw new InvalidDataException("图片规范化后仍超过 10 MiB，请先缩小图片。");
        return new AgentImageContent(
            "image/png",
            output.ToArray(),
            EnsurePngFileName(fileName),
            normalized.PixelSize.Width,
            normalized.PixelSize.Height);
    }

    private static Bitmap ResizeIfNeeded(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;
        if (Math.Max(size.Width, size.Height) <= MaximumLongestEdge)
        {
            using var copy = new MemoryStream();
            bitmap.Save(copy);
            copy.Position = 0;
            return new Bitmap(copy);
        }

        using var source = new MemoryStream();
        bitmap.Save(source);
        source.Position = 0;
        return size.Width >= size.Height
            ? Bitmap.DecodeToWidth(source, MaximumLongestEdge, BitmapInterpolationMode.HighQuality)
            : Bitmap.DecodeToHeight(source, MaximumLongestEdge, BitmapInterpolationMode.HighQuality);
    }

    private static string EnsurePngFileName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(name))
            name = "image";
        return name + ".png";
    }
}
