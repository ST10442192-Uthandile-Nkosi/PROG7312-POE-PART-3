using System.Diagnostics;
using SmartX.Api.Services;
using SmartX.Shared.Collections;
using SmartX.Shared.Mesh;
using SmartX.Shared.Runtime;
using SmartX.Shared.Sensors;

namespace SmartX.Tests;

/// <summary>
/// Correctness checks on stated dataset sizes. Elapsed times are written for this run only.
/// Nothing here fails because a stopwatch was slow or fast.
/// </summary>
public sealed class AlgorithmPerformanceTests
{
    public AlgorithmPerformanceTests()
    {
        Console.WriteLine(
            $"runtime={Environment.Version} os={Environment.OSVersion} processors={Environment.ProcessorCount}");
    }

    [Fact]
    public void Avl_height_stays_within_the_balance_bound_for_sorted_inserts()
    {
        const int count = 8192;
        var tree = new AvlTree<int, int>();
        var insert = Stopwatch.StartNew();
        for (var key = 1; key <= count; key++)
        {
            tree.Insert(key, key);
            if (key % 1024 == 0)
            {
                Assert.True(tree.CheckInvariants());
                Assert.InRange(tree.Height, 1, MaxAvlHeight(key));
            }
        }

        insert.Stop();
        var range = Stopwatch.StartNew();
        var window = tree.RangeInclusive(2000, 3999);
        range.Stop();

        Assert.Equal(2000, window.Count);
        Assert.Equal(2000, window[0]);
        Assert.Equal(3999, window[^1]);
        Assert.True(tree.Height < 2 * Math.Log2(count));
        Console.WriteLine(FormattableString.Invariant(
            $"AVL sorted insert n={count} height={tree.Height} maxHeight={MaxAvlHeight(count)} insertMs={insert.Elapsed.TotalMilliseconds:0.00} rangeMs={range.Elapsed.TotalMilliseconds:0.00} rangeCount={window.Count}"));
    }

    [Fact]
    public void History_range_on_a_long_chronology_returns_the_requested_window()
    {
        const int count = 6000;
        var store = new TelemetryHistoryStore(count);
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var insert = Stopwatch.StartNew();
        for (var index = 0; index < count; index++)
        {
            store.Add(Reading("24:6F:28:AA:00:01", start.AddSeconds(index), index));
        }

        insert.Stop();

        var from = start.AddSeconds(1000);
        var to = start.AddSeconds(1499);
        var query = Stopwatch.StartNew();
        var rows = store.Range("24-6F-28-AA-00-01", from, to);
        query.Stop();

        Assert.Equal(500, rows.Count);
        Assert.Equal(from, rows[0].TimestampUtc);
        Assert.Equal(to, rows[^1].TimestampUtc);
        Assert.Equal("1000", rows[0].DisplayValue);
        Assert.Equal("1499", rows[^1].DisplayValue);
        Console.WriteLine(FormattableString.Invariant(
            $"history n={count} insertMs={insert.Elapsed.TotalMilliseconds:0.00} rangeMs={query.Elapsed.TotalMilliseconds:0.00} rangeCount={rows.Count}"));
    }

    [Fact]
    public void History_eviction_drops_the_old_end_of_a_long_chronology()
    {
        var store = new TelemetryHistoryStore(1500);
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < 4000; index++)
        {
            store.Add(Reading("24:6F:28:AA:00:01", start.AddSeconds(index), index));
        }

