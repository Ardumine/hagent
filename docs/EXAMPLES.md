# Example Prompts

The agent is driven entirely by natural language — you launch it with `agent`
and talk to it. Below are prompts that work well, from trivial to the
build-a-running-service demo, plus tips for getting reliable results.

> Tip: everything after `agent` on the shell line is the first message, or just
> launch `agent` and type. Use `-s <name>` to resume a saved session.

---

## Inspecting the system

```
what packages are installed?
```
```
list the running instances under /proc and tell me what each one is.
```
```
read /etc/agent/config.json and summarize it.
```

## Working with modules

```
spawn the TestDemo Module1 as m1, run it, then confirm it appears in /proc.
```
```
what services are defined, and which are currently running?
```
```
kill the lidar instance and confirm it's gone.
```

## Creating packages on the fly (`pkg_build`)

This is the headline feature: the agent writes C#, compiles it with `dotnet`,
hot-loads it into the running kernel, and spawns it — no reboot.

**Simple — a module that writes a file:**
```
Use pkg_build (name "Proof") to make a runnable module, descriptor Name
"Demo.Proof", whose Run() calls Vfs.WriteAllText("/home/proof.txt", "it works").
Build it, spawn as p1, run p1, then read the file back.
```

**Advanced — an HTTP server that also registers a shell command.**
The agent can't read the kernel contracts (they live outside the VFS), so give
it a short API cheat-sheet; the build loop then converges in one or two tries:

```
Build me a web server as an HCore module, on the fly, using pkg_build (name "Web").

Goal:
- A module (implements IRunnable) that serves files from the VFS directory
  /home/www over HTTP on http://localhost:8080/ ("/" serves index.html).
- It must register a shell command named "web" (via IShell.RegisterCommand) so
  it can be controlled from the shell: `web` prints the URL + running state.
- Run the HTTP listener on a background Task (do NOT block Run()), and stop it
  when the module is killed by observing StopToken.

Then: write a nice /home/www/index.html, spawn the module as web1, run web1,
and use the shell tool to run the `web` command to confirm it registered.

API cheat-sheet (only `using HCore.Modules.Base;` is needed):
- BaseImplement already gives you: Vfs, Host, Logger, StopToken, InstanceName.
- Vfs (IModuleFileSystem): string ReadAllText(path); bool Exists(path);
  void WriteAllText(path, text, append=false); void CreateDirectory(path);
- Get the shell:  var shell = Host.GetModuleInterface<IShell>("init/console");
  IShell has: void RegisterCommand(ICommand command);
- ICommand: string Name { get; } string Description { get; }
            void Execute(IReadOnlyList<string> args, ShellContext ctx);
  ShellContext has: TextWriter Out  (write with ctx.Out.WriteLine(...)).
- HTTP: System.Net.HttpListener with prefix "http://localhost:8080/".
  Start it on Task.Run(...). To stop on kill:
      StopToken.Register(() => { try { listener.Stop(); } catch {} });
  Guard the loop with: while (!StopToken.IsCancellationRequested)
- The triple: interface (: IRunnable), class (: BaseImplement + interface),
  and a class implementing IModuleDescriptor (Name/FriendlyName/ImplementType/
  InterfaceType).

If pkg_build reports BUILD FAILED, fix the reported errors and call pkg_build
again — don't go exploring other files.
```

## Sessions

```
agent -s research        # resume (or start) the "research" session
```
Inside: `/save`, `/sessions`, `/load <name>`, `/new [name]`, `/reset`.
Conversations auto-save after every turn to `/home/agent/sessions/`.

---

## Tips for effective prompts

- **Be explicit about VFS paths.** The agent assumes `/` is root but won't guess
  where your file lives. Say `read /prompt.txt`, not `read prompt.txt`.
- **The `shell` tool runs HShell, not bash.** There is no `curl`, `wget`, pipes
  (`|`), redirection (`>`), or chaining (`&&`, `||`). Available commands: `ls`,
  `cat`, `cd`, `pwd`, `mkdir`, `rm`, `mv`, `touch`, `write`, `append`, `exists`,
  `spawn`, `run`, `kill`, `service`, `hpm`, `afcp`. For anything else, ask the
  agent to use the `vfs_*` tools or to build a module.
- **For `pkg_build`, include an API cheat-sheet.** Only `using
  HCore.Modules.Base;` is needed; `BaseImplement` provides `Vfs`, `Host`,
  `Logger`, `Data`, `StopToken`, `InstanceName`. Don't invent namespaces.
- **Long-running modules should observe `StopToken`** and run their work on a
  background `Task` so `Run()` returns and `kill` actually stops them.
- **On build failure, the agent self-corrects** — `pkg_build` returns the
  compiler errors and the agent fixes and retries.
- **Iterating on a spawned module:** kill the instance, `pkg_build` again
  (it replaces the old descriptor), then re-spawn.
