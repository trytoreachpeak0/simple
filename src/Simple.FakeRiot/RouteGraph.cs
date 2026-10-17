using QuikGraph;
using QuikGraph.Algorithms;

namespace Simple.FakeRiot;

/// <summary>
/// One map's directed route graph (BC-MAP-003: directed, edge cost is the
/// Euclidean length in millimetres). Routes are shortest paths, which is how
/// real RIoT plans (BC-MAP-003 item 4).
/// </summary>
public sealed class RouteGraph
{
    private readonly AdjacencyGraph<int, TaggedEdge<int, double>> graph = new();

    public RouteGraph(SeedMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var stations = map.Stations.ToDictionary(s => s.Id);
        foreach (var station in map.Stations)
        {
            graph.AddVertex(station.Id);
        }

        foreach (var edge in map.Edges)
        {
            graph.AddEdge(new TaggedEdge<int, double>(edge.From, edge.To, Cost(stations[edge.From], stations[edge.To])));
        }
    }

    public static double Cost(SeedStation from, SeedStation to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        return Math.Round(Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2)));
    }

    /// <summary>The cost in millimetres of the shortest route, or null when there is none.</summary>
    public double? CostBetween(int from, int to)
    {
        if (!graph.ContainsVertex(from) || !graph.ContainsVertex(to))
        {
            return null;
        }

        if (from == to)
        {
            return 0;
        }

        var paths = graph.ShortestPathsDijkstra(edge => edge.Tag, from);
        return paths(to, out var path) ? path.Sum(edge => edge.Tag) : null;
    }
}
