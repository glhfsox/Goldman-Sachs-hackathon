using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

using AlertApi.Application.Interfaces;

namespace AlertApi.Api.Endpoints;

public static class LiveEndpoints
{
    public static IEndpointRouteBuilder MapLiveEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // contracts/api.md puts the WebSocket at /ws, outside the /api group.
        app.Map("ws", Connect)
            .ExcludeFromDescription();

        return app;
    }

    private static async Task<Results<BadRequest, EmptyHttpResult>> Connect(
        HttpContext context,
        [FromQuery] string? team,
        ILiveFeed feed,
        CancellationToken cancellationToken)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            return TypedResults.BadRequest();
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();

        await feed.RunAsync(socket, team, cancellationToken);

        return TypedResults.Empty;
    }
}
