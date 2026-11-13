namespace DockCommander.Core.Plugins;

public interface IPlugin
{
    string Id { get; }
    string Name { get; }
    Version Version { get; }
}
