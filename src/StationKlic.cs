using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Stationslocaties;

internal sealed record KlicStyle(string Layer, string Block, double Scale);
internal sealed record KlicGeometry((double X, double Y)[] Points, bool Closed);
internal sealed record KlicEntity(KlicStyle Style, KlicGeometry Geometry, string Subtype, string Label, string Description);
internal sealed record KlicData(List<KlicEntity> Entities, Dictionary<string, int> Unmatched);

internal static partial class StationKlic
{
    private static readonly XNamespace Gml = "http://www.opengis.net/gml/3.2";
    private static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";
    private static readonly Lazy<Dictionary<string, KlicStyle>> Styles = new(() =>
    {
        using var stream = Resource("KlicRules.json");
        return JsonSerializer.Deserialize<Dictionary<string, KlicStyle>>(stream)
            ?? throw new InvalidDataException("De KLIC-tekenregels ontbreken.");
    });

    internal static KlicData ReadFolder(string folder, CancellationToken cancellation)
    {
        var files = Directory.EnumerateFiles(folder, "GI_gebiedsinformatielevering*.xml", SearchOption.AllDirectories).ToArray();
        if (files.Length == 0)
            throw new InvalidDataException("Geen GI_gebiedsinformatielevering-XML gevonden. Kies de uitgepakte KLIC-levering.");
        if (files.Length != 1)
            throw new InvalidDataException("Meerdere gebiedsinformatieleveringen gevonden. Kies de map van één KLIC-levering.");
        return Read(files[0], cancellation);
    }

    private sealed record Link(string Status, KlicGeometry[] Geometry);
    private sealed record Feature(string Kind, string Network, string Status, string Subtype,
        string Label, string Description, string[] Links, KlicGeometry[] Geometry);

