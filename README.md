# hagent

An interactive LLM agent for [HCore](https://github.com/Ardumine/hcore) — a chat REPL you launch from the shell that can reason, use tools, read/write the HCore VFS, drive modules, and even **create, compile, and hot-load new HCore packages on the fly**. Uses the Deepseek OpenAI-compatible API.

## Layout

This is a standalone HCore package repo, cloned **alongside** the kernel:

```
ardumine/
  hcore/      # the kernel (https://github.com/Ardumine/hcore)
  hagent/     # this repo
```

The project references the kernel via a peer relative path (`..\hcore\src\HCore.Modules.Base`) and its PostBuild step deploys the built pack into `../hcore/FS/packs/`.

## Build

```bash
# from a workspace containing both hcore/ and hagent/
git clone https://github.com/Ardumine/hcore.git
git clone https://github.com/Ardumine/hagent.git

cd hagent
dotnet build           # compiles + deploys to ../hcore/FS/packs/HCore.Packages.Agent
```

## Configure

The agent needs a Deepseek API key. Provide it either way:

- VFS file `/etc/agent/apikey` (i.e. `hcore/FS/etc/agent/apikey`), or
- the `DEEPSEEK_API_KEY` environment variable.

Optional `/etc/agent/config.json` selects the model/endpoint (defaults shown):

```json
{ "model": "deepseek-chat", "baseUrl": "https://api.deepseek.com" }
```

> Do not commit your API key. `config.json` intentionally contains no secret.

## Run

Boot the kernel, then launch the agent from the HCore shell:

```
$ dotnet run --project ../hcore/src/HCore.Main
...
/ $ agent
HCore Agent (deepseek-chat). Session: session-... Type your message.
you> what packages are installed?
you> /exit
```

Launch options: `agent [-s|--session <name>] [initial prompt...]`.

### REPL commands

| Command | Description |
|---|---|
| `/exit`, `/quit` | End the session (returns to the shell) |
| `/reset` | Clear the current conversation |
| `/model [name]` | Show or switch the model (persists to config) |
| `/sessions` | List saved sessions |
| `/save [name]` | Save the conversation now (optionally rename) |
| `/load <name>` | Restore a saved session |
| `/new [name]` | Start a fresh session |
| `/help` | Show help |

Conversations auto-save after each turn to `/home/agent/sessions/<name>.json`.

## Tools available to the agent

| Tool | Purpose |
|---|---|
| `vfs_list` / `vfs_read` / `vfs_write` | Browse and edit the HCore VFS |
| `shell` | Run HShell command lines (`ls`, `cat`, `spawn`, `run`, `kill`, `service`, `hpm`, ...) |
| `pkg_build` | Author C# source → compile with `dotnet` → hot-load into the running kernel → spawn/run. Full write → compile → fix → run loop, no reboot. |

`pkg_build` relies on the kernel's `@forge` service (VFS↔host path bridge + runtime module registration), available in recent `hcore`.

## License

See the HCore project.
