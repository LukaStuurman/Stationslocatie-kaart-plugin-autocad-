using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;

namespace Stationslocaties;

internal sealed record StationPaletteItem(StationSheet Sheet, StationFields Fields, bool HasPhoto);

/// <summary>The docked UI contains no database writes; the host executes actions in document context.</summary>
internal sealed class StationPaletteControl : UserControl
{
    private readonly ToolTip tips = new();
    private readonly System.Windows.Forms.Timer selectionTimer = new() { Interval = SystemInformation.DoubleClickTime };
    private readonly Label document = Caption("Geen tekening geopend");
    private readonly TextBox search = new()
    {
        Dock = DockStyle.Fill, PlaceholderText = "Station, adres of deelgebied zoeken…",
        AccessibleName = "Zoek op station, adres of deelgebied"
    };
    private readonly ComboBox kind = new()
    {
        Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList,
        AccessibleName = "Filter op voorkeurslocaties of alternatieven"
    };
    private readonly CheckBox withoutPhoto = new()
    {
        Text = "Zonder foto", AutoSize = true, Anchor = AnchorStyles.Left,
        AccessibleName = "Alleen locaties zonder foto tonen"
    };
    private readonly Label summary = Caption("");
    private readonly Label completeness = Caption("");
    private readonly ListView locations = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
        HideSelection = false, ShowItemToolTips = true, HeaderStyle = ColumnHeaderStyle.Nonclickable,
        BorderStyle = BorderStyle.FixedSingle, AccessibleName = "Stationlocaties"
    };
    private readonly Label selectionTitle = Caption("Selecteer een locatie");
    private readonly Label selectionAddress = Caption("");
    private readonly PictureBox photo = Picture();
    private readonly PictureBox sheetPreview = Picture();
    private readonly Label photoMessage = PreviewMessage("Geen locatie geselecteerd");
    private readonly Label sheetMessage = PreviewMessage("Open blad om de kaart te bekijken.");
    private readonly Dictionary<StationLocationAction, Button> buttons = new();
    private readonly TabPage locationsTab = new("Locaties") { Padding = new Padding(8), AutoScroll = true };
    private readonly TableLayoutPanel locationBody = new()
    {
        Dock = DockStyle.Top, ColumnCount = 1, RowCount = 9, Margin = Padding.Empty
    };
    private IReadOnlyList<StationPaletteItem> items = Array.Empty<StationPaletteItem>();
    private string? selectedName;
    private string? pendingSelectionName;
    private string? pendingDocumentName;
    private string documentName = "";
    private string photoSignature = "";
    private bool changing;
    private bool busy;
    private bool hasDocument;
    private bool hasOverview;

    internal event Action<StationLocationAction, StationSheet?>? ActionRequested;
    internal event Action<StationSheet>? LocationSelected;
    internal StationSheet? Selected => SelectedItem?.Sheet;
    private StationPaletteItem? SelectedItem => locations.SelectedItems.Count == 1
        ? locations.SelectedItems[0].Tag as StationPaletteItem : null;

    internal StationPaletteControl()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9F);
        BackColor = StationTheme.Canvas;
        ForeColor = StationTheme.Ink;
        Dock = DockStyle.Fill;
        Padding = new Padding(8);
        MinimumSize = new Size(280, 300);

        var shell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty
        };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var title = new Label
        {
            Text = "Stationlocaties", AutoSize = true, Dock = DockStyle.Fill,
            Font = new Font(Font.FontFamily, 14F, FontStyle.Bold), Margin = new Padding(0, 0, 0, 4)
        };
        shell.Controls.Add(title, 0, 0);
        document.Margin = new Padding(0, 0, 0, 8);
        shell.Controls.Add(document, 0, 1);
        var tabs = new TabControl { Dock = DockStyle.Fill, Margin = Padding.Empty };
        tabs.TabPages.Add(locationsTab);
        var projectTab = new TabPage("Project en uitvoer")
        {
            Padding = new Padding(8), AutoScroll = true, BackColor = StationTheme.Canvas
        };
        tabs.TabPages.Add(projectTab);
        shell.Controls.Add(tabs, 0, 2);
        Controls.Add(shell);

        locationsTab.BackColor = StationTheme.Canvas;
        locationBody.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        locationBody.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        locationBody.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        locationBody.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
        locationBody.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        locationBody.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        locationBody.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        locationBody.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        locationBody.RowStyles.Add(new RowStyle(SizeType.Absolute, 37));
        locationBody.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
        search.Margin = new Padding(0, 0, 0, 6);
        locationBody.Controls.Add(search, 0, 0);
        var filters = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty
        };
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        kind.Items.AddRange(new object[] { "Alle locaties", "Voorkeurslocaties (VL)", "Alternatieven (AL)" });
        kind.SelectedIndex = 0;
        kind.Margin = new Padding(0, 0, 6, 4);
        withoutPhoto.Margin = new Padding(4, 0, 0, 4);
        filters.Controls.Add(kind, 0, 0);
        filters.Controls.Add(withoutPhoto, 1, 0);
        locationBody.Controls.Add(filters, 0, 1);
        summary.Margin = new Padding(0, 2, 0, 5);
        locationBody.Controls.Add(summary, 0, 2);
        completeness.Margin = new Padding(0, 0, 0, 5);
        locationBody.Controls.Add(completeness, 0, 3);
        locations.Columns.Add("Blad", 130);
        locations.Columns.Add("Type", 48);
        locations.Columns.Add("Foto", 72);
        locations.Margin = Padding.Empty;
        locationBody.Controls.Add(locations, 0, 4);
        var selected = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 6, 0, 2)
        };
        selected.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        selected.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        selected.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        selectionTitle.Font = new Font(Font, FontStyle.Bold);
        selectionTitle.ForeColor = StationTheme.Ink;
        selected.Controls.Add(selectionTitle, 0, 0);
        selected.Controls.Add(selectionAddress, 0, 1);
        locationBody.Controls.Add(selected, 0, 5);
        var previews = new TabControl { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 6) };
        AddPreviewTab(previews, "Locatiefoto", photo, photoMessage);
        AddPreviewTab(previews, "Bladvoorbeeld", sheetPreview, sheetMessage, "Laatst opgeslagen bladvoorbeeld");
        locationBody.Controls.Add(previews, 0, 6);

        var add = ActionButton("Locatie plaatsen", StationLocationAction.Add);
        add.Margin = new Padding(0, 0, 0, 5);
        locationBody.Controls.Add(add, 0, 7);
        var quick = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Margin = Padding.Empty
        };
        quick.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        quick.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (var row = 0; row < 3; row++) quick.RowStyles.Add(new RowStyle(SizeType.Percent, 100F / 3));
        AddQuick("Zoom naar locatie", StationLocationAction.Zoom, 0, 0);
        AddQuick("Blad openen", StationLocationAction.Open, 1, 0);
        AddQuick("Bewerken", StationLocationAction.Edit, 0, 1);
        AddQuick("Alternatief toevoegen", StationLocationAction.AddAlternative, 1, 1);
        AddQuick("Kaartopmerking", StationLocationAction.Note, 0, 2);
        var delete = AddQuick("Verwijderen", StationLocationAction.Delete, 1, 2);
        locationBody.Controls.Add(quick, 0, 8);
        locationsTab.Controls.Add(locationBody);

        var project = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = Padding.Empty
        };
        project.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Group(string text)
        {
            var row = project.RowCount++;
            project.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
            var label = Caption(text);
            label.TextAlign = ContentAlignment.BottomLeft;
            label.Margin = new Padding(0, 5, 0, 4);
            project.Controls.Add(label, 0, row);
        }
        void ProjectAction(string text, StationLocationAction action)
        {
            var row = project.RowCount++;
            project.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
            var button = ActionButton(text, action);
            button.TextAlign = ContentAlignment.MiddleLeft;
            button.Margin = new Padding(0, 0, 0, 5);
            project.Controls.Add(button, 0, row);
        }
        Group("Bladen");
        ProjectAction("Alle bladen bewerken", StationLocationAction.EditAll);
        ProjectAction("Projectgegevens", StationLocationAction.Project);
        Group("Bibliotheken en kaarten");
        ProjectAction("Opdrachtgevers beheren", StationLocationAction.Client);
        ProjectAction("Paraaflogo beheren", StationLocationAction.Signature);
        ProjectAction("Kaartreferenties maken", StationLocationAction.References);
        ProjectAction("Trafoblokken beheren", StationLocationAction.Blocks);
        Group("Uitvoer");
        ProjectAction("Totaal maken", StationLocationAction.Overview);
        ProjectAction("Bladen plotten", StationLocationAction.Plot);
        Group("Hulp");
        var info = new Button
        {
            Text = "Uitleg over functies", Dock = DockStyle.Fill, Height = 34,
            TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 0, 0, 5)
        };
        project.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
        project.Controls.Add(info, 0, project.RowCount++);
        info.Click += (_, _) =>
        {
            using var help = new StationHelpDialog();
            Autodesk.AutoCAD.ApplicationServices.Application.ShowModalDialog(help);
        };
        projectTab.Controls.Add(project);
        StationTheme.StyleButtons(this, add, delete);

        tips.SetToolTip(search, "Zoek op stationnummer, bladnaam, adres of deelgebied.");
        tips.SetToolTip(locations, "Klik om naar de locatie te zoomen. Dubbelklik om het blad te openen.");
        tips.SetToolTip(buttons[StationLocationAction.AddAlternative], "Plaats een nieuwe AL voor het geselecteerde station.");
        search.TextChanged += (_, _) => { if (!changing) FilterItems(); };
        kind.SelectedIndexChanged += (_, _) => { if (!changing) FilterItems(); };
        withoutPhoto.CheckedChanged += (_, _) => { if (!changing) FilterItems(); };
        locations.SelectedIndexChanged += (_, _) =>
        {
            if (changing) return;
            var selectedItem = SelectedItem;
            selectedName = selectedItem?.Sheet.Name;
            ShowSelection();
            QueueSelectionZoom();
        };
        locations.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && locations.GetItemAt(e.X, e.Y) != null) QueueSelectionZoom();
        };
        // Wait out the double-click interval before entering a drawing command. Switching
        // focus/disabling the palette after the first click otherwise swallows the second.
        selectionTimer.Tick += (_, _) =>
        {
            var name = pendingSelectionName;
            var drawing = pendingDocumentName;
            CancelSelectionZoom();
            if (!busy && hasDocument && Selected is { } selectedItem &&
                SameName(selectedItem.Name, name) && string.Equals(drawing, documentName, StringComparison.OrdinalIgnoreCase))
                LocationSelected?.Invoke(selectedItem);
        };
        locations.DoubleClick += (_, _) =>
        {
            CancelSelectionZoom();
            Request(StationLocationAction.Open);
        };
        locations.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            CancelSelectionZoom();
            Request(StationLocationAction.Open);
            e.Handled = e.SuppressKeyPress = true;
        };
        locations.SizeChanged += (_, _) => SizeColumns();
        locationsTab.SizeChanged += (_, _) => SizeBody();
        DpiChangedAfterParent += (_, _) => { SizeColumns(); SizeBody(); };
        SizeBody();
        UpdateButtons();

        Button AddQuick(string text, StationLocationAction action, int column, int row)
        {
            var button = ActionButton(text, action);
            button.Margin = new Padding(column == 0 ? 0 : 3, 0, column == 0 ? 3 : 0, 5);
            quick.Controls.Add(button, column, row);
            return button;
        }
    }

    internal void SetItems(IReadOnlyList<StationPaletteItem> value, string? selected, bool overview, string drawingName)
    {
        CancelSelectionZoom();
        var documentChanged = !string.Equals(documentName, drawingName, StringComparison.OrdinalIgnoreCase);
        items = value;
        documentName = drawingName;
        hasDocument = true;
        hasOverview = overview;
        document.Text = Path.GetFileName(drawingName);
        if (string.IsNullOrWhiteSpace(document.Text)) document.Text = "Naamloze tekening";
        tips.SetToolTip(document, drawingName);
        selectedName = selected ?? (documentChanged ? null : selectedName);
        if (documentChanged)
        {
            photoSignature = "";
            SetPreview(null);
        }
        FilterItems(selectFirst: selectedName == null);
    }

    /// <summary>Model selections are revealed, without sending a selection event back to the host.</summary>
    internal void SetSelection(string? name)
    {
        CancelSelectionZoom();
        selectedName = name;
        var selected = items.FirstOrDefault(item => SameName(item.Sheet.Name, name));
        if (selected != null && !Matches(selected))
        {
            changing = true;
            try
            {
                search.Clear();
                kind.SelectedIndex = 0;
                withoutPhoto.Checked = false;
            }
            finally { changing = false; }
        }
        FilterItems(revealSelection: true);
    }

    internal void SetNoDocument()
    {
        CancelSelectionZoom();
        hasDocument = hasOverview = false;
        items = Array.Empty<StationPaletteItem>();
        selectedName = null;
        documentName = "";
        document.Text = "Geen tekening geopend";
        tips.SetToolTip(document, "Open een DWG om stationlocaties te beheren.");
        FilterItems();
    }

    internal void SetBusy(bool value)
    {
        if (value) CancelSelectionZoom();
        busy = value;
        search.Enabled = kind.Enabled = withoutPhoto.Enabled = locations.Enabled = hasDocument && !busy;
        UpdateButtons();
    }

    /// <summary>Takes ownership of the supplied image. Pass null when no real layout preview is available.</summary>
    internal void SetPreview(Image? image)
    {
        ReplaceImage(sheetPreview, image);
        sheetMessage.Visible = image == null;
    }

    private void FilterItems(bool selectFirst = false, bool revealSelection = false)
    {
        CancelSelectionZoom();
        var topName = locations.TopItem?.Tag is StationPaletteItem top ? top.Sheet.Name : null;
        changing = true;
        locations.BeginUpdate();
        try
        {
            locations.Items.Clear();
            foreach (var item in items.Where(Matches))
            {
                var type = item.Sheet.Alternative == 0 ? "VL" : "AL" + (item.Sheet.Alternative > 1 ? " " + item.Sheet.Alternative : "");
                var row = new ListViewItem(new[] { item.Sheet.Name, type, item.HasPhoto ? "Aanwezig" : "Ontbreekt" })
                {
                    Tag = item, ToolTipText = string.Join(Environment.NewLine,
                        new[] { item.Sheet.Name, item.Fields.Address, item.Fields.Area }.Where(text => !string.IsNullOrWhiteSpace(text)))
                };
                if (locations.Items.Count % 2 != 0) row.BackColor = StationTheme.Canvas;
                if (!item.HasPhoto)
                {
                    row.UseItemStyleForSubItems = false;
                    row.SubItems[2].ForeColor = Color.Firebrick;
                }
                locations.Items.Add(row);
                row.Selected = SameName(item.Sheet.Name, selectedName);
            }
            if (selectFirst && locations.SelectedItems.Count == 0 && locations.Items.Count > 0)
            {
                locations.Items[0].Selected = true;
                selectedName = Selected?.Name;
            }
            var topRow = locations.Items.Cast<ListViewItem>().FirstOrDefault(row =>
                row.Tag is StationPaletteItem item && SameName(item.Sheet.Name, topName));
            if (topRow != null) locations.TopItem = topRow;
            if (revealSelection && locations.SelectedItems.Count == 1) locations.SelectedItems[0].EnsureVisible();
        }
        finally
        {
            locations.EndUpdate();
            changing = false;
        }
        var missing = items.Count(item => !item.HasPhoto);
        summary.Text = !hasDocument ? "Open een tekening om te beginnen."
            : items.Count == 0 ? "Nog geen locaties. Kies Locatie plaatsen."
            : $"{locations.Items.Count} van {items.Count} locaties · {missing} zonder foto";
        if (hasDocument && items.Count > 0 && locations.Items.Count == 0) summary.Text = "Geen locaties voor dit filter.";
        tips.SetToolTip(summary, summary.Text);
        var incomplete = items.GroupBy(item => item.Sheet.Trafo, StringComparer.OrdinalIgnoreCase)
            .Where(group => !group.Any(item => item.Sheet.Alternative == 0) || !group.Any(item => item.Sheet.Alternative > 0))
            .Select(group => StationLocationSheets.TrafoLabel(group.Key)).ToArray();
        completeness.Text = !hasDocument ? ""
            : items.Count == 0 ? "Voeg een VL en minimaal één AL toe."
            : incomplete.Length == 0 ? "Elke trafo heeft een VL en een AL."
            : incomplete.Length == 1 ? $"VL of AL ontbreekt bij {incomplete[0]}."
            : $"VL of AL ontbreekt bij {incomplete.Length} trafo’s.";
        completeness.ForeColor = incomplete.Length == 0 ? StationTheme.Muted : StationTheme.Accent;
        tips.SetToolTip(completeness, incomplete.Length == 0 ? completeness.Text
            : "VL of AL ontbreekt bij:" + Environment.NewLine + string.Join(Environment.NewLine, incomplete));
        ShowSelection();
        SetBusy(busy);
    }

    private bool Matches(StationPaletteItem item)
    {
        if (kind.SelectedIndex == 1 && item.Sheet.Alternative != 0 ||
            kind.SelectedIndex == 2 && item.Sheet.Alternative == 0 ||
            withoutPhoto.Checked && item.HasPhoto) return false;
        var query = search.Text.Trim();
        if (query.Length == 0) return true;
        return new[] { item.Sheet.Trafo, item.Sheet.Name, item.Fields.Address, item.Fields.Area }
            .Any(value => value.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }

    private void ShowSelection()
    {
        var item = SelectedItem;
        var title = item?.Sheet.Name ?? "Selecteer een locatie";
        if (!string.Equals(selectionTitle.Text, title, StringComparison.Ordinal)) SetPreview(null);
        selectionTitle.Text = title;
        selectionAddress.Text = item == null ? "" : string.IsNullOrWhiteSpace(item.Fields.Address)
            ? "Adres niet ingevuld" : item.Fields.Address;
        tips.SetToolTip(selectionTitle, title);
        tips.SetToolTip(selectionAddress, item == null ? "" : string.Join(Environment.NewLine,
            new[] { item.Fields.Address, item.Fields.Area }.Where(text => !string.IsNullOrWhiteSpace(text))));
        ShowPhoto(item);
        UpdateButtons();
    }

    private void ShowPhoto(StationPaletteItem? item)
    {
        string path = "";
        string signature = "";
        try
        {
            path = item?.Fields.Photo ?? "";
            if (path.Length > 0 && !Path.IsPathFullyQualified(path) && Path.IsPathFullyQualified(documentName))
                path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(documentName)!, path));
            var info = path.Length > 0 ? new FileInfo(path) : null;
            signature = $"{item?.Sheet.Name}|{path}|{item?.HasPhoto}|{info?.Exists}|{(info?.Exists == true ? info.LastWriteTimeUtc.Ticks : 0)}";
            if (signature == photoSignature) return;
            photoSignature = signature;
            ReplaceImage(photo, null);
            if (item?.HasPhoto == true && info?.Exists == true)
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var original = Image.FromStream(stream);
                var factor = Math.Min(1d, Math.Min(720d / original.Width, 360d / original.Height));
                var thumbnail = new Bitmap(Math.Max(1, (int)(original.Width * factor)), Math.Max(1, (int)(original.Height * factor)));
                using (var graphics = Graphics.FromImage(thumbnail))
                {
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.DrawImage(original, 0, 0, thumbnail.Width, thumbnail.Height);
                }
                ReplaceImage(photo, thumbnail);
            }
        }
        catch (System.Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException or NotSupportedException)
        {
            photoSignature = signature;
            ReplaceImage(photo, null);
        }
        photoMessage.Text = item == null ? "Geen locatie geselecteerd"
            : item.HasPhoto ? "Foto niet beschikbaar. Kies Bewerken om deze opnieuw te koppelen." : "Nog geen locatiefoto";
        photoMessage.Visible = photo.Image == null;
        tips.SetToolTip(photo, path);
    }

    private void UpdateButtons()
    {
        foreach (var (action, button) in buttons)
        {
            var available = action switch
            {
                StationLocationAction.Zoom or StationLocationAction.Open or StationLocationAction.Edit or
                    StationLocationAction.AddAlternative or StationLocationAction.Note or StationLocationAction.Delete => Selected != null,
                StationLocationAction.EditAll or StationLocationAction.Project => items.Count > 0,
                StationLocationAction.Overview or StationLocationAction.Plot => items.Count > 0 || hasOverview,
                _ => true
            };
            button.Enabled = hasDocument && !busy && available;
        }
        if (buttons.TryGetValue(StationLocationAction.Overview, out var overview))
            overview.Text = hasOverview ? "Totaal vernieuwen" : "Totaal maken";
    }

    private Button ActionButton(string text, StationLocationAction action)
    {
        var button = new Button
        {
            Text = text, Dock = DockStyle.Fill, Margin = Padding.Empty,
            AccessibleName = text, UseMnemonic = false
        };
        buttons.Add(action, button);
        button.Click += (_, _) => Request(action);
        return button;
    }

    private void Request(StationLocationAction action)
    {
        CancelSelectionZoom();
        if (buttons.TryGetValue(action, out var button) && button.Enabled)
            ActionRequested?.Invoke(action, Selected);
    }

    private void QueueSelectionZoom()
    {
        CancelSelectionZoom();
        if (changing || busy || !hasDocument || Selected == null) return;
        pendingSelectionName = Selected.Name;
        pendingDocumentName = documentName;
        selectionTimer.Interval = SystemInformation.DoubleClickTime;
        selectionTimer.Start();
    }

    private void CancelSelectionZoom()
    {
        selectionTimer.Stop();
        pendingSelectionName = pendingDocumentName = null;
    }

    private void SizeColumns()
    {
        if (locations.Columns.Count != 3) return;
        locations.Columns[1].Width = LogicalToDeviceUnits(48);
        locations.Columns[2].Width = LogicalToDeviceUnits(73);
        locations.Columns[0].Width = Math.Max(LogicalToDeviceUnits(75),
            locations.ClientSize.Width - locations.Columns[1].Width - locations.Columns[2].Width - SystemInformation.VerticalScrollBarWidth - 5);
    }

    private void SizeBody() => locationBody.Height = Math.Max(LogicalToDeviceUnits(660),
        locationsTab.ClientSize.Height - locationsTab.Padding.Vertical);

    private static bool SameName(string? first, string? second) =>
        !string.IsNullOrEmpty(first) && string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private static Label Caption(string text) => new()
    {
        Text = text, Dock = DockStyle.Fill, AutoEllipsis = true, Margin = Padding.Empty,
        ForeColor = StationTheme.Muted, TextAlign = ContentAlignment.MiddleLeft, Height = 21
    };

    private static PictureBox Picture() => new()
    {
        Dock = DockStyle.Fill, BackColor = Color.White, SizeMode = PictureBoxSizeMode.Zoom,
        AccessibleName = "Voorbeeldweergave"
    };

    private static Label PreviewMessage(string text) => new()
    {
        Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
        BackColor = Color.White, ForeColor = StationTheme.Muted, Padding = new Padding(12),
        AutoEllipsis = true
    };

    private static void AddPreviewTab(TabControl tabs, string title, PictureBox image, Label message, string? caption = null)
    {
        var tab = new TabPage(title) { Padding = new Padding(2), BackColor = Color.White };
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = caption == null ? 1 : 2, Margin = Padding.Empty
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var frame = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        frame.Controls.Add(image);
        frame.Controls.Add(message);
        message.BringToFront();
        body.Controls.Add(frame, 0, 0);
        if (caption != null)
        {
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 21));
            var label = Caption(caption);
            label.TextAlign = ContentAlignment.MiddleCenter;
            body.Controls.Add(label, 0, 1);
        }
        tab.Controls.Add(body);
        tabs.TabPages.Add(tab);
    }

    private static void ReplaceImage(PictureBox target, Image? value)
    {
        if (ReferenceEquals(target.Image, value)) return;
        var previous = target.Image;
        target.Image = value;
        previous?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CancelSelectionZoom();
            selectionTimer.Dispose();
            tips.Dispose();
            ReplaceImage(photo, null);
            ReplaceImage(sheetPreview, null);
        }
        base.Dispose(disposing);
    }
}
