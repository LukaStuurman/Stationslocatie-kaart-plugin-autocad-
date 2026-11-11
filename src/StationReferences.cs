using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Stationslocaties;

internal enum MapKind { Percelen, Topo }

internal static class StationReferences
{
    private sealed record Request(string Path, ParcelArea Area, MapKind Kind);
    private sealed record Download(Request Request, Task<Func<Database, string>> Task, CancellationTokenSource Cancellation);
    private static readonly Dictionary<Document, Request> queued = new();
    private static readonly Dictionary<Document, Download> downloading = new();

    internal static void QueueCreate(Document host, string path, ParcelArea area, MapKind kind)
    {
        if (queued.ContainsKey(host) || downloading.ContainsKey(host))
            throw new InvalidOperationException("Voor deze tekening wordt al een kaart opgehaald.");
        queued.Add(host, new(path, area, kind));
        host.SendStringToExecute("STATIONXREFSTART\n", true, false, false);
    }

    internal static void Start()
    {
        var host = AcApp.DocumentManager.MdiActiveDocument;
        if (host == null || !queued.Remove(host, out var request)) return;
        if (File.Exists(request.Path))
            throw new IOException("Deze Xref-DWG bestaat al. Kies een nieuwe bestandsnaam.");

        host.Editor.WriteMessage("\nPDOK wordt op de achtergrond opgehaald. Je kunt intussen verder tekenen.\n");
        var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var token = cancellation.Token;
        var task = Task.Run(() => PrepareMap(request, token), token);
        if (downloading.Count == 0) AcApp.Idle += FinishDownload;
        downloading.Add(host, new(request, task, cancellation));
    }

    private static void FinishDownload(object? sender, EventArgs args)
    {
        var host = AcApp.DocumentManager.MdiActiveDocument;
        if (host == null || !string.IsNullOrEmpty(host.CommandInProgress) ||
            !downloading.TryGetValue(host, out var download) || !download.Task.IsCompleted) return;
        downloading.Remove(host);
        if (downloading.Count == 0) AcApp.Idle -= FinishDownload;
        using var cancellation = download.Cancellation;
        try
        {
            var data = download.Task.GetAwaiter().GetResult();
            SaveAndAttach(host, download.Request, data);
        }
        catch (OperationCanceledException)
        {
            host.Editor.WriteMessage("\nDe kaart ophalen duurde te lang. Probeer het opnieuw of kies een kleiner gebied.");
        }
        catch (Exception ex)
        {
            host.Editor.WriteMessage("\nKaartreferentie: " + ex.Message);
        }
    }

    private static Func<Database, string> PrepareMap(Request request, CancellationToken cancellation)
    {
        if (request.Kind == MapKind.Topo)
        {
            var topo = StationTopo.Download(request.Area, cancellation);
            return drawing =>
            {
                StationTopo.Write(drawing, topo);
                return $"{topo.Shapes.Count} geometrieën en {topo.Labels.Count} labels";
            };
        }

        var parcels = StationParcels.Download(request.Area, cancellation);
        return drawing =>
        {
            StationParcels.Write(drawing, parcels);
            return $"{parcels.Boundaries.Count} grenzen en {parcels.Labels.Count} perceelnummers";
        };
    }

    private static void SaveAndAttach(Document host, Request request, Func<Database, string> write)
    {
        var path = request.Path;
        string summary;
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, "." + Guid.NewGuid().ToString("N") + ".dwg");
        try
        {
            using (var drawing = new Database(true, true))
            {
                drawing.Insunits = UnitsValue.Meters;
                summary = write(drawing);
                drawing.SaveAs(temporary, DwgVersion.Current);
            }
            File.Move(temporary, path);
        }
        finally { File.Delete(temporary); }

        Attach(host, path, request.Kind.ToString().ToUpperInvariant());
        host.Editor.Regen();
        host.Editor.WriteMessage($"\n{request.Kind} gekoppeld: {summary}.");
    }

    internal static void Forget(Document document)
    {
        queued.Remove(document);
        if (!downloading.Remove(document, out var download)) return;
        if (downloading.Count == 0) AcApp.Idle -= FinishDownload;
        download.Cancellation.Cancel();
        _ = download.Task.ContinueWith(task =>
        {
            _ = task.Exception;
            download.Cancellation.Dispose();
        }, TaskScheduler.Default);
    }

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