        Assert.Equal(1500, store.Count);
        Assert.Empty(store.Range("24:6F:28:AA:00:01", start, start.AddSeconds(2499)));
        var kept = store.Range("24:6F:28:AA:00:01", start.AddSeconds(2500), start.AddSeconds(3999));
        Assert.Equal(1500, kept.Count);
        Assert.Equal("2500", kept[0].DisplayValue);
        Assert.Equal("3999", kept[^1].DisplayValue);
    }

    [Fact]
    public void Traversal_and_spanning_tree_match_a_known_path_and_a_known_weighted_graph()
    {
        const int count = 2000;
        var line = new WeightedGraph();
        for (var index = 0; index < count; index++)
        {
            line.AddVertex(index.ToString("D4"));
        }

        for (var index = 1; index < count; index++)
        {
            Assert.True(line.AddEdge((index - 1).ToString("D4"), index.ToString("D4"), 1));
        }

        var bfsTime = Stopwatch.StartNew();
        var bfs = line.BreadthFirst("0000");
        bfsTime.Stop();
        var dfs = line.DepthFirst("0000");
        var treeTime = Stopwatch.StartNew();
        var forest = line.MinimumSpanningForest();
        treeTime.Stop();
        var route = line.LeastCost("0000", "1999");

        Assert.Equal(count, bfs.Order.Count);
        Assert.Equal(count, bfs.Order.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(count, dfs.Order.Count);
        Assert.Empty(bfs.Unreachable);
        Assert.True(forest.Connected);
        Assert.Equal(count - 1, forest.Edges.Count);
        Assert.Equal(count - 1, forest.TotalCost);
        Assert.True(route.Reachable);
        Assert.Equal(count - 1, route.Cost);

        var known = new WeightedGraph();
        foreach (var id in new[] { "A", "B", "C", "D" })
        {
            known.AddVertex(id);
        }

        known.AddEdge("A", "B", 1);
        known.AddEdge("B", "C", 2);
        known.AddEdge("A", "C", 4);
        known.AddEdge("C", "D", 3);
        known.AddEdge("B", "D", 6);
        var textbook = known.MinimumSpanningForest();
        Assert.Equal(6, textbook.TotalCost);
        Assert.True(textbook.Connected);
        Assert.Equal(3, textbook.Edges.Count);
        Assert.DoesNotContain(textbook.Edges, edge => edge.Cost == 4 || edge.Cost == 6);

        var shortcut = new WeightedGraph();
        foreach (var id in new[] { "G", "A", "B" })
        {
            shortcut.AddVertex(id);
        }

        shortcut.AddEdge("G", "A", 1);
        shortcut.AddEdge("A", "B", 1);
        shortcut.AddEdge("G", "B", 5);
        Assert.Equal(5, shortcut.FewestHops("G", "B").Cost);
        Assert.Equal(2, shortcut.LeastCost("G", "B").Cost);
        Assert.Equal(2, shortcut.MinimumSpanningForest().TotalCost);
        Assert.NotEqual(shortcut.FewestHops("G", "B").Nodes, shortcut.LeastCost("G", "B").Nodes);

        Console.WriteLine(FormattableString.Invariant(
            $"path graph n={count} edges={count - 1} bfsMs={bfsTime.Elapsed.TotalMilliseconds:0.00} mstMs={treeTime.Elapsed.TotalMilliseconds:0.00} dijkstraCost={route.Cost}"));
    }

    [Fact]
    public void Campus_failure_recalculation_keeps_the_alternate_route()
    {
        var mesh = SeededMesh();
        var before = Stopwatch.StartNew();
        var open = mesh.Route("3C:71:BF:22:10:AA");
        before.Stop();
        Assert.NotNull(open);
        Assert.Equal(15, open.LeastCost);
        Assert.Equal(22, open.FewestHopCost);

        var failed = Stopwatch.StartNew();
        Assert.True(mesh.TrySetOnline("30:AE:A4:10:01:03", online: false));
        var during = mesh.Route("3C:71:BF:22:10:AA");
        failed.Stop();
        Assert.NotNull(during);
        Assert.True(during.Reachable);
        Assert.Equal(16, during.LeastCost);
        Assert.DoesNotContain("30:AE:A4:10:01:03", during.LeastCostPath);

        Assert.True(mesh.TrySetOnline("30:AE:A4:10:01:03", online: true));
        Assert.Equal(15, mesh.Route("3C:71:BF:22:10:AA")!.LeastCost);
        Console.WriteLine(FormattableString.Invariant(
            $"campus vertices=11 catalogueEdges=16 routeMs={before.Elapsed.TotalMilliseconds:0.00} failureRecalcMs={failed.Elapsed.TotalMilliseconds:0.00}"));
    }

    private static int MaxAvlHeight(int count)
    {
        var height = 1;
        while (MinimumNodes(height) < count)
        {
            height++;
        }

        return height;
    }

    /// <summary>Fewest nodes in an AVL tree of this height: N(h) = 1 + N(h-1) + N(h-2).</summary>
    private static int MinimumNodes(int height)
    {
        if (height <= 0)
        {
            return 0;
        }

        var previous = 0;
        var current = 1;
        for (var level = 2; level <= height; level++)
        {
            var next = 1 + current + previous;
            previous = current;
            current = next;
        }

        return current;
    }

    private static TelemetryWorkItem Reading(string mac, DateTimeOffset at, int value) => new()
    {
        DeviceId = mac,
        TimestampUtc = at,
        ValueKind = "int",
        NumericValue = value,
        MetricName = "active_power",
        Unit = "W"
    };

    private static MeshTopologyService SeededMesh()
    {
        var registry = new SensorRegistry();
        registry.Seed(MeshTopologyService.Catalogue
            .SelectMany(link => new[] { link.From, link.To })
            .Where(id => id != WeightedGraph.GatewayId)
            .Distinct(StringComparer.Ordinal)
            .Select(mac => new SensorProfile
            {
                MacAddress = mac,
                DisplayName = mac,
                Location = "seed",
                Category = SensorCategory.Environmental
            }));
        return new MeshTopologyService(registry);
    }
}
