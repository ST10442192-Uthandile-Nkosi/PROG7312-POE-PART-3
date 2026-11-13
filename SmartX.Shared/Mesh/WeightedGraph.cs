namespace SmartX.Shared.Mesh;

/// <summary>
/// Undirected weighted graph stored as an adjacency list.
/// Neighbours are always read in ordinal id order so BFS, DFS, and equal-cost choices are reproducible.
/// Link weights are non-negative and finite. A lower weight is a cheaper hop, not a measured RSSI value.
/// </summary>
public sealed class WeightedGraph
{
    public const string GatewayId = "GATEWAY";

    private readonly Dictionary<string, List<(string Neighbor, int Weight)>> _adjacency = new(StringComparer.Ordinal);

    public int VertexCount => _adjacency.Count;

    public IReadOnlyList<string> Vertices =>
        _adjacency.Keys.OrderBy(id => id, StringComparer.Ordinal).ToList();

    public IReadOnlyList<GraphEdge> Edges
    {
        get
        {
            var edges = new List<GraphEdge>();
            foreach (var (id, links) in _adjacency)
            {
                foreach (var (neighbor, weight) in links)
                {
                    if (string.CompareOrdinal(id, neighbor) < 0)
                    {
                        edges.Add(new GraphEdge(id, neighbor, weight));
                    }
                }
            }

            edges.Sort();
            return edges;
        }
    }

    public bool Contains(string id) =>
        id is not null && _adjacency.ContainsKey(id);

    public bool AddVertex(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return _adjacency.TryAdd(id, []);
    }

