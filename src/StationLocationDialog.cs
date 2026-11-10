using System.Drawing;
using System.Globalization;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(Stationslocaties.Plugin))]

namespace Stationslocaties;

public sealed class Plugin
{
    [CommandMethod("STATIONLOCATIES", CommandFlags.Modal)]
    public void ShowStationLocations()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return;
        StationSheet? selected = null;
        while (true)
        {
            try
            {
                var sheets = StationLocationSheets.List(doc.Database);
                using var dialog = new StationLocationDialog(sheets, selected);
                if (AcApp.ShowModalDialog(dialog) != DialogResult.OK) return;
                selected = dialog.Selected;
                switch (dialog.Action)
                {
                    case StationLocationAction.Add:
                        var project = StationLocationSheets.ProjectFields(doc.Database, selected);
                        using (var input = new StationFieldsDialog(project with
                        {
                            Date = StationFields.Today, Address = "", Notes = "", DrawingNumber = "", Photo = ""
                        }, adding: true,
                            number: selected?.Number ?? 1, preferred: !sheets.Any(s => s.Number == (selected?.Number ?? 1) && s.Alternative == 0),
                            photoFolder: StationLocationSheets.PhotoFolder(doc.Database),
                            existingPreferred: sheets.Where(s => s.Alternative == 0).Select(s => s.Number).ToHashSet()))
                        {
                            if (AcApp.ShowModalDialog(input) != DialogResult.OK) break;
                            var sheet = StationLocationSheets.Next(doc.Database, input.Number, input.Preferred);
                            var fields = input.Fields;
                            if (string.IsNullOrWhiteSpace(fields.DrawingNumber))
                                fields = fields with { DrawingNumber = StationLocationSheets.DefaultDrawingNumber(sheet) };
                            LayoutManager.Current.CurrentLayout = "Model";
                            var editor = doc.Editor;
                            var pick = editor.GetPoint("\nKlik op het midden van de trafo: ");
                            if (pick.Status != PromptStatus.OK) break;
                            var direction = editor.GetPoint(new PromptPointOptions("\nKlik voor de draairichting (Enter voor 0 graden): ")
                            {
                                AllowNone = true, UseBasePoint = true, BasePoint = pick.Value
                            });
                            if (direction.Status is not (PromptStatus.OK or PromptStatus.None)) break;
                            var ucs = editor.CurrentUserCoordinateSystem;
                            var point = pick.Value.TransformBy(ucs);
                            var rotation = 0.0;
                            if (direction.Status == PromptStatus.OK)
                            {
                                var delta = direction.Value.TransformBy(ucs) - point;
                                if (Math.Abs(delta.X) + Math.Abs(delta.Y) < 1e-8)
                                    throw new InvalidOperationException("Kies voor de draairichting een tweede punt naast de trafo.");
                                rotation = Math.Atan2(delta.Y, delta.X);
                            }
                            if (input.LookupAddress && string.IsNullOrWhiteSpace(fields.Address))
                            {
                                editor.WriteMessage("\nDichtstbijzijnd adres ophalen bij PDOK…");
                                var result = PdokAddress.Lookup(point);
                                if (result.Address != null) fields = fields with { Address = result.Address };
                                editor.WriteMessage("\n" + result.Message);
                            }
                            StationLocationSheets.Create(doc, sheet, fields, new Point3d(point.X, point.Y, 0), rotation);
                            selected = sheet;
                            editor.Regen();
                            editor.WriteMessage($"\nA3-layout ‘{sheet.Name}’ aangemaakt. ‘Totaal’ is bijgewerkt.");
                        }
                        break;
                    case StationLocationAction.Edit when selected != null:
                        using (var input = new StationFieldsDialog(StationLocationSheets.ReadFields(doc.Database, selected)
                            .FillSharedFrom(StationLocationSheets.ProjectFields(doc.Database)),
                            photoFolder: StationLocationSheets.PhotoFolder(doc.Database),
                            locate: () => StationLocationSheets.Position(doc.Database, selected)))
                            if (AcApp.ShowModalDialog(input) == DialogResult.OK)
                            {
                                StationLocationSheets.Edit(doc, selected, input.Fields);
                                doc.Editor.Regen();
                            }
                        break;
                    case StationLocationAction.EditAll:
                        using (var table = new SheetTableDialog(StationLocationSheets.ReadSheetRows(doc.Database, sheets),
                            StationLocationSheets.PhotoFolder(doc.Database)))
                            if (AcApp.ShowModalDialog(table) == DialogResult.OK && table.Changes.Count > 0)
                            {
                                StationLocationSheets.UpdateSheetRows(doc.Database, table.Changes, table.LastPhoto);
                                doc.Editor.Regen();
                            }
                        break;
                    case StationLocationAction.Project:
                        using (var input = new StationFieldsDialog(StationLocationSheets.ProjectFields(doc.Database), project: true))
                            if (AcApp.ShowModalDialog(input) == DialogResult.OK)
                                StationLocationSheets.UpdateProject(doc.Database, input.Fields);
                        break;
                    case StationLocationAction.Open when selected != null:
                        StationLocationSheets.OpenSheet(doc, selected.Name);
                        doc.Editor.Regen();
                        return;
                    case StationLocationAction.Overview:
                        StationLocationSheets.OpenSheet(doc, StationLocationSheets.OverviewName);
                        doc.Editor.Regen();
                        return;
                }
            }
            catch (System.Exception ex)
            {
                doc.Editor.WriteMessage("\nStationlocaties: " + ex.Message);
                MessageBox.Show(ex.Message, "Techbase Stationlocaties", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}

internal enum StationLocationAction { Add, Edit, EditAll, Project, Open, Overview }

internal sealed class StationLocationDialog : Form
{
    private readonly ListView locations = new() { View = View.Details, Dock = DockStyle.Fill, FullRowSelect = true, MultiSelect = false, HideSelection = false };
    internal StationSheet? Selected => locations.SelectedItems.Count == 1 ? (StationSheet?)locations.SelectedItems[0].Tag : null;
    internal StationLocationAction Action { get; private set; }

    internal StationLocationDialog(List<StationSheet> sheets, StationSheet? previous)
    {
        Text = "Techbase | Stationlocaties";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(900, 380);
        MinimumSize = new Size(800, 350);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(14) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        var incomplete = sheets.GroupBy(sheet => sheet.Number).Where(group =>
            !group.Any(sheet => sheet.Alternative == 0) || !group.Any(sheet => sheet.Alternative > 0))
            .Select(group => "Trafo " + group.Key);
        var pending = string.Join(", ", incomplete);
        var message = sheets.Count == 0
            ? "Begin met een voorkeurslocatie (VL). Voeg daarna één of meer alternatieve locaties (AL) toe."
            : string.IsNullOrEmpty(pending) ? "Alle trafo’s hebben een VL en minstens één AL."
                : "VL of AL ontbreekt bij " + pending + ".";
        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill, Text = "TRAFOLOCATIEKAARTEN   /   A3\r\n" + message,
            TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true
        }, 0, 0);
        locations.Columns.Add("Blad", 250);
        locations.Columns.Add("Locatietype", 200);
        foreach (var sheet in sheets)
        {
            var item = new ListViewItem(new[] { sheet.Name, sheet.Alternative == 0 ? "Voorkeurslocatie" : "Alternatieve locatie " + sheet.Alternative })
                { Tag = sheet };
            locations.Items.Add(item);
            item.Selected = sheet.Name == previous?.Name;
        }
        if (locations.SelectedItems.Count == 0 && locations.Items.Count > 0 && previous == null)
            locations.Items[0].Selected = true;
        layout.Controls.Add(locations, 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
        Button Add(string caption, StationLocationAction action, bool enabled = true)
        {
            var button = new Button { Text = caption, AutoSize = true, Enabled = enabled };
            button.Click += (_, _) => Choose(action);
            actions.Controls.Add(button);
            return button;
        }
        Add("Locatie plaatsen", StationLocationAction.Add);
        var edit = Add("Geselecteerd blad", StationLocationAction.Edit, false);
        var open = Add("Open blad", StationLocationAction.Open, false);
        Add("Alle bladen bewerken", StationLocationAction.EditAll, sheets.Count > 0);
        Add("Projectgegevens", StationLocationAction.Project, sheets.Count > 0);
        Add("Open Totaal", StationLocationAction.Overview, sheets.Count > 0);
        var close = new Button { Text = "Sluiten", AutoSize = true, DialogResult = DialogResult.Cancel };
        actions.Controls.Add(close);
        CancelButton = close;
        locations.SelectedIndexChanged += (_, _) => edit.Enabled = open.Enabled = Selected != null;
        Shown += (_, _) => edit.Enabled = open.Enabled = Selected != null;
        locations.DoubleClick += (_, _) => { if (Selected != null) Choose(StationLocationAction.Open); };
        layout.Controls.Add(actions, 0, 2);
        Controls.Add(layout);
    }

    private void Choose(StationLocationAction action)
    {
        Action = action;
        DialogResult = DialogResult.OK;
        Close();
    }
}

internal sealed class StationFieldsDialog : Form
{
    private readonly Dictionary<string, TextBox> fields = new();
    private readonly NumericUpDown number = new() { Minimum = 1, Maximum = 999999, Width = 90 };
    private readonly ComboBox kind = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
    private readonly StationFields original;
    private readonly string photoFolder;
    private readonly CheckBox lookupAddress = new() { Text = "Leeg adres automatisch opzoeken bij PDOK", Checked = true, AutoSize = true };
    private readonly TableLayoutPanel grid = new() { Dock = DockStyle.Fill, ColumnCount = 3, AutoScroll = true, Padding = new Padding(12) };
    private int row;
    internal int Number => (int)number.Value;
    internal bool Preferred => kind.SelectedIndex == 0;
    internal bool LookupAddress => lookupAddress.Checked;
    internal StationFields Fields => new(Value("Projectnaam", original.Project), Value("Deelgebied", original.Area),
        Value("Gemeente", original.Municipality), Value("Intern projectnummer", original.ProjectNumber),
        Value("Projectnummer opdrachtgever", original.ClientNumber), Value("Initialen tekenaar", original.Drafter),
        Value("Datum", original.Date), Value("Adres / locatie", original.Address), Value("Opmerking", original.Notes),
        Value("Tekeningnummer", original.DrawingNumber), Value("Locatiefoto", original.Photo));

    internal StationFieldsDialog(StationFields initial, bool adding = false, bool project = false,
        int number = 1, bool preferred = true, string photoFolder = "", HashSet<int>? existingPreferred = null,
        Func<Point3d>? locate = null)
    {
        original = initial;
        this.photoFolder = photoFolder;
        Text = adding ? "Locatie plaatsen" : project ? "Projectgegevens en blad Totaal" : "Bladgegevens en locatiefoto";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(680, adding ? 610 : 540);
        MinimumSize = new Size(610, 345);
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 195));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        if (adding)
        {
            this.number.Value = number;
            kind.Items.AddRange(new[] { "VL – Voorkeurslocatie", "AL – Alternatieve locatie" });
            kind.SelectedIndex = preferred ? 0 : 1;
            this.number.ValueChanged += (_, _) => kind.SelectedIndex = existingPreferred?.Contains(Number) == true ? 1 : 0;
            Row("Trafo-nummer", this.number);
            Row("Locatietype", kind);
        }
        Row("Projectgegevens", new Label { Text = "Gelden voor alle bladen", AutoSize = true, Anchor = AnchorStyles.Left });
        TextRow("Projectnaam", initial.Project);
        TextRow("Deelgebied", initial.Area);
        TextRow("Gemeente", initial.Municipality);
        TextRow("Intern projectnummer", initial.ProjectNumber);
        TextRow("Projectnummer opdrachtgever", initial.ClientNumber);
        TextRow("Initialen tekenaar", initial.Drafter);
        var date = TextRow("Datum", initial.Date);
        var today = new Button { Text = "Vandaag", AutoSize = true };
        today.Click += (_, _) => date.Text = StationFields.Today;
        grid.Controls.Add(today, 2, row - 1);
        var address = TextRow("Adres / locatie", initial.Address);
        if (adding) Row("RD-coördinaten", lookupAddress);
        if (locate != null)
        {
            var search = new Button { Text = "PDOK", AutoSize = true };
            search.Click += (_, _) =>
            {
                try
                {
                    var result = PdokAddress.Lookup(locate());
                    if (result.Address != null) address.Text = result.Address;
                    else MessageBox.Show(this, result.Message, Text);
                }
                catch (System.Exception ex)
                {
                    MessageBox.Show(this, ex.Message, Text);
                }
            };
            grid.Controls.Add(search, 2, row - 1);
        }
        TextRow("Opmerking", initial.Notes);
        TextRow("Tekeningnummer", initial.DrawingNumber);
        if (!project) FileRow("Locatiefoto", initial.Photo, "Afbeeldingen (*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff)|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff");
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft, Padding = new Padding(12, 4, 12, 0) };
        var cancel = new Button { Text = "Annuleren", AutoSize = true, DialogResult = DialogResult.Cancel };
        var ok = new Button { Text = adding ? "Plaatsen op kaart" : "Opslaan", AutoSize = true };
        ok.Click += (_, _) =>
        {
            var current = Fields;
            if (!DateTime.TryParseExact(current.Date, new[] { "d-M-yyyy", "dd-MM-yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                MessageBox.Show(this, "Gebruik een datum als 10-11-2026.", Text);
            else if (current.Photo.Length > 0 && !current.Photo.Equals(original.Photo, StringComparison.OrdinalIgnoreCase) &&
                (!Path.IsPathFullyQualified(current.Photo) || !File.Exists(current.Photo)))
                MessageBox.Show(this, "Selecteer een bestaand afbeeldingsbestand.", Text);
            else DialogResult = DialogResult.OK;
        };
        footer.Controls.Add(ok);
        footer.Controls.Add(cancel);
        Controls.Add(grid);
        Controls.Add(footer);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private string Value(string key, string fallback) => fields.TryGetValue(key, out var box) ? box.Text.Trim() : fallback;

    private void Row(string label, Control field)
    {
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        grid.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
        grid.Controls.Add(field, 1, row++);
    }

    private TextBox TextRow(string label, string value)
    {
        var box = new TextBox { Text = value, Dock = DockStyle.Fill };
        fields.Add(label, box);
        Row(label, box);
        return box;
    }

    private void FileRow(string label, string value, string filter)
    {
        var field = TextRow(label, value);
        var browse = new Button { Text = "Bladeren…", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var picker = new OpenFileDialog { Filter = filter, CheckFileExists = true };
            if (Directory.Exists(photoFolder)) picker.InitialDirectory = photoFolder;
            if (File.Exists(field.Text)) picker.FileName = field.Text;
            if (picker.ShowDialog(this) == DialogResult.OK) field.Text = picker.FileName;
        };
        grid.Controls.Add(browse, 2, row - 1);
    }
}
