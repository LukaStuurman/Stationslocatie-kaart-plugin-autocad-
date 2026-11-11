using System.Text.RegularExpressions;
using System.Xml.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Stationslocaties;

internal static partial class StationTopo
{
    private const string MetadataApp = "STATIONLOCATIES_BGT";

    private static TopoStyle Style(XElement feature)
    {
        var type = feature.Name.LocalName.ToLowerInvariant();
        var material = FirstValue(feature, "bgt-fysiekVoorkomen");
        var subtype = FirstValue(feature, "plus-fysiekVoorkomen", "plus-type", "bgt-type");
        var suffix = Suffix(subtype);
        var shapeSuffix = feature.Elements().Where(e => e.Name.LocalName.StartsWith("geometrie2d", StringComparison.Ordinal))
            .SelectMany(e => e.Descendants()).Any(e => e.Name == Gml + "Polygon" || e.Name == Gml + "Surface") ? "GV" : "G";
        return type switch
        {
            "pand" => new("B-WE-OG-BEBOUWING-GV", 7),
            "gebouwinstallatie" or "overigbouwwerk" => new($"B-WE-OG-BEBOUWING{suffix}-{shapeSuffix}", 7),
            "wegdeel" or "ondersteunendwegdeel" => Road(material, subtype),
            "onbegroeidterreindeel" when material is "erf" or "oever" => new("B-WE-OG-TERREIN" + Suffix(material) + "-GV", 7),
            "onbegroeidterreindeel" when material.Contains("verhard", StringComparison.Ordinal) => Road(material, subtype),
            "onbegroeidterreindeel" => new($"B-WE-OG-TERREIN_ONBEGROEID{suffix}-GV", 7),
            "begroeidterreindeel" => Green(material, subtype),
            "waterdeel" or "ondersteunendwaterdeel" => Water(subtype),
            "vegetatieobject" when subtype.Contains("boom", StringComparison.OrdinalIgnoreCase) => new("B-WE-GR-BOOM-S", 90),
            "vegetatieobject" when subtype.Contains("haag", StringComparison.OrdinalIgnoreCase) => new("B-WE-GR-HAAG-G", 90),
            "vegetatieobject" => new($"B-WE-GR-VEGETATIE{suffix}-{shapeSuffix}", 90),
            "kunstwerkdeel" or "overbruggingsdeel" or "tunneldeel" => new($"B-WE-KW-KUNSTWERK{suffix}-{shapeSuffix}", 170),
            "scheiding" or "overigescheiding" => Fence(subtype, shapeSuffix),
            "spoor" => new("B-WE-AM-AS_SPOOR_TREIN-G", 10),
            "straatmeubilair" or "weginrichtingselement" => new($"B-WE-IE-MEUBILAIR_WEGMEUBILAIR{suffix}-S", 7),
            _ => new($"BGT-{type.ToUpperInvariant()}{suffix}-{shapeSuffix}", 7)
        };
    }

    private static TopoStyle Road(string material, string subtype)
    {
        var variant = subtype switch
        {
            "betonstraatstenen" => "betonstraatsteen",
            "betontegels" or "tegels" => "tegel",
            "straatbakstenen" or "gebakken klinkers" => "straatbaksteen",
            _ => subtype
        };
        var suffix = Suffix(variant);
        return material switch
        {
            "open verharding" => new($"B-WE-VH-OPENVERHARDING{suffix}-GV", 90),
            "gesloten verharding" => new($"B-WE-VH-GESLOTENVERHARDING{suffix}-GV", 10),
            "half verhard" or "halfverhard" => new($"B-WE-VH-HALFVERHARDING{suffix}-GV", variant == "grind" ? (short)252 : (short)130),
            "onverhard" => new($"B-WE-VH-ONVERHARD{suffix}-GV", suffix.Length == 0 ? (short)252 : (short)210),
            _ => new("B-WE-OG-WEG-GV", 7)
        };
    }

