using DockCommander.Core.Files;

namespace DockCommander.Core.Plugins;

public interface IFileSystemPlugin : IPlugin
{
    IFileSystemProvider CreateProvider();
}
