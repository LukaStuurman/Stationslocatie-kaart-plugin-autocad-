using System.Drawing;

namespace Stationslocaties;

internal sealed class StationReferenceDialog : Form
{
    private readonly ComboBox kind = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
    internal MapKind Kind => (MapKind)kind.SelectedItem!;
    internal bool AttachExisting { get; private set; }
    internal bool CreateAll { get; private set; }

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
        panel.Controls.Add(new Label { AutoSize = true, Text = "Maak een kaartreferentie uit PDOK of een uitgepakte KLIC-levering." });
        var instructions = new Label { AutoSize = true, Text = "Kies een DWG-bestandsnaam en wijs twee hoeken van het gebied aan." };
        panel.Controls.Add(instructions);
        kind.Items.AddRange([MapKind.Percelen, MapKind.Topo, MapKind.KLIC]);
        kind.SelectedIndex = 0;
        kind.SelectedIndexChanged += (_, _) => instructions.Text = Kind == MapKind.KLIC
            ? "Kies de map van de KLIC-levering en een DWG-bestandsnaam."
            : "Kies een DWG-bestandsnaam en wijs twee hoeken van het gebied aan.";
        panel.Controls.Add(kind);
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        var create = new Button { Text = "Xref-DWG maken", AutoSize = true, DialogResult = DialogResult.OK };
        var attach = new Button { Text = "Bestaande DWG koppelen", AutoSize = true };
        attach.Click += (_, _) => { AttachExisting = true; DialogResult = DialogResult.OK; };
        var all = new Button { Text = "Alle drie maken", AutoSize = true };
        all.Click += (_, _) => { CreateAll = true; DialogResult = DialogResult.OK; };
        var cancel = new Button { Text = "Annuleren", AutoSize = true, DialogResult = DialogResult.Cancel };
        actions.Controls.AddRange([create, all, attach, cancel]);
        panel.Controls.Add(actions);
        Controls.Add(panel);
        CancelButton = cancel;
        AcceptButton = create;
        StationTheme.StyleButtons(this, create);
    }
}
