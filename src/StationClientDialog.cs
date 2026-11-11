using System.Drawing;

namespace Stationslocaties;

internal sealed class StationClientDialog : Form
{
    private readonly ComboBox choices = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox[] fields = Enumerable.Range(0, 5).Select(_ => new TextBox { Dock = DockStyle.Fill }).ToArray();
    private readonly Label logoLabel = new() { AutoEllipsis = true, Dock = DockStyle.Fill };
    private ClientProfile[] profiles = ClientLibrary.Read();
    private Guid? editing;
    private string logo = "";
    internal ClientProfile? Applied { get; private set; }

    internal StationClientDialog(ClientProfile? current)
    {
        Text = "Techbase | Opdrachtgevers";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(650, 405);
        MinimumSize = new Size(635, 430);
        BackColor = StationTheme.Canvas;
        ForeColor = StationTheme.Ink;
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(16) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Row(string label, Control control)
        {
            var row = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            grid.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            grid.Controls.Add(control, 1, row);
        }
        Button Make(string text, Action click)
        {
            var button = new Button { Text = text, AutoSize = true };
            button.Click += (_, _) =>
            {
                try { click(); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, Text); }
            };
            return button;
        }
        FlowLayoutPanel Bar(params Control[] buttons)
        {
            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            panel.Controls.AddRange(buttons);
            return panel;
        }

        Row("Profiel", choices);
        string[] labels = ["Bedrijf", "Adres", "Telefoon", "Website", "E-mail"];
        for (var index = 0; index < labels.Length; index++) Row(labels[index], fields[index]);
        Row("Logo", logoLabel);
        Row("", Bar(Make("Logo kiezen", () =>
        {
            using var picker = new OpenFileDialog { Filter = "Afbeeldingen|*.png;*.jpg;*.jpeg;*.bmp;*.gif" };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            logo = ClientLibrary.ImportLogo(picker.FileName);
            RefreshLogo();
        }), Make("Logo wissen", () => { logo = ""; RefreshLogo(); })));
        var delete = Make("Verwijderen", () =>
        {
            if (editing is not { } id || MessageBox.Show(this, "Dit profiel uit de bibliotheek verwijderen?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            var remaining = profiles.Where(profile => profile.Id != id).ToArray();
            ClientLibrary.Save(remaining);
            profiles = remaining;
            Reload(null);
        });
        var save = Make("Profiel opslaan", () => SaveProfile());
        Row("Bibliotheek", Bar(save, delete));
        var apply = Make("Tonen op alle bladen", () =>
        {
            Applied = SaveProfile();
            DialogResult = DialogResult.OK;
        });
        Row("Deze DWG", Bar(apply, Make("Verbergen", () => { Applied = null; DialogResult = DialogResult.OK; }),
            new Button { Text = "Sluiten", AutoSize = true, DialogResult = DialogResult.Cancel }));
        choices.SelectedIndexChanged += (_, _) =>
        {
            LoadProfile(choices.SelectedItem as ClientProfile);
            delete.Enabled = editing.HasValue;
        };
        Reload(current?.Id);
        delete.Enabled = editing.HasValue;
        Controls.Add(grid);
        StationTheme.StyleButtons(this, apply, delete);
    }

    private void Reload(Guid? selected)
    {
        choices.Items.Clear();
        choices.Items.Add("Nieuw profiel");
        choices.Items.AddRange(profiles.Cast<object>().ToArray());
        var index = Array.FindIndex(profiles, profile => profile.Id == selected);
        choices.SelectedIndex = index + 1;
    }

    private void LoadProfile(ClientProfile? profile)
    {
        editing = profile?.Id;
        string?[] values = [profile?.Name, profile?.Address, profile?.Phone, profile?.Website, profile?.Email];
        for (var index = 0; index < fields.Length; index++) fields[index].Text = values[index] ?? "";
        logo = profile?.Logo ?? "";
        RefreshLogo();
    }

    private void RefreshLogo() => logoLabel.Text = logo.Length == 0 ? "Geen logo gekozen" : "Logo opgeslagen";

    private ClientProfile SaveProfile()
    {
        var values = fields.Select(field => field.Text.Trim()).ToArray();
        if (values[0].Length == 0) throw new InvalidOperationException("Vul een bedrijfsnaam in.");
        var updated = new ClientProfile(editing ?? Guid.NewGuid(), values[0], values[1], values[2], values[3], values[4], logo);
        var next = profiles.Where(profile => profile.Id != updated.Id).Append(updated)
            .OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        ClientLibrary.Save(next);
        profiles = next;
        Reload(updated.Id);
        return updated;
    }
}
