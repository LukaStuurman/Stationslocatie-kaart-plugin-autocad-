using System.Drawing;

namespace Stationslocaties;

internal sealed class StationReferenceDialog : Form
{
    private readonly ComboBox type = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 290 };
    internal StationReferenceType SelectedType => (StationReferenceType)type.SelectedItem!;
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
        type.Items.AddRange(StationReferenceType.Choices.Cast<object>().ToArray());
        type.SelectedIndex = 0;
        panel.Controls.Add(new Label { AutoSize = true, Text = "Maak een aparte DWG voor BGT-topo, percelen of KLIC." });
        panel.Controls.Add(type);
        panel.Controls.Add(new Label { AutoSize = true,
            Text = "Kies bij KLIC in de InfraCAD Map-wizard de map met meldingen." });
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
