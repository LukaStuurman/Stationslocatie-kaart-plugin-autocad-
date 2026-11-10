using System.Globalization;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Stationslocaties;

internal sealed record StationSheet(string Name, int Number, int Alternative, string Layer);

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
        var suffix = alternative switch { 0 => "VL", 1 => "AL", _ => "AL" + alternative };
        var baseLayer = "TB_STATION_" + number + "_" + suffix;
        using var tr = db.TransactionManager.StartTransaction();
        var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        var layer = baseLayer;
        for (var serial = 2; layers.Has(layer); serial++) layer = baseLayer + "_" + serial;
        return new($"Trafo {number} {suffix}", number, alternative, layer);
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
        var path = photo == null ? "" : ((RasterImageDef)tr.GetObject(photo.ImageDefId, OpenMode.ForRead)).SourceFileName;
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
        bool newOverview;
        using (var check = db.TransactionManager.StartTransaction())
        {
            newOverview = !HasOverview(db, check);
            if (newOverview && layouts.LayoutExists(OverviewName))
                throw new InvalidOperationException("Er bestaat al een layout ‘Totaal’. Hernoem die om een nieuw overzicht te kunnen maken.");
            if (ModelBlocks(db, check).Any(block => BlockName(check, block).Equals("TRAFO", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("De modelruimte bevat al losse TRAFO-blokken. Gebruik een projecttekening met alleen de achtergrondkaarten voor deze werkstroom.");
        }

        var step = "layouttabbladen aanmaken";
        var detailCreated = false;
        var overviewCreated = false;
        var committed = false;
        try
        {
            step = "layouttabbladen aanmaken";
            layouts.CreateLayout(sheet.Name);
            detailCreated = true;
            if (newOverview)
            {
                layouts.CreateLayout(OverviewName);
                overviewCreated = true;
            }
            using var tr = db.TransactionManager.StartTransaction();
            var layout = GetLayout(db, tr, sheet.Name, OpenMode.ForWrite);
            step = "detailblad en kaartvenster opbouwen";
            var viewport = NativeStationGeometry.AppendSheet(db, tr, layout, false);
            if (newOverview)
            {
                step = "blad Totaal opbouwen";
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
            }

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
            step = "onderhoek en foto invullen";
            ClearRevisions(db, tr, layout);
            WriteFields(db, tr, layout, fields);
            WriteSharedProject(db, tr, fields);
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
            if (!committed && overviewCreated) RemoveFailedLayout(OverviewName);
            if (!committed && detailCreated) RemoveFailedLayout(sheet.Name);
            doc.Editor.WriteMessage("\n" + ex);
            throw new InvalidOperationException($"Plaatsen van ‘{sheet.Name}’ mislukt bij {step}. {ex.Message}", ex);
        }

        var previousLayout = layouts.CurrentLayout;
        try
        {
            EnableCreatedViewport(doc, sheet.Name);
            EnableCreatedViewport(doc, OverviewName);
        }
        finally
        {
            layouts.CurrentLayout = previousLayout;
        }
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
        foreach (var photo in Entities(tr, layout).OfType<RasterImage>().Where(photo => photo.Layer == PhotoLayer && !photo.IsClipped))
            if (IsLive(photo.ImageDefId) && tr.GetObject(photo.ImageDefId, OpenMode.ForRead) is RasterImageDef definition &&
                File.Exists(definition.SourceFileName))
                FitPhoto(photo, definition.Size);
        if (layoutName != OverviewName && ReadSheets(db, tr).FirstOrDefault(sheet => sheet.Name == layoutName) is { } location)
            Center(tr, viewport, LocationBlock(tr, ModelBlocks(db, tr), location).Position);
        if (!viewport.On)
        {
            if (!viewport.IsWriteEnabled) viewport.UpgradeOpen();
            viewport.On = true;
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

    internal static void Edit(Document doc, StationSheet sheet, StationFields fields)
    {
        var db = doc.Database;
        using var tr = db.TransactionManager.StartTransaction();
        var layout = GetLayout(db, tr, sheet.Name);
        Center(tr, MapViewport(tr, layout), LocationBlock(tr, ModelBlocks(db, tr), sheet).Position);
        WriteFields(db, tr, layout, fields);
        WriteSharedProject(db, tr, fields);
        RefreshOverview(db, tr);
        tr.Commit();
    }

    internal static IReadOnlyList<SheetRow> ReadSheetRows(Database db, IReadOnlyList<StationSheet> sheets)
    {
        var rows = new List<SheetRow> { new(OverviewName, true, ProjectFields(db)) };
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
        if (!HasOverview(db, tr)) throw new InvalidOperationException("Plaats eerst een locatie om het project te starten.");
        WriteSharedProject(db, tr, fields);
        var overview = GetLayout(db, tr, OverviewName);
        SetAttributes(db, tr, overview, new Dictionary<string, string>
        {
            ["TDD"] = fields.Date, ["TEKENINGNUMMER"] = fields.DrawingNumber,
            ["OVERIGE2"] = fields.Address, ["OVERIGE1"] = fields.Notes
        });
        RefreshOverview(db, tr);
        tr.Commit();
    }

    private static void WriteSharedProject(Database db, Transaction tr, StationFields fields)
    {
        var shared = fields.SharedAttributes();
        foreach (var sheet in ReadSheets(db, tr)) SetAttributes(db, tr, GetLayout(db, tr, sheet.Name), shared);
        if (HasOverview(db, tr)) SetAttributes(db, tr, GetLayout(db, tr, OverviewName), shared);
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
        var locations = layers.Cast<ObjectId>().Where(id =>
        {
            var name = ((LayerTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name;
            return name.StartsWith("TB_STATION_", StringComparison.OrdinalIgnoreCase) && name != PhotoLayer;
        }).ToArray();
        foreach (var sheet in sheets)
        {
            var own = layers[sheet.Layer];
            var viewport = MapViewport(tr, GetLayout(db, tr, sheet.Name));
            if (!viewport.IsWriteEnabled) viewport.UpgradeOpen();
            viewport.ThawLayersInViewport(new ObjectIdCollection(new[] { own }).GetEnumerator());
            viewport.FreezeLayersInViewport(new ObjectIdCollection(locations.Where(id => id != own).ToArray()).GetEnumerator());
        }
    }

    private static void RefreshOverview(Database db, Transaction tr, string? excluded = null)
    {
        if (!HasOverview(db, tr)) return;
        var sheets = ReadSheets(db, tr, excluded);
        var overviewLayer = Layer(db, tr, OverviewLayer);
        ClearOverviewMarkers(db, tr, overviewLayer);
        if (sheets.Count == 0) return;
        var blocks = ModelBlocks(db, tr);
        var locations = sheets.Select(sheet => (Sheet: sheet, Block: LocationBlock(tr, blocks, sheet))).ToArray();
        var layout = GetLayout(db, tr, OverviewName);
        var viewport = FindMapViewport(tr, layout) ?? NativeStationGeometry.AppendMapViewport(db, tr, layout, true);
        const double margin = 35;
        var left = locations.Min(location => location.Block.Position.X) - margin;
        var right = locations.Max(location => location.Block.Position.X) + margin + 45;
        var bottom = locations.Min(location => location.Block.Position.Y) - margin;
        var top = locations.Max(location => location.Block.Position.Y) + margin;
        var needed = Math.Max((right - left) / viewport.Width, (top - bottom) / viewport.Height) * 1000;
        var step = needed <= 1000 ? 250 : needed <= 5000 ? 500 : 1000;
        var scale = Math.Max(500, (int)Math.Ceiling(needed / step) * step);
        Center(tr, viewport, new Point3d((left + right) / 2, (bottom + top) / 2, 0), scale);
        var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        var visible = sheets.Select(sheet => layers[sheet.Layer]).Append(overviewLayer);
        viewport.ThawLayersInViewport(new ObjectIdCollection(visible.ToArray()).GetEnumerator());
        SetAttributes(db, tr, layout, new Dictionary<string, string> { ["SCHAAL"] = "1:" + scale });

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
        // The overview layer is first created here, so refresh visibility after adding it.
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

    private static void SetPhoto(Database db, Transaction tr, Layout layout, string path)
    {
        var oldPhoto = Entities(tr, layout).OfType<RasterImage>().FirstOrDefault(image => image.Layer == PhotoLayer);
        if (oldPhoto != null)
        {
            var oldDef = (RasterImageDef)tr.GetObject(oldPhoto.ImageDefId, OpenMode.ForRead);
            if (string.Equals(oldDef.SourceFileName, path, StringComparison.OrdinalIgnoreCase))
            {
                if (!oldPhoto.IsClipped && File.Exists(path)) FitPhoto(oldPhoto, oldDef.Size);
                return;
            }
            if (!oldPhoto.IsWriteEnabled) oldPhoto.UpgradeOpen();
            oldPhoto.Erase();
        }
        if (string.IsNullOrWhiteSpace(path)) return;
        AttachPhoto(db, tr, layout, ClipboardPhotos.Persist(path, db.Filename));
        RememberPhotoFolder(db, tr, path);
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

    private static void AttachPhoto(Database db, Transaction tr, Layout layout, string path)
    {
        var dictionaryId = RasterImageDef.GetImageDictionary(db);
        if (dictionaryId.IsNull) dictionaryId = RasterImageDef.CreateImageDictionary(db);
        var dictionary = (DBDictionary)tr.GetObject(dictionaryId, OpenMode.ForWrite);
        var definition = new RasterImageDef { SourceFileName = path };
        definition.Load();
        var id = dictionary.SetAt("TB_FOTO_" + Guid.NewGuid().ToString("N"), definition);
        tr.AddNewlyCreatedDBObject(definition, true);
        var image = new RasterImage
        {
            ImageDefId = id, LayerId = Layer(db, tr, PhotoLayer), ShowImage = true
        };
        var space = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForWrite);
        space.AppendEntity(image);
        tr.AddNewlyCreatedDBObject(image, true);
        RasterImage.EnableReactors(true);
        image.AssociateRasterDef(definition);
        FitPhoto(image, definition.Size);
    }

    private static void FitPhoto(RasterImage image, Vector2d pixels)
    {
        const double left = 307.5, bottom = 219.53, width = 109.3, height = 74.28;
        if (pixels.X <= 0 || pixels.Y <= 0)
            throw new InvalidOperationException("De locatiefoto heeft geen geldige afmetingen.");
        if (!image.IsWriteEnabled) image.UpgradeOpen();

        var scale = Math.Max(width / pixels.X, height / pixels.Y);
        var w = pixels.X * scale;
        var h = pixels.Y * scale;
        image.Orientation = new CoordinateSystem3d(new Point3d(left + (width - w) / 2, bottom + (height - h) / 2, 0),
            new Vector3d(w, 0, 0), new Vector3d(0, h, 0));

        var toPixels = image.PixelToModelTransform.Inverse();
        var lower = new Point3d(left, bottom, 0).TransformBy(toPixels);
        var upper = new Point3d(left + width, bottom + height, 0).TransformBy(toPixels);
        image.SetClipBoundary(ClipBoundaryType.Rectangle, new Point2dCollection(new[]
        {
            new Point2d(Math.Min(lower.X, upper.X), Math.Min(lower.Y, upper.Y)),
            new Point2d(Math.Max(lower.X, upper.X), Math.Max(lower.Y, upper.Y))
        }));
        image.IsClipped = true;
    }
}
