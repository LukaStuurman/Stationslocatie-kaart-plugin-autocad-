using System.Globalization;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Stationslocaties;

internal sealed record StationSheet(string Name, int Number, int Alternative, string Layer);
internal sealed record StationSheetChange(StationSheet Before, StationSheet After);
internal sealed record StationEditPlan(StationSheet Target, StationSheetChange[] Changes);

internal sealed record StationFields(string Project, string Area, string Municipality, string ProjectNumber,
    string ClientNumber, string Drafter, string Date, string Address, string Notes, string DrawingNumber, string Photo)
{
    internal static string Today => DateTime.Today.ToString("d-M-yyyy", CultureInfo.InvariantCulture);
    internal static StationFields Empty => new("", "", "", "", "", "", Today, "", "", "", "");

    internal StationFields FillSharedFrom(StationFields other)
    {
        static string Value(string current, string fallback) => string.IsNullOrWhiteSpace(current) ? fallback : current;
        return this with
        {
            Project = Value(Project, other.Project), Area = Value(Area, other.Area),
            Municipality = Value(Municipality, other.Municipality), ProjectNumber = Value(ProjectNumber, other.ProjectNumber),
            ClientNumber = Value(ClientNumber, other.ClientNumber), Drafter = Value(Drafter, other.Drafter)
        };
    }

    internal Dictionary<string, string> SharedAttributes() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["PROJECTNAAM"] = Project, ["DEELGEBIED"] = Area, ["GEMEENTE"] = Municipality,
        ["PROJECTNR.CIRCET"] = ProjectNumber, ["PROJECTNR.OPDR."] = ClientNumber, ["GET"] = Drafter
    };

    internal Dictionary<string, string> Attributes() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["PROJECTNAAM"] = Project, ["DEELGEBIED"] = Area, ["GEMEENTE"] = Municipality,
        ["PROJECTNR.CIRCET"] = ProjectNumber, ["PROJECTNR.OPDR."] = ClientNumber, ["GET"] = Drafter, ["TDD"] = Date,
        ["OVERIGE1"] = Notes, ["OVERIGE2"] = Address, ["TEKENINGNUMMER"] = DrawingNumber,
        ["FORMAAT"] = "A3", ["SCHAAL"] = "1:250", ["TYPE_TEKENING"] = "Trafo locatie"
    };
}

internal static class StationLocationSheets
{
    internal const string OverviewName = "Totaal";
    private const string Tag = "TECHBASE_STATIONBLAD";
    private const string Symbol = "TB_STATION_TRAFO";
    private const string PhotoLayer = "TB_STATION_FOTO";
    private const string OverviewLayer = "TB_STATION_OVERZICHT";
    private const string PhotoFolderKey = "TB_STATION_FOTOMAP";
    private const string TitleBlock = "Onderhoek Spitters";

    internal static List<StationSheet> List(Database db)
    {
        using var tr = db.TransactionManager.StartTransaction();
        return ReadSheets(db, tr).OrderBy(sheet => sheet.Number).ThenBy(sheet => sheet.Alternative).ToList();
    }

    internal static StationSheet Next(Database db, int number, bool preferred)
    {
        var sameStation = List(db).Where(sheet => sheet.Number == number).ToArray();
        if (preferred && sameStation.Any(sheet => sheet.Alternative == 0))
            throw new InvalidOperationException($"Trafo {number} heeft al een voorkeurslocatie.");
        var alternative = preferred ? 0 : sameStation.Select(sheet => sheet.Alternative).DefaultIfEmpty().Max() + 1;
        var suffix = SheetSuffix(alternative);
        var baseLayer = "TB_STATION_" + number + "_" + suffix;
        using var tr = db.TransactionManager.StartTransaction();
        var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        var layer = baseLayer;
        for (var serial = 2; layers.Has(layer); serial++) layer = baseLayer + "_" + serial;
        return new($"Trafo {number} {suffix}", number, alternative, layer);
    }

    private static string SheetSuffix(int alternative) => alternative switch { 0 => "VL", 1 => "AL", _ => "AL" + alternative };

    internal static StationEditPlan PlanEdit(IReadOnlyList<StationSheet> sheets, StationSheet sheet, int number, bool preferred, int alternativeNumber)
    {
        if (!sheets.Contains(sheet))
            throw new InvalidOperationException("De geselecteerde locatie is gewijzigd of verwijderd.");
        var requested = preferred ? 0 : alternativeNumber;
        if (sheet.Number == number && sheet.Alternative == requested)
            return new(sheet, []);

        var others = sheets.Where(item => item.Number == number && item != sheet).ToArray();
        if (!preferred && others.Any(item => item.Alternative == requested))
            throw new InvalidOperationException($"Er bestaat al een locatie met de naam Trafo {number} {SheetSuffix(requested)}.");

        StationSheet Change(StationSheet old, int alternative) =>
            old with { Number = number, Alternative = alternative, Name = $"Trafo {number} {SheetSuffix(alternative)}" };

        var target = Change(sheet, requested);
        var changes = new List<StationSheetChange> { new(sheet, target) };
        if (preferred && others.FirstOrDefault(item => item.Alternative == 0) is { } incumbent)
        {
            var alternative = sheet.Number == number && sheet.Alternative > 0 ? sheet.Alternative
                : others.Select(item => item.Alternative).DefaultIfEmpty().Max() + 1;
            changes.Add(new(incumbent, Change(incumbent, alternative)));
        }
        return new(target, changes.ToArray());
    }

