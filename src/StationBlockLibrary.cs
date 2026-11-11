using System.IO;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace Stationslocaties;

internal sealed record StationBlockChoice(Guid Id, string Name, double XScale, double YScale, double ZScale)
{
    internal Scale3d Scale => new(XScale, YScale, ZScale);
    internal string DefinitionName => StationBlockLibrary.DefinitionPrefix + Id.ToString("N");
    public override string ToString() => Name;
}

internal sealed record StationBlockCatalog(StationBlockChoice[] Blocks, Guid? DefaultId)
{
    internal StationBlockChoice? Default => Blocks.FirstOrDefault(block => block.Id == DefaultId);
}

internal static class StationBlockLibrary
{
    internal const string DefinitionPrefix = "TB_TRAFO_BIB_";
    private const string CornerKey = "STATIONLOCATIES_C1_C4";
    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Stationlocaties", "Trafoblokken");
    private static string CatalogFile => Path.Combine(Folder, "Catalogus.json");
    private static string DrawingFile(StationBlockChoice block) => Path.Combine(Folder, block.Id.ToString("N") + ".dwg");

    internal static StationBlockCatalog Read()
    {
        if (!File.Exists(CatalogFile)) return new([], null);
        using var stream = File.OpenRead(CatalogFile);
        return JsonSerializer.Deserialize<StationBlockCatalog>(stream) ?? new([], null);
    }

