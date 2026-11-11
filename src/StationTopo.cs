using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Stationslocaties;

internal readonly record struct TopoVertex(double X, double Y, double Bulge = 0);
internal sealed record TopoStyle(string Layer);
internal sealed record TopoSource(string Id, string Type, string Category, string Function, string BagId);
internal sealed record TopoShape(TopoStyle Style, TopoSource Source, TopoVertex[] Vertices, bool Closed);
internal sealed record TopoLabel(TopoStyle Style, TopoSource Source, TopoVertex Position, string Text, double Rotation);
internal sealed record TopoData(IReadOnlyList<TopoShape> Shapes, IReadOnlyList<TopoLabel> Labels);

internal static partial class StationTopo
{
    private static readonly Uri Endpoint = new("https://api.pdok.nl/kadaster/bgt/download/v1_0/");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly XNamespace Gml = "http://www.opengis.net/gml";
    private static readonly string[] FeatureTypes =
    [
        "bak", "begroeidterreindeel", "bord", "gebouwinstallatie", "installatie", "kast", "kunstwerkdeel", "mast",
        "onbegroeidterreindeel", "ondersteunendwaterdeel", "ondersteunendwegdeel", "openbareruimtelabel",
        "overbruggingsdeel", "overigbouwwerk", "overigescheiding", "paal", "pand", "put", "scheiding", "sensor",
        "spoor", "straatmeubilair", "tunneldeel", "vegetatieobject", "waterdeel", "waterinrichtingselement",
        "wegdeel", "weginrichtingselement"
    ];

    internal static TopoData Download(ParcelArea area, CancellationToken cancellation) =>
        DownloadAsync(area, cancellation).GetAwaiter().GetResult();

