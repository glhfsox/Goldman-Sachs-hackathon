using AlertApi.Application.Runs;

namespace AlertApi.Application.DTOs.Responses;

// What starting the run would do: the person who starts it approves exactly this. A node with Missing values is blocked.
public sealed record RunPreviewResponse(
    string RunbookId,
    int RunbookVersion,
    string Risk,
    IReadOnlyList<RunPreviewNodeResponse> Nodes)
{
    public static RunPreviewResponse From(string runbookId, int version, RunDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        return new RunPreviewResponse(
            runbookId,
            version,
            draft.Risk,
            draft.Nodes
                .Select(node => new RunPreviewNodeResponse(node.Step.Id, node.Text, node.Step.Action, node.Risk, node.Params, node.Missing))
                .ToList());
    }
}
