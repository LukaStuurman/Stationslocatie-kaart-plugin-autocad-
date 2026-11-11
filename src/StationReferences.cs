using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Stationslocaties;

internal enum MapKind { Percelen, Topo, KLIC }

internal static class StationReferences
{
    private sealed record Output(string Path, MapKind Kind);
    private sealed record Request(Output[] Outputs, ParcelArea Area, string? KlicFolder);
    private sealed record Prepared(Output Output, Func<Database, string> Write);
    private sealed record Download(Task<Prepared[]> Task, CancellationTokenSource Cancellation);
    private static readonly Dictionary<Document, Request> queued = new();
    private static readonly Dictionary<Document, Download> downloading = new();

    internal static void QueueCreate(Document host, string path, ParcelArea area, MapKind kind, string? klicFolder = null)
        => Queue(host, new([new(path, kind)], area, klicFolder));

    internal static void QueueAll(Document host, string folder, string klicFolder)
    {
        var name = Path.GetFileNameWithoutExtension(host.Name);
        Output OutputFor(MapKind kind) => new(Path.Combine(folder, $"{kind} {name}.dwg"), kind);
        Queue(host, new([OutputFor(MapKind.KLIC), OutputFor(MapKind.Topo), OutputFor(MapKind.Percelen)], default, klicFolder));
    }

    private static void Queue(Document host, Request request)
    {
        if (queued.ContainsKey(host) || downloading.ContainsKey(host))
            throw new InvalidOperationException("Voor deze tekening wordt al een kaart opgehaald.");
        queued.Add(host, request);
        host.SendStringToExecute("STATIONXREFSTART\n", true, false, false);
    }

    internal static void Start()
    {
        var host = AcApp.DocumentManager.MdiActiveDocument;
        if (host == null || !queued.Remove(host, out var request)) return;
        foreach (var output in request.Outputs)
            if (File.Exists(output.Path))
                throw new IOException($"Xref-DWG '{Path.GetFileName(output.Path)}' bestaat al. Kies een andere uitvoermap of bestandsnaam.");
        CheckAttachmentNames(host, request.Outputs.Select(output => output.Path));

        host.Editor.WriteMessage(request.Outputs.Length > 1
            ? "\nKLIC wordt ingelezen. Daarna haalt de plug-in Topo en Percelen op voor hetzelfde bereik.\n"
            : request.KlicFolder != null
            ? "\nKLIC-levering wordt op de achtergrond ingelezen.\n"
            : "\nPDOK wordt op de achtergrond opgehaald. Je kunt intussen verder tekenen.\n");
        var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(request.Outputs.Any(x => x.Kind != MapKind.KLIC) ? 10 : 5));
        var token = cancellation.Token;
        var task = Task.Run(() => PrepareMaps(request, token), token);
        if (downloading.Count == 0) AcApp.Idle += FinishDownload;
        downloading.Add(host, new(task, cancellation));
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
            SaveAndAttach(host, data);
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

    private static async Task<Prepared[]> PrepareMaps(Request request, CancellationToken cancellation)
    {
        var area = request.Area;
        Prepared? klicMap = null;
        if (request.KlicFolder != null)
        {
            var klic = StationKlic.ReadFolder(request.KlicFolder, cancellation);
            if (request.Outputs.Length > 1) area = KlicArea(klic);
            klicMap = new(request.Outputs.Single(output => output.Kind == MapKind.KLIC), drawing =>
            {
                var summary = StationKlic.Write(drawing, klic);
                if (klic.Unmatched.Count > 0)
                    summary += ". Zonder tekenregel: " + string.Join(", ", klic.Unmatched.Select(item => $"{item.Key} ({item.Value})"));
                return summary;
            });
        }
        var maps = await Task.WhenAll(request.Outputs.Where(output => output.Kind != MapKind.KLIC)
            .Select(output => Task.Run(() => PreparePdokMap(output, area, cancellation), cancellation))).ConfigureAwait(false);
        return klicMap == null ? maps : [klicMap, .. maps];
    }

