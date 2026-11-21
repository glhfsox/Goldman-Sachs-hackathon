namespace AlertApi.Application.Actions;

public interface IActionRegistry
{
    IReadOnlyDictionary<string, ActionDescriptor> Descriptors { get; }

    bool TryGetPlugin(string name, out IActionPlugin plugin);
}
