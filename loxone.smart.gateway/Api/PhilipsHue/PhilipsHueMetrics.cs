using System.Diagnostics.Metrics;

namespace loxone.smart.gateway.Api.PhilipsHue;

public class PhilipsHueMetrics
{
    private readonly Histogram<long> _durationHistogram;
    
    public PhilipsHueMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create("PhilipsHue");
        _durationHistogram = meter.CreateHistogram<long>("bridge_request_duration");
    }

    public void RecordDuration(long value, string path)
    {
        _durationHistogram.Record(value, new KeyValuePair<string, object?>("resource", path));
    }
}