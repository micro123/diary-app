namespace Diary.Utils;
public static class IOUtils
{
    static public string ReadAllText(string path)
    {
        if (File.Exists(path) && new FileInfo(path).Length < (8 << 20)) // max to 8MB
        {
            return File.ReadAllText(path);
        }
        return "";
    }
    
    static public bool WriteAllText(string path, string text)
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
}
