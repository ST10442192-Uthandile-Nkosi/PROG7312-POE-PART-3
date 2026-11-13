using SmartX.Api.Services;
using SmartX.Shared.Collections;
using SmartX.Shared.Runtime;
using SmartX.Shared.Sensors;
using SmartX.Shared.Telemetry;

namespace SmartX.Tests;

public sealed class AvlHistoryTests
{
    [Fact]
    public void Empty_tree_has_height_zero_and_rejects_queries()
    {
        var tree = new AvlTree<int, string>();
        Assert.Equal(0, tree.Count);
        Assert.Equal(0, tree.Height);
        Assert.True(tree.CheckInvariants());
        Assert.Empty(tree.InOrder());
        Assert.Empty(tree.RangeInclusive(0, 10));
        Assert.False(tree.Remove(1));
        Assert.False(tree.TryGetRoot(out _, out _));
    }

    [Fact]
    public void Left_left_insert_rotates_right()
    {
        var tree = Insert(30, 20, 10);
        AssertRoot(tree, 20);
        Assert.Equal(new[] { 10, 20, 30 }, tree.KeysInOrder());
        Assert.Equal(2, tree.Height);
        Assert.True(tree.CheckInvariants());
    }

    [Fact]
    public void Right_right_insert_rotates_left()
    {
        var tree = Insert(10, 20, 30);
        AssertRoot(tree, 20);
        Assert.Equal(new[] { 10, 20, 30 }, tree.KeysInOrder());
        Assert.Equal(2, tree.Height);
        Assert.True(tree.CheckInvariants());
    }

    [Fact]
    public void Left_right_insert_uses_double_rotation()
    {
        var tree = Insert(30, 10, 20);
        AssertRoot(tree, 20);
        Assert.Equal(new[] { 10, 20, 30 }, tree.KeysInOrder());
        Assert.True(tree.CheckInvariants());
    }

    [Fact]
    public void Right_left_insert_uses_double_rotation()
    {
        var tree = Insert(10, 30, 20);
        AssertRoot(tree, 20);
        Assert.Equal(new[] { 10, 20, 30 }, tree.KeysInOrder());
        Assert.True(tree.CheckInvariants());
    }

    [Fact]
    public void Delete_of_the_right_child_rotates_right()
    {
        var tree = Insert(3, 2, 4, 1);
        Assert.True(tree.Remove(4));
        AssertRoot(tree, 2);
        Assert.Equal(new[] { 1, 2, 3 }, tree.KeysInOrder());
        Assert.True(tree.CheckInvariants());
    }

    [Fact]
    public void Delete_of_the_left_child_rotates_left()
    {
        var tree = Insert(2, 1, 3, 4);
        Assert.True(tree.Remove(1));
        AssertRoot(tree, 3);
        Assert.Equal(new[] { 2, 3, 4 }, tree.KeysInOrder());
        Assert.True(tree.CheckInvariants());
    }

    [Fact]
    public void Delete_uses_left_right_rotation()
    {
        var tree = Insert(5, 3, 6, 4);
        Assert.True(tree.Remove(6));
        AssertRoot(tree, 4);
        Assert.Equal(new[] { 3, 4, 5 }, tree.KeysInOrder());
        Assert.True(tree.CheckInvariants());
    }

    [Fact]
    public void Delete_uses_right_left_rotation()
    {
        var tree = Insert(3, 5, 2, 4);
        Assert.True(tree.Remove(2));
        AssertRoot(tree, 4);
        Assert.Equal(new[] { 3, 4, 5 }, tree.KeysInOrder());
        Assert.True(tree.CheckInvariants());
    }

    [Fact]
    public void In_order_walk_is_sorted_after_shuffled_inserts()
    {
        var tree = new AvlTree<int, int>();
        foreach (var key in new[] { 4, 1, 7, 3, 9, 2, 8, 5, 0, 6 })
        {
            tree.Insert(key, key);
            Assert.True(tree.CheckInvariants());
        }

        Assert.Equal(Enumerable.Range(0, 10), tree.KeysInOrder());
    }

