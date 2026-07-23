using loxone.smart.gateway.Api.Tuya;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace loxone.smart.gateway.Controllers;

[ApiController]
[Route("[controller]")]
public class TuyaController(TuyaMessageSender sender) : ControllerBase
{
    [HttpPost("{name}")]
    public IActionResult SetDataPoint(TuyaRequestModel model)
    {
        Log.Information("New Request:: name: {name}, dp: {dp}, value: {value}", model.Name, model.Dp, model.Value.GetRawText());

        sender.AddToQueue(model);

        return Ok();
    }
}
