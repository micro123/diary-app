using System.IO.Compression;
using System.Text;

namespace Diary.Script.Py;

public static class PythonWorkerSource
{
    private static readonly byte[] CompressedSource = Compress(LoadSource());

    public static IReadOnlyList<string> CreateArguments()
    {
        var encoded = Convert.ToBase64String(CompressedSource);
        var bootstrap = $"import base64,gzip;exec(compile(gzip.decompress(base64.b64decode('{encoded}')),'<diary-python-worker>','exec'))";
        return ["-I", "-c", bootstrap];
    }

    private static string LoadSource()
    {
        var assembly = typeof(PythonWorkerSource).Assembly;
        const string resourceName = "Diary.Script.Py.worker.py";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded Python worker resource '{resourceName}' is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static byte[] Compress(string source)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var writer = new StreamWriter(gzip, new UTF8Encoding(false)))
            writer.Write(source);
        return output.ToArray();
    }
}
