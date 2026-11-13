using SmartX.Shared.Mesh;

namespace SmartX.Api.Services;

/// <summary>
/// Wireless mesh over registered devices plus one gateway vertex.
/// This is not the campus deployment tree: that tree only checks Facility → Zone → Sub-Zone placement.
/// Link costs are a fixed demonstration plan. The gateway does not store measured RSSI.
/// </summary>
public sealed class MeshTopologyService
{
    public const string CostSemantics =
        "Demonstration link cost. A lower number is a cheaper hop. These values are not measured RSSI.";

    /// <summary>
    /// Seeded Stellenbosch, Midrand, and Umhlanga devices. Facility C is not a direct child of the gateway;
    /// several sensors also have a costly direct hop that a spanning tree will not choose.
    /// </summary>
    public static IReadOnlyList<DemonstrationLink> Catalogue { get; } =
    [
        new(WeightedGraph.GatewayId, "24:6F:28:A1:B2:C3", 6, "Gateway to Facility A gutter"),
        new(WeightedGraph.GatewayId, "24:6F:28:A1:B2:F1", 9, "Costly direct canopy hop"),
        new(WeightedGraph.GatewayId, "30:AE:A4:10:01:01", 8, "Gateway to Facility B bay"),
        new(WeightedGraph.GatewayId, "3C:71:BF:22:10:AB", 20, "Costly direct damper hop"),
        new("24:6F:28:A1:B2:C3", "24:6F:28:A1:B2:C4", 2, "Adjacent gutters"),
        new("24:6F:28:A1:B2:C3", "24:6F:28:A1:B2:D1", 3, "Gutter to irrigation solenoid"),
        new("24:6F:28:A1:B2:C4", "24:6F:28:A1:B2:E1", 2, "Gutter to EC probe"),
        new("24:6F:28:A1:B2:C4", "24:6F:28:A1:B2:D1", 5, "Longer gutter-to-solenoid hop"),
        new("24:6F:28:A1:B2:D1", "24:6F:28:A1:B2:F1", 4, "Solenoid to canopy"),
        new("24:6F:28:A1:B2:E1", "24:6F:28:A1:B2:F1", 3, "EC probe to canopy"),
        new("30:AE:A4:10:01:01", "30:AE:A4:10:01:02", 2, "Adjacent CT clamps"),
        new("30:AE:A4:10:01:02", "30:AE:A4:10:01:03", 2, "Clamp to feeder breaker"),
        new("30:AE:A4:10:01:01", "30:AE:A4:10:01:03", 5, "Longer bay-to-feeder hop"),
        new("30:AE:A4:10:01:03", "3C:71:BF:22:10:AA", 3, "Feeder backhaul into Facility C"),
        new("3C:71:BF:22:10:AA", "3C:71:BF:22:10:AB", 2, "Water pulse to damper"),
        new("30:AE:A4:10:01:02", "3C:71:BF:22:10:AA", 6, "Alternate bay hop into Facility C")
    ];

    private readonly SensorRegistry _registry;
    private readonly HashSet<string> _offline = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DemonstrationLink> _operatorLinks = [];
    private readonly object _gate = new();

    public MeshTopologyService(SensorRegistry registry)
    {
        _registry = registry;
    }

    public MeshSnapshot Snapshot()
    {
        lock (_gate)
        {
            var (full, active) = Build();
            var reachable = ReachableIds(active);
            return Describe(full, active, reachable);
        }
    }

    public MeshTraversal? Traverse(string algorithm, string start)
    {
        lock (_gate)
        {
            var (_, active) = Build();
            if (!active.Contains(start))
            {
                return null;
            }

            var result = algorithm.Equals("dfs", StringComparison.OrdinalIgnoreCase)
                ? active.DepthFirst(start)
                : active.BreadthFirst(start);
            return new MeshTraversal
            {
                Algorithm = result.Algorithm,
                Start = start,
                Order = result.Order.ToList(),
                Unreachable = result.Unreachable.ToList()
            };
        }
    }

    public MeshSpanningTree SpanningTree()
    {
        lock (_gate)
        {
            var (_, active) = Build();
            var forest = active.MinimumSpanningForest();
            return new MeshSpanningTree
            {
                Edges = forest.Edges.Select(edge => new MeshLink
                {
                    From = edge.From,
                    To = edge.To,
                    Cost = edge.Cost,
                    Active = true
                }).ToList(),
                TotalCost = forest.TotalCost,
                ComponentCount = forest.ComponentCount,
                Connected = forest.Connected,
                Explanation = forest.Connected
                    ? "Kruskal's algorithm chose these links. The tree minimises total link cost. It is not the least-cost route for every sensor."
                    : "Offline or unlinked sensors split the mesh. Kruskal's algorithm returned a forest, not one spanning tree."
            };
        }
    }

