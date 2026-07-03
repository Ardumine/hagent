using HCore.Modules.Base;

namespace HCore.Packages.Agent;

/// <summary>
/// A one-shot LLM agent. Invoked from the shell as <c>agent &lt;prompt&gt;</c>.
/// It reasons in a tool-calling loop and can read/write/list the HCore VFS.
/// </summary>
public interface IAgent : IModule
{
}
