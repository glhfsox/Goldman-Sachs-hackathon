using Microsoft.AspNetCore.Mvc;

using Notifier.MockGateway.DTOs.Requests;
using Notifier.MockGateway.DTOs.Responses;
using Notifier.MockGateway.Services;

namespace Notifier.MockGateway.Controllers;

// Route is fixed by CALL_GATEWAY_URL in contracts/services.md; the "call" is the TTS text in the log.
[ApiController]
[Route("call")]
public sealed class CallController : ControllerBase
{
    private readonly DeliveryLog _deliveries;

    public CallController(DeliveryLog deliveries)
    {
        _deliveries = deliveries;
    }

    [HttpPost]
    public ActionResult<DeliveryResponse> Create([FromBody] SendMessageRequest request)
    {
        var delivery = _deliveries.Record("call", request.NotificationId, request.To, request.Text);

        return Ok(delivery);
    }
}
