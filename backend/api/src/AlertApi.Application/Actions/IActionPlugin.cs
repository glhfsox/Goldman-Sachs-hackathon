using AlertApi.Domain.Models;

namespace AlertApi.Application.Actions;

// Extension point (contracts/runbooks.md §3): a new kind of action is one class plus one DI line. The plugin knows
// how to perform its request; where it goes comes from action_targets, resolved before the plugin is called.
public interface IActionPlugin
{
    string Name { get; }

    Risk Risk { get; }

    // The target a step without "target" or "url" in its params uses; null means the step must name one.
    string? DefaultTarget { get; }

    // The HTTP method of the "url" form (a step that names a URL instead of a target operation).
    string UrlMethod { get; }

    TimeSpan DefaultTimeout { get; }

    Task<ActionResult> RunAsync(ActionRequest request, ActionContext context, CancellationToken cancellationToken = default);
}
