using System.Drawing;
using Font = System.Drawing.Font;
using PlotType = Autodesk.AutoCAD.DatabaseServices.PlotType;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.PlottingServices;

namespace Stationslocaties;

internal sealed record StationPlotOptions(string[] Layouts, string Device, string Media, string Style,
    bool Landscape, bool Fit, bool Lineweights, string Folder);

internal sealed class StationPlotDialog : Form
{
    private sealed record MediaChoice(string Canonical, string Display)
    {
        public override string ToString() => Display;
    }

    private readonly CheckedListBox sheets = new() { Dock = DockStyle.Fill, CheckOnClick = true };
    private readonly ComboBox device = Choice();
    private readonly ComboBox media = Choice();
    private readonly ComboBox style = Choice();
    private readonly ComboBox rotation = Choice();
    private readonly ComboBox scale = Choice();
    private readonly CheckBox lineweights = new() { Text = "Lijndiktes plotten", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly TextBox folder = new() { Dock = DockStyle.Fill };
    private readonly Button plot = new() { Text = "PDF’s maken", AutoSize = true };
    internal StationPlotOptions? Options { get; private set; }

    internal StationPlotDialog(Database db, IEnumerable<string> layouts)
    {
        Text = "Stationlocaties | Bladen plotten";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9);
        ClientSize = new Size(800, 430);
        MinimumSize = new Size(720, 400);
        BackColor = Color.FromArgb(244, 247, 250);
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 2 };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.Controls.Add(new Label { Text = "Te plotten bladen", Dock = DockStyle.Fill }, 0, 0);
        content.Controls.Add(new Label { Text = "Instellingen voor alle geselecteerde bladen", Dock = DockStyle.Fill }, 1, 0);
        foreach (var name in layouts) sheets.Items.Add(name, true);
        content.Controls.Add(sheets, 0, 1);

        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(14, 0, 0, 0) };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Row(string label, Control input)
        {
            var n = fields.RowCount++;
            fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 37));
            fields.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, n);
            fields.Controls.Add(input, 1, n);
        }

        var validator = PlotSettingsValidator.Current;
        foreach (var name in validator.GetPlotDeviceList().OfType<string>())
            if (name.Contains("PDF", StringComparison.OrdinalIgnoreCase)) device.Items.Add(name);
        device.SelectedIndexChanged += (_, _) => LoadMedia();
        if (device.Items.Count > 0)
        {
            var standard = device.Items.IndexOf("DWG To PDF.pc3");
            device.SelectedIndex = standard >= 0 ? standard : 0;
        }
        style.Items.Add("Geen plotstijltabel");
        foreach (var name in validator.GetPlotStyleSheetList().OfType<string>())
            if (name.EndsWith(db.PlotStyleMode ? ".ctb" : ".stb", StringComparison.OrdinalIgnoreCase)) style.Items.Add(name);
        style.SelectedIndex = 0;
        style.Enabled = false;
        lineweights.CheckedChanged += (_, _) => style.Enabled = lineweights.Checked;
        rotation.Items.AddRange(new[] { "Liggend", "Staand" });
        rotation.SelectedIndex = 0;
        scale.Items.AddRange(new[] { "1:1 op papier", "Passend op papier" });
        scale.SelectedIndex = 0;
        Row("PDF-plotter", device);
        Row("Papierformaat", media);
        Row("Plotstijl", style);
        Row("Afdrukstand", rotation);
        Row("Plotschaal", scale);
        Row("Lijndiktes", lineweights);

        folder.Text = !string.IsNullOrWhiteSpace(db.Filename) && Path.IsPathFullyQualified(db.Filename)
            ? Path.GetDirectoryName(db.Filename) ?? "" : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var browse = new Button { Text = "Bladeren…", AutoSize = true, Dock = DockStyle.Fill };
        browse.Click += (_, _) =>
        {
            using var picker = new FolderBrowserDialog { SelectedPath = folder.Text, ShowNewFolderButton = true };
            if (picker.ShowDialog(this) == DialogResult.OK) folder.Text = picker.SelectedPath;
        };
        var folderRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        folderRow.Controls.Add(folder, 0, 0);
        folderRow.Controls.Add(browse, 1, 0);
        Row("Uitvoermap", folderRow);
        content.Controls.Add(fields, 1, 1);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 54, Padding = new Padding(12, 7, 12, 0),
            FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft };
        var cancel = new Button { Text = "Annuleren", AutoSize = true, DialogResult = DialogResult.Cancel };
        plot.BackColor = Color.FromArgb(25, 89, 126);
        plot.ForeColor = Color.White;
        plot.FlatStyle = FlatStyle.Flat;
        plot.Click += (_, _) => AcceptOptions();
        actions.Controls.AddRange(new Control[] { plot, cancel });
        Controls.Add(content);
        Controls.Add(actions);
        AcceptButton = plot;
        CancelButton = cancel;
    }

    private static ComboBox Choice() => new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };

    private void LoadMedia()
    {
        media.Items.Clear();
        if (device.SelectedItem is not string name) return;
        using var settings = new PlotSettings(false);
        var validator = PlotSettingsValidator.Current;
        validator.SetPlotConfigurationName(settings, name, null);
        validator.RefreshLists(settings);
        var selected = -1;
        foreach (var canonical in validator.GetCanonicalMediaNameList(settings).OfType<string>())
        {
            if (canonical.Contains("A3", StringComparison.OrdinalIgnoreCase) &&
                (selected < 0 || canonical.Equals("ISO_full_bleed_A3_(297.00_x_420.00_MM)", StringComparison.OrdinalIgnoreCase)))
                selected = media.Items.Count;
            media.Items.Add(new MediaChoice(canonical, validator.GetLocaleMediaName(settings, canonical)));
        }
        if (media.Items.Count > 0) media.SelectedIndex = Math.Max(0, selected);
    }

    private void AcceptOptions()
    {
        var chosen = sheets.CheckedItems.Cast<string>().ToArray();
        var output = folder.Text.Trim();
        if (chosen.Length == 0 || device.SelectedItem is not string plotter ||
            media.SelectedItem is not MediaChoice paper || !Path.IsPathFullyQualified(output))
        {
            MessageBox.Show(this, "Selecteer minstens één blad, een PDF-plotter, een papierformaat en een uitvoermap.", Text);
            return;
        }
        Options = new(chosen, plotter, paper.Canonical,
            lineweights.Checked && style.SelectedIndex > 0 ? (string)style.SelectedItem! : "",
            rotation.SelectedIndex == 0, scale.SelectedIndex == 1, lineweights.Checked, output);
        DialogResult = DialogResult.OK;
    }
}

