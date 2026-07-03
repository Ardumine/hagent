using HCore.Modules.Base;

namespace HCore.Packages.Agent;

public class ModDescriptor : IModuleDescriptor
{
    public string Name => "HCore.Packages.Agent.Agent";

    public string FriendlyName => "LLM Agent";

    public Type ImplementType => typeof(AgentImplement);

    public Type InterfaceType => typeof(IAgent);
}
