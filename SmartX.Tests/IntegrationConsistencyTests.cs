using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using SmartX.Shared.Deployment;
using SmartX.Shared.Mesh;
using SmartX.Shared.Runtime;
using SmartX.Shared.Sensors;
using SmartX.Shared.Telemetry;

namespace SmartX.Tests;

public sealed class IntegrationConsistencyTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public IntegrationConsistencyTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Deployment_endpoint_keeps_facility_A_and_accepts_seeded_B_and_C()
    {
        var tree = await _client.GetAsync("/api/deployment/tree");
        Assert.Equal(HttpStatusCode.OK, tree.StatusCode);
        var body = await tree.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Facility A", body.GetProperty("root").GetProperty("name").GetString());
        Assert.Equal(2, body.GetProperty("root").GetProperty("children").GetArrayLength());
        Assert.Equal(12, body.GetProperty("nodeCount").GetInt32());
        Assert.Equal(3, body.GetProperty("facilities").GetArrayLength());

        var bay = await Validate("Facility B -> Zone 1 -> Transformer Bay", "PowerConsumption");
        Assert.True(bay.GetProperty("isValid").GetBoolean());

        var moistureInBay = await Validate("Facility B -> Zone 1 -> Transformer Bay", "Environmental");
        Assert.False(moistureInBay.GetProperty("isValid").GetBoolean());

        var gutterPower = await Validate("Facility A -> Zone 1 -> Sub-Zone B", "PowerConsumption");
        Assert.False(gutterPower.GetProperty("isValid").GetBoolean());
    }

    [Fact]
    public async Task Grid_overload_appears_and_clears_across_faults_recommendations_and_health()
    {
        await IngestPower("30:AE:A4:10:01:01", 1801);
        await IngestPower("30:AE:A4:10:01:02", 1800);
        await WaitForGrid(present: true);

        var health = await _client.GetFromJsonAsync<GatewayIntegrityReport>("/api/gateway/integrity");
        Assert.NotNull(health);
        Assert.Contains(health.Alarms, alarm => alarm.Code == "GRID-OVERLOAD");

        var tips = await _client.GetFromJsonAsync<List<RecommendationDto>>("/api/runtime/recommendations");
        Assert.NotNull(tips);
        Assert.Contains(tips, tip => tip.Id.StartsWith("coupling-grid:", StringComparison.OrdinalIgnoreCase));

        await IngestPower("30:AE:A4:10:01:02", 1400);
        await WaitForGrid(present: false);

        health = await _client.GetFromJsonAsync<GatewayIntegrityReport>("/api/gateway/integrity");
        Assert.NotNull(health);
        Assert.DoesNotContain(health.Alarms, alarm => alarm.Code == "GRID-OVERLOAD");
        tips = await _client.GetFromJsonAsync<List<RecommendationDto>>("/api/runtime/recommendations");
        Assert.NotNull(tips);
        Assert.DoesNotContain(tips, tip => tip.Id.StartsWith("coupling-grid:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_new_sensor_stays_unreachable_until_an_operator_link_is_added()
    {
        const string mac = "AA:BB:CC:DD:EE:10";
        var registered = await _client.PostAsJsonAsync("/api/sensors/register", new SensorRegistrationRequest
        {
            MacAddress = mac,
            DisplayName = "Spare clamp",
            Location = "Facility B -> Zone 1 -> Transformer Bay",
            Category = SensorCategory.PowerConsumption
        });
        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);

        var before = await _client.GetFromJsonAsync<MeshRoute>($"/api/mesh/route?mac={mac}");
        Assert.NotNull(before);
        Assert.Equal("Unreachable", before.Status);

        try
        {
            var added = await _client.PostAsJsonAsync("/api/mesh/links", new MeshLinkRequest
            {
                From = "GATEWAY",
                To = mac,
                Cost = 7,
                Note = "bench hop"
            });
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
            var snapshot = await added.Content.ReadFromJsonAsync<MeshSnapshot>();
            Assert.NotNull(snapshot);
            Assert.Contains(snapshot.Edges, edge =>
                edge.OperatorDefined && edge.Cost == 7 && (edge.From == mac || edge.To == mac));

            var route = await _client.GetFromJsonAsync<MeshRoute>($"/api/mesh/route?mac={mac}");
            Assert.NotNull(route);
            Assert.True(route.Reachable);
            Assert.Equal(7, route.LeastCost);

            var duplicate = await _client.PostAsJsonAsync("/api/mesh/links", new MeshLinkRequest
            {
                From = mac,
                To = "gateway",
                Cost = 1
            });
            Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);

            var negative = await _client.PostAsJsonAsync("/api/mesh/links", new MeshLinkRequest
            {
                From = "GATEWAY",
                To = mac,
                Cost = -1
            });
            Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);

            var catalogue = await _client.DeleteAsync("/api/mesh/links?from=GATEWAY&to=24:6F:28:A1:B2:C3");
            Assert.Equal(HttpStatusCode.BadRequest, catalogue.StatusCode);
        }
        finally
        {
            await _client.DeleteAsync($"/api/mesh/links?from=GATEWAY&to={mac}");
        }

        var after = await _client.GetFromJsonAsync<MeshRoute>($"/api/mesh/route?mac={mac}");
        Assert.NotNull(after);
        Assert.Equal("Unreachable", after.Status);
    }

    private async Task<JsonElement> Validate(string path, string category)
    {
        var response = await _client.PostAsJsonAsync("/api/deployment/validate", new ValidationRequest
        {
            TargetPath = path,
            SensorCategory = category
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task IngestPower(string mac, int watts)
    {
        var response = await _client.PostAsJsonAsync("/api/telemetry/ingest", new TelemetryIngestRequest
        {
            DeviceId = mac,
            ValueKind = "int",
            NumericValue = watts
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task WaitForGrid(bool present)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        List<string>? faults = null;
        while (DateTime.UtcNow < deadline)
        {
            faults = await _client.GetFromJsonAsync<List<string>>("/api/runtime/faults");
            var found = faults?.Any(fault => fault.StartsWith("GRID-OVERLOAD:", StringComparison.OrdinalIgnoreCase)) == true;
            if (found == present)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail(present
            ? "GRID-OVERLOAD was not raised."
            : "GRID-OVERLOAD was still active: " + string.Join(", ", faults ?? []));
    }
}
