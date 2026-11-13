using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using SmartX.Shared.Mesh;

namespace SmartX.Tests;

public sealed class MeshApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public MeshApiTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Mesh_endpoints_describe_the_seeded_graph_without_replacing_the_deployment_tree()
    {
        var mesh = await _client.GetFromJsonAsync<MeshSnapshot>("/api/mesh");
        Assert.NotNull(mesh);
        Assert.Contains("not measured RSSI", mesh.CostSemantics, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(11, mesh.Vertices.Count);
        Assert.Contains(mesh.Vertices, vertex => vertex.Kind == "Gateway");
        Assert.Contains(mesh.Vertices, vertex => vertex.Id == "3C:71:BF:22:10:AA" && vertex.Reachable);
        Assert.Equal(16, mesh.Edges.Count);

        var tree = await _client.GetFromJsonAsync<MeshSpanningTree>("/api/mesh/mst");
        Assert.NotNull(tree);
        Assert.True(tree.Connected);
        Assert.Equal(33, tree.TotalCost);
        Assert.Contains("not the least-cost route", tree.Explanation, StringComparison.OrdinalIgnoreCase);

        var deployment = await _client.GetAsync("/api/deployment/tree");
        Assert.Equal(HttpStatusCode.OK, deployment.StatusCode);
        var body = await deployment.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(12, body.GetProperty("nodeCount").GetInt32());
        Assert.Equal("Facility A", body.GetProperty("root").GetProperty("name").GetString());
        Assert.Equal(2, body.GetProperty("root").GetProperty("children").GetArrayLength());
        Assert.Equal(3, body.GetProperty("facilities").GetArrayLength());

        var history = await _client.GetAsync("/api/runtime/history?take=5");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
    }

    [Fact]
    public async Task Route_distinguishes_hops_from_cost_and_rejects_bad_requests()
    {
        var route = await _client.GetFromJsonAsync<MeshRoute>(
            "/api/mesh/route?mac=3c-71-bf-22-10-aa");
        Assert.NotNull(route);
        Assert.True(route.Reachable);
        Assert.Equal(22, route.FewestHopCost);
        Assert.Equal(15, route.LeastCost);
        Assert.Contains("3C:71:BF:22:10:AB", route.FewestHopPath);
        Assert.Contains("30:AE:A4:10:01:03", route.LeastCostPath);
        Assert.Equal(13, (await _client.GetFromJsonAsync<MeshRoute>("/api/mesh/route?mac=24:6F:28:A1:B2:F1"))!.SpanningTreeCost);

        await AssertMessage(HttpStatusCode.BadRequest, "/api/mesh/route?mac=not-a-mac", "not valid");
        await AssertMessage(HttpStatusCode.BadRequest, "/api/mesh/route", "required");
        await AssertMessage(HttpStatusCode.NotFound, "/api/mesh/route?mac=AA:BB:CC:DD:EE:FF", "not registered");
        await AssertMessage(HttpStatusCode.BadRequest, "/api/mesh/route?mac=GATEWAY", "destination");
        await AssertMessage(HttpStatusCode.BadRequest, "/api/mesh/traverse?algorithm=dijkstra", "bfs or dfs");
    }

    [Fact]
    public async Task Simulated_failure_changes_the_route_and_can_be_restored()
    {
        try
        {
            var offline = await _client.PostAsJsonAsync("/api/mesh/availability", new MeshAvailabilityRequest
            {
                MacAddress = "30-AE-A4-10-01-03",
                Online = false
            });
            Assert.Equal(HttpStatusCode.OK, offline.StatusCode);
            var snapshot = await offline.Content.ReadFromJsonAsync<MeshSnapshot>();
            Assert.NotNull(snapshot);
            Assert.Contains("30:AE:A4:10:01:03", snapshot.OfflineIds);

            var route = await _client.GetFromJsonAsync<MeshRoute>("/api/mesh/route?mac=3C:71:BF:22:10:AA");
            Assert.NotNull(route);
            Assert.True(route.Reachable);
            Assert.DoesNotContain("30:AE:A4:10:01:03", route.LeastCostPath);
            Assert.Equal(16, route.LeastCost);

            var blocked = await _client.GetAsync("/api/mesh/traverse?algorithm=bfs&start=30:AE:A4:10:01:03");
            Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        }
        finally
        {
            var restored = await _client.PostAsJsonAsync("/api/mesh/availability", new MeshAvailabilityRequest
            {
                MacAddress = "30:AE:A4:10:01:03",
                Online = true
            });
            Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        }

        var again = await _client.GetFromJsonAsync<MeshRoute>("/api/mesh/route?mac=3C:71:BF:22:10:AA");
        Assert.Equal(15, again!.LeastCost);
    }

    [Fact]
    public async Task Availability_rejects_the_gateway_and_unknown_sensors()
    {
        await AssertMessage(HttpStatusCode.BadRequest, "/api/mesh/availability", "required", new MeshAvailabilityRequest());
        await AssertMessage(
            HttpStatusCode.BadRequest,
            "/api/mesh/availability",
            "cannot be taken offline",
            new MeshAvailabilityRequest { MacAddress = "GATEWAY", Online = false });
        await AssertMessage(
            HttpStatusCode.NotFound,
            "/api/mesh/availability",
            "not registered",
            new MeshAvailabilityRequest { MacAddress = "AA:BB:CC:DD:EE:FF", Online = false });
    }

    private async Task AssertMessage(HttpStatusCode status, string url, string fragment, MeshAvailabilityRequest? body = null)
    {
        var response = body is null
            ? await _client.GetAsync(url)
            : await _client.PostAsJsonAsync(url, body);
        Assert.Equal(status, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(fragment, payload.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }
}
