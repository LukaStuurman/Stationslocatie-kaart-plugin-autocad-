using System.Drawing;

namespace Stationslocaties;

internal static class StationTheme
{
    internal static readonly Color Canvas = Color.FromArgb(248, 247, 245);
    internal static readonly Color Ink = Color.FromArgb(40, 35, 31);
    internal static readonly Color Muted = Color.FromArgb(102, 91, 82);
    internal static readonly Color Border = Color.FromArgb(220, 207, 195);
    internal static readonly Color Accent = Color.FromArgb(171, 65, 12);
    internal static readonly Color Tint = Color.FromArgb(255, 241, 226);

    internal static void StyleButtons(Control parent, Button primary, Button? destructive = null)
    {
        foreach (Control child in parent.Controls)
        {
            if (child is not Button button)
            {
                StyleButtons(child, primary, destructive);
                continue;
            }

            var prominent = button == primary;
            button.UseVisualStyleBackColor = false;
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = prominent ? Accent : button == destructive ? Color.White : Tint;
            button.ForeColor = prominent ? Color.White : button == destructive ? Color.Firebrick : Ink;
            button.FlatAppearance.BorderColor = prominent ? Accent : Border;
            button.FlatAppearance.MouseOverBackColor = prominent ? Color.FromArgb(148, 53, 8) : Color.FromArgb(251, 220, 190);
            button.FlatAppearance.MouseDownBackColor = prominent ? Color.FromArgb(125, 43, 6) : Color.FromArgb(247, 202, 159);
            button.Padding = new Padding(8, 0, 8, 0);
        }
    }
}
