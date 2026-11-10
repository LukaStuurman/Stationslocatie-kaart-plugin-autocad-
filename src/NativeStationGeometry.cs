using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Stationslocaties;

internal static class NativeStationGeometry
{
    internal const string TitleName = "TB_STATION_ONDERHOEK";
    internal const string ViewportLayer = "STATIONLOCATIES_KAARTVENSTER";
    private static readonly Lazy<Template> Data = new(Load);

    internal static Viewport AppendSheet(Database db, Transaction tr, Layout layout, bool overview)
    {
        var sheet = overview ? Data.Value.Overview : Data.Value.Detail;
        ConfigurePlot(db, layout);
        var viewport = AppendMapViewport(db, tr, layout, overview);
        var space = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForWrite);

        foreach (var shape in sheet.Entities) AppendShape(space, tr, shape);

        var titleId = GetBlock(db, tr, TitleName, block =>
        {
            foreach (var shape in sheet.Title) AppendShape(block, tr, shape);
            foreach (var field in sheet.Attrs)
            {
                var definition = new AttributeDefinition { Tag = field.Tag, Prompt = field.Tag, Invisible = field.Hidden };
                ApplyText(definition, field);
                Add(block, tr, definition);
            }
        });
        var title = Add(space, tr, new BlockReference(Point3d.Origin, titleId));
        var definitionBlock = (BlockTableRecord)tr.GetObject(titleId, OpenMode.ForRead);
        foreach (ObjectId id in definitionBlock)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not AttributeDefinition definition || definition.Constant) continue;
            var attribute = new AttributeReference();
            attribute.SetAttributeFromBlock(definition, title.BlockTransform);
            attribute.TextString = definition.TextString;
            title.AttributeCollection.AppendAttribute(attribute);
            tr.AddNewlyCreatedDBObject(attribute, true);
        }
        return viewport;
    }

    internal static Viewport AppendMapViewport(Database db, Transaction tr, Layout layout, bool overview)
    {
        if (!layout.IsWriteEnabled) layout.UpgradeOpen();
        layout.Initialize();
        var sheet = overview ? Data.Value.Overview : Data.Value.Detail;
        var space = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForWrite);
        var viewport = new Viewport
        {
            CenterPoint = Point(sheet.View), Width = sheet.View[3], Height = sheet.View[4],
            ViewDirection = Vector3d.ZAxis, LayerId = ViewportLayerId(db, tr)
        };
        Add(space, tr, viewport);
        if (sheet.Clip != null)
        {
            var clip = Add(space, tr, PolylineOf(sheet.Clip));
            viewport.NonRectClipEntityId = clip.ObjectId;
            viewport.NonRectClipOn = true;
        }

        return viewport;
    }

    private static ObjectId ViewportLayerId(Database db, Transaction tr)
    {
        var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (layers.Has(ViewportLayer)) return layers[ViewportLayer];
        if (!layers.IsWriteEnabled) layers.UpgradeOpen();
        var layer = new LayerTableRecord { Name = ViewportLayer, IsPlottable = false };
        var id = layers.Add(layer);
        tr.AddNewlyCreatedDBObject(layer, true);
        return id;
    }

    internal static ObjectId GetSymbol(Database db, Transaction tr, string name) =>
        GetBlock(db, tr, name, block =>
        {
            foreach (var shape in Data.Value.Symbol) AppendShape(block, tr, shape);
        });

    private static ObjectId GetBlock(Database db, Transaction tr, string name, Action<BlockTableRecord> populate)
    {
        var blocks = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        if (blocks.Has(name)) return blocks[name];
        if (!blocks.IsWriteEnabled) blocks.UpgradeOpen();
        var block = new BlockTableRecord { Name = name };
        var id = blocks.Add(block);
        tr.AddNewlyCreatedDBObject(block, true);
        populate(block);
        return id;
    }

    private static void ConfigurePlot(Database db, Layout layout)
    {
        var validator = PlotSettingsValidator.Current;
        try
        {
            validator.SetPlotConfigurationName(layout, "DWG To PDF.pc3", "ISO_full_bleed_A3_(297.00_x_420.00_MM)");
            validator.SetPlotPaperUnits(layout, PlotPaperUnit.Millimeters);
            validator.SetPlotRotation(layout, PlotRotation.Degrees090);
            validator.SetPlotType(layout, PlotType.Layout);
            validator.SetUseStandardScale(layout, true);
            validator.SetStdScaleType(layout, StdScaleType.StdScale1To1);
            if (db.PlotStyleMode) validator.SetCurrentStyleSheet(layout, "monochrome.ctb");
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            // A host without the standard PDF plotter can still create and edit the layout.
        }
    }

    private static T Add<T>(BlockTableRecord owner, Transaction tr, T entity) where T : Entity
    {
        owner.AppendEntity(entity);
        tr.AddNewlyCreatedDBObject(entity, true);
        return entity;
    }

    private static void AppendShape(BlockTableRecord owner, Transaction tr, Shape shape)
    {
        if (shape.K == "HATCH")
        {
            var hatch = new Hatch { ColorIndex = shape.C };
            Add(owner, tr, hatch);
            hatch.SetHatchPattern(HatchPatternType.PreDefined, shape.Solid ? "SOLID" : shape.S);
            if (!shape.Solid)
            {
                hatch.PatternScale = shape.W > 0 ? shape.W : 1;
                hatch.PatternAngle = shape.R;
            }
            for (var i = 0; i < shape.Loops.Length; i++)
            {
                var loop = shape.Loops[i];
                var points = new Point2dCollection(loop.Select(p => new Point2d(p[0], p[1])).ToArray());
                if (points.Count < 3) continue;
                if (points[0] != points[points.Count - 1]) points.Add(points[0]);
                hatch.AppendLoop(i == 0 ? HatchLoopTypes.External : HatchLoopTypes.Default,
                    points, new DoubleCollection(new double[points.Count]));
            }
            hatch.EvaluateHatch(true);
            return;
        }

        Entity entity = shape.K switch
        {
            "LINE" => new Line(Point(shape.P), Point(shape.Q)),
            "SOLID" => new Solid(Point(shape.V[0]), Point(shape.V[1]), Point(shape.V[2]), Point(shape.V[3])),
            "LWPOLYLINE" => PolylineOf(shape),
            "TEXT" => ApplyText(new DBText(), shape),
            "MTEXT" => new MText { Location = Point(shape.P), Contents = shape.S, TextHeight = shape.H,
                Width = shape.W, Attachment = (AttachmentPoint)shape.A, Rotation = shape.R },
            _ => throw new InvalidDataException("Onbekend type stationsgeometrie: " + shape.K)
        };
        entity.ColorIndex = shape.C;
        Add(owner, tr, entity);
    }

    private static DBText ApplyText(DBText text, Shape shape)
    {
        text.Position = Point(shape.P);
        text.Height = shape.H;
        text.WidthFactor = shape.W > 0 ? shape.W : 1;
        text.TextString = shape.S;
        text.Rotation = shape.R;
        if (shape.A != 0 || shape.B != 0)
        {
            text.HorizontalMode = (TextHorizontalMode)shape.A;
            text.VerticalMode = (TextVerticalMode)shape.B;
            text.AlignmentPoint = Point(shape.Q);
        }
        return text;
    }

    private static Polyline PolylineOf(Shape shape)
    {
        var polyline = new Polyline(shape.V.Length) { Elevation = shape.Z, ColorIndex = shape.C };
        foreach (var point in shape.V)
        {
            var position = new Point2d(point[0], point[1]);
            if (polyline.NumberOfVertices > 0 && position == polyline.GetPoint2dAt(polyline.NumberOfVertices - 1)) continue;
            polyline.AddVertexAt(polyline.NumberOfVertices, position, point[4], point[2], point[3]);
        }
        polyline.Closed = shape.Closed;
        return polyline;
    }

    private static Point3d Point(double[] xyz) => new(xyz[0], xyz[1], xyz[2]);

    private static Template Load()
    {
        using var resource = typeof(NativeStationGeometry).Assembly.GetManifestResourceStream("StationGeometry.json.gz")
            ?? throw new InvalidOperationException("De ingebouwde stationsgeometrie ontbreekt.");
        using var gzip = new GZipStream(resource, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<Template>(gzip, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("De ingebouwde stationsgeometrie is leeg.");
    }

    private sealed record Template(Sheet Detail, Sheet Overview, Shape[] Symbol);
    private sealed record Sheet(double[] View, Shape? Clip, Shape[] Attrs, Shape[] Entities, Shape[] Title);
    private sealed class Shape
    {
        public string K { get; init; } = "";
        public string S { get; init; } = "";
        public string Tag { get; init; } = "";
        public int C { get; init; } = 7;
        public int A { get; init; }
        public int B { get; init; }
        public double H { get; init; }
        public double W { get; init; }
        public double R { get; init; }
        public double Z { get; init; }
        public bool Hidden { get; init; }
        public bool Solid { get; init; }
        public bool Closed { get; init; }
        public double[] P { get; init; } = [];
        public double[] Q { get; init; } = [];
        public double[][] V { get; init; } = [];
        public double[][][] Loops { get; init; } = [];
    }
}

