using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: ExtensionApplication(typeof(Stationslocaties.StationTableUpdates))]

namespace Stationslocaties;

public sealed class StationTableUpdates : IExtensionApplication
{
    private sealed class Changes(Document document)
    {
        internal Document Document { get; } = document;
        internal HashSet<ObjectId> Stations { get; } = new();
        internal Dictionary<ObjectId, Point3d> PreviousStations { get; } = new();
        internal Dictionary<ObjectId, Point3d> PreviousNotes { get; } = new();
        internal int CommandDepth { get; set; }
        internal bool Updating { get; set; }

        internal void Clear()
        {
            Stations.Clear();
            PreviousStations.Clear();
            PreviousNotes.Clear();
        }
    }

    private readonly Dictionary<Database, Changes> pending = new();

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
        foreach (var changes in pending.Values) Untrack(changes.Document);
        pending.Clear();
        StationPalette.Shutdown();
    }

    private void DocumentCreated(object sender, DocumentCollectionEventArgs args) => Track(args.Document);

    private void Track(Document document)
    {
        if (!pending.TryAdd(document.Database, new Changes(document))) return;
        document.Database.ObjectOpenedForModify += BeforeObjectModified;
        document.Database.ObjectModified += StationModified;
        document.CommandWillStart += CommandWillStart;
        document.CommandEnded += CommandFinished;
        document.CommandCancelled += CommandFinished;
        document.CommandFailed += CommandFinished;
    }

    private void Untrack(Document document)
    {
        document.Database.ObjectOpenedForModify -= BeforeObjectModified;
        document.Database.ObjectModified -= StationModified;
        document.CommandWillStart -= CommandWillStart;
        document.CommandEnded -= CommandFinished;
        document.CommandCancelled -= CommandFinished;
        document.CommandFailed -= CommandFinished;
    }

    private void DocumentToBeDestroyed(object sender, DocumentCollectionEventArgs args)
    {
        StationReferences.Forget(args.Document);
        if (!pending.Remove(args.Document.Database)) return;
        Untrack(args.Document);
    }

    private void BeforeObjectModified(object sender, ObjectEventArgs args)
    {
        if (sender is not Database database || !pending.TryGetValue(database, out var changes) || changes.Updating ||
            args.DBObject is not Entity { IsErased: false, IsNewObject: false } entity) return;
        // Read only the notifying object here. Opening or changing other objects inside
        // this notification can recurse while AutoCAD is still modifying the drawing.
        if (entity is BlockReference block && StationLocationSheets.IsStationLayer(block.Layer))
            changes.PreviousStations.TryAdd(block.ObjectId, block.Position);
        else if (StationLocationSheets.IsMapNoteLayer(entity.Layer))
        {
            if (entity is DBText text) changes.PreviousNotes.TryAdd(text.ObjectId, text.Position);
            else if (entity is MText multi) changes.PreviousNotes.TryAdd(multi.ObjectId, multi.Location);
        }
    }

    private void StationModified(object sender, ObjectEventArgs args)
    {
        if (sender is Database database && pending.TryGetValue(database, out var changes) && !changes.Updating &&
            args.DBObject is BlockReference { IsErased: false } block &&
            StationLocationSheets.IsStationLayer(block.Layer))
            changes.Stations.Add(block.ObjectId);
    }

    private void CommandWillStart(object sender, CommandEventArgs args)
    {
        if (sender is not Document document || !pending.TryGetValue(document.Database, out var changes) || changes.Updating) return;
        changes.CommandDepth++;
        // Undo notifications may omit OpenedForModify. A read-only snapshot lets us
        // recognise comments already restored by AutoCAD and avoid moving them again.
        var command = args.GlobalCommandName.TrimStart('_', '.').ToUpperInvariant();
        if (command is not ("U" or "UNDO" or "REDO" or "MREDO")) return;
        try
        {
            StationLocationSheets.CaptureStationNotePositions(document.Database, changes.PreviousStations, changes.PreviousNotes);
        }
        catch (System.Exception ex)
        {
            document.Editor.WriteMessage("\nStationlocaties vóór ongedaan maken lezen: " + ex.Message);
        }
    }

    private void CommandFinished(object sender, CommandEventArgs args)
    {
        if (sender is not Document document || !pending.TryGetValue(document.Database, out var changes) || changes.Updating) return;
        changes.CommandDepth = Math.Max(0, changes.CommandDepth - 1);
        if (changes.CommandDepth == 0) ApplyChanges(changes);
    }

    private void UpdateOnIdle(object? sender, EventArgs args)
    {
        var document = AcApp.DocumentManager.MdiActiveDocument;
        if (document == null || !string.IsNullOrEmpty(document.CommandInProgress) ||
            !pending.TryGetValue(document.Database, out var changes) || changes.CommandDepth != 0) return;
        ApplyChanges(changes);
    }

    private static void ApplyChanges(Changes changes)
    {
        var document = changes.Document;
        if (changes.Updating || document != AcApp.DocumentManager.MdiActiveDocument) return;
        if (changes.Stations.Count == 0)
        {
            // A comment moved on its own establishes its new offset for the next move.
            changes.Clear();
            return;
        }

        changes.Updating = true;
        try
        {
            using var documentLock = document.LockDocument();
            // Updating at command completion keeps the derived geometry with the user's
            // operation; Idle is only a fallback for direct property/modeless edits.
            StationLocationSheets.UpdateMovedStationTables(document.Database, changes.Stations,
                changes.PreviousStations, changes.PreviousNotes);
            changes.Clear();
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == ErrorStatus.LockViolation)
        {
            // Preserve the first positions and retry after the drawing becomes idle.
        }
        catch (System.Exception ex)
        {
            changes.Clear();
            document.Editor.WriteMessage("\nStationlocatie en opmerkingen bijwerken: " + ex.Message);
        }
        finally
        {
            changes.Updating = false;
        }
    }
}
