using System.Globalization;
using SmartX.Shared.Collections;
using SmartX.Shared.Runtime;

namespace SmartX.Api.Services;

/// <summary>
/// Chronological processed readings. Composite HistoryKey keeps duplicate timestamps distinct.
/// The sorted dictionary remains the Part 2 newest-first window. Each sensor also has an AVL
/// index of those same retained rows so a timestamp range does not scan the whole window.
/// Evicting the oldest row deletes it from that sensor's tree under the same lock.
/// </summary>
public sealed class TelemetryHistoryStore
{
    private readonly SortedDictionary<HistoryKey, ProcessedReading> _history = [];
    private readonly Dictionary<string, AvlTree<HistoryKey, ProcessedReading>> _bySensor = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly int _capacity;
    private long _sequence;

    public TelemetryHistoryStore(int capacity = 2000)
    {
        _capacity = capacity;
    }

    public int Count
    {
        get { lock (_gate) return _history.Count; }
    }

    public ProcessedReading Add(TelemetryWorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var reading = new ProcessedReading
        {
            TimestampUtc = item.TimestampUtc.ToUniversalTime(),
            DeviceId = SensorRegistry.NormalizeMac(item.DeviceId),
            ValueKind = item.ValueKind,
            DisplayValue = item.ValueKind == "bool"
                ? (item.BooleanValue == true ? "ON" : "OFF")
                : item.NumericValue?.ToString("0.##", CultureInfo.InvariantCulture) ?? "",
            MetricName = item.MetricName,
            Unit = item.Unit
        };

        lock (_gate)
        {
            var key = new HistoryKey(reading.TimestampUtc, ++_sequence);
            _history[key] = reading;
            Index(key, reading);
            while (_history.Count > _capacity)
            {
                var oldest = _history.Keys.First();
                var evicted = _history[oldest];
                _history.Remove(oldest);
                Unindex(oldest, evicted);
            }
        }

        return reading;
    }

    /// <summary>
    /// Inclusive UTC range for one sensor, oldest first. Empty when the sensor has no retained rows
    /// or when <paramref name="fromUtc"/> is after <paramref name="toUtc"/>.
    /// </summary>
    public IReadOnlyList<ProcessedReading> Range(string mac, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mac);
        fromUtc = fromUtc.ToUniversalTime();
        toUtc = toUtc.ToUniversalTime();
        if (fromUtc > toUtc)
        {
            return [];
        }

        var normalized = SensorRegistry.NormalizeMac(mac);
        lock (_gate)
        {
            if (!_bySensor.TryGetValue(normalized, out var tree))
            {
                return [];
            }

            return tree.RangeInclusive(
                new HistoryKey(fromUtc, long.MinValue),
                new HistoryKey(toUtc, long.MaxValue));
        }
    }

    public IReadOnlyList<ProcessedReading> Latest(int take, string? mac = null)
    {
        take = Math.Clamp(take, 1, 500);
        lock (_gate)
        {
            IEnumerable<ProcessedReading> query = _history.Values;
            if (!string.IsNullOrWhiteSpace(mac))
            {
                var normalized = SensorRegistry.NormalizeMac(mac);
                query = query.Where(r => r.DeviceId.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            }

            return query.TakeLast(take).Reverse().ToList();
        }
    }

    public IReadOnlyList<ProcessedReading> SnapshotOldestFirst()
    {
        lock (_gate)
        {
            return _history.Values.ToList();
        }
    }

    private void Index(HistoryKey key, ProcessedReading reading)
    {
        if (!_bySensor.TryGetValue(reading.DeviceId, out var tree))
        {
            tree = new AvlTree<HistoryKey, ProcessedReading>();
            _bySensor[reading.DeviceId] = tree;
        }

        tree.Insert(key, reading);
    }

    private void Unindex(HistoryKey key, ProcessedReading reading)
    {
        if (!_bySensor.TryGetValue(reading.DeviceId, out var tree))
        {
            return;
        }

        tree.Remove(key);
        if (tree.Count == 0)
        {
            _bySensor.Remove(reading.DeviceId);
        }
    }
}
