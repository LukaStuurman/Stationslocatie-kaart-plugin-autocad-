using System.Globalization;
using System.IO;
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
        if (result.Left < -10000 || result.Right > 290000 || result.Bottom < 290000 || result.Top > 640000)
            throw new InvalidOperationException("Kies een gebied in Rijksdriehoekscoördinaten (RD, meters).");
        var width = result.Right - result.Left;
        var height = result.Top - result.Bottom;
        if (width < 1 || height < 1 || width > 5000 || height > 5000)
            throw new InvalidOperationException("Kies een gebied tussen 1 en 5000 meter breed en hoog.");
        return result;
    }

    internal bool Contains(double x, double y) => x >= Left && x <= Right && y >= Bottom && y <= Top;
    internal string Bounds => FormattableString.Invariant($"{Left:R},{Bottom:R},{Right:R},{Top:R}");
}

internal static class StationParcels
{
    private const string BaseUrl = "https://api.pdok.nl/kadaster/brk-kadastrale-kaart/ogc/v1/collections/";
    private const string Crs = "http://www.opengis.net/def/crs/EPSG/0/28992";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    internal static (int Boundaries, int Labels) Write(Database db, ParcelArea area)
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

        var boundaries = 0;
        foreach (var feature in Features("kadastralegrens", area))
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
        if (boundaries == 0) throw new InvalidOperationException("PDOK heeft geen perceelgrenzen gevonden binnen dit gebied.");

        var labels = 0;
        foreach (var feature in Features("perceel", area))
        {
            var properties = feature.GetProperty("properties");
            if (!TryNumber(properties, "perceelnummer_plaatscoordinaat_x", out var x) ||
                !TryNumber(properties, "perceelnummer_plaatscoordinaat_y", out var y) ||
                !area.Contains(x, y) ||
                !properties.TryGetProperty("perceelnummer", out var number) ||
                number.ValueKind != JsonValueKind.Number) continue;
            Add(new DBText { Position = new Point3d(x, y, 0), Height = 1.15, TextString = number.GetRawText() }, labelLayer);
            labels++;
        }
        tr.Commit();
        return (boundaries, labels);

        void AddBoundary(JsonElement line)
        {
            var points = line.EnumerateArray().Select(point => new Point2d(point[0].GetDouble(), point[1].GetDouble())).ToArray();
            if (points.Length < 2) return;
            var polyline = new Polyline(points.Length);
            for (var index = 0; index < points.Length; index++) polyline.AddVertexAt(index, points[index], 0, 0, 0);
            Add(polyline, boundaryLayer);
            boundaries++;
        }
    }

    private static bool TryNumber(JsonElement properties, string name, out double number)
    {
        number = default;
        return properties.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetDouble(out number) && double.IsFinite(number);
    }

    private static IEnumerable<JsonElement> Features(string collection, ParcelArea area)
    {
        var endpoint = new Uri(BaseUrl + collection + "/items");
        var crs = Uri.EscapeDataString(Crs);
        Uri? next = new Uri(endpoint + $"?f=json&limit=1000&bbox={area.Bounds}&crs={crs}&bbox-crs={crs}");
        var visited = new HashSet<Uri>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (next != null)
        {
            if (next.Scheme != Uri.UriSchemeHttps || next.Host != endpoint.Host ||
                next.AbsolutePath != endpoint.AbsolutePath || !visited.Add(next) || visited.Count > 150)
                throw new IOException("De paginering van PDOK is ongeldig of het gebied is te groot.");
            using var request = new HttpRequestMessage(HttpMethod.Get, next);
            request.Headers.Accept.ParseAdd("application/geo+json");
            using var response = Http.Send(request);
            if (!response.IsSuccessStatusCode)
                throw new IOException($"PDOK kon {collection} niet ophalen (HTTP {(int)response.StatusCode}).");
            if (response.Headers.TryGetValues("Content-Crs", out var advertised) &&
                advertised.All(value => !value.Contains("28992", StringComparison.Ordinal)))
                throw new IOException("PDOK leverde de kaart niet in Rijksdriehoekscoördinaten.");
            using var stream = response.Content.ReadAsStream();
            using var data = JsonDocument.Parse(stream);
            foreach (var feature in data.RootElement.GetProperty("features").EnumerateArray())
            {
                if (feature.TryGetProperty("id", out var id) && !seen.Add(id.GetRawText())) continue;
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
}
