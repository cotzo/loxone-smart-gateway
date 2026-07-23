using System.Diagnostics.Metrics;

namespace loxone.smart.gateway.Api.Tuya;

public class TuyaMetrics
{
    private readonly Histogram<long> _durationHistogram;

    public TuyaMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create("Tuya");
        _durationHistogram = meter.CreateHistogram<long>("device_request_duration");
    }

    public void RecordDuration(long value, string device)
    {
        _durationHistogram.Record(value, new KeyValuePair<string, object?>("device", device));
    }
}
