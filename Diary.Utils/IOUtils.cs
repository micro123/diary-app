namespace Diary.Utils;

public static class IoUtils
{
    public static string ReadAllText(string path)
    {
        if (File.Exists(path) && new FileInfo(path).Length < (8 << 20)) // max to 8MB
        {
            return File.ReadAllText(path);
        }
        return "";
    }

    public static byte[] ReadAllBytes(string path)
    {
        if (File.Exists(path) && new FileInfo(path).Length < (8 << 20)) 
        {
            return File.ReadAllBytes(path);
        }
        return [];
    }

    public static bool WriteAllText(string path, string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            if (Directory.Exists(Path.GetDirectoryName(path)))
            {
                File.WriteAllText(path, text);
                return true;
            }
        }
        return false;
    }

    public static bool WriteAllBytes(string path, byte[] bytes)
    {
        if (bytes.Length > 0)
        {
            if (Directory.Exists(Path.GetDirectoryName(path)))
            {
                File.WriteAllBytes(path, bytes);
                return true;
            }
        }
        return false;
    }
}