    internal static string DefaultDrawingNumber(StationSheet sheet) =>
        $"{sheet.Number} - " + (sheet.Alternative == 0 ? "Voorkeurslocatie"
            : "Alternatieve locatie" + (sheet.Alternative > 1 ? " " + sheet.Alternative : ""));

    internal static StationFields ReadFields(Database db, StationSheet sheet)
        => ReadFields(db, sheet.Name);

    internal static Point3d Position(Database db, StationSheet sheet)
    {
        using var tr = db.TransactionManager.StartTransaction();
        return LocationBlock(tr, ModelBlocks(db, tr), sheet).Position;
    }

    internal static StationFields ProjectFields(Database db, StationSheet? preferred = null)
    {
        using var tr = db.TransactionManager.StartTransaction();
        var project = HasOverview(db, tr) ? ReadFields(db, OverviewName) : StationFields.Empty;
        var shared = preferred == null ? project : ReadFields(db, preferred).FillSharedFrom(project);
        foreach (var sheet in ReadSheets(db, tr))
            shared = shared.FillSharedFrom(ReadFields(db, sheet));
        return project with
        {
            Project = shared.Project, Area = shared.Area, Municipality = shared.Municipality,
            ProjectNumber = shared.ProjectNumber, ClientNumber = shared.ClientNumber, Drafter = shared.Drafter
        };
    }

    internal static string PhotoFolder(Database db)
    {
        using var tr = db.TransactionManager.StartTransaction();
        var dictionary = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        if (dictionary.Contains(PhotoFolderKey))
        {
            var record = (Xrecord)tr.GetObject(dictionary.GetAt(PhotoFolderKey), OpenMode.ForRead);
            using var data = record.Data;
            var path = data?.AsArray().FirstOrDefault().Value as string;
            if (Directory.Exists(path)) return path!;
        }
        foreach (var sheet in ReadSheets(db, tr).AsEnumerable().Reverse())
        {
            var folder = ExistingFolder(ReadFields(db, sheet).Photo);
            if (folder.Length > 0) return folder;
        }
        return "";
    }

    private static string ExistingFolder(string file)
    {
        if (!Path.IsPathFullyQualified(file)) return "";
        var folder = Path.GetDirectoryName(file);
        return Directory.Exists(folder) ? folder! : "";
    }

    private static void RememberPhotoFolder(Database db, Transaction tr, string file)
    {
        if (ClipboardPhotos.IsStaged(file)) return;
        var folder = ExistingFolder(file);
        if (folder.Length == 0) return;
        var dictionary = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        Xrecord record;
        if (dictionary.Contains(PhotoFolderKey))
            record = (Xrecord)tr.GetObject(dictionary.GetAt(PhotoFolderKey), OpenMode.ForWrite);
        else
        {
            if (!dictionary.IsWriteEnabled) dictionary.UpgradeOpen();
            record = new Xrecord();
            dictionary.SetAt(PhotoFolderKey, record);
            tr.AddNewlyCreatedDBObject(record, true);
        }
        using var data = new ResultBuffer(new TypedValue(1, folder));
        record.Data = data;
    }

    private static StationFields ReadFields(Database db, string name)
    {
        using var tr = db.TransactionManager.StartTransaction();
        var layout = GetLayout(db, tr, name);
        var values = Title(tr, layout).AttributeCollection.Cast<ObjectId>()
            .Where(IsLive).Select(id => (AttributeReference)tr.GetObject(id, OpenMode.ForRead))
            .ToDictionary(a => a.Tag, a => a.TextString, StringComparer.OrdinalIgnoreCase);
        string Field(string key) => values.GetValueOrDefault(key, "");
        var photo = Entities(tr, layout).OfType<RasterImage>().FirstOrDefault(image => image.Layer == PhotoLayer);
        var path = photo == null ? "" : PhotoPath(tr, photo, out _);
        return new(Field("PROJECTNAAM"), Field("DEELGEBIED"), Field("GEMEENTE"), Field("PROJECTNR.CIRCET"),
            Field("PROJECTNR.OPDR."), Field("GET"), Field("TDD"), Field("OVERIGE2"), Field("OVERIGE1"), Field("TEKENINGNUMMER"), path);
    }

    internal static bool PickLocation(Document doc, out Point3d point, out double rotation)
    {
        var db = doc.Database;
        using var tr = db.TransactionManager.StartTransaction();
        using var preview = new BlockReference(Point3d.Origin, NativeStationGeometry.GetSymbol(db, tr, Symbol));
        preview.SetDatabaseDefaults(db);
        var accepted = new StationPlacementJig(preview).Pick(doc.Editor);
        point = preview.Position;
        rotation = preview.Rotation;
        return accepted;
    }

