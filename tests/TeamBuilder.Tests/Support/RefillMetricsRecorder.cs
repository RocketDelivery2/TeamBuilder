using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using TeamBuilder.Infrastructure.Outbox;

namespace TeamBuilder.Tests.Support;

/// <summary>Collects every measurement of the <c>TeamBuilder.Refill</c> meter while alive.</summary>
internal sealed class RefillMetricsRecorder : IDisposable
{
    private readonly MeterListener _listener = new();

    public ConcurrentDictionary<string, ConcurrentQueue<(double Value, KeyValuePair<string, object?>[] Tags)>> Measurements { get; } = new();

    public RefillMetricsRecorder()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == RefillTelemetry.MeterName)
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.Start();
    }

    public IReadOnlyList<double> Values(string instrument) =>
        Measurements.TryGetValue(instrument, out var queue) ? queue.Select(m => m.Value).ToList() : [];

    public double Sum(string instrument) => Values(instrument).Sum();

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) =>
        Measurements.GetOrAdd(instrument.Name, _ => new()).Enqueue((value, tags.ToArray()));

    public void Dispose() => _listener.Dispose();
}
