using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using SmartX.Shared.Runtime;

namespace SmartX.Tests;

public sealed class HistoryRangeApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Gutter = "24:6F:28:A1:B2:C3";
    private readonly HttpClient _client;

    public HistoryRangeApiTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Range_returns_one_sensors_readings_oldest_first()
    {
        var from = DateTimeOffset.UtcNow.AddHours(-8);
        var to = DateTimeOffset.UtcNow.AddMinutes(1);
        var rows = await WaitForReadings(from, to);

        Assert.All(rows, row => Assert.Equal(Gutter, row.DeviceId));
        Assert.All(rows, row => Assert.False(string.IsNullOrWhiteSpace(row.DisplayValue)));
        Assert.All(rows, row => Assert.Equal("%VWC", row.Unit));
        Assert.All(rows, row => Assert.InRange(row.TimestampUtc, from, to));
        for (var i = 1; i < rows.Count; i++)
        {
            Assert.True(rows[i].TimestampUtc >= rows[i - 1].TimestampUtc);
        }

        using var raw = await _client.GetAsync(Url(Gutter, from, to));
        var json = await raw.Content.ReadAsStringAsync();
        Assert.Contains("\"deviceId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"timestampUtc\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exact_timestamp_is_inclusive_and_a_past_window_is_empty()
    {
        var rows = await WaitForReadings(DateTimeOffset.UtcNow.AddHours(-8), DateTimeOffset.UtcNow.AddMinutes(1));
        var instant = rows[0].TimestampUtc;

        var exact = await GetOk(Gutter, instant, instant);
        Assert.Contains(exact, row => row.TimestampUtc == instant && row.DeviceId == Gutter);

        var outside = await GetOk(Gutter, instant.AddTicks(1), instant.AddMinutes(1));
        Assert.DoesNotContain(outside, row => row.TimestampUtc == instant);

        var empty = await GetOk(Gutter, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(1));
        Assert.Empty(empty);
    }

    [Fact]
    public async Task Hyphenated_mac_is_normalised()
    {
        await WaitForReadings(DateTimeOffset.UtcNow.AddHours(-8), DateTimeOffset.UtcNow.AddMinutes(1));
        var rows = await GetOk("24-6f-28-a1-b2-c3", DateTimeOffset.UtcNow.AddHours(-8), DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.Equal(Gutter, row.DeviceId));
    }

    [Fact]
    public async Task Invalid_mac_timestamp_and_range_return_clear_errors()
    {
        await AssertMessage(HttpStatusCode.BadRequest, "/api/runtime/history/range?from=2026-01-01T00:00:00Z&to=2026-01-01T01:00:00Z", "required");
        await AssertMessage(HttpStatusCode.BadRequest, Url("not-a-mac", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1)), "not valid");
        await AssertMessage(HttpStatusCode.BadRequest, "/api/runtime/history/range?mac=24:6F:28:A1:B2:C3&to=2026-01-01T00:00:00Z", "required");
        await AssertMessage(HttpStatusCode.BadRequest, "/api/runtime/history/range?mac=24:6F:28:A1:B2:C3&from=yesterday&to=2026-01-01T00:00:00Z", "ISO-8601");
        await AssertMessage(
            HttpStatusCode.BadRequest,
            Url(Gutter, DateTimeOffset.UnixEpoch.AddHours(2), DateTimeOffset.UnixEpoch),
            "earlier");
        await AssertMessage(HttpStatusCode.NotFound, Url("AA:BB:CC:DD:EE:FF", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1)), "not registered");
    }

    [Fact]
    public async Task Existing_history_endpoint_still_returns_the_newest_window()
    {
        var response = await _client.GetAsync("/api/runtime/history?take=5");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = await response.Content.ReadFromJsonAsync<List<ProcessedReading>>();
        Assert.NotNull(rows);
        Assert.True(rows.Count <= 5);
    }

    private async Task<List<ProcessedReading>> WaitForReadings(DateTimeOffset from, DateTimeOffset to)
    {
        List<ProcessedReading>? rows = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            rows = await GetOk(Gutter, from, to);
            if (rows.Count >= 80)
            {
                return rows;
            }

            await Task.Delay(100);
        }

        Assert.NotNull(rows);
        Assert.True(rows!.Count >= 80, $"Expected the seeded gutter history, found {rows.Count}.");
        return rows;
    }

    private async Task<List<ProcessedReading>> GetOk(string mac, DateTimeOffset from, DateTimeOffset to)
    {
        var response = await _client.GetAsync(Url(mac, from, to));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = await response.Content.ReadFromJsonAsync<List<ProcessedReading>>();
        return rows ?? [];
    }

    private async Task AssertMessage(HttpStatusCode status, string url, string fragment)
    {
        var response = await _client.GetAsync(url);
        Assert.Equal(status, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var message = body.GetProperty("message").GetString();
        Assert.Contains(fragment, message, StringComparison.OrdinalIgnoreCase);
    }

    private static string Url(string mac, DateTimeOffset from, DateTimeOffset to) =>
        $"/api/runtime/history/range?mac={Uri.EscapeDataString(mac)}&from={Uri.EscapeDataString(from.ToString("o"))}&to={Uri.EscapeDataString(to.ToString("o"))}";
}
