using AlertApi.Domain.Models;

namespace AlertApi.Infrastructure.Actions;

public sealed class HttpGetAction : HttpTargetAction
{
    public HttpGetAction(IHttpClientFactory clients)
        : base(clients)
    {
    }

    public override string Name => "http_get";

    public override Risk Risk => Risk.ReadOnly;

    public override string? DefaultTarget => null;

    public override string UrlMethod => "GET";
}