    internal static KlicData Read(string path, CancellationToken cancellation)
    {
        var networks = new Dictionary<string, string>(StringComparer.Ordinal);
        var links = new Dictionary<string, Link>(StringComparer.Ordinal);
        var features = new List<Feature>();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = true };
        using var reader = XmlReader.Create(path, settings);
        while (reader.Read())
        {
            cancellation.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element || reader.NamespaceURI != Gml.NamespaceName ||
                reader.LocalName != "featureMember") continue;
            using var subtree = reader.ReadSubtree();
            subtree.Read();
            subtree.MoveToContent();
            if (XElement.Load(subtree).Elements().FirstOrDefault() is not { } feature) continue;
            var kind = feature.Name.LocalName;
            var id = (string?)feature.Attribute(Gml + "id") ?? "";
            var status = Value(feature, "currentStatus");
            if (kind == "Utiliteitsnet")
            {
                networks[id] = Value(feature, "thema");
                continue;
            }
            if (kind == "UtilityLink")
            {
                links[id] = new(status, Geometry(feature).ToArray());
                continue;
            }
            // These annotation and auxiliary features are not standalone entities in the reference drawing.
            if (kind is "Annotatie" or "Maatvoering" or "ExtraGeometrie" or "DiepteTovMaaiveld") continue;
            var references = feature.Elements().Where(e => e.Name.LocalName == "link").Select(Reference).ToArray();
            var geometry = references.Length == 0 ? Geometry(feature).ToArray() : [];
            if (references.Length == 0 && geometry.Length == 0) continue;
            var subtype = Value(feature, kind == "EigenTopografie" ? "typeTopografischObject" : "appurtenanceType");
            features.Add(new(kind, Value(feature, "inNetwork"), status, subtype,
                Value(feature, "label"), Value(feature, "toelichting"), references, geometry));
        }

        var result = new KlicData([], new(StringComparer.Ordinal));
        foreach (var feature in features)
        {
            cancellation.ThrowIfCancellationRequested();
            var theme = networks.GetValueOrDefault(feature.Network, "");
            void Add(KlicGeometry geometry, string status)
            {
                var shape = geometry.Points.Length == 1 ? "P" : "L";
                var rules = Styles.Value;
                var key = string.Join('|', feature.Kind, theme, status, feature.Subtype, shape);
                if (rules.TryGetValue(key, out var style))
                {
                    if (feature.Kind == "ExtraDetailinfo" && geometry.Closed)
                        style = style with { Layer = "B-WE-OG-INFO-GV" };
                    result.Entities.Add(new(style, geometry, feature.Subtype, feature.Label, feature.Description));
                }
                else
                    result.Unmatched[feature.Kind] = result.Unmatched.GetValueOrDefault(feature.Kind) + 1;
            }
            foreach (var geometry in feature.Geometry) Add(geometry, feature.Status);
            foreach (var reference in feature.Links)
            {
                if (!links.TryGetValue(reference, out var link) || link.Geometry.Length == 0)
                    throw new InvalidDataException($"De lijngeometrie van een KLIC-object ({feature.Kind}) ontbreekt of wordt niet ondersteund.");
                foreach (var geometry in link.Geometry) Add(geometry, link.Status.Length > 0 ? link.Status : feature.Status);
            }
        }
        if (result.Entities.Count == 0)
            throw new InvalidDataException("De levering bevat geen geometrie waarvoor een KLIC-tekenregel beschikbaar is.");
        return result;
    }

    private static string Reference(XElement element) =>
        ((string?)element.Attribute(XLink + "href") ?? "").TrimStart('#');

    private static string Value(XElement element, string name)
    {
        var child = element.Elements().FirstOrDefault(e => e.Name.LocalName == name);
        if (child == null) return "";
        var value = Reference(child);
        return value.Length == 0 ? child.Value.Trim() : value[(value.LastIndexOf('/') + 1)..];
    }

    private static IEnumerable<KlicGeometry> Geometry(XElement feature)
    {
        foreach (var property in feature.Elements())
        {
            if (property.Name.LocalName is not ("centrelineGeometry" or "geometry" or "geometrie" or "ligging" or "vlakgeometrie2D" or "lijngeometrie2D"))
                continue;
            foreach (var geometry in property.Elements())
            {
                if (geometry.Name.Namespace != Gml) continue;
                if (geometry.Name.LocalName is not ("Point" or "LineString" or "Curve" or "Polygon" or "MultiCurve" or "MultiSurface"))
                    throw new InvalidDataException($"Onbekend KLIC-geometrietype: {geometry.Name.LocalName}.");
                var srs = (string?)geometry.Attribute("srsName");
                if (srs != null && srs[(Math.Max(srs.LastIndexOf('/'), srs.LastIndexOf(':')) + 1)..] != "28992")
                    throw new InvalidDataException("De KLIC-geometrie staat niet in RD New (EPSG:28992).");
                foreach (var part in Parts(geometry))
                {
                    if (part.Name == Gml + "Curve" && part.Element(Gml + "segments")?.Elements().Any(e => e.Name != Gml + "LineStringSegment") == true)
                        throw new InvalidDataException("Een KLIC-lijn bevat een boogsegment waarvoor nog geen tekenregel beschikbaar is.");
                    var points = new List<(double X, double Y)>();
                    foreach (var position in part.Descendants().Where(e => e.Name == Gml + "pos" || e.Name == Gml + "posList"))
                    {
                        var dimension = position.AncestorsAndSelf().Select(e => (int?)e.Attribute("srsDimension")).FirstOrDefault(d => d.HasValue) ?? 2;
                        var values = position.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                        if (dimension is < 2 or > 3 || values.Length % dimension != 0)
                            throw new InvalidDataException("Ongeldige KLIC-coördinatenlijst.");
                        for (var i = 0; i < values.Length; i += dimension)
                        {
                            var point = (double.Parse(values[i], CultureInfo.InvariantCulture), double.Parse(values[i + 1], CultureInfo.InvariantCulture));
                            if (points.Count == 0 || points[^1] != point) points.Add(point);
                        }
                    }
                    var closed = part.Name == Gml + "LinearRing";
                    if (closed && points.Count > 1 && points[0] == points[^1]) points.RemoveAt(points.Count - 1);
                    if (points.Count > 0) yield return new(points.ToArray(), closed);
                }
            }
        }
    }

    private static IEnumerable<XElement> Parts(XElement geometry)
    {
        return geometry.Name.LocalName switch
        {
            "Polygon" or "MultiSurface" => geometry.Descendants(Gml + "LinearRing"),
            "MultiCurve" => geometry.Descendants().Where(e => e.Name == Gml + "LineString" || e.Name == Gml + "Curve"),
            _ => [geometry]
        };
    }

    private static Stream Resource(string name) => typeof(StationKlic).Assembly.GetManifestResourceStream(name)
        ?? throw new InvalidDataException($"De KLIC-resource '{name}' ontbreekt.");
}
