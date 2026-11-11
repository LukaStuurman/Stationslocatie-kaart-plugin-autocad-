using System.Globalization;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Stationslocaties;

internal readonly record struct ParcelArea(double Left, double Bottom, double Right, double Top)
{
    internal static ParcelArea FromCorners(Point3d first, Point3d second)
    {
        var result = new ParcelArea(Math.Min(first.X, second.X), Math.Min(first.Y, second.Y),
            Math.Max(first.X, second.X), Math.Max(first.Y, second.Y));
        if (!double.IsFinite(result.Left) || !double.IsFinite(result.Bottom) ||
            !double.IsFinite(result.Right) || !double.IsFinite(result.Top) ||
            result.Left < -10000 || result.Right > 290000 || result.Bottom < 290000 || result.Top > 640000)
            throw new InvalidOperationException("Kies een gebied in Rijksdriehoekscoördinaten (RD, meters).");
        var width = result.Right - result.Left;
        var height = result.Top - result.Bottom;
        if (width < 1 || height < 1 || width > 5000 || height > 5000)
            throw new InvalidOperationException("Kies een gebied tussen 1 en 5000 meter breed en hoog.");
        return result;
    }

    internal bool Contains(double x, double y) => x >= Left && x <= Right && y >= Bottom && y <= Top;
    internal string Bounds => FormattableString.Invariant($"{Left:R},{Bottom:R},{Right:R},{Top:R}");

    internal async Task<T[]> FetchTiles<T>(double maxSide, Func<ParcelArea, CancellationToken, Task<T>> fetch, CancellationToken cancellation)
    {
        var columns = Math.Max(1, (int)Math.Ceiling((Right - Left) / maxSide));
        var rows = Math.Max(1, (int)Math.Ceiling((Top - Bottom) / maxSide));
        var bounds = this;
        double X(int column) => column == columns ? bounds.Right : bounds.Left + (bounds.Right - bounds.Left) * column / columns;
        double Y(int row) => row == rows ? bounds.Top : bounds.Bottom + (bounds.Top - bounds.Bottom) * row / rows;
        using var limit = new SemaphoreSlim(3);
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var tasks = new List<Task<T>>();
        for (var row = 0; row < rows; row++)
            for (var column = 0; column < columns; column++)
                tasks.Add(Fetch(new ParcelArea(X(column), Y(row), X(column + 1), Y(row + 1))));
        try
        {
            return await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            // A failed tile cancels its siblings. Preserve the original failure for the command line.
            var failure = tasks.FirstOrDefault(task => task.IsFaulted);
            if (failure != null) await failure.ConfigureAwait(false);
            throw;
        }

        async Task<T> Fetch(ParcelArea tile)
        {
            await limit.WaitAsync(abort.Token).ConfigureAwait(false);
            try { return await fetch(tile, abort.Token).ConfigureAwait(false); }
            catch
            {
                abort.Cancel();
                throw;
            }
            finally { limit.Release(); }
        }
    }
}

internal sealed record ParcelData(IReadOnlyList<(double X, double Y)[]> Boundaries,
    IReadOnlyList<(double X, double Y, string Text)> Labels);

internal static class StationParcels
{
    private const string BaseUrl = "https://api.pdok.nl/kadaster/brk-kadastrale-kaart/ogc/v1/collections/";
    private const string Crs = "http://www.opengis.net/def/crs/EPSG/0/28992";
    private static readonly HttpClient Http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        { Timeout = TimeSpan.FromSeconds(45) };

