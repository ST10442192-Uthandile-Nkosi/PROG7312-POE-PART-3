using SmartX.Api.Services;
using SmartX.Shared.Sensors;
using SmartX.Shared.Telemetry;

namespace SmartX.Tests;

public sealed class CouplingAndHealthTests
{
    private const string Zone = "Facility A -> Zone 1 -> Sub-Zone B";

    [Fact]
    public void Low_conductivity_does_not_raise_a_soil_moisture_alarm()
    {
        var h = RuntimeHarness.Create();
        h.Register(TestMacs.Moisture, SensorCategory.Environmental, "NFT-Gutter-A moisture", Zone);
        h.Register(TestMacs.Valve, SensorCategory.Actuator, "Irrigation solenoid", Zone);
        h.Ingestion.IngestFloat(Conductivity(TestMacs.Moisture, 1.6f));
        h.Ingestion.IngestBool(TestPackets.Bool(TestMacs.Valve, false));

        var alarms = h.Ingestion.DetectCouplingAlarms(h.Registry.All());
        var report = Report(h);

        Assert.DoesNotContain(alarms, a => a.Code == "IRR-DEADLOCK");
        Assert.Empty(report.Alarms);
        Assert.Equal(0, report.FractureCount);
        Assert.Equal("nominal", Node(report, TestMacs.Moisture).Status);
        Assert.Equal("Watch", report.StewardRank);
    }

    [Fact]
    public void Low_soil_moisture_with_a_closed_valve_still_raises_the_irrigation_alarm()
    {
        var h = RuntimeHarness.Create();
        h.Register(TestMacs.Moisture, SensorCategory.Environmental, "Reservoir EC probe", Zone);
        h.Register(TestMacs.Valve, SensorCategory.Actuator, "Irrigation solenoid", Zone);
        h.Ingestion.IngestFloat(TestPackets.Float(TestMacs.Moisture, 12f));
        h.Ingestion.IngestBool(TestPackets.Bool(TestMacs.Valve, false));

        var alarms = h.Ingestion.DetectCouplingAlarms(h.Registry.All());
        var report = Report(h);

        var alarm = Assert.Single(alarms);
        Assert.Equal("IRR-DEADLOCK", alarm.Code);
        Assert.Equal("Dry bed, valve closed", alarm.Title);
        Assert.Contains("12.0% VWC", alarm.Detail, StringComparison.Ordinal);
        Assert.Equal("fracture", Node(report, TestMacs.Moisture).Status);
        Assert.Equal(1, report.FractureCount);
        Assert.Equal(alarm.Code, Assert.Single(report.Alarms).Code);
        Assert.Equal("Alert", report.StewardRank);
        Assert.Equal("Coupling alarms are active and some nodes are outside safe bands.", report.Narrative);
    }

    [Fact]
    public void In_band_soil_moisture_does_not_raise_a_coupling_alarm()
    {
        var h = RuntimeHarness.Create();
        h.Register(TestMacs.Moisture, SensorCategory.Environmental, "NFT-Gutter-A moisture", Zone);
        h.Register(TestMacs.Valve, SensorCategory.Actuator, "Irrigation solenoid", Zone);
        h.Ingestion.IngestFloat(TestPackets.Float(TestMacs.Moisture, 45f));
        h.Ingestion.IngestBool(TestPackets.Bool(TestMacs.Valve, false));

        var report = Report(h);

        Assert.Empty(h.Ingestion.DetectCouplingAlarms(h.Registry.All()));
        Assert.Empty(report.Alarms);
        Assert.Equal(0, report.FractureCount);
        Assert.Equal("nominal", Node(report, TestMacs.Moisture).Status);
        Assert.Equal("All nodes inside safe bands.", report.Narrative);
    }

    [Fact]
    public void Fracture_count_is_not_the_coupling_alarm_count()
    {
        var dryValveOpen = RuntimeHarness.Create();
        dryValveOpen.Register(TestMacs.Moisture, SensorCategory.Environmental, "NFT-Gutter-A moisture", Zone);
        dryValveOpen.Register(TestMacs.Valve, SensorCategory.Actuator, "Irrigation solenoid", Zone);
        dryValveOpen.Ingestion.IngestFloat(TestPackets.Float(TestMacs.Moisture, 12f));
        dryValveOpen.Ingestion.IngestBool(TestPackets.Bool(TestMacs.Valve, true));

        var fractured = Report(dryValveOpen);
        Assert.Equal(1, fractured.FractureCount);
        Assert.Empty(fractured.Alarms);
        Assert.Equal("Alert", fractured.StewardRank);
        Assert.Equal("Some nodes are outside safe bands.", fractured.Narrative);

        var overloaded = RuntimeHarness.Create();
        overloaded.Register(TestMacs.Clamp, SensorCategory.PowerConsumption, "Bay-1 CT clamp", "Facility B -> Zone 1 -> Transformer Bay");
        overloaded.Register("30:AE:A4:AA:00:04", SensorCategory.PowerConsumption, "Bay-2 CT clamp", "Facility B -> Zone 1 -> Transformer Bay");
        overloaded.Ingestion.IngestInt(TestPackets.Int(TestMacs.Clamp, 2000));
        overloaded.Ingestion.IngestInt(TestPackets.Int("30:AE:A4:AA:00:04", 1800));

        var couplingOnly = Report(overloaded);
        Assert.Equal(0, couplingOnly.FractureCount);
        Assert.Equal("GRID-OVERLOAD", Assert.Single(couplingOnly.Alarms).Code);
        Assert.Equal("Alert", couplingOnly.StewardRank);
        Assert.Equal("Coupling alarms need attention.", couplingOnly.Narrative);
    }

    [Fact]
    public void Rank_is_stable_only_after_a_long_in_range_streak_with_no_faults()
    {
        var h = RuntimeHarness.Create(queueCapacity: 128);
        h.Register(TestMacs.Moisture, SensorCategory.Environmental, "NFT-Gutter-A moisture", Zone);
        for (var i = 0; i < 80; i++)
        {
            h.Ingestion.IngestFloat(TestPackets.Float(TestMacs.Moisture, 50f));
        }

        var report = Report(h);
        Assert.Equal(80, report.ValidPacketStreak);
        Assert.Equal(0, report.FractureCount);
        Assert.Empty(report.Alarms);
        Assert.Equal("Stable", report.StewardRank);
        Assert.Equal("All nodes inside safe bands.", report.Narrative);
        Assert.Equal("Watch", GatewayIntegrityService.Rank(0, 0, 79));
        Assert.Equal("Alert", GatewayIntegrityService.Rank(1, 0, 80));
        Assert.Equal("Alert", GatewayIntegrityService.Rank(0, 1, 80));
    }

    private static GatewayIntegrityReport Report(RuntimeHarness harness) =>
        new GatewayIntegrityService(harness.Registry, harness.Ingestion).Build();

    private static MeshNodeView Node(GatewayIntegrityReport report, string mac) =>
        Assert.Single(report.Nodes, n => n.DeviceId.Equals(mac, StringComparison.OrdinalIgnoreCase));

    private static TelemetryPacket<float> Conductivity(string mac, float milliSiemens) => new()
    {
        DeviceId = mac,
        Timestamp = DateTimeOffset.UnixEpoch.AddHours(2),
        Value = milliSiemens,
        MetricName = "nutrient_ec",
        Unit = "mS/cm"
    };
}
