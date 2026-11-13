namespace SmartX.Shared.Mesh;

public sealed class MeshSnapshot
{
    public string GatewayId { get; init; } = WeightedGraph.GatewayId;
    public string CostSemantics { get; init; } = string.Empty;
    public List<MeshVertex> Vertices { get; init; } = [];
    public List<MeshLink> Edges { get; init; } = [];
    public List<string> OfflineIds { get; init; } = [];
    public List<string> UnreachableIds { get; init; } = [];
}

public sealed class MeshVertex
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Kind { get; init; } = "Sensor";
    public string Location { get; init; } = string.Empty;
    public bool Offline { get; init; }
    public bool Reachable { get; init; }
}

public sealed class MeshLink
{
    public string From { get; init; } = string.Empty;
    public string To { get; init; } = string.Empty;
    public int Cost { get; init; }
    public bool Active { get; init; }
    public bool OperatorDefined { get; init; }
    public string Note { get; init; } = string.Empty;
}

public sealed class MeshLinkRequest
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public int Cost { get; set; }
    public string? Note { get; set; }
}

public sealed class MeshTraversal
{
    public string Algorithm { get; init; } = string.Empty;
    public string Start { get; init; } = string.Empty;
    public List<string> Order { get; init; } = [];
    public List<string> Unreachable { get; init; } = [];
}

public sealed class MeshSpanningTree
{
    public List<MeshLink> Edges { get; init; } = [];
    public int TotalCost { get; init; }
    public int ComponentCount { get; init; }
    public bool Connected { get; init; }
    public string Explanation { get; init; } = string.Empty;
}

public sealed class MeshRoute
{
    public string MacAddress { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool Reachable { get; init; }
    public string Status { get; init; } = string.Empty;
    public List<string> FewestHopPath { get; init; } = [];
    public int FewestHopCost { get; init; }
    public List<string> LeastCostPath { get; init; } = [];
    public int LeastCost { get; init; }
    public List<string> SpanningTreePath { get; init; } = [];
    public int SpanningTreeCost { get; init; }
    public string Explanation { get; init; } = string.Empty;
}

public sealed class MeshAvailabilityRequest
{
    public string MacAddress { get; set; } = string.Empty;
    public bool Online { get; set; }
}
