using Microsoft.AspNetCore.Mvc;

using Notifier.MockGateway.DTOs.Responses;
using Notifier.MockGateway.Services;

namespace Notifier.MockGateway.Controllers;

[ApiController]
[Route("outage")]
public sealed class OutageController : ControllerBase
{
    private readonly OutageSwitch _outage;

    public OutageController(OutageSwitch outage)
    {
        _outage = outage;
    }

    [HttpGet]
    public ActionResult<OutageResponse> GetById()
    {
        var until = _outage.ActiveUntil;

        return Ok(new OutageResponse(until is not null, until));
    }

    [HttpPost]
    public ActionResult<OutageResponse> Create([FromQuery] int seconds = 60)
    {
        var until = _outage.Start(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 3600)));

        return Ok(new OutageResponse(true, until));
    }

    [HttpDelete]
    public IActionResult Delete()
    {
        _outage.Stop();

        return NoContent();
    }
}
