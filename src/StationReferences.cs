using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Stationslocaties;

internal static class StationReferences
{
    private sealed record Request(string Path, ParcelArea Area);
    private static readonly Dictionary<Document, Request> queued = new();

    internal static void QueueCreate(Document host, string path, ParcelArea area)
    {
        queued[host] = new(path, area);
        host.SendStringToExecute("STATIONXREFSTART\n", true, false, false);
    }

    internal static void Start()
    {
        var host = AcApp.DocumentManager.MdiActiveDocument;
        if (host == null || !queued.Remove(host, out var request)) return;
        if (File.Exists(request.Path))
            throw new IOException("Deze Xref-DWG bestaat al. Kies een nieuwe bestandsnaam.");

        host.Editor.WriteMessage("\nPerceelgrenzen en -nummers ophalen bij PDOK...\n");
        (int Boundaries, int Labels) counts;
        var temporary = Path.Combine(Path.GetDirectoryName(request.Path)!, "." + Guid.NewGuid().ToString("N") + ".dwg");
        try
        {
            using (var drawing = new Database(true, true))
            {
                drawing.Insunits = UnitsValue.Meters;
                counts = StationParcels.Write(drawing, request.Area);
                drawing.SaveAs(temporary, DwgVersion.Current);
            }
            File.Move(temporary, request.Path);
        }
        finally { File.Delete(temporary); }

        Attach(host, request.Path, "PERCELEN");
        host.Editor.Regen();
        host.Editor.WriteMessage($"\nPercelen gekoppeld: {counts.Boundaries} grenzen en {counts.Labels} perceelnummers.");
    }

    internal static void Forget(Document document) => queued.Remove(document);

    internal static void Attach(Document host, string path, string? type = null)
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
            foreach (var kind in new[] { "KLIC", "PERCELEN", "TOPO" })
            {
                var ids = references["STATIONLOCATIES_XREF_" + kind]
                    .Select(block => block.ObjectId).ToArray();
                if (ids.Length > 0) order.MoveToBottom(new ObjectIdCollection(ids));
            }
        }
        tr.Commit();
    }

    private static ObjectId ReferenceLayer(Database db, Transaction tr, string type)
    {
        var name = "STATIONLOCATIES_XREF_" + type;
        var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (layers.Has(name)) return layers[name];
        layers.UpgradeOpen();
        var layer = new LayerTableRecord { Name = name };
        var id = layers.Add(layer);
        tr.AddNewlyCreatedDBObject(layer, true);
        return id;
    }
}
