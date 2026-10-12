using System.Collections;
using System.Reflection;
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

/// <summary>
/// Reflection for members whose exact names weren't confirmed from public mods.
/// Every use lists candidate names; check the log and a decompile of sts2.dll if a value comes back empty.
/// </summary>
internal static class Reflect
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static object? Get(object? target, params string[] names)
    {
        if (target is null) return null;
        foreach (var name in names)
        {
            for (var t = target.GetType(); t is not null; t = t.BaseType)
            {
                try
                {
                    var p = t.GetProperty(name, All | BindingFlags.DeclaredOnly);
                    if (p is not null && p.GetIndexParameters().Length == 0) return p.GetValue(target);
                    var f = t.GetField(name, All | BindingFlags.DeclaredOnly);
                    if (f is not null) return f.GetValue(target);
                }
                catch { /* try the next candidate */ }
            }
        }
        return null;
    }

    public static int GetInt(object? target, int fallback, params string[] names)
    {
        return Get(target, names) switch
        {
            int i => i,
            bool b => b ? 1 : 0,
            byte b => b,
            short s => s,
            long l => (int)l,
            decimal d => (int)d,
            _ => fallback,
        };
    }

    public static bool TryInvoke(object? target, string name, params object?[] args)
    {
        if (target is null) return false;
        for (var t = target.GetType(); t is not null; t = t.BaseType)
        {
            var m = t.GetMethods(All | BindingFlags.DeclaredOnly)
                     .FirstOrDefault(x => x.Name == name && x.GetParameters().Length == args.Length);
            if (m is null) continue;
            m.Invoke(target, args);
            return true;
        }
        return false;
    }

    public static IEnumerable<object> Items(object? maybeEnumerable)
    {
        if (maybeEnumerable is IEnumerable e and not string)
            foreach (var x in e) if (x is not null) yield return x;
    }

    /// <summary>Display text for a LocString-ish value: prefers GetFormattedText(), falls back to ToString().</summary>
    public static string Text(object? value)
    {
        if (value is null) return string.Empty;
        if (value is string s) return s;
        try
        {
            var m = value.GetType().GetMethod("GetFormattedText", BindingFlags.Instance | BindingFlags.Public, Type.EmptyTypes);
            if (m?.Invoke(value, null) is string formatted) return StripBbcode(formatted);
        }
        catch { }
        return StripBbcode(value.ToString() ?? string.Empty);
    }

    private static string StripBbcode(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, @"\[/?[a-zA-Z_]+(=[^\]]*)?\]", string.Empty);
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
