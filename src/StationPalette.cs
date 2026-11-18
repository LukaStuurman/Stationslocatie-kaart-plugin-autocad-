using System.Drawing;
using Image = System.Drawing.Image;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Windows;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Stationslocaties;

internal static class StationPalette
{
    private sealed class DocumentState(Document document)
    {
        internal Document Document { get; } = document;
        internal IReadOnlyList<StationPaletteItem> Items { get; set; } = [];
        internal string? SelectedLayer { get; set; }
        internal bool HasOverview { get; set; }
        internal bool Dirty { get; set; } = true;
        internal bool SelectionDirty { get; set; } = true;
        internal bool PreviewDirty { get; set; } = true;
        internal bool RevealSelection { get; set; }
        internal bool ErrorReported { get; set; }
    }

    private sealed record PendingAction(Document Document, StationLocationAction Action, StationSheet? Sheet);

    private static readonly Dictionary<Database, DocumentState> documents = new();
    private static PaletteSet? palette;
    private static StationPaletteControl? control;
    private static Document? displayedDocument;
    private static PendingAction? pending;
    private static bool running;
    private static bool refreshing;

    internal static void Show()
    {
        if (palette == null)
        {
            control = new StationPaletteControl();
            control.ActionRequested += QueueAction;
            control.LocationSelected += LocationSelected;
            palette = new PaletteSet("Techbase | Stationlocaties", new Guid("81B33C18-7D9B-4CC6-B586-70B9E4D0A617"))
            {
                Style = PaletteSetStyles.ShowAutoHideButton | PaletteSetStyles.ShowCloseButton | PaletteSetStyles.ShowPropertiesMenu,
                MinimumSize = new Size(340, 440),
                Size = new Size(430, 760),
                DockEnabled = DockSides.Left | DockSides.Right,
                KeepFocus = false
            };
            palette.Add("Stationlocaties", control);
            var manager = AcApp.DocumentManager;
            manager.DocumentCreated += DocumentCreated;
            manager.DocumentActivated += DocumentActivated;
            manager.DocumentToBeDestroyed += DocumentToBeDestroyed;
            foreach (Document document in manager) Track(document);
            AcApp.Idle += OnIdle;
            palette.Visible = true;
            palette.Dock = DockSides.Right;
        }
        else palette.Visible = true;

        var current = AcApp.DocumentManager.MdiActiveDocument;
        if (current == null) return;
        var state = Track(current);
        state.Dirty = state.SelectionDirty = state.PreviewDirty = true;
        Refresh(current, allowCommand: true);
    }

