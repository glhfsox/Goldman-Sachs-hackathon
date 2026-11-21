using AlertApi.Application.Actions;
using AlertApi.Application.Exceptions;
using AlertApi.Application.Runbooks;
using AlertApi.Domain.Models;

namespace AlertApi.Application.Runs;

public static class RunRisk
{
    // The highest risk of any node, counting nodes an `if` may skip, so a graph cannot hide an approval action behind a
    // condition. A node that names an unknown action or does not resolve to a target is a problem; the caller must not
    // create the run while there are any.
    public static Risk Compute(IReadOnlyList<RunbookNode> nodes, ActionCatalog catalog, List<ProblemItem> problems)
    {
        var risk = Risk.ReadOnly;

        foreach (var resolved in Resolve(nodes, catalog, problems).Values)
        {
            risk = RiskNames.Max(risk, resolved.Risk);
        }

        return risk;
    }

    // Each step with an action resolved against the catalog, by node id; steps that do not resolve are problems.
    public static IReadOnlyDictionary<string, ResolvedAction> Resolve(
        IReadOnlyList<RunbookNode> nodes,
        ActionCatalog catalog,
        List<ProblemItem> problems)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(problems);

        var result = new Dictionary<string, ResolvedAction>();

        foreach (var node in nodes.Where(node => node.Action is not null))
        {
            var path = $"steps[{node.Index}]";

            if (!catalog.Actions.TryGetValue(node.Action!, out var action))
            {
                problems.Add(new ProblemItem($"{path}.action", $"'{node.Action}' is not a registered action"));

                continue;
            }

            var resolved = ActionTargetResolver.Resolve(action, node.Params, catalog.Targets, path, problems);

            if (resolved is not null)
            {
                result[node.Id] = resolved;
            }
        }

        return result;
    }

    public static RunStartMode StartModeOf(Risk risk)
    {
        return risk switch
        {
            Risk.ReadOnly => RunStartMode.Immediately,
            Risk.Auto => RunStartMode.AfterCancelWindow,
            _ => RunStartMode.AfterApproval
        };
    }
}
