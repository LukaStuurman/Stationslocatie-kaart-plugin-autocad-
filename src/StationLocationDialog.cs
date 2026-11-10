using System.Drawing;
using Font = System.Drawing.Font;
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
        try { StationLocationSheets.DisableExistingSheetGrids(doc.Database); }
        catch (System.Exception ex) { doc.Editor.WriteMessage("\nRasterinstellingen: " + ex.Message); }
        StationSheet? selected = null;
        while (true)
        {
            try
            {
                var sheets = StationLocationSheets.List(doc.Database);
                var hasOverview = StationLocationSheets.HasOverview(doc.Database);
                var withPhoto = StationLocationSheets.SheetsWithPhoto(doc.Database, sheets);
                using var dialog = new StationLocationDialog(sheets, selected, hasOverview, withPhoto);
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
                            if (!StationLocationSheets.PickLocation(doc, out var point, out var rotation)) break;
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
                            editor.WriteMessage($"\nA3-layout ‘{sheet.Name}’ aangemaakt.");
                        }
                        break;
                    case StationLocationAction.Edit when selected != null:
                        using (var input = new StationFieldsDialog(StationLocationSheets.ReadFields(doc.Database, selected)
                            .FillSharedFrom(StationLocationSheets.ProjectFields(doc.Database)),
                            number: selected.Number, preferred: selected.Alternative == 0, alternativeNumber: selected.Alternative,
                            photoFolder: StationLocationSheets.PhotoFolder(doc.Database),
                            locate: () => StationLocationSheets.Position(doc.Database, selected)))
                            if (AcApp.ShowModalDialog(input) == DialogResult.OK)
                            {
                                var plan = StationLocationSheets.PlanEdit(sheets, selected, input.Number, input.Preferred, input.AlternativeNumber);
                                if (plan.Changes.Length > 1)
                                {
                                    var other = plan.Changes[1];
                                    if (MessageBox.Show($"'{plan.Target.Name}' wordt de voorkeurslocatie. " +
                                        $"De huidige voorkeurslocatie wordt '{other.After.Name}'. Doorgaan?", "Stationlocaties",
                                        MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                                        break;
                                }
                                StationLocationSheets.Edit(doc, plan, input.Fields);
                                selected = plan.Target;
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
                        using (var input = new StationFieldsDialog(StationLocationSheets.ProjectFields(doc.Database), project: true, showOverviewFields: hasOverview))
                            if (AcApp.ShowModalDialog(input) == DialogResult.OK)
                                StationLocationSheets.UpdateProject(doc.Database, input.Fields);
                        break;
                    case StationLocationAction.Delete when selected != null:
                        if (MessageBox.Show($"Locatie en blad ‘{selected.Name}’ verwijderen?", "Stationlocaties",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                        {
                            StationLocationSheets.Delete(doc, selected);
                            selected = null;
                            doc.Editor.Regen();
                        }
                        break;
                    case StationLocationAction.Plot:
                        var names = sheets.Select(sheet => sheet.Name).ToList();
                        if (hasOverview)
                            names.Insert(0, StationLocationSheets.OverviewName);
                        using (var input = new StationPlotDialog(doc.Database, names))
                            if (AcApp.ShowModalDialog(input) == DialogResult.OK && input.Options is { } options)
                            {
                                var count = StationPlot.Export(doc, options);
                                MessageBox.Show($"{count} PDF’s opgeslagen in {options.Folder}", "Stationlocaties");
                            }
                        break;
                    case StationLocationAction.Open when selected != null:
                        StationLocationSheets.OpenSheet(doc, selected.Name);
                        doc.Editor.Regen();
                        return;
                    case StationLocationAction.Overview:
                        StationLocationSheets.MakeOverview(doc);
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

internal enum StationLocationAction { Add, Edit, EditAll, Delete, Project, Open, Overview, Plot }

internal sealed class StationLocationDialog : Form
{
    private readonly ListView locations = new() { View = View.Details, Dock = DockStyle.Fill, FullRowSelect = true, MultiSelect = false, HideSelection = false };
    internal StationSheet? Selected => locations.SelectedItems.Count == 1 ? (StationSheet?)locations.SelectedItems[0].Tag : null;
    internal StationLocationAction Action { get; private set; }

    internal StationLocationDialog(List<StationSheet> sheets, StationSheet? previous, bool hasOverview, HashSet<string> withPhoto)
    {
        Text = "Techbase | Stationlocaties";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1020, 410);
        BackColor = Color.FromArgb(244, 247, 250);
        Font = new Font("Segoe UI", 9F);
        MinimumSize = new Size(915, 350);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(14) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 75));
        var incomplete = sheets.GroupBy(sheet => sheet.Number).Where(group =>
            !group.Any(sheet => sheet.Alternative == 0) || !group.Any(sheet => sheet.Alternative > 0))
            .Select(group => "Trafo " + group.Key);
        var pending = string.Join(", ", incomplete);
        var message = sheets.Count == 0
            ? "Begin met een voorkeurslocatie (VL). Voeg daarna één of meer alternatieve locaties (AL) toe."
            : string.IsNullOrEmpty(pending) ? "Alle trafo’s hebben een VL en minstens één AL."
                : "VL of AL ontbreekt bij " + pending + ".";
        if (sheets.Count > 0)
            message += $"  |  Foto's: {withPhoto.Count}/{sheets.Count} ({sheets.Count - withPhoto.Count} ontbreekt)";
        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill, Text = "TRAFOLOCATIEKAARTEN   /   A3\r\n" + message,
            TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true
        }, 0, 0);
        locations.Columns.Add("Blad", 250);
        locations.Columns.Add("Locatietype", 200);
        locations.Columns.Add("Locatiefoto", 140);
        locations.BeginUpdate();
        foreach (var sheet in sheets)
        {
            var hasPhoto = withPhoto.Contains(sheet.Name);
            var item = new ListViewItem(new[] { sheet.Name, sheet.Alternative == 0 ? "Voorkeurslocatie" : sheet.Alternative == 1 ? "Alternatieve locatie" : "Alternatieve locatie " + sheet.Alternative, hasPhoto ? "Aanwezig" : "Ontbreekt" })
                { Tag = sheet };
            if (!hasPhoto)
            {
                item.UseItemStyleForSubItems = false;
                item.SubItems[2].ForeColor = Color.Firebrick;
            }
            locations.Items.Add(item);
            item.Selected = sheet.Name == previous?.Name;
        }
        if (locations.SelectedItems.Count == 0 && locations.Items.Count > 0 && previous == null)
            locations.Items[0].Selected = true;
        locations.EndUpdate();
        layout.Controls.Add(locations, 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, Padding = new Padding(0, 8, 0, 0) };
        Button Add(string caption, StationLocationAction action, bool enabled = true)
        {
            var button = new Button { Text = caption, AutoSize = true, Enabled = enabled };
            button.Click += (_, _) => Choose(action);
            actions.Controls.Add(button);
            return button;
        }
        var add = Add("Locatie plaatsen", StationLocationAction.Add);
        add.BackColor = Color.FromArgb(25, 89, 126);
        add.ForeColor = Color.White;
        add.FlatStyle = FlatStyle.Flat;
        add.FlatAppearance.BorderSize = 0;
        var edit = Add("Geselecteerd blad", StationLocationAction.Edit, false);
        var open = Add("Open blad", StationLocationAction.Open, false);
        var delete = Add("Locatie verwijderen", StationLocationAction.Delete, false);
        Add("Alle bladen bewerken", StationLocationAction.EditAll, sheets.Count > 0);
        Add("Projectgegevens", StationLocationAction.Project, sheets.Count > 0);
        Add(hasOverview ? "Totaal vernieuwen" : "Totaal maken", StationLocationAction.Overview, sheets.Count > 0 || hasOverview);
        Add("Bladen plotten", StationLocationAction.Plot, sheets.Count > 0 || hasOverview);
        var close = new Button { Text = "Sluiten", AutoSize = true, DialogResult = DialogResult.Cancel };
        actions.Controls.Add(close);
        CancelButton = close;
        locations.SelectedIndexChanged += (_, _) => edit.Enabled = open.Enabled = delete.Enabled = Selected != null;
        Shown += (_, _) => edit.Enabled = open.Enabled = delete.Enabled = Selected != null;
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
    private readonly ClipboardPhotos clipboardPhotos = new();
    private readonly CheckBox lookupAddress = new() { Text = "Leeg adres automatisch opzoeken bij PDOK", Checked = true, AutoSize = true };
    private readonly TableLayoutPanel grid = new() { Dock = DockStyle.Fill, ColumnCount = 3, AutoScroll = true, Padding = new Padding(12) };
    private int row;
    internal int Number => (int)number.Value;
    private readonly TextBox alternative = new() { Width = 230, PlaceholderText = "Leeg voor AL, of vul 2 of hoger in" };
    internal bool Preferred => kind.SelectedIndex == 0;
    internal int AlternativeNumber { get; private set; } = 1;
    internal bool LookupAddress => lookupAddress.Checked;
    internal StationFields Fields => new(Value("Projectnaam", original.Project), Value("Deelgebied", original.Area),
        Value("Gemeente", original.Municipality), Value("Intern projectnummer", original.ProjectNumber),
        Value("Projectnummer opdrachtgever", original.ClientNumber), Value("Initialen tekenaar", original.Drafter),
        Value("Datum", original.Date), Value("Adres / locatie", original.Address), Value("Opmerking", original.Notes),
        Value("Tekeningnummer", original.DrawingNumber), Value("Locatiefoto", original.Photo));

    internal StationFieldsDialog(StationFields initial, bool adding = false, bool project = false,
        int number = 1, bool preferred = true, string photoFolder = "", HashSet<int>? existingPreferred = null,
        Func<Point3d>? locate = null, bool showOverviewFields = true, int alternativeNumber = 0)
    {
        original = initial;
        this.photoFolder = photoFolder;
        Text = adding ? "Locatie plaatsen" : project ? (showOverviewFields ? "Projectgegevens en blad Totaal" : "Projectgegevens") : "Bladgegevens en locatiefoto";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(850, project ? 645 : 725);
        MinimumSize = new Size(710, 470);
        BackColor = Color.FromArgb(244, 247, 250);
        Font = new Font("Segoe UI", 9F);
        grid.BackColor = Color.White;
        grid.Padding = new Padding(20, 14, 20, 12);
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
        if (!project)
        {
            Heading("Stationlocatie");
            this.number.Value = number;
            kind.Items.AddRange(new[] { "VL – Voorkeurslocatie", "AL – Alternatieve locatie" });
            kind.SelectedIndex = preferred ? 0 : 1;
            if (adding) this.number.ValueChanged += (_, _) => kind.SelectedIndex = existingPreferred?.Contains(Number) == true ? 1 : 0;
            Row("Trafo-nummer", this.number);
            Row("Locatietype", kind);
            if (!adding)
            {
                alternative.Text = alternativeNumber > 1 ? alternativeNumber.ToString(CultureInfo.InvariantCulture) : "";
                alternative.Enabled = !Preferred;
                kind.SelectedIndexChanged += (_, _) => alternative.Enabled = !Preferred;
                Row("AL-nummer (optioneel)", alternative);
            }
        }
        Heading("Projectgegevens voor alle bladen");
        TextRow("Projectnaam", initial.Project);
        TextRow("Deelgebied", initial.Area);
        TextRow("Gemeente", initial.Municipality);
        TextRow("Intern projectnummer", initial.ProjectNumber);
        TextRow("Projectnummer opdrachtgever", initial.ClientNumber);
        TextRow("Initialen tekenaar", initial.Drafter);
        if (!project || showOverviewFields)
        {
            Heading(project ? "Gegevens voor Totaal" : "Gegevens voor dit blad");
            var date = TextRow("Datum", initial.Date, hasAction: true);
            var today = new Button { Text = "Vandaag", AutoSize = true, Anchor = AnchorStyles.Left };
            today.Click += (_, _) => date.Text = StationFields.Today;
            grid.Controls.Add(today, 2, row - 1);
            var address = TextRow("Adres / locatie", initial.Address, hasAction: locate != null);
            var addressRow = row - 1;
            if (adding) Row("RD-coördinaten", lookupAddress, span: 2);
            if (locate != null)
            {
                var search = new Button { Text = "PDOK", AutoSize = true, Anchor = AnchorStyles.Left };
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
                grid.Controls.Add(search, 2, addressRow);
            }
            TextRow("Opmerking", initial.Notes);
            TextRow("Tekeningnummer", initial.DrawingNumber);
            if (!project) FileRow("Locatiefoto", initial.Photo, "Afbeeldingen (*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff)|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff");
        }
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 56, FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft, Padding = new Padding(15, 10, 15, 0) };
        var cancel = new Button { Text = "Annuleren", AutoSize = true, DialogResult = DialogResult.Cancel };
        var ok = new Button { Text = adding ? "Plaatsen op kaart" : "Opslaan", AutoSize = true };
        ok.BackColor = Color.FromArgb(25, 89, 126);
        ok.ForeColor = Color.White;
        ok.FlatStyle = FlatStyle.Flat;
        ok.FlatAppearance.BorderSize = 0;
        ok.Padding = new Padding(10, 2, 10, 2);
        ok.Click += (_, _) =>
        {
            var suffix = alternative.Text.Trim();
            var parsed = 1;
            if (!adding && !project && !Preferred && suffix.Length > 0 &&
                (!int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) || parsed < 2 || parsed > 999999))
            {
                MessageBox.Show(this, "Laat AL-nummer leeg voor AL, of vul een getal van 2 t/m 999999 in.", Text);
                alternative.Focus();
                return;
            }
            AlternativeNumber = parsed;
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

    private void Heading(string text)
    {
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 33));
        var label = new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(25, 89, 126), Font = new Font(Font, FontStyle.Bold) };
        grid.Controls.Add(label, 0, row++);
        grid.SetColumnSpan(label, 3);
    }

    private void Row(string label, Control field, int span = 1)
    {
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 33));
        grid.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
        grid.Controls.Add(field, 1, row++);
        grid.SetColumnSpan(field, span);
    }

    private TextBox TextRow(string label, string value, bool hasAction = false)
    {
        var box = new TextBox { Text = value, Anchor = AnchorStyles.Left | AnchorStyles.Right };
        fields.Add(label, box);
        Row(label, box, hasAction ? 1 : 2);
        return box;
    }

    private void FileRow(string label, string value, string filter)
    {
        Heading("Locatiefoto");
        var field = new TextBox { Text = value, Anchor = AnchorStyles.Left | AnchorStyles.Right,
            PlaceholderText = "Kies een bestand of plak een afbeelding" };
        fields.Add(label, field);
        var browse = new Button { Text = "Bladeren…", AutoSize = true, Anchor = AnchorStyles.Left };
        var paste = new Button { Text = "Plakken", AutoSize = true, Anchor = AnchorStyles.Left };
        var editor = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        editor.Controls.Add(field, 0, 0);
        editor.Controls.Add(browse, 1, 0);
        editor.Controls.Add(paste, 2, 0);
        Row("Afbeelding", editor, 2);

        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        var hint = new Label { Text = "De foto vult het vaste fotovak. Leeg laten verwijdert de foto.",
            Dock = DockStyle.Fill, ForeColor = Color.DimGray, TextAlign = ContentAlignment.MiddleLeft };
        grid.Controls.Add(hint, 1, row++);
        grid.SetColumnSpan(hint, 2);

        void PastePhoto()
        {
            if (clipboardPhotos.Paste(this) is not { } path) return;
            field.Text = path;
            hint.Text = "Foto geplakt. De afbeelding wordt bij het opslaan aan het blad gekoppeld.";
        }

        paste.Click += (_, _) => PastePhoto();
        field.KeyDown += (_, e) =>
        {
            if (!e.Control || e.KeyCode != Keys.V || !ClipboardPhotos.HasPhoto()) return;
            e.SuppressKeyPress = true;
            PastePhoto();
        };
        browse.Click += (_, _) =>
        {
            using var picker = new OpenFileDialog { Filter = filter, CheckFileExists = true };
            if (Directory.Exists(photoFolder)) picker.InitialDirectory = photoFolder;
            if (File.Exists(field.Text) && !ClipboardPhotos.IsStaged(field.Text)) picker.FileName = field.Text;
            if (picker.ShowDialog(this) == DialogResult.OK)
            {
                field.Text = picker.FileName;
                hint.Text = "De foto vult het vaste fotovak. Leeg laten verwijdert de foto.";
            }
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) clipboardPhotos.Dispose();
        base.Dispose(disposing);
    }
}