    public MeshRoute? Route(string mac)
    {
        lock (_gate)
        {
            var profile = _registry.Get(mac);
            if (profile is null)
            {
                return null;
            }

            var normalized = profile.MacAddress;
            var (_, active) = Build();
            if (_offline.Contains(normalized) || !active.Contains(normalized))
            {
                return new MeshRoute
                {
                    MacAddress = normalized,
                    DisplayName = profile.DisplayName,
                    Reachable = false,
                    Status = _offline.Contains(normalized) ? "Offline" : "Unreachable",
                    Explanation = _offline.Contains(normalized)
                        ? "This sensor is offline and is excluded from routing. The gateway is unreachable until it is restored."
                        : "This sensor has no active link to the gateway."
                };
            }

            var hops = active.FewestHops(WeightedGraph.GatewayId, normalized);
            var least = active.LeastCost(WeightedGraph.GatewayId, normalized);
            var tree = active.MinimumSpanningForest();
            var backbone = new WeightedGraph();
            foreach (var id in active.Vertices)
            {
                backbone.AddVertex(id);
            }

            foreach (var edge in tree.Edges)
            {
                backbone.AddEdge(edge.From, edge.To, edge.Cost);
            }

            var alongTree = backbone.FewestHops(WeightedGraph.GatewayId, normalized);
            var reachable = least.Reachable;
            return new MeshRoute
            {
                MacAddress = normalized,
                DisplayName = profile.DisplayName,
                Reachable = reachable,
                Status = reachable ? "Reachable" : "Unreachable",
                FewestHopPath = hops.Nodes.ToList(),
                FewestHopCost = hops.Cost,
                LeastCostPath = least.Nodes.ToList(),
                LeastCost = least.Cost,
                SpanningTreePath = alongTree.Nodes.ToList(),
                SpanningTreeCost = alongTree.Cost,
                Explanation = Explain(hops, least, alongTree)
            };
        }
    }

    public bool IsOffline(string mac)
    {
        lock (_gate)
        {
            return _offline.Contains(SensorRegistry.NormalizeMac(mac));
        }
    }

    public bool TrySetOnline(string mac, bool online)
    {
        var normalized = SensorRegistry.NormalizeMac(mac);
        if (_registry.Get(normalized) is null)
        {
            return false;
        }

        lock (_gate)
        {
            if (online)
            {
                _offline.Remove(normalized);
            }
            else
            {
                _offline.Add(normalized);
            }
        }

        return true;
    }

    /// <summary>
    /// Adds a labelled demonstration hop. Cost is chosen by the caller. Nothing here is measured RSSI.
    /// A new sensor stays unreachable until a link like this, or a catalogue link, actually connects it.
    /// </summary>
    public MeshLinkStatus AddDemonstrationLink(string from, string to, int cost, string? note, out string message)
    {
        if (cost < 0)
        {
            message = "Link cost must be a non-negative whole number. This is a demonstration cost, not measured RSSI.";
            return MeshLinkStatus.Invalid;
        }

        if (!TryCanonical(from, out var start) || !TryCanonical(to, out var end))
        {
            message = "Both ends of a link are required.";
            return MeshLinkStatus.Invalid;
        }

        if (start.Equals(end, StringComparison.OrdinalIgnoreCase))
        {
            message = "A link cannot start and end on the same node.";
            return MeshLinkStatus.Invalid;
        }

        lock (_gate)
        {
            if (!IsKnown(start) || !IsKnown(end))
            {
                message = "Sensor is not registered.";
                return MeshLinkStatus.NotFound;
            }

            if (AllLinks().Any(link => SameEnds(link, start, end)))
            {
                message = "That demonstration link already exists.";
                return MeshLinkStatus.Invalid;
            }

            var label = string.IsNullOrWhiteSpace(note)
                ? "Operator demonstration link. Not measured RSSI."
                : $"Operator demonstration link. Not measured RSSI. {note.Trim()}";
            _operatorLinks.Add(new DemonstrationLink(start, end, cost, label));
        }

        message = "Demonstration link added.";
        return MeshLinkStatus.Updated;
    }

    /// <summary>Removes an operator link only. Seeded catalogue hops stay in place.</summary>
    public MeshLinkStatus RemoveDemonstrationLink(string from, string to, out string message)
    {
        if (!TryCanonical(from, out var start) || !TryCanonical(to, out var end))
        {
            message = "Both ends of a link are required.";
            return MeshLinkStatus.Invalid;
        }

        lock (_gate)
        {
            if (Catalogue.Any(link => SameEnds(link, start, end)))
            {
                message = "Seeded catalogue links are fixed. Only an operator demonstration link can be removed.";
                return MeshLinkStatus.Invalid;
            }

            var index = _operatorLinks.FindIndex(link => SameEnds(link, start, end));
            if (index < 0)
            {
                message = "That operator demonstration link was not found.";
                return MeshLinkStatus.NotFound;
            }

            _operatorLinks.RemoveAt(index);
        }

        message = "Demonstration link removed.";
        return MeshLinkStatus.Updated;
    }

