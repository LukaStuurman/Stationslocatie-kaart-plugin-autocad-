using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Stationslocaties;

internal sealed record StationReferenceType(string Name, string ImportCommand, bool IsParcels = false)
{
    internal static readonly StationReferenceType[] Choices =
    [
        new("Topo", "ICMImportIMGeoInteractive"),
        new("Percelen", "ICMImportIMDKKInteractive", true),
        new("Klic", "ICMImportKLIC")
    ];

    public override string ToString() => Name;
}

internal static class StationReferences
{
    private sealed record Request(Document Host, StationReferenceType Type, string Path);
    private static readonly Dictionary<Document, Request> queued = new();
    private static readonly Dictionary<Document, Request> importing = new();

    internal static void QueueCreate(Document host, StationReferenceType type, string path)
    {
        queued[host] = new(host, type, path);
        host.SendStringToExecute("STATIONXREFSTART\n", true, false, false);
    }

    internal static void Start()
    {
        var documents = AcApp.DocumentManager;
        var host = documents.MdiActiveDocument;
        if (host == null || !queued.Remove(host, out var request)) return;

        if (File.Exists(request.Path))
            throw new IOException("Deze Xref-DWG bestaat al. Kies een nieuwe bestandsnaam.");
        using (var drawing = new Database(true, true))
        {
            drawing.Insunits = UnitsValue.Meters;
            drawing.SaveAs(request.Path, DwgVersion.Current);
        }

        var source = documents.Open(request.Path, false);
        importing[source] = request;
        documents.MdiActiveDocument = source;
        source.Editor.WriteMessage("\nImporteer de kaart met InfraCAD Map. Voer daarna STATIONXREFKLAAR uit in deze brontekening.\n");
        source.SendStringToExecute(request.Type.ImportCommand + "\n", true, false, false);
    }

    internal static void Finish()
    {
        var documents = AcApp.DocumentManager;
        var source = documents.MdiActiveDocument;
        if (source == null) return;
        if (!importing.TryGetValue(source, out var request))
            throw new InvalidOperationException("Open de brontekening die via Kaartreferenties maken is gestart.");

        if (!documents.Cast<Document>().Contains(request.Host))
            throw new InvalidOperationException("De oorspronkelijke stationstekening is gesloten. Open die tekening en koppel de opgeslagen DWG via Bestaande DWG koppelen.");

        using (source.LockDocument())
        {
            var db = source.Database;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var model = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                if (!model.Cast<ObjectId>().Any(id => !id.IsErased))
                    throw new InvalidOperationException("Importeer eerst kaartobjecten in deze brontekening.");
                if (request.Type.IsParcels) ColorParcels(db, tr);
                tr.Commit();
            }
            db.SaveAs(request.Path, DwgVersion.Current);
        }

        Attach(request.Host, request.Path, request.Type);
        importing.Remove(source);
        documents.MdiActiveDocument = request.Host;
        request.Host.Editor.Regen();
        request.Host.Editor.WriteMessage($"\nXref '{Path.GetFileNameWithoutExtension(request.Path)}' gekoppeld.");
    }

    internal static void Forget(Document document)
    {
        queued.Remove(document);
        importing.Remove(document);
    }

    internal static void Attach(Document host, string path, StationReferenceType? type = null)
    {
        var db = host.Database;
        var fullPath = Path.GetFullPath(path);
        if (Path.IsPathFullyQualified(db.Filename) &&
            string.Equals(Path.GetFullPath(db.Filename), fullPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Kies een andere DWG dan de stationstekening.");

        using var documentLock = host.LockDocument();
        using var tr = db.TransactionManager.StartTransaction();
        var blocks = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        var name = Path.GetFileNameWithoutExtension(fullPath);
        if (blocks.Has(name))
            throw new InvalidOperationException($"Er bestaat al een blok of Xref met de naam '{name}'.");
        var xref = db.OverlayXref(fullPath, name);
        if (xref.IsNull) throw new InvalidOperationException("AutoCAD kon de Xref niet koppelen.");

        var model = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
        var reference = new BlockReference(Point3d.Origin, xref);
        if (type != null) reference.LayerId = ReferenceLayer(db, tr, type);
        model.AppendEntity(reference);
        tr.AddNewlyCreatedDBObject(reference, true);
        if (Path.IsPathFullyQualified(db.Filename))
        {
            var relative = Path.GetRelativePath(Path.GetDirectoryName(db.Filename)!, fullPath);
            if (!Path.IsPathRooted(relative))
            {
                var definition = (BlockTableRecord)tr.GetObject(xref, OpenMode.ForWrite);
                definition.PathName = relative.StartsWith("..", StringComparison.Ordinal) ? relative : ".\\" + relative;
            }
        }
        var order = (DrawOrderTable)tr.GetObject(model.DrawOrderTableId, OpenMode.ForWrite);
        order.MoveToBottom(new ObjectIdCollection(new[] { reference.ObjectId }));
        if (type != null)
        {
            var references = model.Cast<ObjectId>().Where(id => !id.IsErased && id.ObjectClass.DxfName == "INSERT")
                .Select(id => (BlockReference)tr.GetObject(id, OpenMode.ForRead)).ToLookup(block => block.Layer);
            // Move in reverse order so Topo lies below the parcel and utility linework.
            foreach (var kind in StationReferenceType.Choices.Reverse())
            {
                var ids = references["STATIONLOCATIES_XREF_" + kind.Name.ToUpperInvariant()]
                    .Select(block => block.ObjectId).ToArray();
                if (ids.Length > 0) order.MoveToBottom(new ObjectIdCollection(ids));
            }
        }
        tr.Commit();
    }

    private static ObjectId ReferenceLayer(Database db, Transaction tr, StationReferenceType type)
    {
        var name = "STATIONLOCATIES_XREF_" + type.Name.ToUpperInvariant();
        var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (layers.Has(name)) return layers[name];
        layers.UpgradeOpen();
        var layer = new LayerTableRecord { Name = name };
        var id = layers.Add(layer);
        tr.AddNewlyCreatedDBObject(layer, true);
        return id;
    }

    private static void ColorParcels(Database db, Transaction tr)
    {
        using var orange = Autodesk.AutoCAD.Colors.Color.FromRgb(184, 85, 9);
        var modelId = SymbolUtilityServices.GetBlockModelSpaceId(db);
        var visited = new HashSet<ObjectId>();
        void ColorBlock(ObjectId id)
        {
            if (!visited.Add(id)) return;
            var block = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
            if (block.IsFromExternalReference) return;
            foreach (ObjectId entityId in block)
            {
                if (entityId.IsErased || tr.GetObject(entityId, OpenMode.ForRead) is not Entity entity) continue;
                if (!entity.IsWriteEnabled) entity.UpgradeOpen();
                entity.Color = orange;
                if (entity is not BlockReference reference) continue;
                ColorBlock(reference.BlockTableRecord);
                foreach (ObjectId attributeId in reference.AttributeCollection)
                    if (!attributeId.IsErased && tr.GetObject(attributeId, OpenMode.ForWrite) is AttributeReference attribute)
                        attribute.Color = orange;
            }
        }
        ColorBlock(modelId);
    }
}
