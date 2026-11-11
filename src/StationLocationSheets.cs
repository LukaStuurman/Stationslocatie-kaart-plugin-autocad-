using System.Globalization;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Stationslocaties;

internal sealed record StationSheet(string Name, string Trafo, int Alternative, string Layer, bool ShowCorners = true);
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
    private const string SheetRepairKey = "STATIONLOCATIES_BLADHERSTEL_1";
    private const string Symbol = "TB_STATION_TRAFO";
    private const string CornerTableLayer = "STATIONLOCATIES_HOEKPUNTEN";
    private const string NoteLayerSuffix = "_KAARTOPMERKINGEN";
    internal const double MarkerRadius = 20;
    private const string SignatureLayer = "STATIONLOCATIES_PARAAF";
    private const string SignatureKey = "STATIONLOCATIES_PARAAFLOGO";
    private const string ClientLayer = "STATIONLOCATIES_OPDRACHTGEVER";
    private const string ClientKey = "STATIONLOCATIES_OPDRACHTGEVER";
    private const string PhotoLayer = "TB_STATION_FOTO";
    private const string OverviewLayer = "TB_STATION_OVERZICHT";
    private const string PhotoFolderKey = "TB_STATION_FOTOMAP";
    private const string TitleBlock = "Onderhoek Spitters";

    internal static List<StationSheet> List(Database db)
    {
        using var tr = db.TransactionManager.StartTransaction();
        return ReadSheets(db, tr).OrderBy(sheet => int.TryParse(sheet.Trafo, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : int.MaxValue)
            .ThenBy(sheet => sheet.Trafo, StringComparer.OrdinalIgnoreCase).ThenBy(sheet => sheet.Alternative).ToList();
    }

    internal static HashSet<string> SheetsWithPhoto(Database db, IReadOnlyList<StationSheet> sheets)
    {
        using var tr = db.TransactionManager.StartTransaction();
        return sheets.Where(sheet =>
        {
            var layout = GetLayout(db, tr, sheet.Name);
            return PhotoImage(tr, layout) is { ShowImage: true } photo && IsLive(photo.ImageDefId);
        }).Select(sheet => sheet.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    internal static StationSheet Next(Database db, string trafo, bool preferred)
    {
        var sameStation = List(db).Where(sheet => SameTrafo(sheet.Trafo, trafo)).ToArray();
        if (preferred && sameStation.Any(sheet => sheet.Alternative == 0))
            throw new InvalidOperationException($"{TrafoLabel(trafo)} heeft al een voorkeurslocatie.");
        var alternative = preferred ? 0 : sameStation.Select(sheet => sheet.Alternative).DefaultIfEmpty().Max() + 1;
        var suffix = SheetSuffix(alternative);
        var baseLayer = "TB_STATION_" + (int.TryParse(trafo, NumberStyles.None, CultureInfo.InvariantCulture, out _) ? trafo : "LOCATIE") + "_" + suffix;
        using var tr = db.TransactionManager.StartTransaction();
        var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        var layer = baseLayer;
        for (var serial = 2; layers.Has(layer); serial++) layer = baseLayer + "_" + serial;
        return new($"{TrafoLabel(trafo)} {suffix}", trafo, alternative, layer);
    }

    internal static bool SameTrafo(string first, string second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    internal static string TrafoLabel(string trafo) =>
        int.TryParse(trafo, NumberStyles.None, CultureInfo.InvariantCulture, out _) ? $"Trafo {trafo}" : trafo;

    internal static bool IsValidTrafo(string trafo) =>
        trafo.Length is > 0 and <= 60 && !trafo.Any(char.IsControl) &&
        trafo.IndexOfAny(['<', '>', '/', '\\', '"', ':', ';', '?', '*', '|', '=', ',', '{', '}', '%']) < 0;

    private static string SheetSuffix(int alternative) => alternative switch { 0 => "VL", 1 => "AL", _ => "AL" + alternative };

    internal static StationEditPlan PlanEdit(IReadOnlyList<StationSheet> sheets, StationSheet sheet, string trafo, bool preferred, int alternativeNumber)
    {
        if (!sheets.Contains(sheet))
            throw new InvalidOperationException("De geselecteerde locatie is gewijzigd of verwijderd.");
        var requested = preferred ? 0 : alternativeNumber;
        if (sheet.Trafo == trafo && sheet.Alternative == requested)
            return new(sheet, []);

        var others = sheets.Where(item => SameTrafo(item.Trafo, trafo) && item != sheet).ToArray();
        if (!preferred && others.Any(item => item.Alternative == requested))
            throw new InvalidOperationException($"Er bestaat al een locatie met de naam {TrafoLabel(trafo)} {SheetSuffix(requested)}.");

        StationSheet Change(StationSheet old, int alternative) =>
            old with { Trafo = trafo, Alternative = alternative, Name = $"{TrafoLabel(trafo)} {SheetSuffix(alternative)}" };

        var target = Change(sheet, requested);
        var changes = new List<StationSheetChange> { new(sheet, target) };
        if (preferred && others.FirstOrDefault(item => item.Alternative == 0) is { } incumbent)
        {
            var alternative = SameTrafo(sheet.Trafo, trafo) && sheet.Alternative > 0 ? sheet.Alternative
                : others.Select(item => item.Alternative).DefaultIfEmpty().Max() + 1;
            changes.Add(new(incumbent, Change(incumbent, alternative)));
        }
        return new(target, changes.ToArray());
    }

    internal static string DefaultDrawingNumber(StationSheet sheet) =>
        $"{sheet.Trafo} - " + (sheet.Alternative == 0 ? "Voorkeurslocatie"
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
        var project = HasOverview(db, tr) ? ReadFields(tr, GetLayout(db, tr, OverviewName), includePhoto: false) : StationFields.Empty;
        var shared = preferred == null ? project : ReadFields(tr, GetLayout(db, tr, preferred.Name), includePhoto: false).FillSharedFrom(project);
        foreach (var sheet in ReadSheets(db, tr))
        {
            if (shared.SharedAttributes().Values.All(value => !string.IsNullOrWhiteSpace(value))) break;
            if (sheet == preferred) continue;
            shared = shared.FillSharedFrom(ReadFields(tr, GetLayout(db, tr, sheet.Name), includePhoto: false));
        }
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
        return ReadFields(tr, GetLayout(db, tr, name), includePhoto: true);
    }

    private static StationFields ReadFields(Transaction tr, Layout layout, bool includePhoto)
    {
        var values = Title(tr, layout).AttributeCollection.Cast<ObjectId>()
            .Where(IsLive).Select(id => (AttributeReference)tr.GetObject(id, OpenMode.ForRead))
            .ToDictionary(a => a.Tag, a => a.TextString, StringComparer.OrdinalIgnoreCase);
        string Field(string key) => values.GetValueOrDefault(key, "");
        var photo = includePhoto ? PhotoImage(tr, layout) : null;
        var path = photo == null ? "" : PhotoPath(tr, photo, out _);
        return new(Field("PROJECTNAAM"), Field("DEELGEBIED"), Field("GEMEENTE"), Field("PROJECTNR.CIRCET"),
            Field("PROJECTNR.OPDR."), Field("GET"), Field("TDD"), Field("OVERIGE2"), Field("OVERIGE1"), Field("TEKENINGNUMMER"), path);
    }

    internal static bool PickLocation(Document doc, StationBlockChoice? choice, out Point3d point, out double rotation)
    {
        var db = doc.Database;
        using var tr = db.TransactionManager.StartTransaction();
        using var preview = new BlockReference(Point3d.Origin, ResolveBlock(db, tr, choice));
        preview.SetDatabaseDefaults(db);
        preview.ScaleFactors = choice?.Scale ?? new Scale3d(1);
        var accepted = new StationPlacementJig(preview).Pick(doc.Editor);
        if (accepted) tr.Commit();
        point = preview.Position;
        rotation = preview.Rotation;
        return accepted;
    }

    internal static void Create(Document doc, StationSheet sheet, StationFields fields, Point3d point, double rotation, StationBlockChoice? choice)
    {
        var db = doc.Database;
        if (db.Insunits is not (UnitsValue.Meters or UnitsValue.Undefined))
            throw new InvalidOperationException("Deze kaart gebruikt modelcoördinaten in meters. Controleer INSUNITS.");
        var layouts = LayoutManager.Current;
        if (layouts.LayoutExists(sheet.Name))
            throw new InvalidOperationException($"Layout ‘{sheet.Name}’ bestaat al.");
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
            var blockId = ResolveBlock(db, tr, choice);
            var model = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
            var reference = new BlockReference(point, blockId) { LayerId = layerId, Rotation = rotation, ScaleFactors = choice?.Scale ?? new Scale3d(1) };
            model.AppendEntity(reference);
            tr.AddNewlyCreatedDBObject(reference, true);
            AddBlockAttributes(tr, reference);
            WriteMetadata(db, tr, layout, sheet);
            UpdateCornerTable(db, tr, layout, reference, sheet.ShowCorners);
            step = "detailkaart centreren";
            Center(tr, viewport, point);
            step = "onderhoek invullen";
            ClearRevisions(db, tr, layout);
            if (ReadClient(db, tr) is { } client) WriteClient(db, tr, layout, client);
            WriteSignatureLogo(db, tr, layout, ReadSignatureLogo(db, tr));
            SetAttributes(db, tr, layout, fields.Attributes());
            WriteSharedProject(db, tr, fields);
            step = "locatiefoto plaatsen";
            SetPhoto(db, tr, layout, fields.Photo);
            step = "overzicht en laagzichtbaarheid bijwerken";
            RefreshMarkers(db, tr);
            AddLocationVisibility(db, tr, sheet, viewport, layerId);
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
            using (var tr = db.TransactionManager.StartTransaction())
            {
                RefreshMarkers(db, tr, updateOverview: true);
                tr.Commit();
            }
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
            if (ReadClient(db, tr) is { } client) WriteClient(db, tr, overview, client);
            WriteSignatureLogo(db, tr, overview, ReadSignatureLogo(db, tr));
            RefreshMarkers(db, tr, updateOverview: true);
            tr.Commit();
        }
        catch
        {
            try { layouts.DeleteLayout(OverviewName); }
            catch (Exception cleanupError) { doc.Editor.WriteMessage("\nTotaal opruimen: " + cleanupError.Message); }
            throw;
        }
        OpenSheet(doc, OverviewName);
    }

    private static void EnableCreatedViewport(Document doc, string layoutName)
    {
        try
        {
            OpenSheet(doc, layoutName);
        }
        catch (Exception ex)
        {
            doc.Editor.WriteMessage($"\nBlad ‘{layoutName}’ is aangemaakt. Open het blad om het kaartvenster opnieuw in te schakelen. {ex}");
        }
    }

    internal static void RepairExistingSheets(Database db)
    {
        using var tr = db.TransactionManager.StartTransaction();
        var dictionary = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        if (dictionary.Contains(SheetRepairKey)) return;
        var sheets = ReadSheets(db, tr);
        var hasOverview = HasOverview(db, tr);
        if (sheets.Count == 0 && !hasOverview) return;

        var signature = ReadSignatureLogo(db, tr);
        void Repair(string name, bool overview)
        {
            var layout = GetLayout(db, tr, name);
            DisableLayoutGrid(tr, layout);
            NativeStationGeometry.RepairFreeSpaceLegend(tr, layout, overview);
            NativeStationGeometry.RepairParcelLegend(db, tr, layout, overview);
            NativeStationGeometry.RemoveStrayLegendPolyline(tr, layout);
            WriteSignatureLogo(db, tr, layout, signature);
        }

        if (sheets.Count > 0)
        {
            var blocks = ModelBlocks(db, tr).Where(block => IsStationBlock(tr, block))
                .ToLookup(block => block.Layer, StringComparer.OrdinalIgnoreCase);
            foreach (var sheet in sheets)
            {
                Repair(sheet.Name, false);
                if (blocks[sheet.Layer].SingleOrDefault() is { } station)
                    UpdateCornerTable(db, tr, GetLayout(db, tr, sheet.Name), station, sheet.ShowCorners);
            }
        }
        if (hasOverview) Repair(OverviewName, true);
        // Save the migration marker in the same transaction as the repaired sheets.
        dictionary.UpgradeOpen();
        using var data = new ResultBuffer(new TypedValue((int)DxfCode.Int16, (short)1));
        var record = new Xrecord { Data = data };
        dictionary.SetAt(SheetRepairKey, record);
        tr.AddNewlyCreatedDBObject(record, true);
        tr.Commit();
    }

    internal static bool IsStationLayer(string layer) =>
        layer.StartsWith("TB_STATION_", StringComparison.OrdinalIgnoreCase);

    internal static void UpdateMovedStationTables(Database db, IReadOnlyCollection<ObjectId> changed)
    {
        using var tr = db.TransactionManager.StartTransaction();
        var stations = changed.Where(IsLive).Select(id => tr.GetObject(id, OpenMode.ForRead))
            .OfType<BlockReference>().Where(station => IsStationBlock(tr, station))
            .ToLookup(station => station.Layer, StringComparer.OrdinalIgnoreCase);
        if (stations.Count == 0) return;
        var refreshMarkers = false;
        foreach (var sheet in ReadSheets(db, tr))
        {
            if (stations[sheet.Layer].FirstOrDefault() is not { } station) continue;
            var layout = GetLayout(db, tr, sheet.Name);
            if (FindMapViewport(tr, layout) is { } viewport)
            {
                if (viewport.ViewTarget != station.Position)
                {
                    Center(tr, viewport, station.Position);
                    refreshMarkers = true;
                }
            }
            else
            {
                refreshMarkers = true;
            }
            UpdateCornerTable(db, tr, layout, station, sheet.ShowCorners);
        }
        if (refreshMarkers) RefreshMarkers(db, tr);
        tr.Commit();
    }

    private static ObjectId ResolveBlock(Database db, Transaction tr, StationBlockChoice? choice) =>
        choice == null ? NativeStationGeometry.GetSymbol(db, tr, Symbol) : StationBlockLibrary.Import(db, tr, choice);

    private static bool IsStationBlock(Transaction tr, BlockReference block)
    {
        var name = BlockName(tr, block);
        return name == Symbol || name.StartsWith(StationBlockLibrary.DefinitionPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddBlockAttributes(Transaction tr, BlockReference reference)
    {
        var definition = (BlockTableRecord)tr.GetObject(reference.BlockTableRecord, OpenMode.ForRead);
        foreach (ObjectId id in definition)
        {
            if (!IsLive(id) || tr.GetObject(id, OpenMode.ForRead) is not AttributeDefinition { Constant: false } template) continue;
            var attribute = new AttributeReference();
            attribute.SetAttributeFromBlock(template, reference.BlockTransform);
            attribute.TextString = template.TextString;
            reference.AttributeCollection.AppendAttribute(attribute);
            tr.AddNewlyCreatedDBObject(attribute, true);
        }
    }

    internal static void ApplyBlockChoice(Database db, StationSheet sheet, StationBlockChoice? choice)
    {
        using var tr = db.TransactionManager.StartTransaction();
        var station = LocationBlock(tr, ModelBlocks(db, tr), sheet);
        var target = ResolveBlock(db, tr, choice);
        if (station.BlockTableRecord != target)
        {
            var owner = (BlockTableRecord)tr.GetObject(station.OwnerId, OpenMode.ForWrite);
            var replacement = new BlockReference(station.Position, target)
            {
                Rotation = station.Rotation, Normal = station.Normal, LayerId = station.LayerId,
                ScaleFactors = choice?.Scale ?? new Scale3d(1)
            };
            owner.AppendEntity(replacement);
            tr.AddNewlyCreatedDBObject(replacement, true);
            AddBlockAttributes(tr, replacement);
            if (!station.IsWriteEnabled) station.UpgradeOpen();
            station.Erase();
            station = replacement;
            var order = (DrawOrderTable)tr.GetObject(owner.DrawOrderTableId, OpenMode.ForWrite);
            order.MoveToTop(new ObjectIdCollection(new[] { station.ObjectId }));
        }
        var layout = GetLayout(db, tr, sheet.Name);
        Center(tr, MapViewport(tr, layout), station.Position);
        UpdateCornerTable(db, tr, layout, station, sheet.ShowCorners);
        tr.Commit();
    }

    internal static void ToggleCornerTable(Database db, StationSheet sheet)
    {
        using var tr = db.TransactionManager.StartTransaction();
        var layout = GetLayout(db, tr, sheet.Name, OpenMode.ForWrite);
        var station = LocationBlock(tr, ModelBlocks(db, tr), sheet);
        var updated = sheet with { ShowCorners = !sheet.ShowCorners };
        WriteMetadata(db, tr, layout, updated);
        UpdateCornerTable(db, tr, layout, station, updated.ShowCorners);
        tr.Commit();
    }

    private static void UpdateCornerTable(Database db, Transaction tr, Layout layout, BlockReference station, bool show)
    {
        var standardBlock = BlockName(tr, station) == Symbol;
        if (!standardBlock) StationBlockLibrary.EnsureCornerArrows(tr, station);
        var coordinates = !show ? [] : standardBlock
            ? NativeStationGeometry.StationCorners(station)
            : StationBlockLibrary.Corners(tr, station);
        var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (coordinates.Length != 4 && !layers.Has(CornerTableLayer)) return;
        var layer = Layer(db, tr, CornerTableLayer);
        const double bottom = 3.124045803; // Bottom edge of the cable legend in the detail sheet template.
        const double offset = bottom - 5;
        const double legendLeft = 189.866277972;
        const double left = legendLeft - 64;
        var cells = new List<(string Text, double X, double Y, double Height)>
        {
            ("Hoekpunten trafo (m)", left + 3, 31 + offset, 2.6),
            ("Punt", left + 3, 25 + offset, 2.1), ("X", left + 18, 25 + offset, 2.1), ("Y", left + 41, 25 + offset, 2.1)
        };
        var culture = CultureInfo.GetCultureInfo("nl-NL");
        for (var index = 0; index < coordinates.Length; index++)
        {
            var y = 20 + offset - 4 * index;
            cells.Add(($"C{index + 1}", left + 3, y, 2.1));
            cells.Add((coordinates[index].X.ToString("F2", culture), left + 18, y, 2.1));
            cells.Add((coordinates[index].Y.ToString("F2", culture), left + 41, y, 2.1));
        }

        var space = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
        var previous = space.Cast<ObjectId>().Where(id => IsLive(id) && id.ObjectClass.DxfName is "TEXT" or "LWPOLYLINE" or "LINE")
            .Select(id => (Entity)tr.GetObject(id, OpenMode.ForRead)).Where(entity => entity.LayerId == layer).ToArray();
        var texts = previous.OfType<DBText>().ToArray();
        if (coordinates.Length == 4 && previous.Length == cells.Count + 2 && texts.Length == cells.Count && cells.All(cell =>
            texts.Any(text => text.TextString == cell.Text && text.Position == new Point3d(cell.X, cell.Y, 0)))) return;

        foreach (var entity in previous)
        {
            if (!entity.IsWriteEnabled) entity.UpgradeOpen();
            entity.Erase();
        }
        if (coordinates.Length != 4) return;
        if (!space.IsWriteEnabled) space.UpgradeOpen();
        void Add(Entity entity)
        {
            entity.LayerId = layer;
            space.AppendEntity(entity);
            tr.AddNewlyCreatedDBObject(entity, true);
        }

        // The cable legend supplies the right edge of the table.
        var border = new Polyline(4);
        var corners = new[] { new Point2d(legendLeft, bottom), new Point2d(left, bottom),
            new Point2d(left, 36 + offset), new Point2d(legendLeft, 36 + offset) };
        for (var index = 0; index < corners.Length; index++) border.AddVertexAt(index, corners[index], 0, 0, 0);
        Add(border);
        Add(new Line(new Point3d(left, 23.5 + offset, 0), new Point3d(legendLeft, 23.5 + offset, 0)));
        foreach (var cell in cells)
            Add(new DBText { TextString = cell.Text, Height = cell.Height, Position = new Point3d(cell.X, cell.Y, 0) });
    }

    private static ObjectId NoteLayerId(LayerTable layers, StationSheet sheet) =>
        layers.Has(sheet.Layer + NoteLayerSuffix) ? layers[sheet.Layer + NoteLayerSuffix] : ObjectId.Null;

    internal static void AddMapNote(Document doc, StationSheet sheet, string text, Point3d position)
    {
        var db = doc.Database;
        using var tr = db.TransactionManager.StartTransaction();
        var station = LocationBlock(tr, ModelBlocks(db, tr), sheet);
        var layer = Layer(db, tr, sheet.Layer + NoteLayerSuffix);
        var model = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
        var note = new DBText { TextString = text, Height = 0.55, Position = new Point3d(position.X, position.Y, station.Position.Z) };
        note.SetDatabaseDefaults(db);
        note.LayerId = layer;
        note.ColorIndex = 1;
        model.AppendEntity(note);
        tr.AddNewlyCreatedDBObject(note, true);
        var bounds = note.GeometricExtents;
        var center = new Point2d(station.Position.X, station.Position.Y);
        foreach (var x in new[] { bounds.MinPoint.X, bounds.MaxPoint.X })
            foreach (var y in new[] { bounds.MinPoint.Y, bounds.MaxPoint.Y })
                if (center.GetDistanceTo(new Point2d(x, y)) > MarkerRadius)
                    throw new InvalidOperationException("Plaats de volledige tekst binnen de rode cirkel. Kies een ander punt of kort de tekst in.");

        void SetVisibility(Layout layout, bool visible)
        {
            var viewport = FindMapViewport(tr, layout);
            if (viewport == null || viewport.IsLayerFrozenInViewport(layer) != visible) return;
            if (!viewport.IsWriteEnabled) viewport.UpgradeOpen();
            if (visible) viewport.ThawLayersInViewport(new[] { layer }.GetEnumerator());
            else viewport.FreezeLayersInViewport(new[] { layer }.GetEnumerator());
        }

        foreach (var other in ReadSheets(db, tr))
            SetVisibility(GetLayout(db, tr, other.Name), other.Layer == sheet.Layer);
        if (HasOverview(db, tr)) SetVisibility(GetLayout(db, tr, OverviewName), false);
        var drawOrder = (DrawOrderTable)tr.GetObject(model.DrawOrderTableId, OpenMode.ForWrite);
        drawOrder.MoveToTop(new ObjectIdCollection(new[] { note.ObjectId }));
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

    internal static void OpenSheet(Document doc, string layoutName)
    {
        LayoutManager.Current.CurrentLayout = layoutName;
        if (Convert.ToInt32(Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("CVPORT")) != 1)
            doc.Editor.SwitchToPaperSpace();
        Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("GRIDMODE", (short)0);

        var db = doc.Database;
        using var tr = db.TransactionManager.StartTransaction();
        var layout = GetLayout(db, tr, layoutName);
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
            var station = LocationBlock(tr, ModelBlocks(db, tr), location);
            Center(tr, viewport, station.Position);
            UpdateCornerTable(db, tr, layout, station, location.ShowCorners);
            var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            IsolateViewport(viewport, layers[location.Layer], StationLayers(tr, layers), NoteLayerId(layers, location));
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
        foreach (var block in ModelBlocks(db, tr).Where(block => block.Layer == sheet.Layer && IsStationBlock(tr, block)))
        {
            block.UpgradeOpen();
            block.Erase();
        }
        var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        var noteLayer = NoteLayerId(layers, sheet);
        if (!noteLayer.IsNull)
        {
            var model = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
            foreach (var id in model.Cast<ObjectId>().Where(IsLive).ToArray())
                if (tr.GetObject(id, OpenMode.ForRead) is DBText note && note.LayerId == noteLayer)
                {
                    note.UpgradeOpen();
                    note.Erase();
                }
        }
        RefreshMarkers(db, tr, sheet.Name);
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
            RefreshMarkers(db, tr);
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
        .Cast<ObjectId>().Where(id => IsLive(id) && id.ObjectClass.DxfName == "INSERT")
            .Select(id => tr.GetObject(id, OpenMode.ForRead)).OfType<BlockReference>().ToArray();

    private static BlockReference LocationBlock(Transaction tr, BlockReference[] blocks, StationSheet sheet) =>
        RequireLocationBlock(blocks.Where(block => block.Layer == sheet.Layer && IsStationBlock(tr, block)), sheet);

    private static BlockReference RequireLocationBlock(IEnumerable<BlockReference> blocks, StationSheet sheet)
    {
        var matches = blocks.Take(2).ToArray();
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

    private static IEnumerable<Entity> Entities(Transaction tr, Layout layout, string? dxfName = null) =>
        ((BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead)).Cast<ObjectId>()
        .Where(id => IsLive(id) && (dxfName == null || id.ObjectClass.DxfName == dxfName))
        .Select(id => tr.GetObject(id, OpenMode.ForRead)).OfType<Entity>();

    private static bool IsLive(ObjectId id) =>
        !id.IsNull && id.IsValid && !id.IsErased && !id.IsEffectivelyErased;

    private static Viewport? FindMapViewport(Transaction tr, Layout layout)
    {
        var viewports = Entities(tr, layout, "VIEWPORT").OfType<Viewport>().ToArray();
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
        Entities(tr, layout, "INSERT").OfType<BlockReference>().SingleOrDefault(block => BlockName(tr, block) is TitleBlock or NativeStationGeometry.TitleName)
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

    private static void AddLocationVisibility(Database db, Transaction tr, StationSheet added, Viewport viewport, ObjectId layer)
    {
        var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        IsolateViewport(viewport, layer, StationLayers(tr, layers));
        var additions = new[] { layer, layers[OverviewLayer] };
        foreach (var sheet in ReadSheets(db, tr, added.Name))
        {
            var existing = MapViewport(tr, GetLayout(db, tr, sheet.Name));
            var toFreeze = additions.Where(id => !existing.IsLayerFrozenInViewport(id)).ToArray();
            if (toFreeze.Length == 0) continue;
            if (!existing.IsWriteEnabled) existing.UpgradeOpen();
            existing.FreezeLayersInViewport(toFreeze.GetEnumerator());
        }
    }

    private static ObjectId[] StationLayers(Transaction tr, LayerTable layers) =>
        layers.Cast<ObjectId>().Where(IsLive).Where(id =>
        {
            var name = ((LayerTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name;
            return name.StartsWith("TB_STATION_", StringComparison.OrdinalIgnoreCase) && name != PhotoLayer;
        }).ToArray();

    private static void IsolateViewport(Viewport viewport, ObjectId own, ObjectId[] locations, ObjectId noteLayer = default)
    {
        var toThaw = new[] { own, noteLayer }.Where(id => !id.IsNull && viewport.IsLayerFrozenInViewport(id)).ToArray();
        var toFreeze = locations.Where(id => id != own && id != noteLayer && !viewport.IsLayerFrozenInViewport(id)).ToArray();
        if (toThaw.Length == 0 && toFreeze.Length == 0) return;
        if (!viewport.IsWriteEnabled) viewport.UpgradeOpen();
        if (toThaw.Length > 0)
            viewport.ThawLayersInViewport(toThaw.GetEnumerator());
        if (toFreeze.Length > 0)
            viewport.FreezeLayersInViewport(toFreeze.GetEnumerator());
    }

    private static void RefreshMarkers(Database db, Transaction tr, string? excluded = null, bool updateOverview = false)
    {
        var sheets = ReadSheets(db, tr, excluded);
        var overviewLayer = Layer(db, tr, OverviewLayer);
        var model = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
        var markers = new List<Entity>();
        var stationBlocks = new List<BlockReference>();
        foreach (var id in model)
        {
            if (!IsLive(id) || id.ObjectClass.DxfName is not ("INSERT" or "CIRCLE" or "MTEXT")) continue;
            if (tr.GetObject(id, OpenMode.ForRead) is not Entity entity) continue;
            if (entity.LayerId == overviewLayer) markers.Add(entity);
            else if (entity is BlockReference block && IsStationBlock(tr, block)) stationBlocks.Add(block);
        }
        var blocks = stationBlocks.ToLookup(block => block.Layer, StringComparer.OrdinalIgnoreCase);
        var locations = sheets.Select(sheet => (Sheet: sheet, Block: RequireLocationBlock(blocks[sheet.Layer], sheet))).ToArray();
        const double radius = MarkerRadius;
        const double textHeight = 10;
        const double labelGap = 3;
        if (updateOverview && HasOverview(db, tr))
        {
            var layout = GetLayout(db, tr, OverviewName);
            var viewport = FindMapViewport(tr, layout) ?? NativeStationGeometry.AppendMapViewport(db, tr, layout, true);
            if (locations.Length > 0)
            {
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
                var frozen = sheets.Select(sheet => layers[sheet.Layer]).Append(overviewLayer)
                    .Where(viewport.IsLayerFrozenInViewport).ToArray();
                if (frozen.Length > 0) viewport.ThawLayersInViewport(frozen.GetEnumerator());
                var notes = sheets.Select(sheet => NoteLayerId(layers, sheet))
                    .Where(id => !id.IsNull && !viewport.IsLayerFrozenInViewport(id)).ToArray();
                if (notes.Length > 0) viewport.FreezeLayersInViewport(notes.GetEnumerator());
                SetAttributes(db, tr, layout, new Dictionary<string, string> { ["SCHAAL"] = "1:" + scale });
            }
        }

        var circles = markers.OfType<Circle>().ToLookup(circle => circle.Center);
        var labels = markers.OfType<MText>().ToLookup(label => label.Contents, StringComparer.Ordinal);
        var retained = new HashSet<ObjectId>();
        var foreground = new ObjectIdCollection();
        using var red = Autodesk.AutoCAD.Colors.Color.FromRgb(255, 0, 0);
        bool HasRedColor(Circle item)
        {
            using var color = item.Color;
            return color.CompareTo(red) == 0;
        }

        foreach (var location in locations)
        {
            var point = location.Block.Position;
            var labelPoint = point + new Vector3d(radius + labelGap, 0, 0);
            var label = labels[location.Sheet.Name].FirstOrDefault(item =>
                item.Location == labelPoint && item.TextHeight == textHeight && item.Attachment == AttachmentPoint.MiddleLeft);
            var circle = circles[point].FirstOrDefault(item => !retained.Contains(item.ObjectId) &&
                item.Radius == radius && item.Normal == Vector3d.ZAxis && HasRedColor(item));

            if (label != null) retained.Add(label.ObjectId);
            if (circle != null) retained.Add(circle.ObjectId);
            if (label != null && circle != null) continue;

            if (!model.IsWriteEnabled) model.UpgradeOpen();
            if (label == null)
            {
                label = new MText { Location = labelPoint, TextHeight = textHeight, Contents = location.Sheet.Name,
                    LayerId = overviewLayer, Attachment = AttachmentPoint.MiddleLeft };
                model.AppendEntity(label);
                tr.AddNewlyCreatedDBObject(label, true);
            }
            if (circle == null)
            {
                circle = new Circle(point, Vector3d.ZAxis, radius) { LayerId = overviewLayer, Color = red };
                model.AppendEntity(circle);
                tr.AddNewlyCreatedDBObject(circle, true);
            }
            foreground.Add(location.Block.ObjectId);
            foreground.Add(circle.ObjectId);
            foreground.Add(label.ObjectId);
        }
        foreach (var marker in markers)
        {
            if (retained.Contains(marker.ObjectId)) continue;
            if (!marker.IsWriteEnabled) marker.UpgradeOpen();
            marker.Erase();
        }
        if (foreground.Count > 0)
        {
            var drawOrder = (DrawOrderTable)tr.GetObject(model.DrawOrderTableId, OpenMode.ForWrite);
            drawOrder.MoveToTop(foreground);
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
            new TypedValue(1000, sheet.Trafo), new TypedValue(1071, sheet.Alternative), new TypedValue(1000, sheet.Layer), new TypedValue(1070, (short)(sheet.ShowCorners ? 1 : 0)));
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
            if (values == null || values.Length is not (4 or 5)) continue;
            result.Add(new(layout.LayoutName, Convert.ToString(values[1].Value, CultureInfo.InvariantCulture) ?? "",
                Convert.ToInt32(values[2].Value, CultureInfo.InvariantCulture), (string)values[3].Value, values.Length == 4 || Convert.ToInt16(values[4].Value) != 0));
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

    private static RasterImage? PhotoImage(Transaction tr, Layout layout) =>
        ((BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead)).Cast<ObjectId>()
            .Where(id => IsLive(id) && id.ObjectClass.DxfName == "IMAGE")
            .Select(id => (RasterImage)tr.GetObject(id, OpenMode.ForRead))
            .FirstOrDefault(image => image.Layer == PhotoLayer);

    private static void SetPhoto(Database db, Transaction tr, Layout layout, string path)
    {
        var oldPhoto = PhotoImage(tr, layout);
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

    internal static string ReadSignatureLogo(Database db)
    {
        using var tr = db.TransactionManager.StartTransaction();
        return ReadSignatureLogo(db, tr);
    }

    private static string ReadSignatureLogo(Database db, Transaction tr)
    {
        var dictionary = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        if (!dictionary.Contains(SignatureKey)) return StationSignatureDialog.DefaultKey;
        var record = (Xrecord)tr.GetObject(dictionary.GetAt(SignatureKey), OpenMode.ForRead);
        using var data = record.Data;
        return data?.AsArray().FirstOrDefault().Value as string ?? StationSignatureDialog.DefaultKey;
    }

    internal static void ApplySignatureLogo(Database db, string logo)
    {
        using var tr = db.TransactionManager.StartTransaction();
        var dictionary = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForWrite);
        Xrecord record;
        if (dictionary.Contains(SignatureKey))
            record = (Xrecord)tr.GetObject(dictionary.GetAt(SignatureKey), OpenMode.ForWrite);
        else
        {
            record = new Xrecord();
            dictionary.SetAt(SignatureKey, record);
            tr.AddNewlyCreatedDBObject(record, true);
        }
        using var data = new ResultBuffer(new TypedValue(1, logo));
        record.Data = data;

        foreach (var sheet in ReadSheets(db, tr)) WriteSignatureLogo(db, tr, GetLayout(db, tr, sheet.Name), logo);
        if (HasOverview(db, tr)) WriteSignatureLogo(db, tr, GetLayout(db, tr, OverviewName), logo);
        tr.Commit();
    }

    private static void WriteSignatureLogo(Database db, Transaction tr, Layout layout, string key)
    {
        var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        var previous = layers.Has(SignatureLayer)
            ? Entities(tr, layout).OfType<RasterImage>().Where(image => image.LayerId == layers[SignatureLayer]).ToArray()
            : [];
        var path = ClientLibrary.LogoPath(key);
        if (path.Length > 0 && !File.Exists(path))
            throw new FileNotFoundException("Het opgeslagen paraaflogo ontbreekt.", path);
        if (path.Length > 0 && previous.Length == 1 &&
            string.Equals(PhotoPath(tr, previous[0], out _), path, StringComparison.OrdinalIgnoreCase)) return;

        foreach (var old in previous)
        {
            if (!old.IsWriteEnabled) old.UpgradeOpen();
            old.Erase();
        }
        if (path.Length == 0) return;

        using var logo = System.Drawing.Image.FromFile(path);
        var scale = Math.Min(27.0 / logo.Width, 8.0 / logo.Height);
        var width = logo.Width * scale;
        var height = logo.Height * scale;
        var origin = Title(tr, layout).Position;
        var frame = new PhotoFrame(origin.X + 377.45 + (27 - width) / 2, origin.Y + 35.08 - height / 2, width, height);
        AttachPhoto(db, tr, layout, path, path, frame, SignatureLayer);
    }

    internal static ClientProfile? ReadClient(Database db)
    {
        using var tr = db.TransactionManager.StartTransaction();
        return ReadClient(db, tr);
    }

    private static ClientProfile? ReadClient(Database db, Transaction tr)
    {
        var dictionary = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        if (!dictionary.Contains(ClientKey)) return null;
        var record = (Xrecord)tr.GetObject(dictionary.GetAt(ClientKey), OpenMode.ForRead);
        using var data = record.Data;
        var fields = data?.AsArray().Select(value => value.Value as string ?? "").ToArray();
        return fields is { Length: 7 } && Guid.TryParse(fields[0], out var id)
            ? new(id, fields[1], fields[2], fields[3], fields[4], fields[5], fields[6])
            : null;
    }

    internal static void ApplyClient(Database db, ClientProfile? client)
    {
        using var tr = db.TransactionManager.StartTransaction();
        var dictionary = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForWrite);
        if (client != null)
        {
            Xrecord record;
            if (dictionary.Contains(ClientKey))
                record = (Xrecord)tr.GetObject(dictionary.GetAt(ClientKey), OpenMode.ForWrite);
            else
            {
                record = new Xrecord();
                dictionary.SetAt(ClientKey, record);
                tr.AddNewlyCreatedDBObject(record, true);
            }
            string[] fields = [client.Id.ToString(), client.Name, client.Address, client.Phone, client.Website, client.Email, client.Logo];
            using var data = new ResultBuffer(fields.Select(field => new TypedValue(1, field)).ToArray());
            record.Data = data;
        }
        else if (dictionary.Contains(ClientKey))
            tr.GetObject(dictionary.Remove(ClientKey), OpenMode.ForWrite).Erase();

        foreach (var sheet in ReadSheets(db, tr))
            WriteClient(db, tr, GetLayout(db, tr, sheet.Name), client);
        if (HasOverview(db, tr)) WriteClient(db, tr, GetLayout(db, tr, OverviewName), client);
        tr.Commit();
    }

    private static void WriteClient(Database db, Transaction tr, Layout layout, ClientProfile? client)
    {
        var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (layers.Has(ClientLayer))
        {
            var oldLayer = layers[ClientLayer];
            foreach (var entity in Entities(tr, layout).Where(entity => entity.LayerId == oldLayer).ToArray())
            {
                if (!entity.IsWriteEnabled) entity.UpgradeOpen();
                entity.Erase();
            }
        }
        if (client == null) return;

        // Client panel under the revision box in the embedded title block.
        const double left = 266.1;
        const double width = 25.85;
        var origin = Title(tr, layout).Position;
        var layer = Layer(db, tr, ClientLayer);
        var space = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForWrite);
        void AddLine(string value, double y, double height)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var text = new DBText();
            text.SetDatabaseDefaults(db);
            text.LayerId = layer;
            text.TextString = value;
            text.Height = height;
            var extent = text.GeometricExtents.MaxPoint.X;
            if (extent > width) text.Height *= width / extent;
            text.Position = origin + new Vector3d(left, y, 0);
            space.AppendEntity(text);
            tr.AddNewlyCreatedDBObject(text, true);
        }

        var logo = ClientLibrary.LogoPath(client.Logo);
        if (File.Exists(logo))
        {
            using var image = System.Drawing.Image.FromFile(logo);
            var scale = Math.Min(width / image.Width, 8.2 / image.Height);
            var logoWidth = image.Width * scale;
            var logoHeight = image.Height * scale;
            var frame = new PhotoFrame(origin.X + left + (width - logoWidth) / 2,
                origin.Y + 23 - logoHeight / 2, logoWidth, logoHeight);
            AttachPhoto(db, tr, layout, logo, logo, frame, ClientLayer);
        }
        else AddLine(client.Name, 22, 2.1);

        AddLine(client.Address, 10.45, 1.4);
        AddLine(client.Phone.Length == 0 ? "" : "T: " + client.Phone, 8.3, 1.4);
        AddLine(client.Website.Length == 0 ? "" : "I: " + client.Website, 6.15, 1.4);
        AddLine(client.Email.Length == 0 ? "" : "E: " + client.Email, 4, 1.4);
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
        var title = Title(tr, layout);
        foreach (ObjectId id in title.AttributeCollection)
        {
            if (!IsLive(id)) continue;
            var attribute = (AttributeReference)tr.GetObject(id, OpenMode.ForRead);
            if (!values.TryGetValue(attribute.Tag, out var value)) continue;
            var height = attribute.Tag.Equals("TEKENINGNUMMER", StringComparison.OrdinalIgnoreCase)
                ? DrawingNumberHeight(db, title, attribute, value)
                : attribute.Height;
            if (attribute.TextString == value && Math.Abs(attribute.Height - height) < 1e-8) continue;
            if (!attribute.IsWriteEnabled) attribute.UpgradeOpen();
            attribute.TextString = value;
            attribute.Height = height;
            attribute.AdjustAlignment(db);
        }
    }

    private static double DrawingNumberHeight(Database db, BlockReference title, AttributeReference attribute, string value)
    {
        // The detail sheets and Totaal use the same title block cell.
        const double templateHeight = 2.94;
        const double widthFromInsertionToBorder = 49.308;
        const double rightPadding = 1.092;
        var height = templateHeight * Math.Abs(title.ScaleFactors.Y);
        if (string.IsNullOrWhiteSpace(value)) return height;

        using var text = new DBText();
        text.SetDatabaseDefaults(db);
        text.TextStyleId = attribute.TextStyleId;
        text.Height = height;
        text.WidthFactor = attribute.WidthFactor;
        text.Oblique = attribute.Oblique;
        text.TextString = value;
        var width = text.GeometricExtents.MaxPoint.X;
        var available = (widthFromInsertionToBorder - rightPadding) * Math.Abs(title.ScaleFactors.X);
        return width > available ? height * available / width : height;
    }

    private static void AttachPhoto(Database db, Transaction tr, Layout layout, string source, string raster, PhotoFrame frame, string layer = PhotoLayer)
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
            ImageDefId = id, LayerId = Layer(db, tr, layer), ShowImage = true,
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