    private (WeightedGraph Full, WeightedGraph Active) Build()
    {
        var full = new WeightedGraph();
        var active = new WeightedGraph();
        full.AddVertex(WeightedGraph.GatewayId);
        active.AddVertex(WeightedGraph.GatewayId);

        foreach (var profile in _registry.All())
        {
            full.AddVertex(profile.MacAddress);
            if (!_offline.Contains(profile.MacAddress))
            {
                active.AddVertex(profile.MacAddress);
            }
        }

        foreach (var link in AllLinks())
        {
            if (!full.Contains(link.From) || !full.Contains(link.To))
            {
                continue;
            }

            full.AddEdge(link.From, link.To, link.Cost);
            if (active.Contains(link.From) && active.Contains(link.To))
            {
                active.AddEdge(link.From, link.To, link.Cost);
            }
        }

        return (full, active);
    }

    private MeshSnapshot Describe(WeightedGraph full, WeightedGraph active, HashSet<string> reachable)
    {
        var vertices = new List<MeshVertex>
        {
            new()
            {
                Id = WeightedGraph.GatewayId,
                DisplayName = "Central gateway",
                Kind = "Gateway",
                Location = "Mesh root",
                Reachable = true
            }
        };

        foreach (var profile in _registry.All().OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var offline = _offline.Contains(profile.MacAddress);
            vertices.Add(new MeshVertex
            {
                Id = profile.MacAddress,
                DisplayName = profile.DisplayName,
                Kind = "Sensor",
                Location = profile.Location,
                Offline = offline,
                Reachable = !offline && reachable.Contains(profile.MacAddress)
            });
        }

        var edges = new List<MeshLink>();
        foreach (var link in AllLinks())
        {
            if (!full.Contains(link.From) || !full.Contains(link.To))
            {
                continue;
            }

            var from = string.CompareOrdinal(link.From, link.To) <= 0 ? link.From : link.To;
            var to = from == link.From ? link.To : link.From;
            var operatorDefined = _operatorLinks.Any(extra => SameEnds(extra, link.From, link.To));
            edges.Add(new MeshLink
            {
                From = from,
                To = to,
                Cost = link.Cost,
                Active = active.Contains(from) && active.Contains(to),
                OperatorDefined = operatorDefined,
                Note = link.Note
            });
        }

        edges.Sort((left, right) =>
        {
            var byFrom = string.CompareOrdinal(left.From, right.From);
            return byFrom != 0 ? byFrom : string.CompareOrdinal(left.To, right.To);
        });

        return new MeshSnapshot
        {
            CostSemantics = CostSemantics,
            Vertices = vertices,
            Edges = edges,
            OfflineIds = _offline.OrderBy(id => id, StringComparer.Ordinal).ToList(),
            UnreachableIds = vertices
                .Where(vertex => vertex.Kind == "Sensor" && !vertex.Offline && !vertex.Reachable)
                .Select(vertex => vertex.Id)
                .ToList()
        };
    }

    private static HashSet<string> ReachableIds(WeightedGraph active)
    {
        var traversal = active.BreadthFirst(WeightedGraph.GatewayId);
        return traversal.Order.ToHashSet(StringComparer.Ordinal);
    }

    private IEnumerable<DemonstrationLink> AllLinks()
    {
        foreach (var link in Catalogue)
        {
            yield return link;
        }

        foreach (var link in _operatorLinks)
        {
            yield return link;
        }
    }

    private bool IsKnown(string id) =>
        id.Equals(WeightedGraph.GatewayId, StringComparison.Ordinal)
        || _registry.Get(id) is not null;

    private static bool TryCanonical(string raw, out string id)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            id = "";
            return false;
        }

        if (raw.Trim().Equals(WeightedGraph.GatewayId, StringComparison.OrdinalIgnoreCase))
        {
            id = WeightedGraph.GatewayId;
            return true;
        }

        id = SensorRegistry.NormalizeMac(raw);
        return true;
    }

    private static bool SameEnds(DemonstrationLink link, string from, string to) =>
        (link.From.Equals(from, StringComparison.OrdinalIgnoreCase) && link.To.Equals(to, StringComparison.OrdinalIgnoreCase))
        || (link.From.Equals(to, StringComparison.OrdinalIgnoreCase) && link.To.Equals(from, StringComparison.OrdinalIgnoreCase));

    private static string Explain(GraphPath hops, GraphPath least, GraphPath alongTree)
    {
        if (!least.Reachable)
        {
            return "No active path reaches the gateway.";
        }

        var parts = new List<string>();
        if (!hops.Nodes.SequenceEqual(least.Nodes))
        {
            parts.Add($"Fewest hops costs {hops.Cost}. The least-cost path costs {least.Cost}. Breadth-first search counts hops, not link cost.");
        }

        if (alongTree.Reachable && alongTree.Cost != least.Cost)
        {
            parts.Add($"The spanning-tree route costs {alongTree.Cost}. A minimum spanning tree minimises the sum of chosen links, so it is not this sensor's shortest path.");
        }
        else if (parts.Count == 0)
        {
            parts.Add("Fewest hops, least cost, and the spanning-tree route match for this sensor.");
        }

        return string.Join(" ", parts);
    }
}

public readonly record struct DemonstrationLink(string From, string To, int Cost, string Note);

public enum MeshLinkStatus
{
    Updated,
    Invalid,
    NotFound
}