    private static ParcelArea KlicArea(KlicData klic)
    {
        var left = double.PositiveInfinity;
        var bottom = double.PositiveInfinity;
        var right = double.NegativeInfinity;
        var top = double.NegativeInfinity;
        foreach (var item in klic.Entities)
            foreach (var (x, y) in item.Geometry.Points)
            {
                left = Math.Min(left, x);
                bottom = Math.Min(bottom, y);
                right = Math.Max(right, x);
                top = Math.Max(top, y);
            }
        try
        {
            return ParcelArea.FromCorners(new Point3d(left, bottom, 0), new Point3d(right, top, 0));
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"Het KLIC-bereik kan niet worden gebruikt voor Topo en Percelen. {ex.Message}", ex);
        }
    }

    private static Prepared PreparePdokMap(Output output, ParcelArea area, CancellationToken cancellation)
    {
        try
        {
            return new(output, DownloadPdok(output.Kind, area, cancellation));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw new IOException($"De beschikbare tijd voor {output.Kind} is verstreken. Kies indien mogelijk een kleiner gebied.");
        }
        catch (Exception ex)
        {
            throw new IOException($"{output.Kind}: {ex.Message}", ex);
        }
    }

    private static Func<Database, string> DownloadPdok(MapKind kind, ParcelArea area, CancellationToken cancellation)
    {
        if (kind == MapKind.Topo)
        {
            var topo = StationTopo.Download(area, cancellation);
            return drawing =>
            {
                StationTopo.Write(drawing, topo);
                return $"{topo.Shapes.Count} geometrieën en {topo.Labels.Count} labels";
            };
        }

        var parcels = StationParcels.Download(area, cancellation);
        return drawing =>
        {
            StationParcels.Write(drawing, parcels);
            return $"{parcels.Boundaries.Count} grenzen en {parcels.Labels.Count} perceelnummers";
        };
    }

    private static void SaveAndAttach(Document host, Prepared[] maps)
    {
        CheckAttachmentNames(host, maps.Select(map => map.Output.Path));
        var staged = new List<(Output Output, string Temporary, string Summary)>();
        var saved = new List<string>();
        try
        {
            foreach (var map in maps)
            {
                var temporary = Path.Combine(Path.GetDirectoryName(map.Output.Path)!, "." + Guid.NewGuid().ToString("N") + ".dwg");
                try
                {
                    using var drawing = new Database(map.Output.Kind != MapKind.KLIC, true);
                    if (map.Output.Kind == MapKind.KLIC) StationKlic.LoadStyles(drawing);
                    drawing.Insunits = UnitsValue.Meters;
                    var summary = map.Write(drawing);
                    drawing.SaveAs(temporary, DwgVersion.Current);
                    staged.Add((map.Output, temporary, summary));
                }
                catch
                {
                    File.Delete(temporary);
                    throw;
                }
            }
            foreach (var map in staged)
            {
                File.Move(map.Temporary, map.Output.Path);
                saved.Add(map.Output.Path);
            }
        }
        catch
        {
            foreach (var path in saved) File.Delete(path);
            throw;
        }
        finally
        {
            foreach (var map in staged) File.Delete(map.Temporary);
        }

        try
        {
            AttachMaps(host, staged.Select(map => (map.Output.Path, (string?)map.Output.Kind.ToString().ToUpperInvariant())).ToArray());
        }
        catch (Exception ex)
        {
            throw new IOException($"DWG-bestanden opgeslagen in '{Path.GetDirectoryName(staged[0].Output.Path)}'. Koppelen mislukt: {ex.Message}", ex);
        }
        host.Editor.Regen();
        foreach (var map in staged)
            host.Editor.WriteMessage($"\n{map.Output.Kind} gekoppeld: {map.Summary}.");
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

    internal static void Attach(Document host, string path, string? type = null) => AttachMaps(host, [(path, type)]);

    private static void CheckAttachmentNames(Document host, IEnumerable<string> paths)
    {
        var db = host.Database;
        using var documentLock = host.LockDocument();
        using var tr = db.TransactionManager.StartTransaction();
        var blocks = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        foreach (var path in paths)
            CheckAttachmentName(db, blocks, Path.GetFullPath(path));
    }

    private static void CheckAttachmentName(Database db, BlockTable blocks, string fullPath)
    {
        if (Path.IsPathFullyQualified(db.Filename) &&
            string.Equals(Path.GetFullPath(db.Filename), fullPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Kies een andere DWG dan de stationstekening.");

        var name = Path.GetFileNameWithoutExtension(fullPath);
        if (blocks.Has(name))
            throw new InvalidOperationException($"Er bestaat al een blok of Xref met de naam '{name}'.");
    }

    private static void AttachMaps(Document host, (string Path, string? Type)[] maps)
    {
        var db = host.Database;
        using var documentLock = host.LockDocument();
        using var tr = db.TransactionManager.StartTransaction();
        var blocks = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        foreach (var map in maps) CheckAttachmentName(db, blocks, Path.GetFullPath(map.Path));
        var model = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
        var order = (DrawOrderTable)tr.GetObject(model.DrawOrderTableId, OpenMode.ForWrite);
        foreach (var map in maps) AttachMap(db, tr, model, order, Path.GetFullPath(map.Path), map.Type);
        if (maps.Any(map => map.Type != null))
        {
            var references = model.Cast<ObjectId>().Where(id => !id.IsErased && id.ObjectClass.DxfName == "INSERT")
                .Select(id => (BlockReference)tr.GetObject(id, OpenMode.ForRead)).ToLookup(block => block.Layer);
            // Move in reverse order so Topo lies below the parcel and utility linework.
            foreach (var kind in new[] { "KLIC", "PERCELEN", "TOPO" })
            {
                var ids = references["STATIONLOCATIES_XREF_" + kind].Select(block => block.ObjectId).ToArray();
                if (ids.Length > 0) order.MoveToBottom(new ObjectIdCollection(ids));
            }
        }
        tr.Commit();
    }

    private static void AttachMap(Database db, Transaction tr, BlockTableRecord model, DrawOrderTable order, string fullPath, string? type)
    {
        var name = Path.GetFileNameWithoutExtension(fullPath);
        var xref = db.OverlayXref(fullPath, name);
        if (xref.IsNull) throw new InvalidOperationException("AutoCAD kon de Xref niet koppelen.");

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
        order.MoveToBottom(new ObjectIdCollection(new[] { reference.ObjectId }));
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
