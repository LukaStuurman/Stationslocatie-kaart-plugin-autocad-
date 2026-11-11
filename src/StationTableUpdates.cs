using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: ExtensionApplication(typeof(Stationslocaties.StationTableUpdates))]

namespace Stationslocaties;

public sealed class StationTableUpdates : IExtensionApplication
{
    private readonly Dictionary<Database, HashSet<ObjectId>> pending = new();

    public void Initialize()
    {
        var documents = AcApp.DocumentManager;
        documents.DocumentCreated += DocumentCreated;
        documents.DocumentToBeDestroyed += DocumentToBeDestroyed;
        foreach (Document document in documents) Track(document);
        AcApp.Idle += UpdateOnIdle;
    }

    public void Terminate()
    {
        AcApp.Idle -= UpdateOnIdle;
        var documents = AcApp.DocumentManager;
        documents.DocumentCreated -= DocumentCreated;
        documents.DocumentToBeDestroyed -= DocumentToBeDestroyed;
        foreach (var database in pending.Keys) database.ObjectModified -= StationModified;
        pending.Clear();
    }

    private void DocumentCreated(object sender, DocumentCollectionEventArgs args) => Track(args.Document);

    private void Track(Document document)
    {
        var database = document.Database;
        if (pending.TryAdd(database, new HashSet<ObjectId>())) database.ObjectModified += StationModified;
    }

    private void DocumentToBeDestroyed(object sender, DocumentCollectionEventArgs args)
    {
        var database = args.Document.Database;
        if (pending.Remove(database)) database.ObjectModified -= StationModified;
    }

    private void StationModified(object sender, ObjectEventArgs args)
    {
        if (sender is Database database && pending.TryGetValue(database, out var changes) &&
            args.DBObject is BlockReference { IsErased: false } block &&
            StationLocationSheets.IsStationLayer(block.Layer))
            changes.Add(block.ObjectId);
    }

    private void UpdateOnIdle(object? sender, EventArgs args)
    {
        var document = AcApp.DocumentManager.MdiActiveDocument;
        if (document == null || !string.IsNullOrEmpty(document.CommandInProgress) ||
            !pending.TryGetValue(document.Database, out var changes) || changes.Count == 0) return;

        var stationIds = changes.ToArray();
        changes.Clear();
        try
        {
            using var documentLock = document.LockDocument();
            StationLocationSheets.UpdateMovedStationTables(document.Database, stationIds);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == ErrorStatus.LockViolation)
        {
            changes.UnionWith(stationIds);
        }
        catch (System.Exception ex)
        {
            document.Editor.WriteMessage("\nHoekpuntcoördinaten bijwerken: " + ex.Message);
        }
    }
}
