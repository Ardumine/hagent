using System.Text.Json;
using System.Text.Json.Nodes;
using HCore.Modules.Base;

namespace HCore.Packages.Agent;

/// <summary>
/// An interactive agent app. Launched from the shell as <c>agent</c> (optionally
/// with an initial prompt), it takes over the terminal and runs a chat REPL:
/// you type, it thinks and uses tools (VFS + shell), it answers, and the
/// conversation history persists across turns so you can reply to its questions.
/// Type <c>/exit</c> to quit and return to the shell.
/// </summary>
public class AgentImplement : BaseImplement, IAgent, IOneshotCommand
{
    private const string ApiKeyPath = "/etc/agent/apikey";
    private const string ConfigPath = "/etc/agent/config.json";
    private const int MaxSteps = 12;

    private string[] _args = [];

    public void SetArguments(string[] args) => _args = args;

    public void Run()
    {
        string apiKey;
        try
        {
            apiKey = LoadApiKey();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"agent: {ex.Message}");
            return;
        }

        var (model, baseUrl) = LoadConfig();
        var client = new DeepseekClient(apiKey, model, baseUrl);
        var tools = new AgentTools(Vfs, Host, ShellPath());
        var store = new SessionStore(Vfs);

        // Parse launch args: `agent [-s|--session <name>] [initial prompt...]`.
        var (sessionArg, promptArg) = ParseLaunchArgs(_args);

        var messages = new JsonArray { Msg("system", SystemPrompt()) };
        string sessionName;
        if (sessionArg is not null && store.Exists(sessionArg))
        {
            sessionName = SessionStore.Sanitize(sessionArg);
            var restored = store.Load(sessionName);
            if (restored is not null)
            {
                foreach (var m in restored) messages.Add(m?.DeepClone());
                Console.WriteLine($"(restored session '{sessionName}' — {restored.Count} messages)");
            }
        }
        else
        {
            sessionName = sessionArg is not null ? SessionStore.Sanitize(sessionArg) : SessionStore.NewName();
        }

        Console.WriteLine($"HCore Agent ({model}). Session: {sessionName}. Type your message.");
        Console.WriteLine("Commands: /exit, /reset, /model <name>, /sessions, /save [name], /load <name>, /new [name], /help.");
        Logger.I($"agent: interactive session started, model={model}, session={sessionName}");

        // Optional initial prompt passed on the command line: `agent do something`.
        var pending = string.IsNullOrWhiteSpace(promptArg) ? null : promptArg;