    [Fact]
    public void Duplicate_key_replaces_the_value_and_keeps_balance()
    {
        var tree = Insert(2, 1, 3);
        tree.Insert(2, 99);
        Assert.Equal(3, tree.Count);
        Assert.Equal(new[] { 1, 99, 3 }, tree.InOrder());
        Assert.True(tree.CheckInvariants());
    }

    [Fact]
    public void Inclusive_range_keeps_both_boundaries_and_rejects_an_inverted_span()
    {
        var tree = new AvlTree<int, int>();
        for (var key = 1; key <= 10; key++)
        {
            tree.Insert(key, key);
        }

        Assert.Equal(Enumerable.Range(3, 5), tree.RangeInclusive(3, 7));
        Assert.Equal(new[] { 5 }, tree.RangeInclusive(5, 5));
        Assert.Empty(tree.RangeInclusive(8, 3));
        Assert.Empty(tree.RangeInclusive(11, 20));
    }

    [Fact]
    public void Chronological_inserts_stay_balanced_for_a_long_history()
    {
        var tree = new AvlTree<int, int>();
        for (var key = 0; key < 1000; key++)
        {
            tree.Insert(key, key);
        }

        Assert.Equal(1000, tree.Count);
        Assert.True(tree.Height <= 16);
        Assert.True(tree.TryGetRoot(out var root, out _));
        Assert.NotEqual(0, root);
        Assert.NotEqual(999, root);
        Assert.True(tree.CheckInvariants());

        var slice = tree.RangeInclusive(100, 199);
        Assert.Equal(100, slice.Count);
        Assert.Equal(100, slice[0]);
        Assert.Equal(199, slice[^1]);

        for (var key = 1; key < 1000; key += 2)
        {
            Assert.True(tree.Remove(key));
        }

        Assert.Equal(500, tree.Count);
        Assert.True(tree.CheckInvariants());
        Assert.Equal(Enumerable.Range(0, 500).Select(i => i * 2), tree.KeysInOrder());
    }

    [Fact]
    public void Deleting_every_node_preserves_height_and_balance()
    {
        var tree = new AvlTree<int, int>();
        for (var key = 1; key <= 31; key++)
        {
            tree.Insert(key, key);
        }

        for (var key = 31; key >= 1; key--)
        {
            Assert.True(tree.Remove(key));
            Assert.True(tree.CheckInvariants());
        }

        Assert.Equal(0, tree.Count);
        Assert.Equal(0, tree.Height);
    }

    [Fact]
    public void Identical_timestamps_stay_distinct_and_inclusive()
    {
        var store = new TelemetryHistoryStore(8);
        var stamp = DateTimeOffset.UnixEpoch.AddHours(2);
        store.Add(Reading(TestMacs.Moisture, stamp, 11));
        store.Add(Reading(TestMacs.Moisture, stamp, 12));

        var rows = store.Range(TestMacs.Moisture, stamp, stamp);
        Assert.Equal(new[] { "11", "12" }, rows.Select(r => r.DisplayValue).ToArray());
        Assert.Empty(store.Range(TestMacs.Moisture, stamp.AddTicks(1), stamp.AddMinutes(1)));
        Assert.Empty(store.Range(TestMacs.Moisture, stamp.AddMinutes(-5), stamp.AddTicks(-1)));
        Assert.Empty(store.Range(TestMacs.Moisture, stamp.AddMinutes(1), stamp));
    }

    [Fact]
    public void Range_queries_do_not_cross_sensors()
    {
        var store = new TelemetryHistoryStore(8);
        var start = DateTimeOffset.UnixEpoch.AddHours(1);
        store.Add(Reading(TestMacs.Moisture, start, 40));
        store.Add(Reading(TestMacs.MoistureAlt, start.AddMinutes(1), 70));

        var moisture = store.Range("24-6f-28-aa-00-01", start.AddMinutes(-1), start.AddMinutes(5));
        var only = Assert.Single(moisture);
        Assert.Equal(TestMacs.Moisture, only.DeviceId);
        Assert.Equal("40", only.DisplayValue);

        var other = Assert.Single(store.Range(TestMacs.MoistureAlt, start, start.AddMinutes(1)));
        Assert.Equal("70", other.DisplayValue);
        Assert.Empty(store.Range(TestMacs.Valve, start, start.AddHours(1)));
    }

