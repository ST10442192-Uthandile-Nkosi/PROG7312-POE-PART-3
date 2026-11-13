using SmartX.Api.Services;
using SmartX.Shared.Sensors;
using SmartX.Shared.Telemetry;

namespace SmartX.Tests;

public sealed class GridOverloadRecoveryTests
{
    private const string Bay = "Facility B -> Zone 1 -> Transformer Bay";
    private const string Bay1 = "30:AE:A4:AA:00:03";
    private const string Bay2 = "30:AE:A4:AA:00:04";

    [Fact]
    public void Grid_overload_is_raised_once_cleared_on_recovery_and_raised_again()
    {
        var h = RuntimeHarness.Create();
        h.Register(Bay1, SensorCategory.PowerConsumption, "Bay-1 CT clamp", Bay);
        h.Register(Bay2, SensorCategory.PowerConsumption, "Bay-2 CT clamp", Bay);

        // 1800 + 1700 = 3500, which is the limit and not an overload.
        Ingest(h, Bay1, 1800, 1);
        Ingest(h, Bay2, 1700, 2);
        AssertNoGrid(h);

        // One watt over the same limit.
        Ingest(h, Bay1, 1801, 3);
        var key = Assert.Single(h.Faults.Snapshot(), fault => fault.StartsWith("GRID-OVERLOAD:", StringComparison.OrdinalIgnoreCase));
        Assert.Equal($"GRID-OVERLOAD:{Bay1}", key);
        Assert.True(h.Devices.TryGet(Bay1, out var alerting));
        Assert.True(alerting.HasActiveAlert);
        Assert.Contains(Tips(h, 3), tip => tip.Id.StartsWith("coupling-grid:", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("GRID-OVERLOAD", Assert.Single(Report(h).Alarms).Code);
        Assert.Equal("Alert", Report(h).StewardRank);

        // A further reading that is still over the limit must not create a second fault.
        Ingest(h, Bay2, 1800, 4);
        Assert.Single(h.Faults.Snapshot(), fault => fault.StartsWith("GRID-OVERLOAD:", StringComparison.OrdinalIgnoreCase));

        // Recovery is applied on the second meter, which is not the fault key.
        // 1801 + 1699 = 3500.
        Ingest(h, Bay2, 1699, 5);
        AssertNoGrid(h);
        Assert.True(h.Devices.TryGet(Bay1, out var recovered));
        Assert.False(recovered.HasActiveAlert);
        Assert.Contains(h.Journal.Snapshot(), entry => entry.Kind == "CLEAR" && entry.Detail.Contains(Bay1, StringComparison.OrdinalIgnoreCase));

        Ingest(h, Bay1, 1900, 6);
        Assert.Single(h.Faults.Snapshot(), fault => fault.StartsWith("GRID-OVERLOAD:", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(Tips(h, 6), tip => tip.Id.StartsWith("coupling-grid:", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(Report(h).Alarms, alarm => alarm.Code == "GRID-OVERLOAD");
    }

    [Fact]
    public void A_single_hot_clamp_does_not_raise_grid_overload()
    {
        var h = RuntimeHarness.Create();
        h.Register(Bay1, SensorCategory.PowerConsumption, "Bay-1 CT clamp", Bay);
        Ingest(h, Bay1, 4000, 1);

        Assert.Contains(h.Faults.Snapshot(), fault => fault.StartsWith("POWER-CRITICAL:", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(h.Faults.Snapshot(), fault => fault.StartsWith("GRID-OVERLOAD:", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(Report(h).Alarms, alarm => alarm.Code == "GRID-OVERLOAD");
    }

    private static void Ingest(RuntimeHarness harness, string mac, int watts, int hour)
    {
        var packet = TestPackets.Int(mac, watts);
        packet.Timestamp = DateTimeOffset.UnixEpoch.AddHours(hour);
        harness.Ingestion.IngestInt(packet);
        harness.Engine.Drain();
    }

    private static void AssertNoGrid(RuntimeHarness harness)
    {
        Assert.DoesNotContain(harness.Faults.Snapshot(), fault => fault.StartsWith("GRID-OVERLOAD:", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(Tips(harness, 6), tip => tip.Id.StartsWith("coupling-grid:", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(Report(harness).Alarms, alarm => alarm.Code == "GRID-OVERLOAD");
    }

    private static IReadOnlyList<SmartX.Shared.Runtime.RecommendationDto> Tips(RuntimeHarness harness, int hour) =>
        new RecommendationService(harness.Devices, harness.History, harness.Faults, harness.Undo, harness.Thresholds)
            .Evaluate(DateTimeOffset.UnixEpoch.AddHours(hour));

    private static GatewayIntegrityReport Report(RuntimeHarness harness) =>
        new GatewayIntegrityService(harness.Registry, harness.Ingestion).Build();
}