        while (true)
        {
            // Honor our own kill signal: if this instance was reaped (e.g. someone
            // ran `kill` on us), the kernel cancelled StopToken — end the session
            // instead of lingering as a zombie on the shell's thread.
            if (StopToken.IsCancellationRequested)
            {
                Console.WriteLine("agent: this instance was killed — ending session.");
                break;
            }

            string? line;
            if (!string.IsNullOrEmpty(pending))
            {
                line = pending;
                pending = null;
                Console.WriteLine($"you> {line}");
            }
            else
            {
                Console.Write("you> ");
                line = Console.ReadLine();
            }

            if (line is null) break;                 // EOF / Ctrl-D
            line = line.Trim();
            if (line.Length == 0) continue;

            if (line is "/exit" or "/quit") break;
            if (line == "/help")
            {
                Console.WriteLine(
                    "Commands: /exit quit · /reset clear conversation · /model <name> switch model ·\n" +
                    "          /sessions list saved · /save [name] save now · /load <name> restore ·\n" +
                    "          /new [name] start fresh session · /help this help.");
                continue;
            }
            if (line == "/reset")
            {
                messages = new JsonArray { Msg("system", SystemPrompt()) };
                Console.WriteLine("(conversation reset)");
                continue;
            }
            if (line == "/sessions")
            {
                var names = store.List();
                if (names.Count == 0) Console.WriteLine("(no saved sessions)");
                else foreach (var n in names)
                    Console.WriteLine($"  {(n == sessionName ? "*" : " ")} {n} ({store.Count(n)} messages)");
                continue;
            }
            if (line == "/save" || line.StartsWith("/save "))
            {
                var given = line.Length > 5 ? line[5..].Trim() : "";
                if (given.Length > 0) sessionName = SessionStore.Sanitize(given);
                store.Save(sessionName, model, messages);
                Console.WriteLine($"(saved session '{sessionName}')");
                continue;
            }
            if (line == "/load" || line.StartsWith("/load "))
            {
                var given = line.Length > 5 ? line[5..].Trim() : "";
                if (given.Length == 0) { Console.WriteLine("usage: /load <name>"); continue; }
                var restored = store.Load(given);
                if (restored is null) { Console.WriteLine($"(no session '{given}')"); continue; }
                sessionName = SessionStore.Sanitize(given);
                messages = new JsonArray { Msg("system", SystemPrompt()) };
                foreach (var m in restored) messages.Add(m?.DeepClone());
                Console.WriteLine($"(loaded session '{sessionName}' — {restored.Count} messages)");
                continue;
            }
            if (line == "/new" || line.StartsWith("/new "))
            {
                var given = line.Length > 4 ? line[4..].Trim() : "";
                sessionName = given.Length > 0 ? SessionStore.Sanitize(given) : SessionStore.NewName();
                messages = new JsonArray { Msg("system", SystemPrompt()) };
                Console.WriteLine($"(new session '{sessionName}')");
                continue;
            }
            if (line == "/model" || line.StartsWith("/model "))
            {
                var requested = line.Length > 6 ? line[6..].Trim() : "";
                if (requested.Length == 0) { Console.WriteLine($"model: {model}"); continue; }
                model = requested;
                SaveModel(model, baseUrl);
                client = new DeepseekClient(apiKey, model, baseUrl);
                Console.WriteLine($"(model set to {model})");
                continue;
            }

            // Hot-reload config each turn so edits to /etc/agent/config.json
            // (by you, or by the agent itself via a tool) take effect live.
            var (curModel, curBase) = LoadConfig();
            if (curModel != model || curBase != baseUrl)
            {
                model = curModel;
                baseUrl = curBase;
                client = new DeepseekClient(apiKey, model, baseUrl);
                Console.WriteLine($"(config reloaded: model={model})");
            }

            messages.Add(Msg("user", line));
            if (!RunTurn(client, tools, messages))
                break; // fatal API error already reported

            // Auto-persist the conversation so it survives crashes / kills.
            try { store.Save(sessionName, model, messages); }
            catch (Exception ex) { Logger.W($"agent: session save failed: {ex.Message}"); }
        }

