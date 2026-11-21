namespace AlertApi.Application.DTOs.Responses;

// Created is false when an identical open proposal already existed and was returned instead.
public sealed record CreatedProposal(
    ProposalResponse Proposal,
    bool Created);
