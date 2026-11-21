using Microsoft.AspNetCore.Mvc;

using Notifier.MockGateway.DTOs.Requests;
using Notifier.MockGateway.DTOs.Responses;
using Notifier.MockGateway.Services;

namespace Notifier.MockGateway.Controllers;

// Route is fixed by SMS_GATEWAY_URL in contracts/services.md (http://mock-gateway:8002/sms).
[ApiController]
[Route("sms")]
public sealed class SmsController : ControllerBase
{
    private readonly DeliveryLog _deliveries;

    public SmsController(DeliveryLog deliveries)
    {
        _deliveries = deliveries;
    }

    [HttpPost]
    public ActionResult<DeliveryResponse> Create([FromBody] SendMessageRequest request)
    {
        var delivery = _deliveries.Record("sms", request.NotificationId, request.To, request.Text);

        return Ok(delivery);
    }
}
