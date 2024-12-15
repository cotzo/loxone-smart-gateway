using loxone.smart.gateway.Api.PhilipsHue;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace loxone.smart.gateway.Controllers;

[ApiController]
[Route("[controller]")]
public class PhilipsHueController(PhilipsHueMessageSender sender) : ControllerBase
{
    [HttpPost("{id}")]
    public IActionResult SetLights(PhilipsHueRequestModel model)
    {
        Log.Information("New Request:: id: {id}, value: {value}, lightType: {lightType}, resourceType: {resourceType}", model.Id, model.Value, model.LightType, model.ResourceType);
     
        sender.AddToQueue(model);

        return Ok();
    }
}