    private static async Task<TopoData> DownloadAsync(ParcelArea area, CancellationToken cancellation)
    {
        var polygon = FormattableString.Invariant($"POLYGON(({area.Left:R} {area.Bottom:R},{area.Right:R} {area.Bottom:R},{area.Right:R} {area.Top:R},{area.Left:R} {area.Top:R},{area.Left:R} {area.Bottom:R}))");
        using var start = await Http.PostAsJsonAsync(new Uri(Endpoint, "full/custom"),
            new { featuretypes = FeatureTypes, format = "gmllight", geofilter = polygon }, cancellation).ConfigureAwait(false);
        using var initial = await ReadJson(start, cancellation).ConfigureAwait(false);
        var statusUrl = Link(initial.RootElement, "status");
        Uri zipUrl;
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellation).ConfigureAwait(false);
            using var reply = await Http.GetAsync(statusUrl, cancellation).ConfigureAwait(false);
            if ((int)reply.StatusCode == 429) continue;
            using var status = await ReadJson(reply, cancellation).ConfigureAwait(false);
            switch (status.RootElement.GetProperty("status").GetString())
            {
                case "COMPLETED":
                    zipUrl = Link(status.RootElement, "download");
                    break;
                case "FAILED":
                    throw new IOException("PDOK kon de BGT-levering niet samenstellen.");
                case "PENDING":
                case "RUNNING":
                case "QUEUED":
                    continue;
                default:
                    // PDOK can add intermediate statuses. Cancellation bounds the waiting time.
                    continue;
            }
            break;
        }

        using var archiveResponse = await Http.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        archiveResponse.EnsureSuccessStatusCode();
        var tempPath = Path.Combine(Path.GetTempPath(), "Stationslocaties-BGT-" + Guid.NewGuid().ToString("N") + ".zip");
        using var archiveFile = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            65536, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        using var source = await archiveResponse.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        var buffer = new byte[65536];
        int size;
        while ((size = await source.ReadAsync(buffer.AsMemory(), cancellation).ConfigureAwait(false)) != 0)
        {
            if (archiveFile.Length + size > 350_000_000) throw new IOException("De BGT-levering is te groot. Kies een kleiner gebied.");
            await archiveFile.WriteAsync(buffer.AsMemory(0, size), cancellation).ConfigureAwait(false);
        }
        archiveFile.Position = 0;
        using var archive = new ZipArchive(archiveFile, ZipArchiveMode.Read);
        return ReadArchive(archive, area, cancellation);
    }

    private static Uri Link(JsonElement response, string name)
    {
        var uri = new Uri(Endpoint, response.GetProperty("_links").GetProperty(name).GetProperty("href").GetString()!);
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != Endpoint.Host ||
            !uri.AbsolutePath.StartsWith(Endpoint.AbsolutePath, StringComparison.Ordinal))
            throw new IOException("PDOK gaf een onverwacht adres voor de BGT-levering.");
        return uri;
    }

    private static async Task<JsonDocument> ReadJson(HttpResponseMessage response, CancellationToken cancellation)
    {
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellation).ConfigureAwait(false);
    }

    internal static TopoData ReadArchive(ZipArchive archive, ParcelArea area, CancellationToken cancellation)
    {
        var shapes = new List<TopoShape>();
        var labels = new List<TopoLabel>();
        var usedIds = new HashSet<(string Type, string Id)>();
        foreach (var entry in archive.Entries.Where(item => item.Name.EndsWith(".gml", StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = entry.Open();
            using var xml = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 700_000_000
            });
            while (xml.Read())
            {
                if (xml.NodeType != XmlNodeType.Element || xml.LocalName != "featureMember" ||
                    xml.NamespaceURI != Gml.NamespaceName) continue;
                cancellation.ThrowIfCancellationRequested();
                using var subtree = xml.ReadSubtree();
                var feature = XElement.Load(subtree).Elements().FirstOrDefault();
                if (feature == null || Value(feature, "objectEindTijd").Length > 0 ||
                    Value(feature, "eindRegistratie").Length > 0) continue;
                var type = feature.Name.LocalName;
                var category = FirstValue(feature, "plus-fysiekVoorkomen", "plus-type", "bgt-fysiekVoorkomen", "bgt-type");
                var function = FirstValue(feature, "plus-functie", "bgt-functie");
                var metadata = new TopoSource(Value(feature, "identificatie.lokaalID"), type, category, function,
                    Value(feature, "identificatieBAGPND"));
                if (metadata.Id.Length > 0 && !usedIds.Add((type, metadata.Id))) continue;
                var style = Style(feature);
                foreach (var geometry in feature.Elements().Where(e => e.Name.LocalName.StartsWith("geometrie2d", StringComparison.Ordinal)))
                    ReadGeometry(geometry, style, metadata, area, shapes);
                ReadLabels(feature, metadata, area, labels);
            }
        }
        if (shapes.Count == 0 && labels.Count == 0) throw new IOException("PDOK leverde geen actuele BGT-objecten in dit gebied.");
        return new(shapes, labels);
    }

    private static void ReadGeometry(XElement geometry, TopoStyle style, TopoSource metadata, ParcelArea area, List<TopoShape> output)
    {
        var rings = geometry.Descendants().Where(e => e.Name == Gml + "exterior" || e.Name == Gml + "interior").ToArray();
        if (rings.Length > 0)
        {
            foreach (var ring in rings) AddPath(ring, true);
            return;
        }
        foreach (var part in geometry.Descendants().Where(e => e.Name == Gml + "Point" ||
            e.Name == Gml + "LineString" || e.Name == Gml + "Curve" || e.Name == Gml + "CompositeCurve")
            .Where(e => !e.Ancestors().TakeWhile(parent => parent != geometry).Any(parent =>
                parent.Name == Gml + "Curve" || parent.Name == Gml + "CompositeCurve")))
        {
            AddPath(part, false);
        }

        void AddPath(XElement part, bool closed)
        {
            var points = new List<TopoVertex>();
            foreach (var node in part.Descendants().Where(e => e.Name == Gml + "posList" || e.Name == Gml + "pos"))
            {
                var positions = Positions(node);
                if (node.Parent?.Name == Gml + "Arc" || node.Parent?.Name == Gml + "ArcString")
                {
                    for (var index = 0; index + 2 < positions.Length; index += 2)
                    {
                        var start = positions[index];
                        var mid = positions[index + 1];
                        var end = positions[index + 2];
                        var ax = mid.X - start.X;
                        var ay = mid.Y - start.Y;
                        var bx = end.X - mid.X;
                        var by = end.Y - mid.Y;
                        var turn = Math.Atan2(ax * by - ay * bx, ax * bx + ay * by);
                        if (Math.Abs(turn) >= Math.PI - 1e-8)
                        {
                            Append(start);
                            Append(mid);
                        }
                        else Append(start with { Bulge = Math.Tan(turn / 2) });
                        Append(end);
                    }
                }
                else foreach (var position in positions) Append(position);
            }
            if (closed && points.Count > 1 && Same(points[0], points[^1])) points.RemoveAt(points.Count - 1);
            if (points.Count < (closed ? 3 : 1) || !Intersects(points, area)) return;
            output.Add(new(style, metadata, points.ToArray(), closed));

            void Append(TopoVertex point)
            {
                if (points.Count > 0 && Same(points[^1], point))
                {
                    if (point.Bulge != 0) points[^1] = point;
                }
                else points.Add(point);
            }
        }
    }

    private static bool Same(TopoVertex first, TopoVertex second) =>
        Math.Abs(first.X - second.X) < 1e-6 && Math.Abs(first.Y - second.Y) < 1e-6;

    private static bool Intersects(List<TopoVertex> points, ParcelArea area)
    {
        // Include the full envelope of curved edges so an arc that enters the area is retained.
        double left = double.PositiveInfinity, bottom = double.PositiveInfinity;
        double right = double.NegativeInfinity, top = double.NegativeInfinity;
        for (var index = 0; index < points.Count; index++)
        {
            var start = points[index];
            var end = points[(index + 1) % points.Count];
            var pad = Math.Abs(start.Bulge) * Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2)) / 2;
            left = Math.Min(left, Math.Min(start.X, end.X) - pad);
            right = Math.Max(right, Math.Max(start.X, end.X) + pad);
            bottom = Math.Min(bottom, Math.Min(start.Y, end.Y) - pad);
            top = Math.Max(top, Math.Max(start.Y, end.Y) + pad);
        }
        return left <= area.Right && right >= area.Left && bottom <= area.Top && top >= area.Bottom;
    }

    private static TopoVertex[] Positions(XElement element)
    {
        var coordinateSystem = element.AncestorsAndSelf().Select(e => (string?)e.Attribute("srsName")).FirstOrDefault(v => v != null);
        if (coordinateSystem != null && !coordinateSystem.EndsWith("28992", StringComparison.Ordinal))
            throw new IOException("De BGT-levering bevat geometrie buiten het RD-coördinatenstelsel.");
        var dimension = element.AncestorsAndSelf().Select(e => (int?)e.Attribute("srsDimension")).FirstOrDefault(v => v != null) ?? 2;
        var numbers = element.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (dimension is < 2 or > 3 || numbers.Length % dimension != 0)
            throw new IOException("De BGT-levering bevat een ongeldige coördinatenreeks.");
        var points = new TopoVertex[numbers.Length / dimension];
        for (var index = 0; index < points.Length; index++)
            points[index] = new(double.Parse(numbers[index * dimension], CultureInfo.InvariantCulture),
                double.Parse(numbers[index * dimension + 1], CultureInfo.InvariantCulture));
        return points;
    }

    private static void ReadLabels(XElement feature, TopoSource metadata, ParcelArea area, List<TopoLabel> output)
    {
        foreach (var text in feature.Elements().Where(e => e.Name.LocalName.EndsWith(".tekst", StringComparison.Ordinal)))
        {
            if (string.IsNullOrWhiteSpace(text.Value)) continue;
            var prefix = text.Name.LocalName[..^6];
            foreach (var position in feature.Elements().Where(e => e.Name.LocalName.StartsWith(prefix + ".positie_", StringComparison.Ordinal) &&
                e.Name.LocalName.EndsWith(".plaatsingspunt", StringComparison.Ordinal)))
            {
                var coordinates = position.Descendants(Gml + "pos").FirstOrDefault();
                if (coordinates == null) continue;
                var point = Positions(coordinates).Single();
                if (!area.Contains(point.X, point.Y)) continue;
                var angle = Value(feature, position.Name.LocalName.Replace(".plaatsingspunt", ".hoek", StringComparison.Ordinal));
                double.TryParse(angle, NumberStyles.Float, CultureInfo.InvariantCulture, out var degrees);
                var layer = prefix.StartsWith("nummeraanduiding", StringComparison.Ordinal) ? "B-WE-OG-BEBOUWING-T18" : "B-WE-OG-TOPOGRAFIE-T18";
                output.Add(new(new(layer), metadata, point, text.Value.Trim(), -degrees * Math.PI / 180));
            }
        }
    }

    private static string Value(XElement feature, string name) =>
        feature.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() ?? "";

    private static string FirstValue(XElement feature, params string[] names) =>
        names.Select(name => Value(feature, name)).FirstOrDefault(value => value.Length > 0 && value != "niet-bgt") ?? "";
}
