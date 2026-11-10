using System.Drawing;
using System.Globalization;
using System.IO;

namespace Stationslocaties;

internal sealed record SheetRow(string Name, bool Overview, StationFields Fields);

internal sealed class SheetTableDialog : Form
{
    private readonly DataGridView grid = new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        EditMode = DataGridViewEditMode.EditOnEnter
    };
    private readonly ClipboardPhotos clipboardPhotos = new();
    internal List<SheetRow> Changes { get; } = new();
    internal string LastPhoto { get; private set; } = "";

    internal SheetTableDialog(IReadOnlyList<SheetRow> sheets, string photoFolder)
    {
        Text = "Alle bladen bewerken";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1050, 450);
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.FromArgb(244, 247, 250);
        grid.BackgroundColor = Color.White;
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(244, 247, 250);
        MinimumSize = new Size(750, 300);
        var headers = new[] { "Blad", "Datum", "Adres / locatie", "Opmerking", "Tekeningnummer", "Locatiefoto" };
        for (var i = 0; i < headers.Length; i++) grid.Columns.Add("Field" + i, headers[i]);
        grid.Columns[0].ReadOnly = true;
        grid.Columns[1].FillWeight = 65;
        grid.Columns[5].FillWeight = 125;
        foreach (var sheet in sheets)
        {
            var fields = sheet.Fields;
            var row = grid.Rows[grid.Rows.Add(sheet.Name, fields.Date, fields.Address, fields.Notes, fields.DrawingNumber, fields.Photo)];
            row.Tag = sheet;
            row.Cells[5].ReadOnly = sheet.Overview;
        }
        grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 5) return;
            var cell = grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
            if (cell.ReadOnly) return;
            grid.EndEdit();
            using var picker = new OpenFileDialog
            {
                Filter = "Afbeeldingen|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff",
                CheckFileExists = true
            };
            var folder = string.IsNullOrEmpty(LastPhoto) ? photoFolder : Path.GetDirectoryName(LastPhoto);
            if (Directory.Exists(folder)) picker.InitialDirectory = folder!;
            if (File.Exists(Convert.ToString(cell.Value))) picker.FileName = Convert.ToString(cell.Value);
            if (picker.ShowDialog(this) == DialogResult.OK) cell.Value = LastPhoto = picker.FileName;
        };
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8) };
        var today = new Button { Text = "Vandaag voor alle bladen", AutoSize = true };
        today.Click += (_, _) =>
        {
            grid.EndEdit();
            foreach (DataGridViewRow row in grid.Rows) row.Cells[1].Value = StationFields.Today;
        };
        var paste = new Button { Text = "Foto plakken", AutoSize = true };
        paste.Click += (_, _) =>
        {
            if (grid.CurrentRow is not { Tag: SheetRow { Overview: false } } row)
            {
                MessageBox.Show(this, "Selecteer eerst een locatieblad.", Text);
                return;
            }
            if (grid.EndEdit() && clipboardPhotos.Paste(this) is { } path)
                row.Cells[5].Value = path;
        };
        var save = new Button { Text = "Opslaan", AutoSize = true };
        save.Click += (_, _) => Save();
        var cancel = new Button { Text = "Annuleren", AutoSize = true, DialogResult = DialogResult.Cancel };
        footer.Controls.AddRange(new Control[] { today, paste, save, cancel });
        Controls.Add(grid);
        Controls.Add(footer);
        CancelButton = cancel;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) clipboardPhotos.Dispose();
        base.Dispose(disposing);
    }

    private void Save()
    {
        if (!grid.EndEdit()) return;
        Changes.Clear();
        foreach (DataGridViewRow row in grid.Rows)
        {
            var original = (SheetRow)row.Tag!;
            string Value(int column) => Convert.ToString(row.Cells[column].Value)?.Trim() ?? "";
            var fields = original.Fields with
            {
                Date = Value(1), Address = Value(2), Notes = Value(3), DrawingNumber = Value(4), Photo = Value(5)
            };
            if (fields == original.Fields) continue;
            string? error = null;
            if (fields.Date != original.Fields.Date && !DateTime.TryParseExact(fields.Date,
                new[] { "d-M-yyyy", "dd-MM-yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                error = "Gebruik een datum als 10-11-2026.";
            else if (fields.Photo.Length > 0 && !fields.Photo.Equals(original.Fields.Photo, StringComparison.OrdinalIgnoreCase)
                && (!Path.IsPathFullyQualified(fields.Photo) || !File.Exists(fields.Photo)))
                error = "Kies een bestaand afbeeldingsbestand.";
            if (error != null)
            {
                MessageBox.Show(this, original.Name + ". " + error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Changes.Add(original with { Fields = fields });
        }
        DialogResult = DialogResult.OK;
    }
}
