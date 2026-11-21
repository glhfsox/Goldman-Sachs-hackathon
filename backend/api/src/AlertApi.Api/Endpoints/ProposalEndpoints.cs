using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using AlertApi.Application.DTOs.Requests;
using AlertApi.Application.DTOs.Responses;
using AlertApi.Application.Interfaces;

namespace AlertApi.Api.Endpoints;

public static class ProposalEndpoints
{
    public static IEndpointRouteBuilder MapProposalEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // A proposal is filed on an alert, so Create is mapped under the alert's route.
        app.MapPost("api/alerts/{id}/proposals", Create)
            .WithTags("Proposals")
            .WithName("CreateProposal")
            .WithSummary("Files a proposal on an alert as the user in X-User (a person or ai-worker). An identical open proposal is returned with 200, not filed twice.");

        var group = app.MapGroup("api/proposals").WithTags("Proposals");

        group.MapGet("", GetAll)
            .WithName("GetProposals")
            .WithSummary("Lists proposals, newest first; filter with ?status=open|accepted|rejected, at most 200 per call.");

        group.MapGet("{id}", GetById)
            .WithName("GetProposalById")
            .WithSummary("Returns one proposal.");

        group.MapPost("{id}/accept", Accept)
            .WithName("AcceptProposal")
            .WithSummary("A person accepts an open proposal and its effect is applied; 403 for a service, 409 when not open.");

        group.MapPost("{id}/reject", Reject)
            .WithName("RejectProposal")
            .WithSummary("A person rejects an open proposal; 403 for a service, 409 when not open.");

        return app;
    }

    private static async Task<Results<Created<ProposalResponse>, Ok<ProposalResponse>>> Create(
        string id,
        [FromBody] CreateProposalRequest request,
        [FromHeader(Name = "X-User")] string? user,
        IProposalService service,
        CancellationToken cancellationToken)
    {
        var result = await service.CreateAsync(id, request, user, cancellationToken);

        return result.Created
            ? TypedResults.Created($"/api/proposals/{result.Proposal.Id}", result.Proposal)
            : TypedResults.Ok(result.Proposal);
    }

    private static async Task<Ok<IReadOnlyList<ProposalResponse>>> GetAll(
        [FromQuery] string? status,
        [FromQuery] int? limit,
        IProposalService service,
        CancellationToken cancellationToken)
    {
        var proposals = await service.GetAllAsync(status, limit ?? 50, cancellationToken);

        return TypedResults.Ok(proposals);
    }

    private static async Task<Ok<ProposalResponse>> GetById(
        string id,
        IProposalService service,
        CancellationToken cancellationToken)
    {
        var proposal = await service.GetByIdAsync(id, cancellationToken);

        return TypedResults.Ok(proposal);
    }

    private static async Task<Ok<ProposalResponse>> Accept(
        string id,
        [FromHeader(Name = "X-User")] string? user,
        IProposalService service,
        CancellationToken cancellationToken)
    {
        var proposal = await service.AcceptAsync(id, user, cancellationToken);

        return TypedResults.Ok(proposal);
    }

    private static async Task<Ok<ProposalResponse>> Reject(
        string id,
        [FromHeader(Name = "X-User")] string? user,
        IProposalService service,
        CancellationToken cancellationToken)
    {
        var proposal = await service.RejectAsync(id, user, cancellationToken);

        return TypedResults.Ok(proposal);
    }
}
