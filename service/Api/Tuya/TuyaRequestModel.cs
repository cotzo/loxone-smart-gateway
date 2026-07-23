using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace loxone.smart.gateway.Api.Tuya;

public class TuyaRequestModel
{
    [FromRoute]
    public required string Name { get; set; }

    [FromQuery]
    public int Dp { get; set; }

    // Raw JSON value so the body's type carries through to the device:
    // true/false for Boolean DPs, 4 for Integer DPs, "forward" for Enum DPs.
    [FromBody]
    public JsonElement Value { get; set; }

    public int Retries { get; set; }

    public override string ToString()
    {
        return $"name: {Name}, dp: {Dp}, value: {Value.GetRawText()}";
    }
}