internal static class StationPlot
{
    internal static int Export(Document doc, StationPlotOptions options)
    {
        if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
            throw new InvalidOperationException("Wacht tot de lopende plot gereed is.");
        Directory.CreateDirectory(options.Folder);
        var previous = LayoutManager.Current.CurrentLayout;
        var written = 0;
        using var settings = CreateSettings(doc.Database, options);
        try
        {
            foreach (var layoutName in options.Layouts)
            {
                StationLocationSheets.OpenSheet(doc, layoutName);
                doc.Editor.Regen();
                var pdf = NextFile(options.Folder, layoutName);
                var temporary = Path.Combine(options.Folder, ".stations-" + Guid.NewGuid().ToString("N") + ".pdf");
                try
                {
                    doc.Editor.WriteMessage($"\nPlotten {written + 1}/{options.Layouts.Length}  {layoutName}");
                    PlotSheet(doc, layoutName, settings, temporary);
                    if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0)
                        throw new IOException("AutoCAD heeft geen PDF aangemaakt.");
                    File.Move(temporary, pdf);
                    written++;
                }
                finally
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Plotten onderbroken. {written} van {options.Layouts.Length} PDF’s opgeslagen. {ex.Message}", ex);
        }
        finally
        {
            LayoutManager.Current.CurrentLayout = previous;
        }
        return written;
    }

    private static string NextFile(string folder, string layout)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = string.Concat(layout.Select(c => invalid.Contains(c) ? '_' : c)).TrimEnd(' ', '.');
        if (name.Length == 0) name = "Stationblad";
        var path = Path.Combine(folder, name + ".pdf");
        for (var i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{name} ({i}).pdf");
        return path;
    }

    private static PlotSettings CreateSettings(Database db, StationPlotOptions options)
    {
        var settings = new PlotSettings(false);
        try
        {
            using var tr = db.TransactionManager.StartTransaction();
            var id = LayoutManager.Current.GetLayoutId(options.Layouts[0]);
            settings.CopyFrom((Layout)tr.GetObject(id, OpenMode.ForRead));
            ConfigureSettings(settings, options);
            tr.Commit();
            return settings;
        }
        catch
        {
            settings.Dispose();
            throw;
        }
    }

    private static void ConfigureSettings(PlotSettings settings, StationPlotOptions options)
    {
        var validator = PlotSettingsValidator.Current;
        validator.SetPlotConfigurationName(settings, options.Device, options.Media);
        validator.SetPlotPaperUnits(settings, PlotPaperUnit.Millimeters);
        var paper = settings.PlotPaperSize;
        validator.SetPlotRotation(settings, options.Landscape == (paper.X >= paper.Y) ? PlotRotation.Degrees000 : PlotRotation.Degrees090);
        validator.SetPlotType(settings, options.Fit ? PlotType.Extents : PlotType.Layout);
        validator.SetUseStandardScale(settings, true);
        validator.SetStdScaleType(settings, options.Fit ? StdScaleType.ScaleToFit : StdScaleType.StdScale1To1);
        if (options.Fit) validator.SetPlotCentered(settings, true);
        if (options.Lineweights && options.Style.Length > 0) validator.SetCurrentStyleSheet(settings, options.Style);
        settings.PlotPlotStyles = options.Lineweights && options.Style.Length > 0;
        settings.PrintLineweights = options.Lineweights;

    }

    private static void PlotSheet(Document doc, string layoutName, PlotSettings settings, string path)
    {
        var id = LayoutManager.Current.GetLayoutId(layoutName);
        using var info = new PlotInfo { Layout = id, OverrideSettings = settings };
        using var validate = new PlotInfoValidator { MediaMatchingPolicy = MatchingPolicy.MatchEnabled };
        validate.Validate(info);
        using var progress = new PlotProgressDialog(false, 1, true);
        using var engine = PlotFactory.CreatePublishEngine();
        progress.OnBeginPlot();
        progress.IsVisible = false;
        try
        {
            engine.BeginPlot(progress, null);
            try
            {
                engine.BeginDocument(info, doc.Name, null, 1, true, path);
                try
                {
                    using var page = new PlotPageInfo();
                    progress.OnBeginSheet();
                    engine.BeginPage(page, info, true, null);
                    try
                    {
                        engine.BeginGenerateGraphics(null);
                        engine.EndGenerateGraphics(null);
                    }
                    finally
                    {
                        engine.EndPage(null);
                        progress.OnEndSheet();
                    }
                }
                finally { engine.EndDocument(null); }
            }
            finally { engine.EndPlot(null); }
        }
        finally { progress.OnEndPlot(); }
    }
}

