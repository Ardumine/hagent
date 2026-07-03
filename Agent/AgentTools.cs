using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HCore.Modules.Base;

namespace HCore.Packages.Agent;

/// <summary>
/// The tool surface exposed to the LLM. Every tool operates on the module's
/// injected <see cref="IModuleFileSystem"/>, so the agent acts entirely inside
/// the HCore VFS sandbox (/proc, /packs, /etc, /home, ... all reachable).
/// </summary>
public sealed class AgentTools
{
    private readonly IModuleFileSystem _vfs;
    private readonly IModuleHost _host;
    private readonly string _shellPath;

    public AgentTools(IModuleFileSystem vfs, IModuleHost host, string shellPath)
    {
        _vfs = vfs;
        _host = host;
        _shellPath = shellPath;
    }

    /// <summary>OpenAI-style tool schema advertised to the model.</summary>
    public static JsonElement Schema { get; } = JsonDocument.Parse("""
    [
      {
        "type": "function",
        "function": {
          "name": "vfs_list",
          "description": "List the entries of a directory in the HCore VFS.",
          "parameters": {
            "type": "object",
            "properties": {
              "path": { "type": "string", "description": "Directory path, e.g. '/', '/proc', '/packs'. Defaults to '.'." }
            }
          }
        }
      },
      {
        "type": "function",
        "function": {
          "name": "vfs_read",
          "description": "Read the full text contents of a file in the HCore VFS.",
          "parameters": {
            "type": "object",
            "properties": {
              "path": { "type": "string", "description": "File path to read." }
            },
            "required": ["path"]
          }
        }
      },
      {
        "type": "function",
        "function": {
          "name": "vfs_write",
          "description": "Write (create/overwrite) a text file in the HCore VFS.",
          "parameters": {
            "type": "object",
            "properties": {
              "path": { "type": "string", "description": "File path to write." },
              "content": { "type": "string", "description": "Text content to write." },
              "append": { "type": "boolean", "description": "Append instead of overwrite. Default false." }
            },
            "required": ["path", "content"]
          }
        }
      },
      {
        "type": "function",
        "function": {
          "name": "shell",
          "description": "Run one or more HShell command lines and return their combined output. Use this to interact with modules and the system. Available commands include: ls [path], cat <file>, cd <path>, pwd, mkdir/rmdir/rm/mv/touch/write/append, exists <path>, spawn <module> <instance> (create instance, does not run), run <instance> (run a spawned instance by /proc path), kill <instance>, service <start|stop|restart|status|list> [name], hpm install|list|remove|pack. One command per line.",
          "parameters": {
            "type": "object",
            "properties": {
              "command": { "type": "string", "description": "Command line(s) to run, one per line, e.g. 'ls /proc' or 'spawn HCore.Packages.TestDemo.Module1 m1\\nrun m1'." }
            },
            "required": ["command"]
          }
        }
      },
      {
        "type": "function",
        "function": {
          "name": "pkg_build",
          "description": "Create/compile/hot-load a NEW HCore package on the fly, then register it so it can be spawned. Provide a simple 'name' (e.g. 'Calc') and full C# 'source'. RULES: (1) The ONLY using you need is 'using HCore.Modules.Base;' — do NOT invent namespaces like HCore.Vfs or HCore.Modules. (2) Your implement class MUST extend BaseImplement, which already provides Vfs (IModuleFileSystem), Host (IModuleHost), Logger (IModuleLogger), Data, StopToken and InstanceName — use them directly, do not redeclare them. (3) Provide the module triple. (4) If the result is 'BUILD FAILED', read the compiler errors, fix the source, and call pkg_build AGAIN — do NOT go exploring other files. Minimal working template:\n\nusing HCore.Modules.Base;\nnamespace My.Pkg;\npublic interface IHello : IRunnable { }\npublic class Hello : BaseImplement, IHello {\n    public void Run() { Vfs.WriteAllText(\"/home/hello.txt\", \"hi\"); Logger.I(\"ran\"); }\n}\npublic class Desc : IModuleDescriptor {\n    public string Name => \"My.Hello\";\n    public string FriendlyName => \"Hello\";\n    public System.Type ImplementType => typeof(Hello);\n    public System.Type InterfaceType => typeof(IHello);\n}\n\nOn success it returns the registered module name(s); then use the shell tool: 'spawn <Name> <inst>' then 'run <inst>'. To iterate on an already-spawned module: kill the instance, pkg_build again, re-spawn.",
          "parameters": {
            "type": "object",
            "properties": {
              "name": { "type": "string", "description": "Simple package name, letters/digits only, e.g. 'Calc'. Becomes assembly HCore.Packages.<name>." },
              "source": { "type": "string", "description": "Full C# source for the module (interface + BaseImplement class + IModuleDescriptor). 'using HCore.Modules.Base;' is available." }
            },
            "required": ["name", "source"]
          }
        }
      }
    ]
    """).RootElement.Clone();

    /// <summary>Execute a tool call by name; returns a text result for the model.</summary>
    public string Invoke(string name, JsonElement args)
    {
        try
        {
            return name switch
            {
                "vfs_list" => ListDir(GetStr(args, "path", ".")),
                "vfs_read" => _vfs.ReadAllText(GetStr(args, "path", "")),
                "vfs_write" => Write(GetStr(args, "path", ""), GetStr(args, "content", ""), GetBool(args, "append")),
                "shell" => Shell(GetStr(args, "command", "")),
                "pkg_build" => PkgBuild(GetStr(args, "name", ""), GetStr(args, "source", "")),
                _ => $"error: unknown tool '{name}'"
            };
        }
        catch (Exception ex)
        {
            return $"error: {ex.Message}";
        }
    }

