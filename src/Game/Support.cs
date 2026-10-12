using System.Text.Json;
using Godot;
using RingDraft.Core;

namespace RingDraft;

internal static class Log
{
    private static string? _path;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}\n{ex}");

    private static void Write(string level, string message)
    {
        string line = $"[RingDraft] {level} {message}";
        GD.Print(line);
        try
        {
            _path ??= Path.Combine(OS.GetUserDataDir(), "ringdraft_log.txt");
            File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss.fff} {line}\n");
        }
        catch { /* logging must never break the game */ }
    }
}

/// <summary>Per-player settings in the game's user data folder (ringdraft_config.json). Written with defaults on first run.</summary>
internal static class Config
{
    public static RulesConfig Rules { get; private set; } = new();

    public static void Load()
    {
        var opts = new JsonSerializerOptions { WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        string path = Path.Combine(OS.GetUserDataDir(), "ringdraft_config.json");
        try
        {
            if (File.Exists(path))
                Rules = JsonSerializer.Deserialize<RulesConfig>(File.ReadAllText(path), opts) ?? new RulesConfig();
            else
                File.WriteAllText(path, JsonSerializer.Serialize(Rules, opts));
            Log.Info($"Config loaded from {path}");
        }
        catch (Exception ex)
        {
            Rules = new RulesConfig();
            Log.Error($"Config at {path} unreadable, using defaults", ex);
        }
    }
}

internal static class Nodes
{
    public static List<T> FindAll<T>(Node root) where T : class
    {
        var found = new List<T>();
        Walk(root, found);
        return found;

        static void Walk(Node n, List<T> acc)
        {
            foreach (var child in n.GetChildren())
            {
                if (child is T match) acc.Add(match);
                Walk(child, acc);
            }
        }
    }

    public static T? FindFirst<T>(Node root) where T : class
    {
        foreach (var child in root.GetChildren())
        {
            if (child is T match) return match;
            var deeper = FindFirst<T>(child);
            if (deeper is not null) return deeper;
        }
        return null;
    }

    public static bool Alive(Node? n) => n is not null && GodotObject.IsInstanceValid(n) && n.IsInsideTree();
}