    // Only this command entry point performs actions requested by the modeless palette.
    // The captured Document prevents queued clicks from being applied to another DWG.
    internal static void ExecutePending()
    {
        var request = pending;
        if (request == null) return;
        var current = AcApp.DocumentManager.MdiActiveDocument;
        // A delayed bridge from another document must not consume a newer click.
        if (current != request.Document) return;
        pending = null;
        if (!documents.TryGetValue(request.Document.Database, out var state))
        {
            control?.SetBusy(false);
            return;
        }

        running = true;
        control?.SetBusy(true);
        try
        {
            var selected = Plugin.ExecuteAction(current, request.Action, request.Sheet);
            state.SelectedLayer = selected?.Layer;
            state.RevealSelection = selected != null && request.Action is
                StationLocationAction.Add or StationLocationAction.AddAlternative or StationLocationAction.Edit;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.UserBreak)
        {
            // Escape cancels a point or text prompt without closing the palette.
        }
        catch (System.Exception ex)
        {
            current.Editor.WriteMessage("\nStationlocaties: " + ex.Message);
            MessageBox.Show(ex.Message, "Techbase | Stationlocaties", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            running = false;
            state.Dirty = state.PreviewDirty = true;
            // The action's returned station is authoritative after Add, Edit or Delete.
            state.SelectionDirty = false;
            control?.SetBusy(false);
        }
    }

    private static void QueueAction(StationLocationAction action, StationSheet? sheet)
    {
        var document = AcApp.DocumentManager.MdiActiveDocument;
        if (document == null || displayedDocument != document || pending != null || running) return;
        if (!string.IsNullOrEmpty(document.CommandInProgress))
        {
            document.Editor.WriteMessage("\nRond eerst de lopende AutoCAD-opdracht af.");
            return;
        }
        var state = Track(document);
        if (sheet != null) state.SelectedLayer = sheet.Layer;
        pending = new(document, action, sheet);
        control?.SetBusy(true);
        try
        {
            document.SendStringToExecute("STATIONLOCATIESACTIE ", true, false, false);
        }
        catch (System.Exception ex)
        {
            pending = null;
            control?.SetBusy(false);
            document.Editor.WriteMessage("\nStationlocaties: " + ex.Message);
            MessageBox.Show(ex.Message, "Techbase | Stationlocaties", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static void LocationSelected(StationSheet sheet)
    {
        var current = AcApp.DocumentManager.MdiActiveDocument;
        if (current == null || displayedDocument != current || running || pending != null) return;
        var state = Track(current);
        state.SelectedLayer = sheet.Layer;
        state.PreviewDirty = true;
        QueueAction(StationLocationAction.Zoom, sheet);
    }

    private static DocumentState Track(Document document)
    {
        if (documents.TryGetValue(document.Database, out var state)) return state;
        state = new(document);
        documents.Add(document.Database, state);
        document.ImpliedSelectionChanged += SelectionChanged;
        document.CommandWillStart += CommandWillStart;
        document.CommandEnded += CommandFinished;
        document.CommandCancelled += CommandFinished;
        document.CommandFailed += CommandFinished;
        document.Database.ObjectModified += ObjectChanged;
        document.Database.ObjectAppended += ObjectChanged;
        document.Database.ObjectErased += ObjectErased;
        return state;
    }

    private static void Untrack(DocumentState state)
    {
        var document = state.Document;
        document.ImpliedSelectionChanged -= SelectionChanged;
        document.CommandWillStart -= CommandWillStart;
        document.CommandEnded -= CommandFinished;
        document.CommandCancelled -= CommandFinished;
        document.CommandFailed -= CommandFinished;
        document.Database.ObjectModified -= ObjectChanged;
        document.Database.ObjectAppended -= ObjectChanged;
        document.Database.ObjectErased -= ObjectErased;
    }

    private static void DocumentCreated(object sender, DocumentCollectionEventArgs args) => Track(args.Document);

    private static void DocumentActivated(object sender, DocumentCollectionEventArgs args)
    {
        var state = Track(args.Document);
        state.Dirty = state.SelectionDirty = state.PreviewDirty = true;
        if (pending != null && pending.Document != args.Document) pending = null;
        if (displayedDocument == args.Document) return;
        // Hide stale rows immediately, before the next idle refresh can read the new DWG.
        displayedDocument = null;
        control?.SetNoDocument();
    }

    private static void DocumentToBeDestroyed(object sender, DocumentCollectionEventArgs args)
    {
        if (documents.Remove(args.Document.Database, out var state)) Untrack(state);
        if (pending?.Document == args.Document) pending = null;
        if (displayedDocument != args.Document) return;
        displayedDocument = null;
        control?.SetNoDocument();
    }

    private static void SelectionChanged(object? sender, EventArgs args)
    {
        if (sender is Document document && documents.TryGetValue(document.Database, out var state))
            state.SelectionDirty = true;
    }

    private static void CommandWillStart(object sender, CommandEventArgs args)
    {
        if (sender is Document document && document == AcApp.DocumentManager.MdiActiveDocument) control?.SetBusy(true);
    }

    private static void CommandFinished(object sender, CommandEventArgs args)
    {
        if (sender is not Document document || !documents.TryGetValue(document.Database, out var state)) return;
        state.PreviewDirty = true;
        var command = args.GlobalCommandName.TrimStart('_', '.').ToUpperInvariant();
        if (command is "U" or "UNDO" or "REDO" or "MREDO")
            state.Dirty = state.SelectionDirty = true;
        if (args.GlobalCommandName.Equals("STATIONLOCATIESACTIE", StringComparison.OrdinalIgnoreCase) && pending?.Document == document)
            pending = null;
    }

    private static void ObjectChanged(object sender, ObjectEventArgs args)
    {
        if (sender is not Database database || !documents.TryGetValue(database, out var state)) return;
        if (args.DBObject is Layout or AttributeReference or RasterImage or RasterImageDef or DBDictionary or Xrecord ||
            args.DBObject is Entity { IsErased: false } entity && StationLocationSheets.IsStationLayer(entity.Layer))
            state.Dirty = state.PreviewDirty = true;
    }

    private static void ObjectErased(object sender, ObjectErasedEventArgs args)
    {
        if (sender is Database database && documents.TryGetValue(database, out var state))
            state.Dirty = state.PreviewDirty = true;
    }

    private static void OnIdle(object? sender, EventArgs args)
    {
        if (palette?.Visible != true || running || refreshing || pending != null) return;
        var document = AcApp.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            if (displayedDocument != null)
            {
                displayedDocument = null;
                control?.SetNoDocument();
            }
            return;
        }
        Refresh(document);
    }

    private static void Refresh(Document document, bool allowCommand = false)
    {
        if (control == null || refreshing || running || pending != null) return;
        if (!allowCommand && !string.IsNullOrEmpty(document.CommandInProgress)) return;
        var state = Track(document);
        var switched = displayedDocument != document;
        if (!switched && !state.Dirty && !state.SelectionDirty && !state.PreviewDirty)
        {
            control.SetBusy(false);
            return;
        }

        refreshing = true;
        try
        {
            using var documentLock = document.LockDocument();
            if (state.Dirty || switched)
            {
                var sheets = StationLocationSheets.List(document.Database);
                var rows = StationLocationSheets.ReadSheetRows(document.Database, sheets)
                    .Where(row => !row.Overview).ToDictionary(row => row.Name, StringComparer.OrdinalIgnoreCase);
                var photos = StationLocationSheets.SheetsWithPhoto(document.Database, sheets);
                state.Items = sheets.Select(sheet => new StationPaletteItem(sheet,
                    rows.GetValueOrDefault(sheet.Name)?.Fields ?? StationFields.Empty, photos.Contains(sheet.Name))).ToArray();
                state.HasOverview = StationLocationSheets.HasOverview(document.Database);
                var selected = state.Items.FirstOrDefault(item => item.Sheet.Layer.Equals(state.SelectedLayer, StringComparison.OrdinalIgnoreCase));
                control.SetItems(state.Items, selected?.Sheet.Name, state.HasOverview, document.Name);
                if (state.RevealSelection && selected != null) control.SetSelection(selected.Sheet.Name);
                state.RevealSelection = false;
                state.SelectedLayer = control.Selected?.Layer;
                state.Dirty = false;
                state.PreviewDirty = true;
            }
            displayedDocument = document;
            if (state.SelectionDirty)
            {
                state.SelectionDirty = false;
                var selected = SelectedStation(document, state.Items);
                if (selected != null)
                {
                    state.SelectedLayer = selected.Layer;
                    control.SetSelection(selected.Name);
                    state.PreviewDirty = true;
                }
            }
            if (state.PreviewDirty)
            {
                var selected = control.Selected;
                control.SetPreview(selected == null ? null : ReadPreview(document.Database, selected.Name));
                state.PreviewDirty = false;
            }
            state.ErrorReported = false;
            control.SetBusy(false);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.LockViolation)
        {
            state.Dirty = true;
        }
        catch (System.Exception ex)
        {
            if (!state.ErrorReported) document.Editor.WriteMessage("\nStationlocaties zijpaneel: " + ex.Message);
            state.ErrorReported = true;
            // A failed refresh must not leave rows or actions from a different drawing visible.
            control.SetNoDocument();
            state.Dirty = true;
        }
        finally { refreshing = false; }
    }

    private static StationSheet? SelectedStation(Document document, IReadOnlyList<StationPaletteItem> items)
    {
        var selection = document.Editor.SelectImplied();
        if (selection.Status != PromptStatus.OK || selection.Value == null) return null;
        using var selectedObjects = selection.Value;
        using var tr = document.Database.TransactionManager.StartOpenCloseTransaction();
        foreach (var id in selectedObjects.GetObjectIds())
        {
            if (id.IsNull || !id.IsValid || id.IsErased || id.IsEffectivelyErased) continue;
            if (tr.GetObject(id, OpenMode.ForRead) is not BlockReference block ||
                block.OwnerId != SymbolUtilityServices.GetBlockModelSpaceId(document.Database)) continue;
            var item = items.FirstOrDefault(item => item.Sheet.Layer.Equals(block.Layer, StringComparison.OrdinalIgnoreCase));
            if (item != null) return item.Sheet;
        }
        return null;
    }

    private static Image? ReadPreview(Database database, string name)
    {
        try
        {
            using var tr = database.TransactionManager.StartOpenCloseTransaction();
            var layouts = (DBDictionary)tr.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
            if (!layouts.Contains(name)) return null;
            var layout = (Layout)tr.GetObject(layouts.GetAt(name), OpenMode.ForRead);
            using var thumbnail = layout.Thumbnail;
            return thumbnail == null ? null : new Bitmap(thumbnail);
        }
        catch (System.Exception)
        {
            // New layouts or drawings with thumbnails disabled can have no stored preview.
            return null;
        }
    }

    internal static void Shutdown()
    {
        AcApp.Idle -= OnIdle;
        var manager = AcApp.DocumentManager;
        manager.DocumentCreated -= DocumentCreated;
        manager.DocumentActivated -= DocumentActivated;
        manager.DocumentToBeDestroyed -= DocumentToBeDestroyed;
        foreach (var state in documents.Values) Untrack(state);
        documents.Clear();
        pending = null;
        displayedDocument = null;
        if (control != null)
        {
            control.ActionRequested -= QueueAction;
            control.LocationSelected -= LocationSelected;
            control.Dispose();
            control = null;
        }
        palette?.Dispose();
        palette = null;
    }
}
