using SmartX.Api.Services;
using SmartX.Shared.Mesh;
using SmartX.Shared.Sensors;

namespace SmartX.Tests;

public sealed class MeshGraphTests
{
    [Fact]
    public void Vertices_and_edges_reject_duplicates_and_negative_costs()
    {
        var graph = new WeightedGraph();
        Assert.True(graph.AddVertex("A"));
        Assert.False(graph.AddVertex("A"));
        Assert.True(graph.AddVertex("B"));
        Assert.True(graph.AddEdge("A", "B", 4));
        Assert.False(graph.AddEdge("B", "A", 9));
        Assert.False(graph.AddEdge("A", "A", 1));
        Assert.Equal(4, Assert.Single(graph.Edges).Cost);
        Assert.Throws<ArgumentOutOfRangeException>(() => graph.AddEdge("A", "B", -1));
    }

    [Fact]
    public void Breadth_first_and_depth_first_orders_differ_and_skip_cycles()
    {
        var graph = Connected("A", "B", "C", "D", "E");
        Add(graph, "A", "B", 1);
        Add(graph, "A", "C", 1);
        Add(graph, "B", "D", 1);
        Add(graph, "C", "E", 1);
        Assert.True(graph.AddEdge("B", "C", 2));

        var breadth = graph.BreadthFirst("A");
        Assert.Equal(["A", "B", "C", "D", "E"], breadth.Order);
        Assert.Equal(breadth.Order.Count, breadth.Order.Distinct().Count());
        Assert.Equal(["A", "B", "C", "E", "D"], graph.DepthFirst("A").Order);
        Assert.Empty(breadth.Unreachable);
    }

    [Fact]
    public void Disconnected_graph_reports_the_unreached_component()
    {
        var graph = Connected("A", "B", "C");
        Add(graph, "A", "B", 2);

        var bfs = graph.BreadthFirst("A");
        Assert.Equal(["A", "B"], bfs.Order);
        Assert.Equal(["C"], bfs.Unreachable);
        Assert.False(graph.FewestHops("A", "C").Reachable);

        var forest = graph.MinimumSpanningForest();
        Assert.False(forest.Connected);
        Assert.Equal(2, forest.ComponentCount);
        Assert.Equal(2, forest.TotalCost);
    }

    [Fact]
    public void Empty_and_missing_starts_do_not_throw()
    {
        var empty = new WeightedGraph();
        var traversal = empty.BreadthFirst("GATEWAY");
        Assert.False(traversal.StartFound);
        Assert.Empty(traversal.Order);

        var forest = empty.MinimumSpanningForest();
        Assert.True(forest.Connected);
        Assert.Equal(0, forest.TotalCost);
        Assert.False(empty.LeastCost("A", "B").Found);
    }

    [Fact]
    public void Kruskal_matches_a_known_tree_and_breaks_equal_weight_ties_by_endpoint()
    {
        var graph = Connected("A", "B", "C", "D");
        Add(graph, "A", "B", 1);
        Add(graph, "B", "C", 2);
        Add(graph, "A", "C", 4);
        Add(graph, "C", "D", 3);
        Add(graph, "B", "D", 6);

        var tree = graph.MinimumSpanningForest();
        Assert.True(tree.Connected);
        Assert.Equal(6, tree.TotalCost);
        Assert.Equal(["A|B|1", "B|C|2", "C|D|3"], tree.Edges.Select(Label).ToArray());

        var tied = Connected("A", "B", "C");
        Add(tied, "B", "C", 1);
        Add(tied, "A", "C", 1);
        Add(tied, "A", "B", 1);
        var tiedTree = tied.MinimumSpanningForest();
        Assert.Equal(2, tiedTree.TotalCost);
        Assert.Equal(["A|B|1", "A|C|1"], tiedTree.Edges.Select(Label).ToArray());
    }

    [Fact]
    public void Dijkstra_least_cost_differs_from_fewest_hops_and_from_the_spanning_tree()
    {
        var graph = Connected("G", "A", "B");
        Add(graph, "G", "A", 1);
        Add(graph, "A", "B", 1);
        Add(graph, "G", "B", 5);

        Assert.Equal(["G", "B"], graph.FewestHops("G", "B").Nodes);
        Assert.Equal(5, graph.FewestHops("G", "B").Cost);
        Assert.Equal(["G", "A", "B"], graph.LeastCost("G", "B").Nodes);
        Assert.Equal(2, graph.LeastCost("G", "B").Cost);

        var backbone = new WeightedGraph();
        foreach (var id in graph.Vertices)
        {
            backbone.AddVertex(id);
        }

        foreach (var edge in graph.MinimumSpanningForest().Edges)
        {
            backbone.AddEdge(edge.From, edge.To, edge.Cost);
        }

        var alongTree = backbone.FewestHops("G", "B");
        Assert.Equal(["G", "A", "B"], alongTree.Nodes);
        Assert.Equal(2, alongTree.Cost);
        Assert.NotEqual(graph.FewestHops("G", "B").Cost, alongTree.Cost);
    }

