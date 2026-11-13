# Smart-X IoT Mesh Gateway

Smart-X is a simulated ESP32 mesh gateway and operator dashboard for a small South African campus. It ingests mixed telemetry, keeps a live processing pipeline, answers timestamp-range history queries, and routes sensors to a central gateway across a weighted radio mesh.

There is no database and no physical radio. Queues, live state, history, faults, commands, and the mesh exist only in the API process. Stopping the API discards that state. A bounded drain runs on graceful shutdown.

---

## Problem

Operators need one place to see whether a gutter is dry, a feeder is overloaded, and whether a sensor can still reach the gateway when a hop fails. The placement of a device (facility, zone, sub-zone) is not the same thing as the wireless path it uses. Smart-X keeps those models separate:

- Part 1 accepts packets, checks a deployment path, and shows ingestion health.
- Part 2 processes packets in order, raises unique faults, and suggests actions without sending a command.
- Part 3 indexes history in an AVL tree and routes the seeded devices over a weighted undirected graph.

---

## Architecture

```text
Browser (Blazor WebAssembly, port 5197)
        |  HTTP, camelCase JSON
        v
SmartX.Api (Minimal API, port 5057)
        |-- SensorRegistry, DeploymentValidator
        |-- TelemetryIngestionService  -> IngestionBuffer<T>
        |-- PendingTelemetryQueue       -> TelemetryProcessingWorker
        |-- TelemetryHistoryStore       -> SortedDictionary + per-sensor AvlTree
        |-- ActiveFaultSet, RecommendationService, CommandService
        |-- MeshTopologyService         -> WeightedGraph
        v
SmartX.Shared (packets, AVL, graph, DTOs)
```

Solution file: `SmartX.slnx`.

| Project | Role |
| --- | --- |
| `SmartX.Api` | ASP.NET Core Minimal API (`net10.0`), OpenAPI, seed hosted service, processing worker |
| `SmartX.Client` | Blazor WebAssembly dashboard (`net10.0`) |
| `SmartX.Shared` | Packets, deployment nodes, `AvlTree<TKey,TValue>`, `WeightedGraph`, mesh contracts |
| `SmartX.Tests` | xUnit 2.9.3 with `Microsoft.AspNetCore.Mvc.Testing` |

Client routes:

| Route | Page |
| --- | --- |
| `/` | Home menu: ingestion, command stream, topology |
| `/ingestion` | Send a packet and read the health constellation |
| `/register` | Register a sensor and attach an encrypted file |
| `/tools`, `/gateway-lab` | Meter addition, location check, batch history |
| `/command-stream` | Live devices, commands, history range, faults, recommendations |
| `/topology` | Mesh, traversals, spanning tree, routes, demonstration links |
| `/not-found` | Unknown route |

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download). Projects target `net10.0`.
- Optional: Docker Desktop with Docker Compose, for the container setup below.

Package versions from the project files:

- `Microsoft.AspNetCore.OpenApi` 10.0.12
- `Microsoft.AspNetCore.Components.WebAssembly` 10.0.12
- `Microsoft.NET.Test.Sdk` 17.14.1
- `xunit` 2.9.3, `xunit.runner.visualstudio` 3.1.4
- `Microsoft.AspNetCore.Mvc.Testing` 10.0.12

---

## Restore, build, test, and run

From the repository root:

```bash
dotnet restore SmartX.slnx
dotnet build SmartX.slnx
dotnet test SmartX.Tests/SmartX.Tests.csproj
```

`SmartX.Tests` defines 106 `[Fact]` methods and no `[Theory]` methods. Run the commands above on the machine used for assessment. A count from another machine is not a substitute for that run.

**Terminal 1 — API** (`SmartX.Api/Properties/launchSettings.json`, profile `http`):

```bash
dotnet run --project SmartX.Api/SmartX.Api.csproj --launch-profile http
```

Wait for `Now listening on: http://localhost:5057`.