    /// <summary>
    /// Adds one undirected edge. A repeated pair or a self-loop is rejected and leaves the graph unchanged.
    /// </summary>
    public bool AddEdge(string a, string b, int weight)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(a);
        ArgumentException.ThrowIfNullOrWhiteSpace(b);
        if (weight < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(weight), "Link cost must be non-negative.");
        }

        if (string.Equals(a, b, StringComparison.Ordinal) || !_adjacency.ContainsKey(a) || !_adjacency.ContainsKey(b))
        {
            return false;
        }

        if (_adjacency[a].Any(link => link.Neighbor == b))
        {
            return false;
        }

        _adjacency[a].Add((b, weight));
        _adjacency[b].Add((a, weight));
        return true;
    }

    public IReadOnlyList<(string Neighbor, int Weight)> Neighbours(string id) =>
        _adjacency.TryGetValue(id, out var links)
            ? links.OrderBy(link => link.Neighbor, StringComparer.Ordinal).ToList()
            : [];

    public TraversalResult BreadthFirst(string start) => Traverse(start, "bfs", depthFirst: false);

    public TraversalResult DepthFirst(string start) => Traverse(start, "dfs", depthFirst: true);

    public GraphPath FewestHops(string from, string to) =>
        Search(from, to, weighted: false);

    public GraphPath LeastCost(string from, string to) =>
        Search(from, to, weighted: true);

    /// <summary>
    /// Kruskal's algorithm. Equal weights break ties by endpoint id, so the same mesh always yields the same tree.
    /// A disconnected graph returns a forest and Connected = false. The tree minimises the sum of chosen
    /// link costs; it is not the least-cost route for every device.
    /// </summary>
    public MstResult MinimumSpanningForest()
    {
        var vertices = Vertices;
        var parent = vertices.ToDictionary(id => id, id => id, StringComparer.Ordinal);
        var rank = vertices.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        var chosen = new List<GraphEdge>();

        foreach (var edge in Edges)
        {
            if (Union(edge.From, edge.To))
            {
                chosen.Add(edge);
            }
        }

        var components = vertices.Count - chosen.Count;
        return new MstResult
        {
            Edges = chosen,
            TotalCost = chosen.Sum(edge => edge.Cost),
            ComponentCount = components,
            Connected = components <= 1
        };

        bool Union(string left, string right)
        {
            left = Find(left);
            right = Find(right);
            if (left == right)
            {
                return false;
            }

            if (rank[left] < rank[right])
            {
                (left, right) = (right, left);
            }

            parent[right] = left;
            if (rank[left] == rank[right])
            {
                rank[left]++;
            }

            return true;
        }

        string Find(string id)
        {
            if (parent[id] != id)
            {
                parent[id] = Find(parent[id]);
            }

            return parent[id];
        }
    }

    private TraversalResult Traverse(string start, string algorithm, bool depthFirst)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(start);
        if (!_adjacency.ContainsKey(start))
        {
            return new TraversalResult
            {
                Algorithm = algorithm,
                Start = start,
                StartFound = false,
                Order = [],
                Unreachable = Vertices
            };
        }

        var order = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        if (depthFirst)
        {
            var stack = new Stack<string>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (!visited.Add(node))
                {
                    continue;
                }

                order.Add(node);
                // Push higher ids first so the stack pops the lower id next.
                foreach (var (neighbor, _) in Neighbours(node).OrderByDescending(link => link.Neighbor, StringComparer.Ordinal))
                {
                    if (!visited.Contains(neighbor))
                    {
                        stack.Push(neighbor);
                    }
                }
            }
        }
        else
        {
            var queue = new Queue<string>();
            queue.Enqueue(start);
            visited.Add(start);
            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                order.Add(node);
                foreach (var (neighbor, _) in Neighbours(node))
                {
                    if (visited.Add(neighbor))
                    {
                        queue.Enqueue(neighbor);
                    }
                }
            }
        }

        var unreachable = Vertices.Where(id => !visited.Contains(id)).ToList();
        return new TraversalResult
        {
            Algorithm = algorithm,
            Start = start,
            StartFound = true,
            Order = order,
            Unreachable = unreachable
        };
    }

    private GraphPath Search(string from, string to, bool weighted)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        ArgumentException.ThrowIfNullOrWhiteSpace(to);
        if (!_adjacency.ContainsKey(from) || !_adjacency.ContainsKey(to))
        {
            return GraphPath.NotFound(from, to);
        }

        if (from == to)
        {
            return new GraphPath { Found = true, Reachable = true, From = from, To = to, Nodes = [from], Cost = 0 };
        }

        var previous = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!weighted)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal) { from };
            var queue = new Queue<string>();
            queue.Enqueue(from);
            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                foreach (var (neighbor, _) in Neighbours(node))
                {
                    if (!visited.Add(neighbor))
                    {
                        continue;
                    }

                    previous[neighbor] = node;
                    if (neighbor == to)
                    {
                        queue.Clear();
                        break;
                    }

                    queue.Enqueue(neighbor);
                }
            }
        }
        else
        {
            // Equal distances keep the first path. Extraction order is (distance, id), and neighbours
            // are relaxed from low id to high id, so two equal-cost routes always resolve the same way.
            var distance = new Dictionary<string, int>(StringComparer.Ordinal) { [from] = 0 };
            var pending = new PriorityQueue<string, (int Distance, string Id)>();
            pending.Enqueue(from, (0, from));
            while (pending.Count > 0)
            {
                if (!pending.TryDequeue(out var node, out var priority)
                    || node is null
                    || !distance.TryGetValue(node, out var best)
                    || priority.Distance != best)
                {
                    continue;
                }

                if (node == to)
                {
                    break;
                }

                foreach (var (neighbor, weight) in Neighbours(node))
                {
                    var candidate = distance[node] + weight;
                    if (distance.TryGetValue(neighbor, out var known) && candidate >= known)
                    {
                        continue;
                    }

                    distance[neighbor] = candidate;
                    previous[neighbor] = node;
                    pending.Enqueue(neighbor, (candidate, neighbor));
                }
            }
        }

        if (!previous.ContainsKey(to))
        {
            return new GraphPath { Found = true, Reachable = false, From = from, To = to };
        }

        var nodes = new List<string>();
        for (var cursor = to; cursor != from; cursor = previous[cursor])
        {
            nodes.Add(cursor);
        }

        nodes.Add(from);
        nodes.Reverse();
        return new GraphPath
        {
            Found = true,
            Reachable = true,
            From = from,
            To = to,
            Nodes = nodes,
            Cost = PathCost(nodes)
        };
    }

    private int PathCost(IReadOnlyList<string> nodes)
    {
        var cost = 0;
        for (var i = 1; i < nodes.Count; i++)
        {
            var step = Neighbours(nodes[i - 1]).First(link => link.Neighbor == nodes[i]);
            cost += step.Weight;
        }

        return cost;
    }
}

public readonly record struct GraphEdge(string From, string To, int Cost) : IComparable<GraphEdge>
{
    public int CompareTo(GraphEdge other)
    {
        var byCost = Cost.CompareTo(other.Cost);
        if (byCost != 0)
        {
            return byCost;
        }

        var byFrom = string.CompareOrdinal(From, other.From);
        return byFrom != 0 ? byFrom : string.CompareOrdinal(To, other.To);
    }
}

public sealed class TraversalResult
{
    public string Algorithm { get; init; } = string.Empty;
    public string Start { get; init; } = string.Empty;
    public bool StartFound { get; init; }
    public IReadOnlyList<string> Order { get; init; } = [];
    public IReadOnlyList<string> Unreachable { get; init; } = [];
}

public sealed class MstResult
{
    public IReadOnlyList<GraphEdge> Edges { get; init; } = [];
    public int TotalCost { get; init; }
    public int ComponentCount { get; init; }
    public bool Connected { get; init; }
}

public sealed class GraphPath
{
    public bool Found { get; init; }
    public bool Reachable { get; init; }
    public string From { get; init; } = string.Empty;
    public string To { get; init; } = string.Empty;
    public IReadOnlyList<string> Nodes { get; init; } = [];
    public int Cost { get; init; }
    public int Hops => Nodes.Count == 0 ? 0 : Nodes.Count - 1;

    public static GraphPath NotFound(string from, string to) => new() { From = from, To = to };
}