    internal static void Create(Document doc, StationSheet sheet, StationFields fields, Point3d point, double rotation)
    {
        var db = doc.Database;
        if (db.Insunits is not (UnitsValue.Meters or UnitsValue.Undefined))
            throw new InvalidOperationException("Deze kaart gebruikt modelcoördinaten in meters. Controleer INSUNITS.");
        var layouts = LayoutManager.Current;
        if (layouts.LayoutExists(sheet.Name))
            throw new InvalidOperationException($"Layout ‘{sheet.Name}’ bestaat al.");
        bool hasOverview;
        using (var check = db.TransactionManager.StartTransaction())
        {
            hasOverview = HasOverview(db, check);
            if (ModelBlocks(db, check).Any(block => BlockName(check, block).Equals("TRAFO", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("De modelruimte bevat al losse TRAFO-blokken. Gebruik een projecttekening met alleen de achtergrondkaarten voor deze werkstroom.");
        }

        var step = "layouttabbladen aanmaken";
        var detailCreated = false;
        var committed = false;
        try
        {
            step = "layouttabbladen aanmaken";
            layouts.CreateLayout(sheet.Name);
            detailCreated = true;
            using var tr = db.TransactionManager.StartTransaction();
            var layout = GetLayout(db, tr, sheet.Name, OpenMode.ForWrite);
            step = "detailblad en kaartvenster opbouwen";
            var viewport = NativeStationGeometry.AppendSheet(db, tr, layout, false);
            step = "trafoblok plaatsen";
            var layerId = Layer(db, tr, sheet.Layer, true);
            var blockId = NativeStationGeometry.GetSymbol(db, tr, Symbol);
            var model = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
            var reference = new BlockReference(point, blockId) { LayerId = layerId, Rotation = rotation };
            model.AppendEntity(reference);
            tr.AddNewlyCreatedDBObject(reference, true);
            WriteMetadata(db, tr, layout, sheet);
            step = "detailkaart centreren";
            Center(tr, viewport, point);
            step = "onderhoek invullen";
            ClearRevisions(db, tr, layout);
            SetAttributes(db, tr, layout, fields.Attributes());
            WriteSharedProject(db, tr, fields);
            step = "locatiefoto plaatsen";
            SetPhoto(db, tr, layout, fields.Photo);
            step = "overzicht en laagzichtbaarheid bijwerken";
            RefreshOverview(db, tr);
            tr.Commit();
            committed = true;
        }
        catch (Exception ex)
        {
            void RemoveFailedLayout(string name)
            {
                try
                {
                    if (layouts.LayoutExists(name)) layouts.DeleteLayout(name);
                }
                catch (Exception cleanupError)
                {
                    doc.Editor.WriteMessage($"\nLayout ‘{name}’ opruimen: {cleanupError.Message}");
                }
            }
            if (!committed && detailCreated) RemoveFailedLayout(sheet.Name);
            doc.Editor.WriteMessage("\n" + ex);
            throw new InvalidOperationException($"Plaatsen van ‘{sheet.Name}’ mislukt bij {step}. {ex.Message}", ex);
        }

        var previousLayout = layouts.CurrentLayout;
        try
        {
            EnableCreatedViewport(doc, sheet.Name);
            if (hasOverview) EnableCreatedViewport(doc, OverviewName);
        }
        finally
        {
            layouts.CurrentLayout = previousLayout;
        }
    }

    internal static void MakeOverview(Document doc)
    {
        var db = doc.Database;
        if (HasOverview(db))
        {
            OpenSheet(doc, OverviewName);
            return;
        }
        if (List(db).Count == 0)
            throw new InvalidOperationException("Plaats minstens één stationlocatie om Totaal te maken.");
        var layouts = LayoutManager.Current;
        if (layouts.LayoutExists(OverviewName))
            throw new InvalidOperationException("Er bestaat al een ander blad met de naam Totaal. Hernoem dat blad eerst.");

        var fields = ProjectFields(db);
        layouts.CreateLayout(OverviewName);
        try
        {
            using var tr = db.TransactionManager.StartTransaction();
            var overview = GetLayout(db, tr, OverviewName, OpenMode.ForWrite);
            NativeStationGeometry.AppendSheet(db, tr, overview, true);
            overview.CreateExtensionDictionary();
            var dictionary = (DBDictionary)tr.GetObject(overview.ExtensionDictionary, OpenMode.ForWrite);
            var marker = new Xrecord();
            dictionary.SetAt(Tag, marker);
            tr.AddNewlyCreatedDBObject(marker, true);
            SetAttributes(db, tr, overview, (fields with { Date = StationFields.Today }).Attributes());
            SetAttributes(db, tr, overview, new Dictionary<string, string>
            {
                ["OVERIGE1"] = "", ["OVERIGE2"] = "",
                ["TEKENINGNUMMER"] = string.IsNullOrWhiteSpace(fields.Area) ? "Trafo-overzicht" : "Overzicht " + fields.Area
            });
            ClearRevisions(db, tr, overview);
            RefreshOverview(db, tr);
            tr.Commit();
        }
        catch
        {
            try { layouts.DeleteLayout(OverviewName); }
            catch (Exception cleanupError) { doc.Editor.WriteMessage("\nTotaal opruimen: " + cleanupError.Message); }
            throw;
        }
        OpenSheet(doc, OverviewName, refreshOverview: false);
    }

    private static void EnableCreatedViewport(Document doc, string layoutName)
    {
        try
        {
            OpenSheet(doc, layoutName, refreshOverview: false);
        }
        catch (Exception ex)
        {
            doc.Editor.WriteMessage($"\nBlad ‘{layoutName}’ is aangemaakt. Open het blad om het kaartvenster opnieuw in te schakelen. {ex}");
        }
    }

    internal static void DisableExistingSheetGrids(Database db)
    {
        using var tr = db.TransactionManager.StartTransaction();
        foreach (var sheet in ReadSheets(db, tr)) DisableLayoutGrid(tr, GetLayout(db, tr, sheet.Name));
        if (HasOverview(db, tr)) DisableLayoutGrid(tr, GetLayout(db, tr, OverviewName));
        tr.Commit();
    }

    private static void DisableLayoutGrid(Transaction tr, Layout layout)
    {
        foreach (var item in Entities(tr, layout).OfType<Viewport>())
        {
            if (!item.GridOn) continue;
            if (!item.IsWriteEnabled) item.UpgradeOpen();
            item.GridOn = false;
        }
    }

    internal static void OpenSheet(Document doc, string layoutName, bool refreshOverview = true)
    {
        LayoutManager.Current.CurrentLayout = layoutName;
        if (Convert.ToInt32(Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("CVPORT")) != 1)
            doc.Editor.SwitchToPaperSpace();
        Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("GRIDMODE", (short)0);

        var db = doc.Database;
        using var tr = db.TransactionManager.StartTransaction();
        var layout = GetLayout(db, tr, layoutName);
        if (refreshOverview && layoutName == OverviewName && HasOverview(db, tr))
            RefreshOverview(db, tr);
        DisableLayoutGrid(tr, layout);
        var viewport = MapViewport(tr, layout);
        if (!viewport.On)
        {
            if (!viewport.IsWriteEnabled) viewport.UpgradeOpen();
            viewport.On = true;
        }
        if (layoutName != OverviewName && ReadSheets(db, tr).FirstOrDefault(sheet =>
            string.Equals(sheet.Name, layoutName, StringComparison.OrdinalIgnoreCase)) is { } location)
        {
            Center(tr, viewport, LocationBlock(tr, ModelBlocks(db, tr), location).Position);
            var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            IsolateViewport(viewport, layers[location.Layer], StationLayers(tr, layers));
        }
        tr.Commit();
    }

    internal static void Delete(Document doc, StationSheet sheet)
    {
        var layouts = LayoutManager.Current;
        if (layouts.CurrentLayout == sheet.Name) layouts.CurrentLayout = "Model";
        var db = doc.Database;
        if (!List(db).Contains(sheet))
            throw new InvalidOperationException("De geselecteerde stationlocatie is al verwijderd.");
        using var tr = db.TransactionManager.StartTransaction();
        foreach (var block in ModelBlocks(db, tr).Where(block => block.Layer == sheet.Layer && BlockName(tr, block) == Symbol))
        {
            block.UpgradeOpen();
            block.Erase();
        }
        RefreshOverview(db, tr, sheet.Name);
        layouts.DeleteLayout(sheet.Name);
        tr.Commit();
    }

    internal static void Edit(Document doc, StationEditPlan plan, StationFields fields)
    {
        var db = doc.Database;
        var layouts = LayoutManager.Current;
        var sources = plan.Changes.Select(change => change.Before.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var change in plan.Changes)
            if (layouts.LayoutExists(change.After.Name) && !sources.Contains(change.After.Name))
                throw new InvalidOperationException($"Er bestaat al een ander blad met de naam '{change.After.Name}'.");

        var renames = new Stack<(string From, string To)>();
        void Rename(string from, string to)
        {
            layouts.RenameLayout(from, to);
            renames.Push((from, to));
        }

        try
        {
            // Temporary names also support swapping a VL and AL within the same station.
            var pending = plan.Changes.Select(change =>
                (Change: change, Temp: "Stationlocaties-" + Guid.NewGuid().ToString("N"))).ToArray();
            foreach (var item in pending) Rename(item.Change.Before.Name, item.Temp);
            foreach (var item in pending) Rename(item.Temp, item.Change.After.Name);

            using var tr = db.TransactionManager.StartTransaction();
            foreach (var change in plan.Changes)
            {
                var changedLayout = GetLayout(db, tr, change.After.Name, OpenMode.ForWrite);
                WriteMetadata(db, tr, changedLayout, change.After);
                if (change.After != plan.Target)
                {
                    var drawingNumber = ReadFields(db, change.After).DrawingNumber;
                    if (string.Equals(drawingNumber, DefaultDrawingNumber(change.Before), StringComparison.Ordinal))
                        SetAttributes(db, tr, changedLayout, new Dictionary<string, string>
                        {
                            ["TEKENINGNUMMER"] = DefaultDrawingNumber(change.After)
                        });
                }
                else if (string.IsNullOrWhiteSpace(fields.DrawingNumber) ||
                    string.Equals(fields.DrawingNumber, DefaultDrawingNumber(change.Before), StringComparison.Ordinal))
                    fields = fields with { DrawingNumber = DefaultDrawingNumber(change.After) };
            }
            var layout = GetLayout(db, tr, plan.Target.Name);
            Center(tr, MapViewport(tr, layout), LocationBlock(tr, ModelBlocks(db, tr), plan.Target).Position);
            WriteFields(db, tr, layout, fields);
            WriteSharedProject(db, tr, fields);
            RefreshOverview(db, tr);
            tr.Commit();
        }
        catch
        {
            while (renames.TryPop(out var rename))
            {
                try { layouts.RenameLayout(rename.To, rename.From); }
                catch (Exception ex)
                {
                    doc.Editor.WriteMessage($"\nBladnaam '{rename.From}' herstellen is mislukt. {ex.Message}");
                }
            }
            throw;
        }
    }

    internal static IReadOnlyList<SheetRow> ReadSheetRows(Database db, IReadOnlyList<StationSheet> sheets)
    {
        var rows = new List<SheetRow>();
        if (HasOverview(db)) rows.Add(new(OverviewName, true, ProjectFields(db)));
        rows.AddRange(sheets.Select(sheet => new SheetRow(sheet.Name, false, ReadFields(db, sheet))));
        return rows;
    }

    internal static void UpdateSheetRows(Database db, IReadOnlyList<SheetRow> rows, string lastPhoto)
    {
        using var tr = db.TransactionManager.StartTransaction();
        foreach (var row in rows)
        {
            var layout = GetLayout(db, tr, row.Name);
            var fields = row.Fields;
            SetAttributes(db, tr, layout, new Dictionary<string, string>
            {
                ["TDD"] = fields.Date, ["OVERIGE2"] = fields.Address,
                ["OVERIGE1"] = fields.Notes, ["TEKENINGNUMMER"] = fields.DrawingNumber
            });
            if (!row.Overview) SetPhoto(db, tr, layout, fields.Photo);
        }
        RememberPhotoFolder(db, tr, lastPhoto);
        tr.Commit();
    }

    internal static void UpdateProject(Database db, StationFields fields)
    {
        using var tr = db.TransactionManager.StartTransaction();
        WriteSharedProject(db, tr, fields);
        if (HasOverview(db, tr))
        {
            var overview = GetLayout(db, tr, OverviewName);
            SetAttributes(db, tr, overview, new Dictionary<string, string>
            {
                ["TDD"] = fields.Date, ["TEKENINGNUMMER"] = fields.DrawingNumber,
                ["OVERIGE2"] = fields.Address, ["OVERIGE1"] = fields.Notes
            });
            RefreshOverview(db, tr);
        }
        tr.Commit();
    }

    private static void WriteSharedProject(Database db, Transaction tr, StationFields fields)
    {
        var shared = fields.SharedAttributes();
        foreach (var sheet in ReadSheets(db, tr)) SetAttributes(db, tr, GetLayout(db, tr, sheet.Name), shared);
        if (HasOverview(db, tr)) SetAttributes(db, tr, GetLayout(db, tr, OverviewName), shared);
    }

    internal static bool HasOverview(Database db)
    {
        using var tr = db.TransactionManager.StartTransaction();
        return HasOverview(db, tr);
    }

    private static bool HasOverview(Database db, Transaction tr)
    {
        var layouts = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        if (!layouts.Contains(OverviewName)) return false;
        var layout = (Layout)tr.GetObject(layouts.GetAt(OverviewName), OpenMode.ForRead);
        return !layout.ExtensionDictionary.IsNull &&
            ((DBDictionary)tr.GetObject(layout.ExtensionDictionary, OpenMode.ForRead)).Contains(Tag);
    }

    private static BlockReference[] ModelBlocks(Database db, Transaction tr) =>
        ((BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead))
        .Cast<ObjectId>().Where(IsLive).Select(id => tr.GetObject(id, OpenMode.ForRead)).OfType<BlockReference>().ToArray();

    private static BlockReference LocationBlock(Transaction tr, BlockReference[] blocks, StationSheet sheet)
    {
        var matches = blocks.Where(block => block.Layer == sheet.Layer && BlockName(tr, block) == Symbol).Take(2).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException($"Verwacht één trafo-blok op laag ‘{sheet.Layer}’. Gevonden: {matches.Length}.");
        return matches[0];
    }

    private static Layout GetLayout(Database db, Transaction tr, string name, OpenMode mode = OpenMode.ForRead)
    {
        var dictionary = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        if (!dictionary.Contains(name)) throw new InvalidOperationException($"Layout ‘{name}’ ontbreekt in de tekening.");
        return (Layout)tr.GetObject(dictionary.GetAt(name), mode);
    }

    private static IEnumerable<Entity> Entities(Transaction tr, Layout layout) =>
        ((BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead)).Cast<ObjectId>()
        .Where(IsLive).Select(id => tr.GetObject(id, OpenMode.ForRead)).OfType<Entity>();

    private static bool IsLive(ObjectId id) =>
        !id.IsNull && id.IsValid && !id.IsErased && !id.IsEffectivelyErased;

    private static Viewport? FindMapViewport(Transaction tr, Layout layout)
    {
        var viewports = Entities(tr, layout).OfType<Viewport>().ToArray();
        return viewports.FirstOrDefault(vp => vp.Layer == NativeStationGeometry.ViewportLayer)
            ?? (layout.LayoutName == OverviewName
                ? viewports.FirstOrDefault(vp => vp.Number != 1 && vp.Width > 400 && vp.Width < 425 && vp.Height > 240 && vp.Height < 250)
                : viewports.FirstOrDefault(vp => vp.NonRectClipOn));
    }

    private static Viewport MapViewport(Transaction tr, Layout layout) =>
        FindMapViewport(tr, layout) ?? throw new InvalidOperationException($"Layout ‘{layout.LayoutName}’ mist het kaartvenster.");

    private static string BlockName(Transaction tr, BlockReference block)
    {
        var id = block.IsDynamicBlock ? block.DynamicBlockTableRecord : block.BlockTableRecord;
        return IsLive(id) ? ((BlockTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name : "";
    }

    private static BlockReference Title(Transaction tr, Layout layout) =>
        Entities(tr, layout).OfType<BlockReference>().SingleOrDefault(block => BlockName(tr, block) is TitleBlock or NativeStationGeometry.TitleName)
        ?? throw new InvalidOperationException($"Layout ‘{layout.LayoutName}’ mist ‘{TitleBlock}’.");

    private static ObjectId Layer(Database db, Transaction tr, string name, bool location = false)
    {
        var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (table.Has(name))
        {
            if (location) throw new InvalidOperationException($"Laag ‘{name}’ bestaat al.");
            return table[name];
        }
        if (!table.IsWriteEnabled) table.UpgradeOpen();
        var layer = new LayerTableRecord { Name = name };
        var id = table.Add(layer);
        tr.AddNewlyCreatedDBObject(layer, true);
        return id;
    }

    private static void Center(Transaction tr, Viewport viewport, Point3d point, int denominator = 250)
    {
        if (!viewport.IsWriteEnabled) viewport.UpgradeOpen();
        viewport.Locked = false;
        viewport.ViewDirection = Vector3d.ZAxis;
        viewport.TwistAngle = 0;
        viewport.ViewTarget = point;
        var viewCenter = Point2d.Origin;
        if (viewport.NonRectClipOn && IsLive(viewport.NonRectClipEntityId) &&
            tr.GetObject(viewport.NonRectClipEntityId, OpenMode.ForRead) is Polyline { NumberOfVertices: >= 3 } clip)
        {
            var centroid = ClipCentroid(clip);
            viewCenter = new Point2d((viewport.CenterPoint.X - centroid.X) * denominator / 1000.0,
                (viewport.CenterPoint.Y - centroid.Y) * denominator / 1000.0);
        }
        viewport.ViewHeight = viewport.Height * denominator / 1000.0;
        viewport.ViewCenter = viewCenter;
        viewport.Locked = true;
    }

    private static Point2d ClipCentroid(Polyline clip)
    {
        var origin = clip.GetPoint2dAt(0);
        double area = 0, x = 0, y = 0;
        for (var i = 0; i < clip.NumberOfVertices; i++)
        {
            var a = clip.GetPoint2dAt(i) - origin;
            var b = clip.GetPoint2dAt((i + 1) % clip.NumberOfVertices) - origin;
            var cross = a.X * b.Y - b.X * a.Y;
            area += cross;
            x += (a.X + b.X) * cross;
            y += (a.Y + b.Y) * cross;
        }
        if (Math.Abs(area) < 1e-10) return origin;
        return origin + new Vector2d(x / (3 * area), y / (3 * area));
    }

    private static void Isolate(Database db, Transaction tr, IReadOnlyList<StationSheet> sheets)
    {
        var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        var locations = StationLayers(tr, layers);
        foreach (var sheet in sheets)
            IsolateViewport(MapViewport(tr, GetLayout(db, tr, sheet.Name)), layers[sheet.Layer], locations);
    }

    private static ObjectId[] StationLayers(Transaction tr, LayerTable layers) =>
        layers.Cast<ObjectId>().Where(IsLive).Where(id =>
        {
            var name = ((LayerTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name;
            return name.StartsWith("TB_STATION_", StringComparison.OrdinalIgnoreCase) && name != PhotoLayer;
        }).ToArray();

    private static void IsolateViewport(Viewport viewport, ObjectId own, ObjectId[] locations)
    {
        if (!viewport.IsWriteEnabled) viewport.UpgradeOpen();
        if (viewport.IsLayerFrozenInViewport(own))
            viewport.ThawLayersInViewport(new[] { own }.GetEnumerator());
        var toFreeze = locations.Where(id => id != own && !viewport.IsLayerFrozenInViewport(id)).ToArray();
        if (toFreeze.Length > 0)
            viewport.FreezeLayersInViewport(toFreeze.GetEnumerator());
    }

    private static void RefreshOverview(Database db, Transaction tr, string? excluded = null)
    {
        var sheets = ReadSheets(db, tr, excluded);
        var overviewLayer = Layer(db, tr, OverviewLayer);
        ClearOverviewMarkers(db, tr, overviewLayer);
        if (sheets.Count == 0) return;
        var blocks = ModelBlocks(db, tr);
        var locations = sheets.Select(sheet => (Sheet: sheet, Block: LocationBlock(tr, blocks, sheet))).ToArray();
        var scale = 500;
        if (HasOverview(db, tr))
        {
            var layout = GetLayout(db, tr, OverviewName);
            var viewport = FindMapViewport(tr, layout) ?? NativeStationGeometry.AppendMapViewport(db, tr, layout, true);
            const double margin = 35;
            var left = locations.Min(location => location.Block.Position.X) - margin;
            var right = locations.Max(location => location.Block.Position.X) + margin + 45;
            var bottom = locations.Min(location => location.Block.Position.Y) - margin;
            var top = locations.Max(location => location.Block.Position.Y) + margin;
            var needed = Math.Max((right - left) / viewport.Width, (top - bottom) / viewport.Height) * 1000;
            var step = needed <= 1000 ? 250 : needed <= 5000 ? 500 : 1000;
            scale = Math.Max(500, (int)Math.Ceiling(needed / step) * step);
            Center(tr, viewport, new Point3d((left + right) / 2, (bottom + top) / 2, 0), scale);
            var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            var visible = sheets.Select(sheet => layers[sheet.Layer]).Append(overviewLayer);
            viewport.ThawLayersInViewport(visible.ToArray().GetEnumerator());
            SetAttributes(db, tr, layout, new Dictionary<string, string> { ["SCHAAL"] = "1:" + scale });
        }

        var model = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
        var foreground = new ObjectIdCollection();
        foreach (var location in locations)
        {
            foreground.Add(location.Block.ObjectId);
            var p = location.Block.Position;
            var height = scale * 0.003;
            var label = new MText { Location = p + new Vector3d(height * 3, height, 0), TextHeight = height,
                Contents = location.Sheet.Name, LayerId = overviewLayer, Attachment = AttachmentPoint.MiddleLeft };
            model.AppendEntity(label);
            tr.AddNewlyCreatedDBObject(label, true);
            var circle = new Circle(p, Vector3d.ZAxis, 20.0) { LayerId = overviewLayer, Color = Autodesk.AutoCAD.Colors.Color.FromRgb(255, 0, 0) };
            model.AppendEntity(circle);
            tr.AddNewlyCreatedDBObject(circle, true);
            foreground.Add(circle.ObjectId);
            foreground.Add(label.ObjectId);
        }
        var drawOrder = (DrawOrderTable)tr.GetObject(model.DrawOrderTableId, OpenMode.ForWrite);
        drawOrder.MoveToTop(foreground);
        // Keep model-space markers out of the detail viewports.
        Isolate(db, tr, sheets);
    }

    private static void ClearOverviewMarkers(Database db, Transaction tr, ObjectId overviewLayer)
    {
        var model = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
        foreach (var id in model.Cast<ObjectId>().Where(IsLive).ToArray())
            if (tr.GetObject(id, OpenMode.ForRead) is Entity entity && entity.LayerId == overviewLayer)
            {
                if (!entity.IsWriteEnabled) entity.UpgradeOpen();
                entity.Erase();
            }
    }

    private static void WriteMetadata(Database db, Transaction tr, Layout layout, StationSheet sheet)
    {
        var apps = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
        if (!apps.Has(Tag))
        {
            if (!apps.IsWriteEnabled) apps.UpgradeOpen();
            var app = new RegAppTableRecord { Name = Tag };
            apps.Add(app);
            tr.AddNewlyCreatedDBObject(app, true);
        }
        using var data = new ResultBuffer(new TypedValue(1001, Tag),
            new TypedValue(1071, sheet.Number), new TypedValue(1071, sheet.Alternative), new TypedValue(1000, sheet.Layer));
        layout.XData = data;
    }

    private static List<StationSheet> ReadSheets(Database db, Transaction tr, string? excluded = null)
    {
        var result = new List<StationSheet>();
        var dictionary = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        foreach (var entry in dictionary)
        {
            if (entry.Key == excluded || !IsLive(entry.Value)) continue;
            var layout = (Layout)tr.GetObject(entry.Value, OpenMode.ForRead);
            using var data = layout.GetXDataForApplication(Tag);
            var values = data?.AsArray();
            if (values?.Length != 4) continue;
            result.Add(new(layout.LayoutName, Convert.ToInt32(values[1].Value, CultureInfo.InvariantCulture),
                Convert.ToInt32(values[2].Value, CultureInfo.InvariantCulture), (string)values[3].Value));
        }
        return result;
    }

    private static void WriteFields(Database db, Transaction tr, Layout layout, StationFields fields)
    {
        SetAttributes(db, tr, layout, fields.Attributes());
        SetPhoto(db, tr, layout, fields.Photo);
    }

    private const string PhotoSourceKey = "STATIONLOCATIES_FOTOBRON";

    private static string PhotoPath(Transaction tr, RasterImage photo, out bool prepared)
    {
        prepared = false;
        var definition = (RasterImageDef)tr.GetObject(photo.ImageDefId, OpenMode.ForRead);
        if (!definition.ExtensionDictionary.IsNull)
        {
            var dictionary = (DBDictionary)tr.GetObject(definition.ExtensionDictionary, OpenMode.ForRead);
            if (dictionary.Contains(PhotoSourceKey))
            {
                var record = (Xrecord)tr.GetObject(dictionary.GetAt(PhotoSourceKey), OpenMode.ForRead);
                using var data = record.Data;
                if (data?.AsArray().FirstOrDefault().Value is string source)
                {
                    prepared = true;
                    return source;
                }
            }
        }
        return definition.SourceFileName;
    }

    private static PhotoFrame PhotoBounds(Transaction tr, Layout layout)
    {
        var viewport = FindMapViewport(tr, layout);
        if (viewport is not { NonRectClipOn: true } || !IsLive(viewport.NonRectClipEntityId) ||
            tr.GetObject(viewport.NonRectClipEntityId, OpenMode.ForRead) is not Polyline clip)
            return PhotoFrame.Default;

        const double tolerance = 0.01;
        var count = clip.NumberOfVertices;
        double area = 0;
        for (var i = 0; i < count; i++)
        {
            var a = clip.GetPoint2dAt(i);
            var b = clip.GetPoint2dAt((i + 1) % count);
            area += a.X * b.Y - a.Y * b.X;
        }
        for (var i = 0; i < count; i++)
        {
            var corner = clip.GetPoint2dAt(i);
            var a = clip.GetPoint2dAt((i + count - 1) % count);
            var b = clip.GetPoint2dAt((i + 1) % count);
            var incoming = corner - a;
            var outgoing = b - corner;
            if ((incoming.X * outgoing.Y - incoming.Y * outgoing.X) * area >= 0) continue;
            if (Math.Abs(b.X - corner.X) < tolerance) (a, b) = (b, a);
            if (Math.Abs(a.X - corner.X) < tolerance && a.Y > corner.Y + tolerance &&
                Math.Abs(b.Y - corner.Y) < tolerance && b.X > corner.X + tolerance)
                return new(corner.X, corner.Y, b.X - corner.X, a.Y - corner.Y);
        }
        return PhotoFrame.Default;
    }

    private static void SetPhoto(Database db, Transaction tr, Layout layout, string path)
    {
        var oldPhoto = Entities(tr, layout).OfType<RasterImage>().FirstOrDefault(image => image.Layer == PhotoLayer);
        var frame = PhotoBounds(tr, layout);
        if (oldPhoto != null && string.Equals(PhotoPath(tr, oldPhoto, out var prepared), path, StringComparison.OrdinalIgnoreCase))
        {
            var orientation = oldPhoto.Orientation;
            if (!File.Exists(path) || (prepared &&
                Math.Abs(orientation.Origin.X - frame.Left) < 1e-6 && Math.Abs(orientation.Origin.Y - frame.Bottom) < 1e-6 &&
                Math.Abs(orientation.Xaxis.Length - frame.Width) < 1e-6 && Math.Abs(orientation.Yaxis.Length - frame.Height) < 1e-6))
                return;
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            var source = ClipboardPhotos.Persist(path, db.Filename);
            var raster = PhotoRaster.Prepare(source, db.Filename, frame);
            AttachPhoto(db, tr, layout, source, raster, frame);
            RememberPhotoFolder(db, tr, path);
        }

        if (oldPhoto != null)
        {
            if (!oldPhoto.IsWriteEnabled) oldPhoto.UpgradeOpen();
            oldPhoto.Erase();
        }
    }

    private static void ClearRevisions(Database db, Transaction tr, Layout layout)
    {
        var values = new Dictionary<string, string>();
        for (var revision = 1; revision <= 4; revision++)
            foreach (var prefix in new[] { "WIJ-", "GET-", "OMSCHRIJVING-WIJZ-", "WIJZ-DAT-" })
                values[prefix + revision] = "";
        SetAttributes(db, tr, layout, values);
    }

    private static void SetAttributes(Database db, Transaction tr, Layout layout, Dictionary<string, string> values)
    {
        foreach (ObjectId id in Title(tr, layout).AttributeCollection)
        {
            if (!IsLive(id)) continue;
            var attribute = (AttributeReference)tr.GetObject(id, OpenMode.ForRead);
            if (!values.TryGetValue(attribute.Tag, out var value)) continue;
            if (!attribute.IsWriteEnabled) attribute.UpgradeOpen();
            attribute.TextString = value;
            attribute.AdjustAlignment(db);
        }
    }

    private static void AttachPhoto(Database db, Transaction tr, Layout layout, string source, string raster, PhotoFrame frame)
    {
        var dictionaryId = RasterImageDef.GetImageDictionary(db);
        if (dictionaryId.IsNull) dictionaryId = RasterImageDef.CreateImageDictionary(db);
        var dictionary = (DBDictionary)tr.GetObject(dictionaryId, OpenMode.ForWrite);
        var definition = new RasterImageDef { SourceFileName = raster };
        definition.Load();
        var id = dictionary.SetAt("TB_FOTO_" + Guid.NewGuid().ToString("N"), definition);
        tr.AddNewlyCreatedDBObject(definition, true);
        var image = new RasterImage
        {
            ImageDefId = id, LayerId = Layer(db, tr, PhotoLayer), ShowImage = true,
            Orientation = new CoordinateSystem3d(new Point3d(frame.Left, frame.Bottom, 0),
                new Vector3d(frame.Width, 0, 0), new Vector3d(0, frame.Height, 0))
        };
        var space = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForWrite);
        space.AppendEntity(image);
        tr.AddNewlyCreatedDBObject(image, true);
        RasterImage.EnableReactors(true);
        image.AssociateRasterDef(definition);

        definition.CreateExtensionDictionary();
        var metadata = (DBDictionary)tr.GetObject(definition.ExtensionDictionary, OpenMode.ForWrite);
        using var data = new ResultBuffer(new TypedValue(1, source));
        var record = new Xrecord { Data = data };
        metadata.SetAt(PhotoSourceKey, record);
        tr.AddNewlyCreatedDBObject(record, true);
    }
}
