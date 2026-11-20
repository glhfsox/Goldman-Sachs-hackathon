using Microsoft.AspNetCore.Mvc;

using Notifier.MockGateway.DTOs.Responses;
using Notifier.MockGateway.Services;

namespace Notifier.MockGateway.Controllers;

[ApiController]
[Route("deliveries")]
public sealed class DeliveriesController : ControllerBase
{
    private readonly DeliveryLog _deliveries;

    public DeliveriesController(DeliveryLog deliveries)
    {
        _deliveries = deliveries;
    }

    [HttpGet]
    public ActionResult<IReadOnlyList<DeliveryResponse>> GetAll([FromQuery] string? channel, [FromQuery] int limit = 100)
    {
        var deliveries = _deliveries.GetAll(channel, limit);

        return Ok(deliveries);
    }
}
