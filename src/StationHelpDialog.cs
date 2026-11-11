using System.Drawing;

namespace Stationslocaties;

internal sealed class StationHelpDialog : Form
{
    internal StationHelpDialog()
    {
        Text = "Techbase | Functies van Stationlocaties";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(720, 460);
        MinimumSize = new Size(500, 360);
        BackColor = StationTheme.Canvas;
        ForeColor = StationTheme.Ink;
        Font = new Font("Segoe UI", 10F);
        ShowInTaskbar = false;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 2 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        var tabs = new TabControl { Dock = DockStyle.Fill };

        AddPage("Automatisch", """
            Bij het plaatsen maakt de plug-in een A3-detailblad op schaal 1:250. Dat blad toont alleen het bijbehorende trafoblok. De rode cirkel en de naam verschijnen meteen in modelruimte.

            Verplaats of draai een geplaatst trafoblok met AutoCAD. Na het afronden van het commando werkt de plug-in de C1–C4-coördinaten op het detailblad bij als die zijn ingeschakeld. Bij verplaatsen volgt het kaartvenster het blok, zodat het in het midden blijft.

            Een nieuw blad krijgt de datum van vandaag. Je kunt de datum overschrijven. Gedeelde projectgegevens worden overgenomen uit bestaande bladen en bij het opslaan van een wijziging doorgevoerd in alle onderhoeken.

            Als je het adres leeg laat en PDOK-adresopzoeking aanstaat, zoekt de plug-in bij plaatsing het dichtstbijzijnde BAG-adres. De kaart moet daarvoor in RD New (EPSG:28992) staan.

            Een gekozen of geplakte foto vult het vaste fotovak met een uitsnede. De plug-in onthoudt de laatst gebruikte fotomap in de DWG en toont in de lijst welke bladen een foto missen.
            """);

        AddPage("Locaties en bladen", """
            Locatie plaatsen — kies een trafonummer of naam, kies VL of AL en wijs het midden en de draairichting aan. Elke trafo hoort één voorkeurslocatie (VL) en minstens één alternatieve locatie (AL) te hebben.

            Open blad — open het detailblad. Dubbelklikken op een locatie werkt ook.

            Blad bewerken — wijzig onder meer de naam, VL/AL, datum, adres, foto en de zichtbaarheid van de hoekcoördinaten.

            Kaartopmerking plaatsen — zet een rode tekst bij de trafo. Deze verschijnt op het eigen detailblad en blijft verborgen op Totaal.

            Locatie verwijderen — verwijder de geselecteerde locatie en het bijbehorende detailblad.

            Alle bladen bewerken — wijzig datums, adressen, opmerkingen, tekeningnummers en foto’s in één tabel.
            """);

        AddPage("Project en uitvoer", """
            Projectgegevens — wijzig gedeelde gegevens zoals projectnaam, deelgebied, gemeente, projectnummers en tekenaarsinitialen.

            Trafoblokken beheren — voeg eigen blokken toe, wissel van blok en leg C1–C4 per eigen blok één keer vast.

            Opdrachtgevers beheren en Paraaflogo beheren — beheer de opgeslagen gegevens en logo’s voor de onderhoek.

            Kaartreferenties maken — maak externe DWG-referenties voor topografie, perceelgrenzen of een uitgepakte KLIC-levering.

            Totaal maken of Totaal vernieuwen — maak of herbereken het overzicht en de kaartuitsnede wanneer je op deze knop klikt.

            Afdrukicoon — selecteer de gewenste bladen, eventueel met Totaal, en gebruik één set plotinstellingen en één uitvoermap. Elk blad krijgt een eigen PDF. Lijndiktes staan standaard uit.
            """);

        var close = new Button { Text = "Sluiten", DialogResult = DialogResult.Cancel,
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom, Size = new Size(100, 32) };
        layout.Controls.Add(tabs, 0, 0);
        layout.Controls.Add(close, 0, 1);
        StationTheme.StyleButtons(layout, close);
        CancelButton = close;
        Controls.Add(layout);

        void AddPage(string title, string content)
        {
            var page = new TabPage(title) { BackColor = Color.White, Padding = new Padding(12) };
            page.Controls.Add(new TextBox
            {
                AccessibleName = title, Text = content.ReplaceLineEndings(Environment.NewLine), Multiline = true,
                ReadOnly = true, WordWrap = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
                BackColor = Color.White, ForeColor = StationTheme.Ink, BorderStyle = BorderStyle.None
            });
            tabs.TabPages.Add(page);
        }
    }
}
