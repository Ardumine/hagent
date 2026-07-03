using System.Text;
using System.Text.Json.Nodes;
using HCore.Modules.Base;

namespace HCore.Packages.Agent;

/// <summary>
/// Persists agent conversations to the HCore VFS so sessions can be created,
/// listed, saved, and restored across agent launches. Each session is a JSON
/// file under <see cref="SessionsDir"/>: <c>{ name, model, updated, messages }</c>.
/// The <c>system</c> message is intentionally NOT stored — it is rebuilt fresh on
/// load so the (instance-specific) system prompt always reflects the live process.
/// </summary>
public sealed class SessionStore
{
    public const string SessionsDir = "/home/agent/sessions";

    private readonly IModuleFileSystem _vfs;

    public SessionStore(IModuleFileSystem vfs) => _vfs = vfs;

    public static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name.Trim())
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        var clean = sb.ToString().Trim('.', '_');
        return clean.Length == 0 ? "session" : clean;
    }

    public static string NewName() => $"session-{DateTime.Now:yyyyMMdd-HHmmss}";

    private string PathFor(string name) => $"{SessionsDir}/{Sanitize(name)}.json";

    public bool Exists(string name) => _vfs.Exists(PathFor(name));

    /// <summary>Names of all saved sessions (files stripped of their .json suffix).</summary>
    public IReadOnlyList<string> List()
    {
        try
        {
            if (!_vfs.Exists(SessionsDir)) return [];
            return _vfs.ListDirectory(SessionsDir)
                .Where(e => e.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .Select(e => e[..^5])
                .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Persist <paramref name="messages"/> (minus the system message) under <paramref name="name"/>.</summary>
    public void Save(string name, string model, JsonArray messages)
    {
        var stored = new JsonArray();
        foreach (var m in messages)
        {
            if (m is null) continue;
            if (m["role"]?.GetValue<string>() == "system") continue;
            stored.Add(m.DeepClone());
        }

        var doc = new JsonObject
        {
            ["name"] = Sanitize(name),
            ["model"] = model,
            ["updated"] = DateTime.UtcNow.ToString("o"),
            ["messages"] = stored,
        };

        try { _vfs.CreateDirectory(SessionsDir); } catch { /* already exists */ }
        _vfs.WriteAllText(PathFor(name), doc.ToJsonString());
    }

    /// <summary>
    /// Load a saved session's messages (excluding system). Returns null if the
    /// session does not exist or cannot be parsed.
    /// </summary>
    public JsonArray? Load(string name)
    {
        try
        {
            if (!Exists(name)) return null;
            var doc = JsonNode.Parse(_vfs.ReadAllText(PathFor(name)));
            var msgs = doc?["messages"] as JsonArray;
            return msgs?.DeepClone() as JsonArray ?? new JsonArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Number of user/assistant/tool messages in a saved session (0 if unreadable).</summary>
    public int Count(string name) => Load(name)?.Count ?? 0;
}