    private string ListDir(string path)
    {
        var sb = new StringBuilder();
        foreach (var entry in _vfs.ListDirectory(path))
            sb.AppendLine(entry);
        var text = sb.ToString();
        return text.Length == 0 ? "(empty)" : text;
    }

    private string Write(string path, string content, bool append)
    {
        _vfs.WriteAllText(path, content, append);
        return $"ok: wrote {content.Length} chars to {path}";
    }

    private string Shell(string command)
    {
        command = command.Replace("\r\n", "\n").Trim();
        if (command.Length == 0) return "error: empty command";

        var shell = _host.GetModuleInterface<IShell>(_shellPath);

        const string scriptPath = "/tmp/agent_shell.hsh";
        try { _vfs.CreateDirectory("/tmp"); } catch { /* already exists */ }
        _vfs.WriteAllText(scriptPath, command + "\n");

        var original = Console.Out;
        var capture = new StringWriter();
        bool ok;
        try
        {
            Console.SetOut(capture);
            ok = shell.RunScript(scriptPath);
        }
        finally
        {
            Console.SetOut(original);
        }

        var output = capture.ToString().Trim();
        if (output.Length > 8000) output = output[..8000] + "\n...(truncated)";
        var status = ok ? "" : "\n(a command failed; execution stopped)";
        return output.Length == 0 ? $"(no output){status}" : output + status;
    }

    private string PkgBuild(string name, string source)
    {
        name = new string(name.Where(char.IsLetterOrDigit).ToArray());
        if (name.Length == 0) return "error: package name must contain letters/digits";
        if (string.IsNullOrWhiteSpace(source)) return "error: empty source";

        var forge = _host.GetModuleInterface<IForge>("@forge");
        var pkg = $"HCore.Packages.{name}";
        var srcVfs = $"/home/agent/build/{pkg}";
        var packVfs = $"/packs/{pkg}";

        // 1. Author the project on the VFS (which is host-backed under /home).
        _vfs.CreateDirectory(srcVfs);
        _vfs.WriteAllText($"{srcVfs}/Module.cs", source);
        _vfs.WriteAllText($"{srcVfs}/{pkg}.csproj", Csproj(pkg, forge.ReferenceDir));

        var srcHost = forge.ToHostPath(srcVfs);
        if (srcHost is null) return $"error: cannot map {srcVfs} to a host path";
        var binHost = Path.Combine(srcHost, "bin");

        // 2. Compile on the host with the real dotnet toolchain.
        var (exit, buildOut) = RunProcess("dotnet",
            $"build \"{Path.Combine(srcHost, $"{pkg}.csproj")}\" -c Debug -o \"{binHost}\" --nologo -v quiet",
            srcHost);

        if (exit != 0)
        {
            var errs = string.Join("\n", buildOut
                .Split('\n')
                .Where(l => l.Contains(": error") || l.Contains(": warning") || l.Contains("Build FAILED")));
            var shown = errs.Length > 0 ? errs : buildOut;
            if (shown.Length > 6000) shown = shown[..6000] + "\n...(truncated)";
            return $"BUILD FAILED (fix the source and call pkg_build again):\n{shown}";
        }

        var builtDll = Path.Combine(binHost, $"{pkg}.dll");
        if (!File.Exists(builtDll))
            return $"error: build succeeded but {pkg}.dll not found in output";

        // 3. Deploy into /packs (host-backed) with an mpd + manifest, then hot-load.
        var packHost = forge.ToHostPath(packVfs);
        if (packHost is null) return $"error: cannot map {packVfs} to a host path";
        Directory.CreateDirectory(packHost);
        File.Copy(builtDll, Path.Combine(packHost, $"{pkg}.dll"), overwrite: true);
        _vfs.WriteAllText($"{packVfs}/mpd", $"{pkg}.dll\n");
        _vfs.WriteAllText($"{packVfs}/manifest.json",
            $"{{\n  \"name\": \"{pkg}\",\n  \"version\": \"0.1.0\",\n  \"description\": \"Built on the fly by the agent\"\n}}\n");

        List<string> modules;
        try
        {
            modules = forge.InstallPack(pkg).ToList();
        }
        catch (Exception ex)
        {
            return $"error: built OK but hot-load failed: {ex.Message}";
        }

        return $"BUILD OK. Registered module(s): {string.Join(", ", modules)}. " +
               $"Now use the shell tool: 'spawn <ModuleName> <inst>' then (if IRunnable) 'run <inst>'.";
    }

    private static string Csproj(string assemblyName, string referenceDir)
        => $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
            <AssemblyName>{assemblyName}</AssemblyName>
            <DebugType>none</DebugType>
          </PropertyGroup>
          <ItemGroup>
            <Reference Include="HCore.Modules.Base">
              <HintPath>{referenceDir}/HCore.Modules.Base.dll</HintPath>
              <Private>false</Private>
            </Reference>
            <Reference Include="HCore.Modules.Robotics">
              <HintPath>{referenceDir}/HCore.Modules.Robotics.dll</HintPath>
              <Private>false</Private>
            </Reference>
          </ItemGroup>
        </Project>
        """;

    private static (int exit, string output) RunProcess(string file, string args, string workingDir)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return (-1, "error: could not start process");
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(180_000))
            {
                try { proc.Kill(true); } catch { }
                return (-1, "error: build timed out after 180s");
            }
            return (proc.ExitCode, (stdout + "\n" + stderr).Trim());
        }
        catch (Exception ex)
        {
            return (-1, $"error: {ex.Message}");
        }
    }

    private static string GetStr(JsonElement o, string key, string fallback)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? fallback
            : fallback;

    private static bool GetBool(JsonElement o, string key)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out var v) &&
           (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b) && b));
}