    private static void Save(StationBlockCatalog catalog)
    {
        Directory.CreateDirectory(Folder);
        var temporary = Path.Combine(Folder, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(catalog));
            File.Move(temporary, CatalogFile, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    internal static void SetDefault(StationBlockChoice? choice)
    {
        var catalog = Read();
        Save(catalog with { DefaultId = choice?.Id });
    }

    internal static void Remove(StationBlockChoice choice)
    {
        var catalog = Read();
        Save(new(catalog.Blocks.Where(block => block.Id != choice.Id).ToArray(),
            catalog.DefaultId == choice.Id ? null : catalog.DefaultId));
        File.Delete(DrawingFile(choice));
    }

    internal static void Register(Document doc, bool includeCorners)
    {
        var editor = doc.Editor;
        var prompt = new PromptEntityOptions("\nSelecteer een trafoblok in modelruimte: ");
        prompt.SetRejectMessage("\nSelecteer een blokreferentie.");
        prompt.AddAllowedClass(typeof(BlockReference), true);
        var result = editor.GetEntity(prompt);
        if (result.Status != PromptStatus.OK) return;

        var db = doc.Database;
        ObjectId sourceId;
        Matrix3d inverse;
        Scale3d scale;
        string name;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var reference = (BlockReference)tr.GetObject(result.ObjectId, OpenMode.ForRead);
            sourceId = reference.BlockTableRecord;
            var definition = (BlockTableRecord)tr.GetObject(sourceId, OpenMode.ForRead);
            if (reference.IsDynamicBlock || definition.IsAnonymous || definition.IsFromExternalReference ||
                reference.OwnerId != SymbolUtilityServices.GetBlockModelSpaceId(db))
                throw new InvalidOperationException("Kies een benoemd, statisch blok in modelruimte.");
            inverse = reference.BlockTransform.Inverse();
            scale = reference.ScaleFactors;
            name = definition.Name;
        }

        var points = new List<Point3d>();
        if (includeCorners)
        {
            for (var index = 1; index <= 4; index++)
            {
                var corner = editor.GetPoint($"\nWijs hoekpunt C{index} aan: ");
                if (corner.Status != PromptStatus.OK) return;
                points.Add(corner.Value.TransformBy(editor.CurrentUserCoordinateSystem).TransformBy(inverse));
            }
            if (points.Select(p => new Point2d(p.X, p.Y)).Distinct().Count() != 4)
                throw new InvalidOperationException("Kies vier verschillende hoekpunten.");
        }

        var catalog = Read();
        var displayName = name;
        for (var index = 2; catalog.Blocks.Any(block => string.Equals(block.Name, displayName, StringComparison.OrdinalIgnoreCase)); index++)
            displayName = $"{name} ({index})";
        var choice = new StationBlockChoice(Guid.NewGuid(), displayName, scale.X, scale.Y, scale.Z);
        Directory.CreateDirectory(Folder);
        using var copy = new Database(true, true);
        var mapping = new IdMapping();
        db.WblockCloneObjects(new ObjectIdCollection(new[] { sourceId }), copy.BlockTableId, mapping, DuplicateRecordCloning.Ignore, false);
        using (var tr = copy.TransactionManager.StartTransaction())
        {
            var definition = (BlockTableRecord)tr.GetObject(mapping[sourceId].Value, OpenMode.ForWrite);
            definition.Name = choice.DefinitionName;
            WriteCorners(tr, definition, points);
            tr.Commit();
        }

        var file = DrawingFile(choice);
        try
        {
            copy.SaveAs(file, DwgVersion.Current);
            Save(new(catalog.Blocks.Append(choice).ToArray(), choice.Id));
        }
        catch
        {
            File.Delete(file);
            throw;
        }
        editor.WriteMessage($"\n'{choice.Name}' is opgeslagen en ingesteld voor nieuwe locaties.");
    }

    internal static ObjectId Import(Database destination, Transaction tr, StationBlockChoice choice)
    {
        var blocks = (BlockTable)tr.GetObject(destination.BlockTableId, OpenMode.ForRead);
        if (blocks.Has(choice.DefinitionName)) return blocks[choice.DefinitionName];

        using var source = new Database(false, true);
        source.ReadDwgFile(DrawingFile(choice), FileOpenMode.OpenForReadAndAllShare, false, null);
        source.CloseInput(true);
        ObjectId sourceId;
        using (var read = source.TransactionManager.StartTransaction())
        {
            var saved = (BlockTable)read.GetObject(source.BlockTableId, OpenMode.ForRead);
            sourceId = saved[choice.DefinitionName];
        }
        var mapping = new IdMapping();
        source.WblockCloneObjects(new ObjectIdCollection(new[] { sourceId }), destination.BlockTableId,
            mapping, DuplicateRecordCloning.MangleName, false);
        var imported = (BlockTableRecord)tr.GetObject(mapping[sourceId].Value, OpenMode.ForWrite);
        if (imported.Name != choice.DefinitionName) imported.Name = choice.DefinitionName;
        return imported.ObjectId;
    }

    private static void WriteCorners(Transaction tr, BlockTableRecord definition, IReadOnlyList<Point3d> points)
    {
        if (definition.ExtensionDictionary.IsNull) definition.CreateExtensionDictionary();
        var dictionary = (DBDictionary)tr.GetObject(definition.ExtensionDictionary, OpenMode.ForWrite);
        if (dictionary.Contains(CornerKey))
        {
            var old = dictionary.Remove(CornerKey);
            tr.GetObject(old, OpenMode.ForWrite).Erase();
        }
        if (points.Count != 4) return;
        using var data = new ResultBuffer(points.Select(point => new TypedValue((int)DxfCode.XCoordinate, point)).ToArray());
        var record = new Xrecord { Data = data };
        dictionary.SetAt(CornerKey, record);
        tr.AddNewlyCreatedDBObject(record, true);
    }

    internal static Point3d[] Corners(Transaction tr, BlockReference station)
    {
        var definition = (BlockTableRecord)tr.GetObject(station.BlockTableRecord, OpenMode.ForRead);
        if (definition.ExtensionDictionary.IsNull) return [];
        var dictionary = (DBDictionary)tr.GetObject(definition.ExtensionDictionary, OpenMode.ForRead);
        if (!dictionary.Contains(CornerKey)) return [];
        var record = (Xrecord)tr.GetObject(dictionary.GetAt(CornerKey), OpenMode.ForRead);
        using var data = record.Data;
        return data?.AsArray().Select(value => value.Value).OfType<Point3d>()
            .Select(point => point.TransformBy(station.BlockTransform)).ToArray() ?? [];
    }
}