**Terminal 2 — client** (`SmartX.Client/Properties/launchSettings.json`, profile `http`):

```bash
dotnet run --project SmartX.Client/SmartX.Client.csproj --launch-profile http
```

Open [http://localhost:5197](http://localhost:5197). Start the API first.

Ready check:

```bash
curl -sS http://localhost:5057/
```

Expect HTTP 200 and `"status":"ready"`.

OpenAPI: [http://localhost:5057/openapi/v1.json](http://localhost:5057/openapi/v1.json)

---

## Ports, configuration, and troubleshooting

| Process | `http` profile | `https` profile |
| --- | --- | --- |
| API | http://localhost:5057 | https://localhost:7204 and http://localhost:5057 |
| Client | http://localhost:5197 | https://localhost:7091 and http://localhost:5197 |

`SmartX.Client/wwwroot/appsettings.json` sets `ApiBaseUrl` to `http://localhost:5057`. If the API port changes, edit that file and restart the client.

CORS in `SmartX.Api/Program.cs` allows `https://localhost:7091`, `http://localhost:5197`, and `http://localhost:8088`.

### Docker Compose

`docker-compose.yml` builds the two Dockerfiles already in the tree.

| Service | Build | Container listen | Published host port |
| --- | --- | --- | --- |
| `smartx-api` | `SmartX.Api/Dockerfile`, runtime image `mcr.microsoft.com/dotnet/aspnet:10.0` | 8080, set by `ASPNETCORE_URLS=http://+:8080` | **5057** |
| `smartx-client` | `SmartX.Client/Dockerfile`, runtime image `nginx:1.27-alpine` | 80, from `SmartX.Client/nginx.conf` | **5197** |

The client service depends on the API. The image still contains `ApiBaseUrl` of `http://localhost:5057`, so the browser calls the published API port on the host.

```bash
docker compose up --build
```

Open [http://localhost:5197](http://localhost:5197). The readiness check remains `curl -sS http://localhost:5057/`.

| Symptom | What to check |
| --- | --- |
| Client says it cannot reach the API | API is not listening on 5057, or the page was opened from an origin that is not in the CORS list |
| Blank WASM page after a code change | Rebuild the client and hard-refresh the browser |
| Upload fails | Register the sensor first. The limit is 10 MB |
| Live reading does not change | The packet timestamp is older than `lastSeenUtc`. History can still store it |
| No recommendation | The reading is in band, the evidence is older than 6 hours, or the same fault is already active |
| History range looks empty | The window is outside the retained 2000 rows, or the end is before the start |
| Port already in use | Stop the previous `dotnet run` before starting the rebuilt API or client |
| Mesh looks unchanged after a code edit | The mesh lives in the API process. Restart the API |

A development attachment key is in `SmartX.Api/appsettings.json`. It is not a production secret. There is no authentication.

---

## Using the three modules

The home page links to all three. Ingestion, the command stream, and topology also link to each other.

### 1. Sensor ingestion and telemetry

Open **Sensor Data Ingestion and Telemetry**.

1. On `/ingestion`, choose a seeded sensor.
2. Send moisture (`0`–`100`), power (`0`–`20000` W), or a valve state.
3. The health constellation and latest-packet table refresh from `GET /api/gateway/integrity` and `GET /api/telemetry/recent`.

That constellation is an ingestion-health view. It is not the radio mesh. Gold, silver, and bronze seals follow `GatewayIntegrityService.Rank`: **Alert** when any coupling alarm or fractured node exists, **Stable** when neither exists and the in-range streak is at least 80, **Watch** otherwise.

`/register` accepts a MAC, a display name, a category, and a path that exists in the campus tree. Examples:

- `Facility A -> Zone 1 -> Sub-Zone B`
- `Facility B -> Zone 1 -> Transformer Bay`
- `Facility C -> Zone Lobby -> Stack B`

`/tools` adds two `PowerMetric` values, validates a path, and flattens a jagged batch.

### 2. Commands, history, and recommendations

Open **Real-Time Command Stream and History** (`/command-stream`). The page polls about every 2 seconds. There is no SignalR.

- Stats come from `GET /api/runtime/stats`.
- Suggested actions come from `GET /api/runtime/recommendations`. They do not send a command.
- Actuator ON/OFF uses `POST /api/commands`. Undo uses `POST /api/commands/undo`.
- The history range card calls `GET /api/runtime/history/range`. The newest-window table still uses `GET /api/runtime/history`.
- Active faults are `CODE:MAC` keys. Resolve calls `POST /api/runtime/faults/resolve`.

The range inputs use `datetime-local` bound as `yyyy-MM-ddTHH:mm:ss`. An end value with zero seconds is expanded to the end of that minute before it is sent as UTC.

To see a feeder overload, send current power readings for both seeded clamps so the sum exceeds 3500 W. The health page shows **Feeder overload**. The command stream shows `GRID-OVERLOAD` on the first power meter, a coupling recommendation, and an alerting row. Send a newer pair whose sum is 3500 W or less. The fault, the recommendation, and the health alarm clear. The journal records `CLEAR`.

### 3. Topology and mesh routing

Open **Network Topology and Mesh Routing** (`/topology`).

- Gold is the gateway, cyan is a reachable sensor, grey is offline, and red is online but unreachable.
- A purple edge is in the spanning tree. A blue edge is the selected Dijkstra route.
- Choose a sensor to see the fewest-hop path, the least-cost path, and the path along the spanning tree.
- BFS and DFS show the visit order from the gateway or from an online sensor.
- **Add a demonstration link** connects two registered nodes with a non-negative cost you type. The page states that the number is not measured signal strength.
- Taking an intermediate hop offline recalculates the route. Restore puts the node back.

Seeded demonstration, with every catalogue device online:

| Sensor | Fewest hops | Least cost (Dijkstra) | Path on the spanning tree |
| --- | --- | --- | --- |
| Unit 1204 water pulse `3C:71:BF:22:10:AA` | Gateway, HVAC damper, water pulse. Cost 22 | Gateway, Bay-1, Bay-2, feeder, water pulse. Cost 15 | Same nodes as Dijkstra. Cost 15 |
| Canopy DHT22 `24:6F:28:A1:B2:F1` | Direct gateway hop. Cost 9 | Direct gateway hop. Cost 9 | Gateway, gutter A, gutter B, EC probe, canopy. Cost 13 |

Kruskal's tree for the seeded catalogue costs **33** and has **10** edges. Taking the feeder `30:AE:A4:10:01:03` offline moves the water-pulse least-cost path through Bay-2 at cost **16**. Restore returns cost 15.

---

## API endpoints

JSON uses camelCase. Errors use `{ "message": "..." }`. `SensorCategory` serialises as a number: `0` Environmental, `1` PowerConsumption, `2` Actuator. MAC addresses are trimmed, hyphens become colons, and letters are uppercased.

### Part 1

| Method | Route |
| --- | --- |
| GET | `/` |
| GET | `/api/sensors`, `/api/sensors/{mac}` |
| POST | `/api/sensors/register` |
| POST | `/api/sensors/{mac}/upload` |
| POST | `/api/telemetry/ingest-float`, `/ingest-int`, `/ingest-bool`, `/ingest` |
| GET | `/api/telemetry/recent` |
| POST | `/api/telemetry/batch` |
| GET | `/api/telemetry/history` |
| POST | `/api/metrics/aggregate` |
| GET | `/api/deployment/tree` |
| POST | `/api/deployment/validate` |
| GET | `/api/gateway/integrity` |

`GET /api/telemetry/history` is the Part 1 jagged-batch flatten. It is not the runtime history.

```bash
curl -sS -H 'Content-Type: application/json' \
  -d '{"deviceId":"24:6F:28:A1:B2:C4","timestamp":"'"$(date -u +%Y-%m-%dT%H:%M:%SZ)"'","value":45.0,"metricName":"soil_moisture","unit":"%VWC"}' \
  http://localhost:5057/api/telemetry/ingest-float
```

HTTP 200 means the packet entered the ingestion buffer and the FIFO. HTTP 429 means the processing queue (capacity 2048) rejected it. The worker is normally draining, so 429 is demonstrated by `PendingQueueTests` and `CommandAndIngestionTests`, not by a casual live call.

```bash
curl -sS -H 'Content-Type: application/json' \
  -d '{"targetPath":"Facility B -> Zone 1 -> Transformer Bay","sensorCategory":"PowerConsumption"}' \
  http://localhost:5057/api/deployment/validate
```

### Part 2

| Method | Route |
| --- | --- |
| GET | `/api/runtime/stats` |
| GET | `/api/runtime/devices`, `/api/runtime/devices/{mac}` |
| GET | `/api/runtime/history?take=&mac=` |
| GET | `/api/runtime/history/range?mac=&from=&to=` |
| GET | `/api/runtime/faults` |
| POST | `/api/runtime/faults/resolve` |
| GET | `/api/runtime/journal` |
| GET | `/api/runtime/recommendations` |
| POST | `/api/runtime/search-observations` |
| GET | `/api/commands` |
| POST | `/api/commands` |
| POST | `/api/commands/undo` |

`GET /api/runtime/history` returns the newest retained rows, up to `take` (default 40, clamped to 1–500). The range route is inclusive, oldest-first, and limited to one registered MAC.

```bash
curl -sS 'http://localhost:5057/api/runtime/history/range?mac=24:6F:28:A1:B2:C3&from=2026-01-01T00:00:00Z&to=2026-12-31T23:59:59Z'
```

Invalid MAC, missing timestamps, or an inverted window return HTTP 400. An unknown but well-formed MAC returns HTTP 404.

### Part 3 mesh

| Method | Route |
| --- | --- |
| GET | `/api/mesh` |
| GET | `/api/mesh/traverse?algorithm=bfs\|dfs&start=` |
| GET | `/api/mesh/mst` |
| GET | `/api/mesh/route?mac=` |
| POST | `/api/mesh/availability` |
| POST | `/api/mesh/links` |
| DELETE | `/api/mesh/links?from=&to=` |

```bash
curl -sS 'http://localhost:5057/api/mesh/route?mac=3C:71:BF:22:10:AA'
curl -sS http://localhost:5057/api/mesh/mst
curl -sS -H 'Content-Type: application/json' \
  -d '{"macAddress":"30:AE:A4:10:01:03","online":false}' \
  http://localhost:5057/api/mesh/availability
```

`algorithm` must be `bfs` or `dfs`. The gateway cannot be taken offline. A negative link cost, a self-loop, a duplicate link, and an attempt to delete a catalogue link return HTTP 400. An unknown sensor returns HTTP 404.

Seeded devices after `MeshSeedHostedService` starts (RNG seed 7321, 10 devices × 80 packets):

| MAC | Name | Location |
| --- | --- | --- |
| `24:6F:28:A1:B2:C3` | NFT-Gutter-A moisture | Facility A → Zone 1 → Sub-Zone B |
| `24:6F:28:A1:B2:C4` | NFT-Gutter-B moisture | Facility A → Zone 1 → Sub-Zone B |
| `24:6F:28:A1:B2:D1` | Irrigation solenoid | Facility A → Zone 1 → Sub-Zone B |
| `24:6F:28:A1:B2:E1` | Reservoir EC probe | Facility A → Zone 1 → Sub-Zone Reservoir |
| `24:6F:28:A1:B2:F1` | Canopy DHT22 | Facility A → Zone 2 → Sub-Zone Canopy |
| `30:AE:A4:10:01:01` | Bay-1 CT clamp | Facility B → Zone 1 → Transformer Bay |
| `30:AE:A4:10:01:02` | Bay-2 CT clamp | Facility B → Zone 1 → Transformer Bay |
| `30:AE:A4:10:01:03` | Feeder breaker | Facility B → Zone 1 → Transformer Bay |
| `3C:71:BF:22:10:AA` | Unit 1204 water pulse | Facility C → Zone Lobby → Stack B |
| `3C:71:BF:22:10:AB` | HVAC damper | Facility C → Zone Lobby → Stack B |

---

## AVL history index

`TelemetryHistoryStore` keeps two views of the same retained rows, under one lock:

- A `SortedDictionary<HistoryKey, ProcessedReading>` for the newest-first window. `HistoryKey` is UTC timestamp, then a monotonic sequence, so equal timestamps stay distinct.
- One `AvlTree<HistoryKey, ProcessedReading>` per sensor for an inclusive timestamp range.

Capacity is **2000**. Evicting the oldest dictionary key deletes that key from the sensor tree. A range cannot return a row the dictionary has already dropped.

`AvlTree<TKey,TValue>` (`SmartX.Shared/Collections/AvlTree.cs`) is a height-balanced binary search tree. Telemetry often arrives in time order. A plain BST would become a linked list. Each insert and delete ends in `Rebalance`. The balance factor is left height minus right height. A factor above 1 rotates right, with a left rotation of the left child first when that child is right-heavy. A factor below -1 rotates left, with a right rotation of the right child first when that child is left-heavy.

```csharp
private static Node RotateLeft(Node pivot)
{
    var raised = pivot.Right!;
    pivot.Right = raised.Left;
    raised.Left = pivot;
    Update(pivot);
    Update(raised);
    return raised;
}
```

`RotateRight` is the mirror. `Update` sets `Height` to one plus the taller child. An empty tree has height 0. `CheckInvariants` checks sorted order, stored height, and that the two child heights differ by at most one.

`RangeInclusive` walks only the subtrees that can contain the window:

```csharp
if (node.Key.CompareTo(low) > 0)
    CollectRange(node.Left, low, high, matches);
if (node.Key.CompareTo(low) >= 0 && node.Key.CompareTo(high) <= 0)
    matches.Add(node.Value);
if (node.Key.CompareTo(high) < 0)
    CollectRange(node.Right, low, high, matches);
```

The class comment states the resulting range cost as O(log n + matches). Insert and delete are O(log n) because the height stays near 1.44 log2(n). Tests in `AvlHistoryTests` cover all four rotations, deletion rotations, duplicate keys, inclusive bounds, per-sensor isolation, and eviction. `HistoryRangeApiTests` checks the HTTP contract, including hyphenated MACs and the unchanged newest-first history route.

---

## Weighted mesh

`WeightedGraph` stores an undirected adjacency list. Neighbours are read in ordinal id order. `AddEdge` rejects a self-loop, a missing vertex, a duplicate pair, and a negative weight. The gateway id is the constant `GATEWAY`.

`MeshTopologyService` builds two graphs on each query: every registered device plus the gateway, and the same graph with offline devices removed. Catalogue edges are added only when both ends exist. Offline sensors remain in the snapshot with `offline: true` and `reachable: false`. Their edges are inactive.

The catalogue is a fixed demonstration plan in `MeshTopologyService.Catalogue`. It is not a star. Facility C hangs off the feeder and Bay-2. A costly direct damper hop (cost 20) and a costly direct canopy hop (cost 9) exist so the algorithms can disagree. The service constant `CostSemantics` is: "Demonstration link cost. A lower number is a cheaper hop. These values are not measured RSSI."

`TelemetryPacket<T>` has an `RssiDbm` field that defaults to `"-70"`. Ingestion does not copy it into `TelemetryWorkItem`, and the mesh does not read it. Do not treat that default as a measured link.

### BFS and DFS

BFS uses `Queue<string>`. A neighbour is marked visited when it is enqueued, so a cycle cannot enqueue it twice. DFS uses `Stack<string>`. Higher neighbour ids are pushed first, so the lower id is popped next. Both return the visit order and the vertices that were not visited.

Fewest-hop routing is a separate BFS from the gateway to the sensor. It minimises the number of hops. The cost it reports is the sum of the demonstration weights along that hop path, which can be larger than Dijkstra's cost.

### Kruskal minimum spanning tree

`MinimumSpanningForest` sorts edges with `GraphEdge.CompareTo` (cost, then `From`, then `To`) and accepts an edge when union-find puts its ends in different components. Union-find uses path compression and union by rank. `componentCount` is vertex count minus chosen edges. The mesh is reported connected when that count is at most 1. An empty graph is treated as connected with cost 0.

The tree minimises the **sum** of the chosen links. The service text and the topology page both say it is not the least-cost route for every sensor. The canopy row in the table above is the concrete case: Dijkstra costs 9 and the tree path costs 13.

### Dijkstra least-cost routing

`LeastCost` uses `PriorityQueue<string, (int Distance, string Id)>`. A candidate replaces a known distance only when it is strictly smaller, so the first equal-cost path wins. Stale queue entries are skipped when the dequeued distance no longer matches the best known distance. Weights are non-negative, which is the condition Dijkstra requires.

`GET /api/mesh/route` returns all three paths: `fewestHopPath`, `leastCostPath`, and `spanningTreePath`, with their costs and a short explanation when they differ.

---

## Structures that answer different questions

| Structure | Question it answers | Where it lives |
| --- | --- | --- |
| Deployment tree | Is this facility → zone → sub-zone path legal for this category? | `DeploymentValidator`. Root remains Facility A (6 nodes). Facilities B and C are additional roots. `GET /api/deployment/tree` reports 12 nodes. |
| Ingestion constellation | Is the latest packet inside its sensor band, and is a coupling alarm active? | `GatewayIntegrityService` and the SVG on `/ingestion` |
| Radio mesh | Which demonstration hops exist, who is offline, and who can reach the gateway? | `WeightedGraph` via `MeshTopologyService` |
| Spanning tree | Which subset of those hops connects the online mesh at the smallest total cost? | Kruskal in `MinimumSpanningForest` |
| Least-cost route | What is the cheapest path from the gateway to this sensor? | Dijkstra in `LeastCost` |

A device can be in Facility C and still reach the gateway through Facility B. Registering a sensor adds a vertex. It does not add a wireless edge.

---

## Complexity, with the code that implements it

| Operation | What the code does | Bound used in this project |
| --- | --- | --- |
| AVL insert, delete | Descend one path and rotate | O(log n), height kept within the AVL bound checked by `AlgorithmPerformanceTests` |
| AVL range | Skip subtrees outside the window | O(log n + matches), as stated on `AvlTree<TKey,TValue>` |
| BFS, DFS | Each visited vertex once; each neighbour list once | O(V + E) on the adjacency list |
| Kruskal | Sort `Edges`, then union-find | O(E log E) for the sort, with near-constant union-find operations |
| Dijkstra | Binary heap decrease by enqueueing a new entry | O((V + E) log V) for non-negative weights |

Those bounds describe the algorithms as written. `AlgorithmPerformanceTests` asserts height, visit counts, edge sets, and path costs on fixed sizes. It prints elapsed milliseconds with `Stopwatch` and does not fail when that reading is slower. The printed time is one Debug process, with no warmup and no repeated samples. The AVL insert loop also calls `CheckInvariants` on every 1024th key. The campus timing wraps `MeshTopologyService.Route`, which rebuilds both graphs, and the failure timing includes `TrySetOnline` as well as the next route.

| Check in `AlgorithmPerformanceTests` | What the test asserts |
| --- | --- |
| Sorted AVL insert, then an inclusive range | 8192 keys. The window `2000`–`3999` returns 2000 values. Height stays below `2 * log2(n)` and within the AVL minimum-node bound |
| History insert, then a timestamp range | 6000 readings. The window returns 500 rows, from second 1000 through second 1499 |
| History eviction | Capacity 1500 after 4000 inserts. The older window is empty and the kept window has 1500 rows |
| BFS, DFS, Kruskal, and Dijkstra on a path | 2000 vertices and 1999 edges of weight 1. Every vertex is visited, the forest has 1999 edges and total cost 1999, and the least-cost path costs 1999 |
| Seeded campus route, then feeder failure | Least cost 15 and fewest-hop cost 22 while every catalogue device is online. With `30:AE:A4:10:01:03` offline the least cost is 16 and that MAC is absent from the path. Restoring the feeder returns cost 15 |

Run the same checks with:

```bash
dotnet test SmartX.Tests/SmartX.Tests.csproj --filter FullyQualifiedName~AlgorithmPerformanceTests
```

---

## Part 2 structures that Part 3 still uses

| Structure | Class | Behaviour |
| --- | --- | --- |
| `Queue<T>` | `PendingTelemetryQueue` | Bounded FIFO, capacity 2048 |
| `PriorityQueue` | `AlertPriorityInbox` | Critical priority 0 before warning priority 1 |
| `Stack<T>` | `CommandUndoStore` | Undoable commands. A separate audit list keeps the visible history |
| `Dictionary` | `LiveDeviceStore` | Live state keyed by normalised MAC |
| `SortedDictionary` | `TelemetryHistoryStore` | Newest-first window, cap 2000 |
| `HashSet` | `ActiveFaultSet` | Unique `CODE:MAC` |

`IngestionBuffer<T>` is the Part 1 ring buffer. `SensorRegistry` uses `ConcurrentDictionary` for profiles. It is not a substitute for the live-device dictionary.

Bands used by both health and the runtime engine: volumetric water content 28–82 when the unit contains `VWC`; clamp warning above 2800 W; feeder overload when the first two power meters sum to more than 3500 W. Conductivity (`mS/cm`) is not soil moisture (`SoilMoistureReading.IsVolumetricWaterContent`).

Recommendation categories produced by `RecommendationService`: `threshold`, `trend`, `coupling` (irrigation deadlock and feeder overload), `diagnostic`, `pattern`, and `search`. Insights read state. They do not call `CommandService`.

---

## Tests

| Area | Class |
| --- | --- |
| FIFO, priority queue, undo, history, faults | `PendingQueueTests`, `PriorityAlertTests`, `CommandAndIngestionTests`, `DeviceHistoryFaultTests`, `CorrectnessFixesTests` |
| Recommendations and dashboard filters | `RecommendationEngineTests`, `DeviceDashboardFilterTests` |
| Moisture versus conductivity, health rank | `CouplingAndHealthTests` |
| AVL rotations, range, eviction | `AvlHistoryTests`, `HistoryRangeApiTests` |
| Graph, MST, Dijkstra, failure, operator links | `MeshGraphTests`, `MeshApiTests` |
| Feeder recovery, facility tree, new-sensor link | `GridOverloadRecoveryTests`, `DeploymentHierarchyTests`, `IntegrationConsistencyTests` |
| Measured size checks | `AlgorithmPerformanceTests` |
| Part 1 HTTP contract | `Part1ApiContractTests` |

There is no automated browser project. `dotnet test` is the automated suite. The dashboard is exercised by opening the client routes above against a running API.

---

## Limitations

- Process memory only. Restarting the API clears queues, history, faults, commands, offline flags, and operator links.
- Link numbers are a demonstration catalogue plus any link an operator adds. They are not measured RSSI.
- A newly registered sensor has no catalogue edge, so it stays unreachable until a demonstration link is added.
- `POWER-CRITICAL` and `POWER-HIGH` clear when that clamp's own reading is at or below 2800 W. The feeder fault is the one that follows the 3500 W sum.
- The deployment tree validates placement. It does not describe radio hops.
- No authentication, no durable store, and no push channel. The client polls.
- Recreating the API container clears the same in-memory state as stopping the API process.

## Further work

Durable history, authenticated operators, a measured link metric from the device, and a push channel would remove the limits above.
