using loxone.smart.gateway.Api.PhilipsHue;
using Microsoft.AspNetCore.Mvc;

namespace loxone.smart.gateway.Controllers;

[ApiController]
[Route("[controller]")]
public class PhilipsHueController(ILogger<PhilipsHueController> logger, PhilipsHueMessageSender sender) : ControllerBase
{
    [HttpPost("{id}")]
    public IActionResult SetLights(PhilipsHueRequestModel model)
    {
        logger.LogInformation(
            $"New Request:: id: {model.Id}, value: {model.Value}, lightType: {model.LightType}, resourceType: {model.ResourceType}");

        sender.AddToQueue(model);

        return Ok();
    }
}

