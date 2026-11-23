using System.Text;
using System.Text.Json;

namespace ASPAssistant.Core.Data;

public static class UserJsonFile
{
    public static void Write(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(Encoding.UTF8.GetBytes(json));
            stream.Flush(true);
        }
        if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
        else File.Move(temporary, path);
    }

    public static T? Read<T>(string path, Func<string, T?> parse) where T : class
    {
        if (!File.Exists(path)) return null;
        try { return parse(File.ReadAllText(path)); }
        catch (JsonException) { PreserveBroken(path); }

        var backup = path + ".bak";
        if (File.Exists(backup))
        {
            var json = File.ReadAllText(backup);
            T? value = null;
            try { value = parse(json); }
            catch (JsonException) { PreserveBroken(backup); }
            if (value is not null)
            {
                // 恢复正式文件，让本次及之后的读取都使用同一份恢复结果。
                Write(path, json);
                AppLogger.Warn("UserJsonFile", $"{path} 解析失败，已使用 .bak 恢复。");
                return value;
            }
        }
        AppLogger.Warn("UserJsonFile", $"{path} 解析失败，无可用 .bak，从空开始。");
        return null;
    }

    private static void PreserveBroken(string path)
    {
        var broken = path + $".broken-{DateTime.Now:yyyyMMdd-HHmmss}";
        // 同一秒再次损坏时也保留原证据。
        if (File.Exists(broken)) broken += "-" + Guid.NewGuid().ToString("N");
        File.Move(path, broken);
    }
}