    [Fact]
    public void Campus_mesh_keeps_facilities_and_uses_the_calculated_backbone()
    {
        var mesh = SeededMesh();
        var snapshot = mesh.Snapshot();
        Assert.Equal(11, snapshot.Vertices.Count);
        Assert.Contains(snapshot.Vertices, vertex => vertex.Id == WeightedGraph.GatewayId && vertex.Kind == "Gateway");
        Assert.Contains(snapshot.Vertices, vertex => vertex.Id == "24:6F:28:A1:B2:C3");
        Assert.Contains(snapshot.Vertices, vertex => vertex.Id == "30:AE:A4:10:01:03");
        Assert.Contains(snapshot.Vertices, vertex => vertex.Id == "3C:71:BF:22:10:AA");
        Assert.Contains("not measured RSSI", snapshot.CostSemantics, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(16, snapshot.Edges.Count);
        Assert.All(snapshot.Edges, edge => Assert.True(edge.Cost >= 0));

        var tree = mesh.SpanningTree();
        Assert.True(tree.Connected);
        Assert.Equal(1, tree.ComponentCount);
        Assert.Equal(33, tree.TotalCost);
        Assert.Equal(10, tree.Edges.Count);
        Assert.Contains(tree.Edges, edge => edge.Cost == 6 && edge.From == "24:6F:28:A1:B2:C3" && edge.To == WeightedGraph.GatewayId);
        Assert.DoesNotContain(tree.Edges, edge => edge.Cost == 20);
        Assert.DoesNotContain(tree.Edges, edge => edge.Cost == 9);
    }

    [Fact]
    public void Failure_of_the_feeder_switches_the_water_pulse_onto_the_alternate_hop()
    {
        var mesh = SeededMesh();
        var before = mesh.Route("3C:71:BF:22:10:AA");
        Assert.NotNull(before);
        Assert.True(before.Reachable);
        Assert.Equal(
            [WeightedGraph.GatewayId, "3C:71:BF:22:10:AB", "3C:71:BF:22:10:AA"],
            before.FewestHopPath);
        Assert.Equal(22, before.FewestHopCost);
        Assert.Equal(
            [WeightedGraph.GatewayId, "30:AE:A4:10:01:01", "30:AE:A4:10:01:02", "30:AE:A4:10:01:03", "3C:71:BF:22:10:AA"],
            before.LeastCostPath);
        Assert.Equal(15, before.LeastCost);
        Assert.Equal(13, mesh.Route("24:6F:28:A1:B2:F1")!.SpanningTreeCost);
        Assert.Equal(9, mesh.Route("24:6F:28:A1:B2:F1")!.LeastCost);

        Assert.True(mesh.TrySetOnline("30:AE:A4:10:01:03", online: false));
        var during = mesh.Route("3c-71-bf-22-10-aa");
        Assert.NotNull(during);
        Assert.True(during.Reachable);
        Assert.Equal(
            [WeightedGraph.GatewayId, "30:AE:A4:10:01:01", "30:AE:A4:10:01:02", "3C:71:BF:22:10:AA"],
            during.LeastCostPath);
        Assert.Equal(16, during.LeastCost);
        Assert.DoesNotContain("30:AE:A4:10:01:03", during.LeastCostPath);
        Assert.Contains(mesh.Snapshot().Vertices, vertex => vertex.Id == "30:AE:A4:10:01:03" && vertex.Offline && !vertex.Reachable);

        Assert.True(mesh.TrySetOnline("30:AE:A4:10:01:03", online: true));
        Assert.Equal(15, mesh.Route("3C:71:BF:22:10:AA")!.LeastCost);

        Assert.True(mesh.TrySetOnline("3C:71:BF:22:10:AA", online: false));
        var offline = mesh.Route("3C:71:BF:22:10:AA");
        Assert.NotNull(offline);
        Assert.False(offline.Reachable);
        Assert.Equal("Offline", offline.Status);
        Assert.Contains("unreachable", offline.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_registered_sensor_with_no_link_cannot_reach_the_gateway()
    {
        var registry = new SensorRegistry();
        registry.Seed(
        [
            new SensorProfile
            {
                MacAddress = "AA:BB:CC:DD:EE:01",
                DisplayName = "Unlinked probe",
                Location = "Facility A -> Zone 1 -> Sub-Zone B",
                Category = SensorCategory.Environmental
            }
        ]);
        var mesh = new MeshTopologyService(registry);
        var route = mesh.Route("AA:BB:CC:DD:EE:01");
        Assert.NotNull(route);
        Assert.False(route.Reachable);
        Assert.Equal("Unreachable", route.Status);
        Assert.Contains("AA:BB:CC:DD:EE:01", mesh.Snapshot().UnreachableIds);
        Assert.Null(mesh.Route("AA:BB:CC:DD:EE:FF"));
        Assert.False(mesh.TrySetOnline("AA:BB:CC:DD:EE:FF", online: false));
    }

    [Fact]
    public void An_operator_demonstration_link_makes_an_unreachable_sensor_routable()
    {
        var registry = new SensorRegistry();
        registry.Seed(
        [
            new SensorProfile
            {
                MacAddress = "AA:BB:CC:DD:EE:01",
                DisplayName = "Unlinked probe",
                Location = "Facility A -> Zone 1 -> Sub-Zone B",
                Category = SensorCategory.Environmental
            }
        ]);
        var mesh = new MeshTopologyService(registry);
        Assert.Equal("Unreachable", mesh.Route("AA:BB:CC:DD:EE:01")!.Status);

        var added = mesh.AddDemonstrationLink("gateway", "aa-bb-cc-dd-ee-01", 4, "bench hop", out _);
        Assert.Equal(MeshLinkStatus.Updated, added);
        var link = Assert.Single(mesh.Snapshot().Edges);
        Assert.True(link.OperatorDefined);
        Assert.Equal(4, link.Cost);
        Assert.Contains("not measured RSSI", link.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(mesh.Snapshot().UnreachableIds);

        var route = mesh.Route("AA:BB:CC:DD:EE:01");
        Assert.NotNull(route);
        Assert.True(route.Reachable);
        Assert.Equal(4, route.LeastCost);
        Assert.Equal([WeightedGraph.GatewayId, "AA:BB:CC:DD:EE:01"], route.LeastCostPath);

        Assert.Equal(MeshLinkStatus.Invalid, mesh.AddDemonstrationLink("AA:BB:CC:DD:EE:01", "GATEWAY", 9, null, out var duplicate));
        Assert.Contains("already exists", duplicate, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(MeshLinkStatus.Invalid, mesh.AddDemonstrationLink("AA:BB:CC:DD:EE:01", "AA:BB:CC:DD:EE:01", 1, null, out var loop));
        Assert.Contains("same node", loop, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(MeshLinkStatus.Invalid, mesh.AddDemonstrationLink("AA:BB:CC:DD:EE:01", "GATEWAY", -3, null, out var negative));
        Assert.Contains("non-negative", negative, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(MeshLinkStatus.NotFound, mesh.AddDemonstrationLink("AA:BB:CC:DD:EE:FF", "GATEWAY", 1, null, out _));

        Assert.Equal(MeshLinkStatus.Updated, mesh.RemoveDemonstrationLink("AA:BB:CC:DD:EE:01", "GATEWAY", out _));
        Assert.Equal("Unreachable", mesh.Route("AA:BB:CC:DD:EE:01")!.Status);
        Assert.Empty(mesh.Snapshot().Edges);

        var seeded = SeededMesh();
        Assert.Equal(
            MeshLinkStatus.Invalid,
            seeded.RemoveDemonstrationLink("GATEWAY", "24:6F:28:A1:B2:C3", out var catalogue));
        Assert.Contains("catalogue", catalogue, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(16, seeded.Snapshot().Edges.Count);
    }

    [Fact]
    public void Traversal_from_the_gateway_visits_each_online_sensor_once()
    {
        var mesh = SeededMesh();
        var bfs = mesh.Traverse("bfs", WeightedGraph.GatewayId);
        var dfs = mesh.Traverse("dfs", WeightedGraph.GatewayId);
        Assert.NotNull(bfs);
        Assert.NotNull(dfs);
        Assert.Equal(WeightedGraph.GatewayId, bfs.Order[0]);
        Assert.Equal(11, bfs.Order.Count);
        Assert.Equal(bfs.Order.Count, bfs.Order.Distinct().Count());
        Assert.Empty(bfs.Unreachable);
        Assert.NotEqual(bfs.Order, dfs.Order);
        Assert.Null(mesh.Traverse("bfs", "AA:BB:CC:DD:EE:01"));
    }

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

    private static WeightedGraph Connected(params string[] ids)
    {
        var graph = new WeightedGraph();
        foreach (var id in ids)
        {
            graph.AddVertex(id);
        }

        return graph;
    }

    private static void Add(WeightedGraph graph, string from, string to, int cost) =>
        Assert.True(graph.AddEdge(from, to, cost));

    private static string Label(GraphEdge edge) => $"{edge.From}|{edge.To}|{edge.Cost}";
}
