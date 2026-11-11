using System.IO;
using System.IO.Compression;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Stationslocaties;

internal static partial class StationKlic
{
    internal static void LoadStyles(Database db)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dxf");
        try
        {
            using (var archive = new GZipStream(Resource("KlicStyles.dxf.gz"), CompressionMode.Decompress))
            using (var file = File.Create(path))
                archive.CopyTo(file);
            db.DxfIn(path, null);
            ResolveNlcsShapeFile(db);
        }
        finally { File.Delete(path); }
    }

    private static void ResolveNlcsShapeFile(Database db)
    {
        const string shapeName = "NLCS.SHX";
        try
        {
            if (!string.IsNullOrEmpty(HostApplicationServices.Current.FindFile(shapeName, db, FindFileHint.CompiledShapeFile)))
                return;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.FileNotFound)
        {
            // AutoCAD can still load the installed NLCS file by its full path.
        }

        var library = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "InfraCAD", "NLCSLIB");
        if (!Directory.Exists(library)) return;
        var installed = Directory.EnumerateDirectories(library, "v*")
            .Select(folder => Path.Combine(folder, "NLCS", "Lijntypes", shapeName)).FirstOrDefault(File.Exists);
        if (installed == null) return;

        using var tr = db.TransactionManager.StartTransaction();
        var styles = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
        foreach (ObjectId id in styles)
        {
            var style = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
            if (!style.IsShapeFile || !string.Equals(style.FileName, shapeName, StringComparison.OrdinalIgnoreCase)) continue;
            style.UpgradeOpen();
            style.FileName = installed;
        }
        tr.Commit();
    }

    internal static string Write(Database db, KlicData data)
    {
        using var tr = db.TransactionManager.StartTransaction();
        var blocks = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        var model = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
        var layerIds = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
        var symbols = new Dictionary<string, (ObjectId Id, AttributeDefinition[] Attributes)>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in data.Entities)
        {
            var style = item.Style;
            if (!layerIds.TryGetValue(style.Layer, out var layer))
                layerIds.Add(style.Layer, layer = layers[style.Layer]);
            var points = item.Geometry.Points;
            if (style.Block.Length == 0)
            {
                var line = new Polyline(points.Length) { LayerId = layer, Closed = item.Geometry.Closed, LinetypeScale = 1 };
                for (var i = 0; i < points.Length; i++)
                    line.AddVertexAt(i, new Point2d(points[i].X, points[i].Y), 0, 0, 0);
                model.AppendEntity(line);
                tr.AddNewlyCreatedDBObject(line, true);
                continue;
            }

            if (!symbols.TryGetValue(style.Block, out var symbol))
            {
                var id = blocks[style.Block];
                var definition = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                var attributes = definition.Cast<ObjectId>().Where(child => child.ObjectClass.DxfName == "ATTDEF")
                    .Select(child => (AttributeDefinition)tr.GetObject(child, OpenMode.ForRead))
                    .Where(attribute => !attribute.Constant).ToArray();
                symbols.Add(style.Block, symbol = (id, attributes));
            }
            var reference = new BlockReference(new Point3d(points[0].X, points[0].Y, 0), symbol.Id)
            {
                LayerId = layer,
                ScaleFactors = new Scale3d(style.Scale),
                LinetypeScale = 1
            };
            model.AppendEntity(reference);
            tr.AddNewlyCreatedDBObject(reference, true);
            foreach (var definition in symbol.Attributes)
            {
                var attribute = new AttributeReference();
                attribute.SetAttributeFromBlock(definition, reference.BlockTransform);
                attribute.TextString = attribute.Tag switch
                {
                    "TYPE" => item.Subtype,
                    "LABEL" => item.Label,
                    "OMSCHRIJVING" => item.Description,
                    _ => attribute.TextString
                };
                reference.AttributeCollection.AppendAttribute(attribute);
                tr.AddNewlyCreatedDBObject(attribute, true);
            }
        }
        tr.Commit();
        var lines = data.Entities.Count(item => item.Style.Block.Length == 0);
        return $"{lines} polylijnen en {data.Entities.Count - lines} symbolen";
    }
}