    [Fact]
    public void Evicted_readings_are_removed_from_the_sensor_index()
    {
        var store = new TelemetryHistoryStore(3);
        var start = DateTimeOffset.UnixEpoch;
        for (var i = 0; i < 6; i++)
        {
            store.Add(Reading(TestMacs.Moisture, start.AddSeconds(i), i));
        }

        Assert.Equal(3, store.Count);
        var retained = store.Range(TestMacs.Moisture, DateTimeOffset.MinValue, DateTimeOffset.MaxValue);
        Assert.Equal(new[] { "3", "4", "5" }, retained.Select(r => r.DisplayValue).ToArray());
        Assert.Equal(new[] { "5", "4", "3" }, store.Latest(10).Select(r => r.DisplayValue).ToArray());
    }

    [Fact]
    public void Global_capacity_evicts_the_oldest_sensor_row()
    {
        var store = new TelemetryHistoryStore(2);
        var start = DateTimeOffset.UnixEpoch;
        store.Add(Reading(TestMacs.Moisture, start, 1));
        store.Add(Reading(TestMacs.MoistureAlt, start.AddSeconds(1), 2));
        store.Add(Reading(TestMacs.Moisture, start.AddSeconds(2), 3));

        Assert.Empty(store.Range(TestMacs.Moisture, start, start));
        var kept = Assert.Single(store.Range(TestMacs.Moisture, DateTimeOffset.MinValue, DateTimeOffset.MaxValue));
        Assert.Equal("3", kept.DisplayValue);
        var other = Assert.Single(store.Range(TestMacs.MoistureAlt, DateTimeOffset.MinValue, DateTimeOffset.MaxValue));
        Assert.Equal("2", other.DisplayValue);
    }

    [Fact]
    public void Chronological_history_range_returns_the_requested_slice_in_order()
    {
        var store = new TelemetryHistoryStore(2000);
        var start = DateTimeOffset.UnixEpoch;
        for (var i = 0; i < 1200; i++)
        {
            store.Add(Reading(TestMacs.Moisture, start.AddSeconds(i), i));
        }

        var slice = store.Range(TestMacs.Moisture, start.AddSeconds(100), start.AddSeconds(199));
        Assert.Equal(100, slice.Count);
        Assert.Equal("100", slice[0].DisplayValue);
        Assert.Equal("199", slice[^1].DisplayValue);
        for (var i = 1; i < slice.Count; i++)
        {
            Assert.True(slice[i].TimestampUtc >= slice[i - 1].TimestampUtc);
        }
    }

    [Fact]
    public void Processing_engine_makes_a_reading_queryable_by_timestamp()
    {
        var harness = RuntimeHarness.Create();
        harness.Register(TestMacs.Moisture, SensorCategory.Environmental, "bed", "Facility A -> Zone 1 -> Sub-Zone B");
        var at = DateTimeOffset.UnixEpoch.AddHours(5);
        harness.Ingestion.IngestFloat(new TelemetryPacket<float>
        {
            DeviceId = "24-6f-28-aa-00-01",
            Timestamp = at,
            Value = 44.5f,
            MetricName = "soil_moisture",
            Unit = "%VWC"
        });
        harness.Engine.Drain();

        var row = Assert.Single(harness.History.Range(TestMacs.Moisture, at, at));
        Assert.Equal("44.5", row.DisplayValue);
        Assert.Equal("%VWC", row.Unit);
        Assert.Equal(TestMacs.Moisture, row.DeviceId);
        Assert.Equal(at, row.TimestampUtc);
    }

    private static AvlTree<int, int> Insert(params int[] keys)
    {
        var tree = new AvlTree<int, int>();
        foreach (var key in keys)
        {
            tree.Insert(key, key);
        }

        return tree;
    }

    private static void AssertRoot(AvlTree<int, int> tree, int expected)
    {
        Assert.True(tree.TryGetRoot(out var key, out var value));
        Assert.Equal(expected, key);
        Assert.Equal(expected, value);
    }

    private static TelemetryWorkItem Reading(string mac, DateTimeOffset at, double value) => new()
    {
        DeviceId = mac,
        TimestampUtc = at,
        ValueKind = "float",
        NumericValue = value,
        MetricName = "soil_moisture",
        Unit = "%VWC"
    };
}
