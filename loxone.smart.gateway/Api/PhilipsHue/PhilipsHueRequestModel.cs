using Microsoft.AspNetCore.Mvc;

namespace loxone.smart.gateway.Api.PhilipsHue;

public class PhilipsHueRequestModel
{
    [FromRoute]
    public required string Id { get; set; }
    
    [FromBody]
    public int Value { get; set; }
    
    [FromQuery]
    public PhilipsHueLightType LightType { get; set; }
    
    [FromQuery]
    public required string ResourceType { get; set; }
    
    [FromQuery]
    public int TransitionTime { get; set; }
    
    public int Retries { get; set; }

    public override string ToString()
    {
        return $"id: {Id}, value: {Value}, resourceType: {ResourceType}, lightType: {LightType}";
    }
}