    private static TopoStyle Green(string material, string subtype)
    {
        if (subtype.Contains("gras", StringComparison.OrdinalIgnoreCase) || material.Contains("gras", StringComparison.OrdinalIgnoreCase))
            return new("B-WE-GR-GRAS-GV", 60);
        if (subtype == "bodembedekkers") subtype = "bodembedekker";
        if (material == "loofbos" || subtype == "loofbos") return new("B-WE-GR-BOS_LOOF-GV", 40);
        if (subtype.Length > 0) return new($"B-WE-GR-BEPLANTING_CULTUUR{Suffix(subtype)}-GV", 90);
        return new("B-WE-OG-TERREIN_BEGROEID-GV", 7);
    }

    private static TopoStyle Water(string subtype) => subtype switch
    {
        "sloot" => new("B-WE-OG-WATER_SLOOT-GV", 120),
        "meer" => new("B-WE-OG-WATER_MEER-GV", 140),
        "greppel" => new("B-WE-OG-WATER_GREPPEL-GV", 160),
        _ => new("B-WE-OG-WATER-GV", 130)
    };

    private static TopoStyle Fence(string subtype, string shapeSuffix) => subtype switch
    {
        "muur" => new("B-WE-IE-TERREINAFSCHEIDING_KUNSTMATIG_MUUR-" + shapeSuffix, 90),
        "hek" or "hekwerk" => new("B-WE-IE-TERREINAFSCHEIDING_KUNSTMATIG_HEKWERK-G", 20),
        "beschoeiing" => new("B-WE-GK-BESCHOEIING-G", 130),
        "kademuur" => new("B-WE-GK-KADEMUUR-" + shapeSuffix, 150),
        "damwand" => new("B-WE-GK-KEERWAND_DAMWAND-G", 30),
        _ => new($"BGT-SCHEIDING{Suffix(subtype)}-{shapeSuffix}", 7)
    };

    private static string Suffix(string value)
    {
        var cleaned = Regex.Replace(value.ToUpperInvariant(), @"[^\p{L}\p{N} _-]", "").Trim();
        return cleaned.Length == 0 ? "" : "_" + cleaned[..Math.Min(cleaned.Length, 70)];
    }

    internal static void Write(Database db, TopoData data)
    {
        using var tr = db.TransactionManager.StartTransaction();
        var model = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
        var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForWrite);
        var apps = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForWrite);
        var app = new RegAppTableRecord { Name = MetadataApp };
        apps.Add(app);
        tr.AddNewlyCreatedDBObject(app, true);

        void Add(Entity entity, TopoStyle style, TopoSource source)
        {
            if (!layers.Has(style.Layer))
            {
                var layer = new LayerTableRecord { Name = style.Layer, Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                    Autodesk.AutoCAD.Colors.ColorMethod.ByAci, style.Color) };
                layers.Add(layer);
                tr.AddNewlyCreatedDBObject(layer, true);
            }
            entity.SetDatabaseDefaults(db);
            entity.LayerId = layers[style.Layer];
            entity.ColorIndex = 256;
            model.AppendEntity(entity);
            tr.AddNewlyCreatedDBObject(entity, true);
            using var metadata = new ResultBuffer(new TypedValue(1001, MetadataApp), new TypedValue(1000, source.Id),
                new TypedValue(1000, source.Type), new TypedValue(1000, source.Category), new TypedValue(1000, source.Function),
                new TypedValue(1000, source.BagId));
            entity.XData = metadata;
        }

        foreach (var shape in data.Shapes)
        {
            var points = shape.Vertices;
            if (points.Length == 1)
            {
                Add(new Circle(new Point3d(points[0].X, points[0].Y, 0), Vector3d.ZAxis, 0.30), shape.Style, shape.Source);
                continue;
            }
            var line = new Polyline(points.Length) { Closed = shape.Closed };
            for (var index = 0; index < points.Length; index++)
                line.AddVertexAt(index, new Point2d(points[index].X, points[index].Y), points[index].Bulge, 0, 0);
            Add(line, shape.Style, shape.Source);
        }
        foreach (var label in data.Labels)
            Add(new DBText { TextString = label.Text, Position = new Point3d(label.Position.X, label.Position.Y, 0),
                Height = label.Style.Layer == "B-WE-OG-BEBOUWING-T18" ? 0.95 : 1.65, Rotation = label.Rotation },
                label.Style, label.Source);
        tr.Commit();
    }
}
