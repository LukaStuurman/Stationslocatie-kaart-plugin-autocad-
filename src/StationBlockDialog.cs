using System.Drawing;

namespace Stationslocaties;

internal enum BlockLibraryAction { Register, Use, Replace, Remove, ToggleCorners }

internal sealed class StationBlockDialog : Form
{
    private readonly ComboBox choices = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
    internal StationBlockChoice? Choice => choices.SelectedItem as StationBlockChoice;
    internal BlockLibraryAction Action { get; private set; }

    internal StationBlockDialog(StationBlockCatalog catalog, StationSheet? sheet)
    {
        Text = "Techbase | Trafoblokken";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(530, 330);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        BackColor = StationTheme.Canvas;
        ForeColor = StationTheme.Ink;
        Padding = new Padding(16);
        choices.Items.Add("Standaard trafoblok");
        choices.Items.AddRange(catalog.Blocks.Cast<object>().ToArray());
        choices.SelectedIndex = catalog.Default is { } current ? Array.IndexOf(catalog.Blocks, current) + 1 : 0;

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        Button Add(string text, BlockLibraryAction action, bool enabled = true)
        {
            var button = new Button { Text = text, AutoSize = true, Enabled = enabled };
            button.Click += (_, _) => { Action = action; DialogResult = DialogResult.OK; };
            buttons.Controls.Add(button);
            return button;
        }
        var register = Add("Blok uit de tekening opslaan", BlockLibraryAction.Register);
        Add("Als standaard voor nieuwe locaties gebruiken", BlockLibraryAction.Use);
        Add("Geselecteerd trafoblok vervangen", BlockLibraryAction.Replace, sheet != null);
        var remove = Add("Blok uit bibliotheek verwijderen", BlockLibraryAction.Remove, Choice != null);
        choices.SelectedIndexChanged += (_, _) => remove.Enabled = Choice != null;
        Add(sheet?.ShowCorners == false ? "Coördinatentabel op dit blad tonen" : "Coördinatentabel op dit blad verbergen",
            BlockLibraryAction.ToggleCorners, sheet != null);
        var close = new Button { Text = "Sluiten", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(close);
        CancelButton = close;

        var header = new Label { Text = sheet == null ? "Kies een blok voor nieuwe locaties." : $"Geselecteerd locatieblad: {sheet.Name}",
            Dock = DockStyle.Top, Height = 26 };
        choices.Height = 28;
        var spacer = new Panel { Height = 12, Dock = DockStyle.Top };
        Controls.Add(buttons);
        Controls.Add(spacer);
        Controls.Add(choices);
        Controls.Add(header);
        StationTheme.StyleButtons(this, register, remove);
    }
}
