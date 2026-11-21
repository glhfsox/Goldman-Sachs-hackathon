using System.Text.Json;

using AlertApi.Application.Actions;
using AlertApi.Application.Exceptions;
using AlertApi.Application.Runbooks;
using AlertApi.Application.Serialization;
using AlertApi.Domain.Models;

namespace AlertApi.Application.Runs;

// Turns a canonical runbook and an alert into what a run would do, without storing anything: the preview shows it and
// run creation stores it, so the person approves exactly the requests that will be sent.
public static class RunDrafter
{
    public static RunDraft Draft(Alert alert, JsonElement canonical, ActionCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(alert);
        ArgumentNullException.ThrowIfNull(catalog);

        var steps = RunbookGraph.Parse(canonical);
        var problems = new List<ProblemItem>();
        var resolved = RunRisk.Resolve(steps, catalog, problems);

        if (problems.Count > 0)
        {
            throw new InvalidRunbookException(problems);
        }

        var needs = RunbookGraph.EffectiveNeeds(steps);
        var envelope = RecordJson.ToElement(alert.SampleEvent);
        var nodes = new List<DraftNode>();
        var risk = Risk.ReadOnly;

        foreach (var step in steps)
        {
            var filled = PlaceholderFiller.Fill(step.Params, envelope);
            var text = PlaceholderFiller.FillText(step.Text, envelope, []);
            var nodeRisk = Risk.ReadOnly;
            ResolvedAction? request = null;

            if (resolved.TryGetValue(step.Id, out var unfilled))
            {
                nodeRisk = unfilled.Risk;
                request = filled.Missing.Count == 0 ? ResolveFilled(step, filled, catalog, problems) : null;
            }

            risk = RiskNames.Max(risk, nodeRisk);
            nodes.Add(new DraftNode(
                step,
                text,
                filled.Params,
                filled.Missing,
                nodeRisk.ToName(),
                request?.TargetId ?? unfilled?.TargetId,
                request?.TargetVersion ?? unfilled?.TargetVersion,
                request is null ? null : JsonSerializer.Serialize(request.Request, RecordJson.Options),
                needs[step.Id]));
        }

        if (problems.Count > 0)
        {
            throw new InvalidRunbookException(problems);
        }

        return new RunDraft(risk.ToName(), nodes);
    }

    // A filled value can only change path segments and the body, so a step that resolved before filling resolves now;
    // a problem here would be a bug, and it is reported as 422 rather than stored.
    private static ResolvedAction? ResolveFilled(RunbookNode step, FilledParams filled, ActionCatalog catalog, List<ProblemItem> problems)
    {
        return ActionTargetResolver.Resolve(catalog.Actions[step.Action!], filled.Params, catalog.Targets, $"steps[{step.Index}]", problems);
    }
}
