using System.Net.Http.Json;
using System.Text.Json;
using SmartX.Shared.Mesh;

namespace SmartX.Client.Services;

/// <summary>
/// Calls the mesh graph endpoints. Failures throw HttpRequestException with the API message.
/// </summary>
public sealed class MeshClient
{
    private readonly HttpClient _http;

    public MeshClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<MeshSnapshot?> GetMeshAsync(CancellationToken cancellationToken)
    {
        var response = await _http.GetAsync("/api/mesh", cancellationToken);
        await EnsureOk(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<MeshSnapshot>(cancellationToken);
    }

    public async Task<MeshTraversal?> TraverseAsync(string algorithm, string start, CancellationToken cancellationToken)
    {
        var path = $"/api/mesh/traverse?algorithm={Uri.EscapeDataString(algorithm)}&start={Uri.EscapeDataString(start)}";
        var response = await _http.GetAsync(path, cancellationToken);
        await EnsureOk(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<MeshTraversal>(cancellationToken);
    }

    public async Task<MeshSpanningTree?> GetSpanningTreeAsync(CancellationToken cancellationToken)
    {
        var response = await _http.GetAsync("/api/mesh/mst", cancellationToken);
        await EnsureOk(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<MeshSpanningTree>(cancellationToken);
    }

    public async Task<MeshRoute?> GetRouteAsync(string mac, CancellationToken cancellationToken)
    {
        var response = await _http.GetAsync($"/api/mesh/route?mac={Uri.EscapeDataString(mac)}", cancellationToken);
        await EnsureOk(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<MeshRoute>(cancellationToken);
    }

    public async Task AddLinkAsync(string from, string to, int cost, string? note, CancellationToken cancellationToken)
    {
        var response = await _http.PostAsJsonAsync(
            "/api/mesh/links",
            new MeshLinkRequest { From = from, To = to, Cost = cost, Note = note },
            cancellationToken);
        await EnsureOk(response, cancellationToken);
    }

    public async Task RemoveLinkAsync(string from, string to, CancellationToken cancellationToken)
    {
        var path = $"/api/mesh/links?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}";
        var response = await _http.DeleteAsync(path, cancellationToken);
        await EnsureOk(response, cancellationToken);
    }

    public async Task SetAvailabilityAsync(string mac, bool online, CancellationToken cancellationToken)
    {
        var response = await _http.PostAsJsonAsync(
            "/api/mesh/availability",
            new MeshAvailabilityRequest { MacAddress = mac, Online = online },
            cancellationToken);
        await EnsureOk(response, cancellationToken);
    }

    private static async Task EnsureOk(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(ExtractMessage(body, (int)response.StatusCode));
    }

    private static string ExtractMessage(string body, int status)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var message)
                && message.GetString() is { Length: > 0 } text)
            {
                return text;
            }
        }
        catch (JsonException)
        {
        }

        return string.IsNullOrWhiteSpace(body) ? $"HTTP {status}" : body;
    }
}
