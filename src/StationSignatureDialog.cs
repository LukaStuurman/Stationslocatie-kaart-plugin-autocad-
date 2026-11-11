using System.Drawing;
using System.IO;
using System.Text.Json;

namespace Stationslocaties;

internal sealed record SignatureLogo(string Name, string Key)
{
    public override string ToString() => Name;
}

internal sealed class StationSignatureDialog : Form
{
    internal const string DefaultKey = "@Techbase";
    private static readonly SignatureLogo DefaultLogo = new("Techbase (standaard)", DefaultKey);
    private static readonly SignatureLogo EmptyLogo = new("Geen logo", "");
    private static string Catalog => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Stationlocaties", "Paraaflogos.json");
    private readonly ComboBox choices = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 390 };
    private List<SignatureLogo> saved = Read();

    internal string SelectedKey => (choices.SelectedItem as SignatureLogo)?.Key ?? DefaultKey;

    internal StationSignatureDialog(string current)
    {
        Text = "Techbase | Paraaflogo's";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(515, 165);
        BackColor = StationTheme.Canvas;
        ForeColor = StationTheme.Ink;
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(14) };
        panel.Controls.Add(new Label { AutoSize = true, Text = "Kies het paraaflogo voor alle bladen in deze tekening." });
        panel.Controls.Add(choices);
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        var apply = new Button { Text = "Toepassen", AutoSize = true, DialogResult = DialogResult.OK };
        var add = new Button { Text = "Logo toevoegen", AutoSize = true };
        var remove = new Button { Text = "Uit lijst verwijderen", AutoSize = true };
        var cancel = new Button { Text = "Sluiten", AutoSize = true, DialogResult = DialogResult.Cancel };
        choices.SelectedIndexChanged += (_, _) => remove.Enabled = saved.Any(logo => logo.Key == SelectedKey);
        add.Click += (_, _) => Run(() =>
        {
            using var picker = new OpenFileDialog { Filter = "Afbeeldingen|*.png;*.jpg;*.jpeg;*.bmp;*.gif" };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            var key = ClientLibrary.ImportLogo(picker.FileName);
            if (!saved.Any(logo => logo.Key == key))
                Save([..saved, new(Path.GetFileNameWithoutExtension(picker.FileName), key)]);
            Reload(key);
        });
        remove.Click += (_, _) => Run(() =>
        {
            var key = SelectedKey;
            if (!saved.Any(logo => logo.Key == key) || MessageBox.Show(this, "Logo uit de lijst verwijderen?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            Save(saved.Where(logo => logo.Key != key).ToList());
            Reload(DefaultKey);
        });
        actions.Controls.AddRange([apply, add, remove, cancel]);
        panel.Controls.Add(actions);
        Controls.Add(panel);
        Reload(current);
        AcceptButton = apply;
        CancelButton = cancel;
        StationTheme.StyleButtons(this, apply, remove);
    }

    private void Reload(string key)
    {
        choices.Items.Clear();
        choices.Items.Add(DefaultLogo);
        choices.Items.Add(EmptyLogo);
        choices.Items.AddRange(saved.Cast<object>().ToArray());
        var selected = choices.Items.Cast<SignatureLogo>().FirstOrDefault(logo => logo.Key == key);
        if (selected == null)
        {
            selected = new("Huidig logo van deze tekening", key);
            choices.Items.Add(selected);
        }
        choices.SelectedItem = selected;
    }

    private void Run(Action action)
    {
        try { action(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Text); }
    }

    private static List<SignatureLogo> Read() => File.Exists(Catalog)
        ? JsonSerializer.Deserialize<List<SignatureLogo>>(File.ReadAllText(Catalog)) ?? [] : [];

    private void Save(List<SignatureLogo> logos)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Catalog)!);
        var pending = Catalog + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(pending, JsonSerializer.Serialize(logos));
            File.Move(pending, Catalog, overwrite: true);
            saved = logos;
        }
        finally { File.Delete(pending); }
    }
}
