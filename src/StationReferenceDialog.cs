using System.Drawing;

namespace Stationslocaties;

internal sealed class StationReferenceDialog : Form
{
    private readonly ComboBox kind = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
    internal MapKind Kind => (MapKind)kind.SelectedItem!;
    internal bool AttachExisting { get; private set; }

    internal StationReferenceDialog()
    {
        Text = "Techbase | Kaartreferenties";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(555, 180);
        BackColor = StationTheme.Canvas;
        ForeColor = StationTheme.Ink;
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(14) };
        panel.Controls.Add(new Label { AutoSize = true, Text = "Maak een kaartreferentie rechtstreeks uit PDOK." });
        panel.Controls.Add(new Label { AutoSize = true, Text = "Kies een DWG-bestandsnaam en wijs twee hoeken van het gebied aan." });
        kind.Items.AddRange([MapKind.Percelen, MapKind.Topo]);
        kind.SelectedIndex = 0;
        panel.Controls.Add(kind);
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        var create = new Button { Text = "Xref-DWG maken", AutoSize = true, DialogResult = DialogResult.OK };
        var attach = new Button { Text = "Bestaande DWG koppelen", AutoSize = true };
        attach.Click += (_, _) => { AttachExisting = true; DialogResult = DialogResult.OK; };
        var cancel = new Button { Text = "Annuleren", AutoSize = true, DialogResult = DialogResult.Cancel };
        actions.Controls.AddRange([create, attach, cancel]);
        panel.Controls.Add(actions);
        Controls.Add(panel);
        CancelButton = cancel;
        AcceptButton = create;
        StationTheme.StyleButtons(this, create);
    }
}