        Console.WriteLine($"agent: session '{sessionName}' ended.");
        Logger.I($"agent: interactive session '{sessionName}' ended.");
    }

    /// <summary>
    /// Drive one user turn: call the model, execute any tool calls, loop until it
    /// produces a plain-text answer (or the step budget runs out). Returns false
    /// only on a fatal API error (caller should stop the session).
    /// </summary>
    private bool RunTurn(DeepseekClient client, AgentTools tools, JsonArray messages)
    {
        for (var step = 0; step < MaxSteps; step++)
        {
            JsonNode message;
            try
            {
                message = client.Chat(messages, AgentTools.Schema);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"agent: request failed: {ex.Message}");
                Logger.E($"agent request failed: {ex.Message}");
                return false;
            }

            messages.Add(message.DeepClone());

            var toolCalls = message["tool_calls"] as JsonArray;
            if (toolCalls is { Count: > 0 })
            {
                foreach (var call in toolCalls)
                {
                    if (call is null) continue;
                    var id = call["id"]?.GetValue<string>() ?? "";
                    var fn = call["function"];
                    var name = fn?["name"]?.GetValue<string>() ?? "";
                    var argsRaw = fn?["arguments"]?.GetValue<string>() ?? "{}";

                    var args = ParseArgs(argsRaw);
                    Console.WriteLine($"  [tool] {name}({Compact(argsRaw)})");
                    var result = tools.Invoke(name, args);
                    Logger.I($"agent tool {name} -> {result.Length} chars");

                    messages.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = id,
                        ["content"] = result,
                    });
                }
                continue;
            }

            var content = message["content"]?.GetValue<string>() ?? "";
            Console.WriteLine($"agent> {content.Trim()}");
            return true;
        }

        Console.WriteLine($"agent: stopped after {MaxSteps} steps without a final answer.");
        return true;
    }

    private string ShellPath()
    {
        var name = InstanceName;
        var slash = name.LastIndexOf('/');
        return slash > 0 ? name[..slash] : "init/console";
    }

    private string LoadApiKey()
    {
        if (Vfs.Exists(ApiKeyPath))
        {
            var key = Vfs.ReadAllText(ApiKeyPath).Trim();
            if (key.Length > 0) return key;
        }

        var env = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

        throw new InvalidOperationException(
            $"no API key. Put it in {ApiKeyPath} or set DEEPSEEK_API_KEY.");
    }

    private (string model, string baseUrl) LoadConfig()
    {
        var model = "deepseek-chat";
        var baseUrl = "https://api.deepseek.com";
        try
        {
            if (Vfs.Exists(ConfigPath))
            {
                var doc = JsonNode.Parse(Vfs.ReadAllText(ConfigPath));
                model = doc?["model"]?.GetValue<string>() ?? model;
                baseUrl = doc?["baseUrl"]?.GetValue<string>() ?? baseUrl;
            }
        }
        catch { /* fall back to defaults */ }
        return (model, baseUrl);
    }

    private void SaveModel(string model, string baseUrl)
    {
        try
        {
            var cfg = new JsonObject { ["model"] = model, ["baseUrl"] = baseUrl };
            Vfs.WriteAllText(ConfigPath, cfg.ToJsonString());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"(warning: could not persist config: {ex.Message})");
        }
    }

    private string SystemPrompt()
    {
        return
            "You are an autonomous agent running inside HCore, a microkernel with a " +
            "virtual filesystem (VFS). You help the user by reasoning and using tools.\n" +
            "\n" +
            "Environment:\n" +
            $"- Your instance name: {InstanceName}\n" +
            $"- Current working directory: {Vfs.WorkingDirectory}\n" +
            "- The VFS layout: /packs (installed packages), /proc (running module " +
            "instances, read-only), /etc (config + services), /home, /data.\n" +
            "\n" +
            "Tools: vfs_list(path), vfs_read(path), vfs_write(path, content, append?), " +
            "shell(command), pkg_build(name, source). Use vfs_* for direct file access; use " +
            "shell to run HShell command lines and interact with modules (ls, cat, spawn, run, " +
            "kill, service, hpm, ...). Use pkg_build to CREATE, COMPILE and HOT-LOAD a brand-new " +
            "HCore package from C# source on the fly, then 'spawn'/'run' it via shell — a full " +
            "write→compile→(read errors)→fix→run→debug loop, all without a reboot. " +
            "All paths are VFS paths. Use tools to inspect or act on the system " +
            "before answering. When you have the answer, reply in plain text with no tool " +
            "call. Be concise.\n" +
            "\n" +
            "This is an ongoing, multi-turn conversation with the user at a terminal. " +
            "If a request is ambiguous or you need more information, ask the user a " +
            "clarifying question in plain text (no tool call) and wait for their reply.\n" +
            "\n" +
            "About yourself: you run as a one-shot interactive command spawned by the " +
            "shell (instance under init/console), NOT as a registered service. So " +
            $"'service stop {InstanceName}' or 'service start agent' will NOT affect this " +
            "session — there is no 'agent' service. Your model/endpoint come from " +
            "/etc/agent/config.json and are hot-reloaded every turn: if you (or the user) " +
            "change 'model' there, it takes effect on the next message automatically — no " +
            "restart needed. The user can also switch models with the /model command.";
    }

    private static JsonObject Msg(string role, string content)
        => new() { ["role"] = role, ["content"] = content };

    /// <summary>
    /// Split argv (argv[0] == command name) into an optional session name
    /// (<c>-s</c>/<c>--session &lt;name&gt;</c>) and the remaining initial prompt.
    /// </summary>
    private static (string? session, string? prompt) ParseLaunchArgs(string[] args)
    {
        string? session = null;
        var rest = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            if ((args[i] is "-s" or "--session") && i + 1 < args.Length)
            {
                session = args[++i];
            }
            else
            {
                rest.Add(args[i]);
            }
        }
        var prompt = rest.Count > 0 ? string.Join(' ', rest).Trim() : null;
        return (session, prompt);
    }

    private static JsonElement ParseArgs(string raw)
    {
        try
        {
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw).RootElement.Clone();
        }
        catch
        {
            return JsonDocument.Parse("{}").RootElement.Clone();
        }
    }

    private static string Compact(string s)
    {
        s = s.Replace("\n", " ").Replace("\r", " ");
        return s.Length > 120 ? s[..120] + "..." : s;
    }
}
