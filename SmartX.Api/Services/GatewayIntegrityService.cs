using System.Globalization;
using SmartX.Shared.Sensors;
using SmartX.Shared.Telemetry;

namespace SmartX.Api.Services;

public sealed class GatewayIntegrityService
{
    private readonly SensorRegistry _sensors;
    private readonly TelemetryIngestionService _telemetry;

    public GatewayIntegrityService(SensorRegistry sensors, TelemetryIngestionService telemetry)
    {
        _sensors = sensors;
        _telemetry = telemetry;
    }

    public GatewayIntegrityReport Build()
    {
        var sensors = _sensors.All();
        var nodes = new List<MeshNodeView>();
        var i = 0;
        foreach (var sensor in sensors)
        {
            var angle = (i / (double)Math.Max(sensors.Count, 1)) * Math.PI * 2;
            var radius = sensor.Category switch
            {
                SensorCategory.Actuator => 78,
                SensorCategory.PowerConsumption => 58,
                _ => 38
            };

            _telemetry.TryGetLatest(sensor.MacAddress, out var latest);
            var status = Classify(sensor, latest);
            nodes.Add(new MeshNodeView
            {
                DeviceId = sensor.MacAddress,
                DisplayName = sensor.DisplayName,
                Category = sensor.Category,
                Location = sensor.Location,
                X = 120 + Math.Cos(angle) * radius,
                Y = 120 + Math.Sin(angle) * radius,
                Status = status.status,
                LatestValue = latest?.DisplayValue ?? "awaiting",
                Alert = status.alert
            });
            i++;
        }

        var alarms = _telemetry.DetectCouplingAlarms(sensors);
        var streak = _telemetry.ValidPacketStreak;
        var fractureCount = nodes.Count(n => n.Status == "fracture");
        return new GatewayIntegrityReport
        {
            Nodes = nodes,
            Alarms = alarms,
            StewardRank = Rank(alarms.Count, fractureCount, streak),
            ValidPacketStreak = streak,
            TotalPackets = _telemetry.TotalPackets,
            FractureCount = fractureCount,
            Narrative = Describe(alarms.Count, fractureCount)
        };
    }

    /// <summary>
    /// Alert: one or more coupling alarms, or one or more fractured nodes.
    /// Stable: neither, and at least 80 consecutive in-range packets.
    /// Watch: neither, while that streak is still below 80.
    /// </summary>
    public static string Rank(int couplingAlarmCount, int fractureCount, int validPacketStreak)
    {
        if (couplingAlarmCount > 0 || fractureCount > 0)
        {
            return "Alert";
        }

        return validPacketStreak >= 80 ? "Stable" : "Watch";
    }

    /// <summary>
    /// Fracture count is nodes outside a sensor band. Coupling alarms are separate conditions
    /// such as a dry bed with a closed valve, or a feeder overload.
    /// </summary>
    public static string Describe(int couplingAlarmCount, int fractureCount)
    {
        if (couplingAlarmCount == 0 && fractureCount == 0)
        {
            return "All nodes inside safe bands.";
        }

        if (couplingAlarmCount > 0 && fractureCount > 0)
        {
            return "Coupling alarms are active and some nodes are outside safe bands.";
        }

        return fractureCount > 0
            ? "Some nodes are outside safe bands."
            : "Coupling alarms need attention.";
    }

    private static (string status, string alert) Classify(SensorProfile sensor, TelemetrySnapshot? latest)
    {
        if (latest is null) return ("silent", "No packet since last gateway sweep.");

        if (sensor.Category == SensorCategory.Environmental &&
            SoilMoistureReading.IsVolumetricWaterContent(latest.Unit) &&
            double.TryParse(latest.DisplayValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var vwc) &&
            (vwc < ThresholdEvaluator.VwcLow || vwc > ThresholdEvaluator.VwcHigh))
        {
            return ("fracture", $"VWC {vwc:0.0}% outside hydroponic comfort band 28–82%.");
        }

        if (sensor.Category == SensorCategory.PowerConsumption &&
            int.TryParse(latest.DisplayValue, out var watts) && watts > 2800)
        {
            return ("fracture", $"Clamp {watts} W approaching CT saturation.");
        }

        return ("nominal", string.Empty);
    }
}