    internal static void Write(Database db, ParcelData data)
    {
        using var tr = db.TransactionManager.StartTransaction();
        var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForWrite);
        using var orange = Autodesk.AutoCAD.Colors.Color.FromRgb(184, 85, 9);
        ObjectId Layer(string name)
        {
            var layer = new LayerTableRecord { Name = name, Color = orange };
            var id = layers.Add(layer);
            tr.AddNewlyCreatedDBObject(layer, true);
            return id;
        }
        var boundaryLayer = Layer("B-WE-KG-GRENS_KADASTRAAL-G");
        var labelLayer = Layer("B-WE-KG-GRENS_KADASTRAAL-S");
        var model = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);

        void Add(Entity entity, ObjectId layer)
        {
            entity.SetDatabaseDefaults(db);
            entity.LayerId = layer;
            entity.ColorIndex = 256;
            model.AppendEntity(entity);
            tr.AddNewlyCreatedDBObject(entity, true);
        }

        foreach (var points in data.Boundaries)
        {
            var polyline = new Polyline(points.Length);
            for (var index = 0; index < points.Length; index++)
                polyline.AddVertexAt(index, new Point2d(points[index].X, points[index].Y), 0, 0, 0);
            Add(polyline, boundaryLayer);
        }
        foreach (var label in data.Labels)
            Add(new DBText { Position = new Point3d(label.X, label.Y, 0), Height = 1.15, TextString = label.Text }, labelLayer);
        tr.Commit();
    }

    internal static ParcelData Download(ParcelArea area, CancellationToken cancellation)
    {
        var seenBoundaries = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var seenParcels = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var tiles = area.FetchTiles(1250, (tile, token) =>
            Task.Run(() => DownloadTile(tile, area, seenBoundaries, seenParcels, token), token), cancellation).GetAwaiter().GetResult();
        var boundaries = tiles.SelectMany(tile => tile.Boundaries).ToArray();
        if (boundaries.Length == 0) throw new InvalidOperationException("PDOK heeft geen perceelgrenzen gevonden binnen dit gebied.");
        return new(boundaries, tiles.SelectMany(tile => tile.Labels).Distinct().ToArray());
    }

    private static ParcelData DownloadTile(ParcelArea tile, ParcelArea area,
        ConcurrentDictionary<string, byte> seenBoundaries, ConcurrentDictionary<string, byte> seenParcels,
        CancellationToken cancellation)
    {
        var boundaries = new List<(double X, double Y)[]>();
        foreach (var feature in Features("kadastralegrens", tile, seenBoundaries, cancellation))
        {
            if (!feature.TryGetProperty("geometry", out var geometry) || geometry.ValueKind == JsonValueKind.Null) continue;
            var lines = geometry.GetProperty("coordinates");
            switch (geometry.GetProperty("type").GetString())
            {
                case "LineString":
                    AddBoundary(lines);
                    break;
                case "MultiLineString":
                    foreach (var line in lines.EnumerateArray()) AddBoundary(line);
                    break;
                default:
                    throw new IOException("PDOK gaf een onverwacht geometrieformaat voor een perceelgrens.");
            }
        }
        var labels = new List<(double X, double Y, string Text)>();
        foreach (var feature in Features("perceel", tile, seenParcels, cancellation))
        {
            var properties = feature.GetProperty("properties");
            if (!TryNumber(properties, "perceelnummer_plaatscoordinaat_x", out var x) ||
                !TryNumber(properties, "perceelnummer_plaatscoordinaat_y", out var y) ||
                !area.Contains(x, y) ||
                !properties.TryGetProperty("perceelnummer", out var number) ||
                number.ValueKind != JsonValueKind.Number) continue;
            labels.Add((x, y, number.GetRawText()));
        }
        return new(boundaries, labels);

        void AddBoundary(JsonElement line)
        {
            var points = line.EnumerateArray().Select(point => (point[0].GetDouble(), point[1].GetDouble())).ToArray();
            if (points.Length >= 2) boundaries.Add(points);
        }
    }

    private static bool TryNumber(JsonElement properties, string name, out double number)
    {
        number = default;
        return properties.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetDouble(out number) && double.IsFinite(number);
    }

    private static IEnumerable<JsonElement> Features(string collection, ParcelArea area,
        ConcurrentDictionary<string, byte> seen, CancellationToken cancellation)
    {
        var endpoint = new Uri(BaseUrl + collection + "/items");
        var crs = Uri.EscapeDataString(Crs);
        Uri? next = new Uri(endpoint + $"?f=json&limit=1000&bbox={area.Bounds}&crs={crs}&bbox-crs={crs}");
        var visited = new HashSet<Uri>();
        while (next != null)
        {
            if (next.Scheme != Uri.UriSchemeHttps || next.Host != endpoint.Host ||
                next.AbsolutePath != endpoint.AbsolutePath || !visited.Add(next) || visited.Count > 150)
                throw new IOException("De paginering van PDOK is ongeldig of het gebied is te groot.");
            using var data = FetchPage(next, collection, visited.Count, cancellation);
            foreach (var feature in data.RootElement.GetProperty("features").EnumerateArray())
            {
                cancellation.ThrowIfCancellationRequested();
                if (feature.TryGetProperty("id", out var id) && id.ValueKind != JsonValueKind.Null && !seen.TryAdd(id.GetRawText(), 0)) continue;
                yield return feature;
            }

            Uri? following = null;
            foreach (var link in data.RootElement.GetProperty("links").EnumerateArray())
            {
                if (link.GetProperty("rel").GetString() != "next") continue;
                following = new Uri(next, link.GetProperty("href").GetString()!);
                break;
            }
            next = following;
        }
    }

    private static JsonDocument FetchPage(Uri uri, string collection, int page, CancellationToken cancellation)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Accept.ParseAdd("application/geo+json");
                using var response = Http.Send(request, cancellation);
                if (!response.IsSuccessStatusCode)
                {
                    var status = (int)response.StatusCode;
                    if (attempt == maxAttempts || status is not (408 or 429 or 500 or 502 or 503 or 504))
                        throw new IOException($"PDOK kon {collection}, pagina {page}, niet ophalen (HTTP {status}).");
                }
                else
                {
                    if (response.Headers.TryGetValues("Content-Crs", out var advertised) &&
                        advertised.All(value => !value.Contains("28992", StringComparison.Ordinal)))
                        throw new IOException("PDOK leverde de kaart niet in Rijksdriehoekscoördinaten.");
                    using var stream = response.Content.ReadAsStream();
                    return JsonDocument.Parse(stream);
                }
            }
            catch (OperationCanceledException ex) when (!cancellation.IsCancellationRequested)
            {
                if (attempt == maxAttempts)
                    throw new IOException($"PDOK reageerde na {maxAttempts} pogingen niet op {collection}, pagina {page}.", ex);
            }
            catch (HttpRequestException ex)
            {
                if (attempt == maxAttempts)
                    throw new IOException($"Verbinding met PDOK mislukt voor {collection}, pagina {page}: {ex.Message}", ex);
            }
            if (cancellation.WaitHandle.WaitOne(TimeSpan.FromSeconds(2 * attempt)))
                cancellation.ThrowIfCancellationRequested();
        }
    }
}
