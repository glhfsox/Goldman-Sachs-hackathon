namespace AlertApi.Application.Actions;

// Every IActionPlugin registered in DI, by name. A duplicate name is a startup error, not a silent override.
public sealed class ActionRegistry : IActionRegistry
{
    private readonly Dictionary<string, IActionPlugin> _plugins;

    public ActionRegistry(IEnumerable<IActionPlugin> plugins)
    {
        ArgumentNullException.ThrowIfNull(plugins);

        _plugins = plugins.ToDictionary(plugin => plugin.Name);
        Descriptors = _plugins.Values.ToDictionary(
            plugin => plugin.Name,
            plugin => new ActionDescriptor(
                plugin.Name,
                plugin.Risk,
                plugin.DefaultTarget,
                plugin.UrlMethod,
                (int)plugin.DefaultTimeout.TotalSeconds));
    }

    public IReadOnlyDictionary<string, ActionDescriptor> Descriptors { get; }

    public bool TryGetPlugin(string name, out IActionPlugin plugin)
    {
        return _plugins.TryGetValue(name, out plugin!);
    }
}
