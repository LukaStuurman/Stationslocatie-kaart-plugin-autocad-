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

        AddPage("Zijpaneel", """
            STATIONLOCATIES opent het vaste zijpaneel. Je kunt blijven tekenen terwijl het paneel openstaat. Sleep het naar links of rechts, laat het zweven of gebruik de punaise om het automatisch te laten inschuiven.

            Zoek op stationnummer, bladnaam, adres of deelgebied. Filter op VL, AL of locaties zonder foto. Klik om naar de locatie te zoomen; dubbelklik of druk op Enter om het blad te openen.

            Selecteer een trafoblok in de tekening om de bijbehorende gegevens in het paneel te zien. Een filter dat de geselecteerde locatie verbergt wordt gewist. Bij een andere actieve DWG wisselt de inhoud mee.

            Onder de lijst staan de foto en de opgeslagen AutoCAD-miniatuur van het blad. Als nog geen miniatuur beschikbaar is, open je het blad om de kaart te bekijken.

            Bewerken, Alternatief toevoegen en Kaartopmerking werken direct op de gekozen locatie. Onder Project en uitvoer staan de overige bestaande functies. Tijdens een lopende AutoCAD-opdracht zijn acties tijdelijk uitgeschakeld.
            """);

        AddPage("Automatisch", """
            Bij het plaatsen maakt de plug-in een A3-detailblad op schaal 1:250. Dat blad toont alleen het bijbehorende trafoblok. De rode cirkel en de naam verschijnen meteen in modelruimte.

            Verplaats of draai een geplaatst trafoblok met AutoCAD. Na het afronden van het commando werkt de plug-in de C1–C4-coördinaten op het detailblad bij als die zijn ingeschakeld. Bij verplaatsen volgen de rode cirkel, het label, de kaartopmerkingen en het kaartvenster het blok. De opmerkingen behouden hun afstand tot het station. Als je opmerkingen samen met het station selecteert, worden ze één keer verplaatst. Een apart verplaatste opmerking behoudt haar nieuwe positie. Draaien om het stationcentrum draait de opmerkingen niet mee.

            Een nieuw blad krijgt de datum van vandaag. Je kunt de datum overschrijven. Gedeelde projectgegevens worden overgenomen uit bestaande bladen en bij het opslaan van een wijziging doorgevoerd in alle onderhoeken.

            Als je het adres leeg laat en PDOK-adresopzoeking aanstaat, zoekt de plug-in bij plaatsing het dichtstbijzijnde BAG-adres. De kaart moet daarvoor in RD New (EPSG:28992) staan.

            Een gekozen of geplakte foto vult het vaste fotovak met een uitsnede. De plug-in onthoudt de laatst gebruikte fotomap in de DWG en toont in de lijst welke bladen een foto missen.
            """);

        AddPage("Locaties en bladen", """
            Locatie plaatsen — kies een trafonummer of naam, kies VL of AL en wijs het midden en de draairichting aan. Elke trafo hoort één voorkeurslocatie (VL) en minstens één alternatieve locatie (AL) te hebben.

            Blad openen — open het detailblad. Dubbelklikken op een locatie werkt ook.

            Bewerken — wijzig onder meer de naam, VL/AL, datum, adres, foto en de zichtbaarheid van de hoekcoördinaten.

            Kaartopmerking — zoomt meteen in op de rode cirkel van de geselecteerde trafo. Voer de tekst in en kies een punt binnen de cirkel. De rode tekst verschijnt op het eigen detailblad en blijft verborgen op Totaal.

            Verwijderen — verwijder de geselecteerde locatie en het bijbehorende detailblad.

            Alle bladen bewerken — wijzig datums, adressen, opmerkingen, tekeningnummers en foto’s in één tabel.
            """);

        AddPage("Project en uitvoer", """
            Projectgegevens — wijzig gedeelde gegevens zoals projectnaam, deelgebied, gemeente, projectnummers en tekenaarsinitialen.

            Trafoblokken beheren — voeg eigen blokken toe, wissel van blok en leg C1–C4 per eigen blok één keer vast.

            Opdrachtgevers beheren en Paraaflogo beheren — beheer de opgeslagen gegevens en logo’s voor de onderhoek.

            Kaartreferenties maken — maak externe DWG-referenties voor topografie, perceelgrenzen of een uitgepakte KLIC-levering. Met Alle drie maken bepaalt de KLIC automatisch het kaartgebied voor Topo en Percelen.

            Totaal maken of Totaal vernieuwen — maak of herbereken het overzicht en de kaartuitsnede wanneer je op deze knop klikt.

            Bladen plotten — selecteer de gewenste bladen, eventueel met Totaal, en gebruik één set plotinstellingen en één uitvoermap. Elk blad krijgt een eigen PDF. Lijndiktes staan standaard uit.
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
