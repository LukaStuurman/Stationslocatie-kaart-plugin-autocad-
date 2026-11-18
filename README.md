# Stationlocaties voor AutoCAD

Zelfstandige AutoCAD-plug-in voor het plaatsen van trafolocaties en het maken van A3-detailbladen en een overzicht. De plug-in draait zonder Techbase Blocks.

## Installatie

Download `Stationslocaties-AutoCAD.zip` uit deze repo, pak het bestand uit, sluit AutoCAD en voer `Installeren.cmd` uit. Start AutoCAD 2025, 2026 of 2027 en voer `STATIONLOCATIES` uit. Het eigen commando kan naast Techbase Blocks worden gebruikt. Het opent een vast zijpaneel; de tekening blijft beschikbaar terwijl het paneel openstaat.

## Zijpaneel gebruiken

- Het paneel opent rechts naast de tekening. Sleep de titelbalk om het links te koppelen of los te plaatsen; met de punaise kun je het automatisch laten inschuiven. `STATIONLOCATIES` opent hetzelfde paneel opnieuw.
- Zoek op stationnummer, bladnaam, adres of deelgebied. Filter op **Alle locaties**, **Voorkeurslocaties (VL)**, **Alternatieven (AL)** of **Zonder foto**.
- Klik op een locatie om naar de rode cirkel in modelruimte te zoomen. Dubbelklik of druk op Enter om het detailblad te openen.
- Selecteer een trafoblok in de tekening om de bijbehorende locatie in het paneel te tonen. Een filter dat die locatie verbergt wordt dan gewist.
- Gebruik **Bewerken**, **Alternatief toevoegen** of **Kaartopmerking** voor de geselecteerde locatie. Het paneel blijft na de handeling beschikbaar. Tijdens een lopende AutoCAD-opdracht zijn acties tijdelijk uitgeschakeld.
- Onder de lijst staan een fotovoorbeeld en het laatst door AutoCAD opgeslagen bladvoorbeeld. Heeft een nieuw blad nog geen miniatuur, open dan het blad om de kaart te bekijken.
- Onder **Project en uitvoer** staan de bestaande functies voor alle bladen, projectgegevens, opdrachtgevers, paraaflogo’s, kaartreferenties, trafoblokken, Totaal en plotten.
- Bij wisselen tussen open DWG’s toont het paneel de locaties uit de actieve tekening.

## Locaties maken

1. Open een project-DWG met de gewenste achtergrond in modelruimte en werk in meters.
2. Kies **Locatie plaatsen**. De plug-in bevat een leeg A3-sjabloon, de onderhoek, het fotovak en het trafoblok.
3. Vul de projectgegevens in, kies het trafonummer en VL of AL en prik het midden en de draairichting. De plug-in maakt een detailblad op 1:250.
4. Laat **Adres / locatie** leeg om met PDOK het dichtstbijzijnde BAG-adres binnen 1 km in te vullen. De optie gebruikt het aangeklikte punt in RD New (EPSG:28992) en kan per plaatsing worden uitgezet. Een handmatig opgegeven adres krijgt voorrang. Bij een onbereikbare dienst kun je de locatie gewoon plaatsen.
5. Voeg de alternatieve locaties toe. Elk detailblad toont de eigen trafolocatie.
6. Kies **Totaal maken** zodra je het overzicht wilt. Daarna kun je het blad openen via **Totaal bijwerken**.

## Bestaande bladen bijwerken

- Open **Alle bladen bewerken** voor de tabel met Totaal en alle VL- en AL-bladen. Wijzig datums, adressen, opmerkingen, tekeningnummers en fotopaden in één sessie. Dubbelklik in een fotocel om een bestand te kiezen. Maak de fotocel leeg om de foto te verwijderen.
- Gebruik **Vandaag voor alle bladen** om alle datums in de tabel te vullen. De wijzigingen worden pas toegepast met **Opslaan**.
- Projectnaam, deelgebied, gemeente, beide projectnummers en tekenaarsinitialen worden uit bestaande onderhoeken ingevuld. Sla een wijziging op vanuit **Bewerken** of **Projectgegevens** om de gedeelde waarden in alle onderhoeken bij te werken.
- De laatst gebruikte fotomap wordt bij het opslaan in de project-DWG onthouden. De fotokiezer opent die map bij de volgende locatie.
- Gebruik **PDOK** naast **Adres / locatie** in **Bewerken** om het adres van een bestaande locatie opnieuw op te zoeken.
- Via **Bewerken** kun je één locatie bewerken. Na het verplaatsen van een trafoblok volgen de rode cirkel, het label, de kaartopmerkingen en het kaartvenster automatisch. De C1–C4-coördinaten worden eveneens bijgewerkt.
- Kaartopmerkingen blijven op dezelfde afstand van het station staan. Als je het station en opmerkingen samen verplaatst, worden de opmerkingen één keer verschoven. Een opmerking kun je ook afzonderlijk verplaatsen; die nieuwe positie blijft daarna het uitgangspunt. Draaien om het stationcentrum draait de opmerkingen niet mee.
- Kies **Kaartopmerking** om meteen in te zoomen op de rode cirkel van de geselecteerde trafo. De volledige cirkel blijft zichtbaar met ruimte eromheen. Voer de tekst in en wijs daarna het beginpunt binnen de cirkel aan.

Locatiefoto’s blijven rasterreferenties. Neem ze mee bij overdracht van de DWG. Het ingebouwde sjabloon bevat de A3-indeling en de vectoronderdelen. Projectfoto’s, logo’s en kaartachtergronden komen uit het eigen project.

## Trafonummer en VL/AL wijzigen

Kies een locatie en open **Bewerken**. Wijzig het trafonummer of locatietype en klik op **Opslaan**. Als je een bestaande alternatieve locatie tot voorkeur maakt terwijl de trafo al een VL heeft, vraagt de plug-in om bevestiging en zet de oude VL om naar een AL.

## Foto’s en PDF’s

Gebruik **Bladeren** of **Plakken** bij de locatiefoto. De foto vult het vaste vak met een centrale uitsnede. Een geplakte afbeelding wordt bij het opslaan als PNG naast de DWG bewaard in `Stationlocaties-fotos`. Bij een onopgeslagen DWG of een map zonder schrijfrechten gebruikt de plug-in een lokale fotomap.

Kies **Bladen plotten**, vink de gewenste layouts aan en stel de PDF-plotter, het papierformaat, de plotstijl, de afdrukstand, de schaal en de uitvoermap één keer in. Elk blad krijgt een afzonderlijke PDF met de layoutnaam. Bij een bestaande bestandsnaam krijgt de nieuwe PDF een volgnummer. Lijndiktes staan standaard uit. Vink **Lijndiktes plotten** aan om ook een plotstijltabel te kunnen kiezen.

## KLIC-kaartreferentie

Kies **Kaartreferenties maken → KLIC → Xref-DWG maken** en selecteer de map van één uitgepakte KLIC-levering. De plug-in leest de gebiedsinformatielevering en koppelt de gemaakte DWG als XREF op RD-coördinaten. Een voorbeeld-DWG is tijdens het importeren niet nodig.

Kies **Alle drie maken** om ook Topo en Percelen op te halen. Selecteer één KLIC-map en één uitvoermap. De uiterste RD-coördinaten van de geïmporteerde KLIC-geometrie bepalen het aanvraaggebied voor beide PDOK-kaarten. De drie DWG's worden apart opgeslagen en samen als XREF gekoppeld.

Grote aanvraaggebieden worden voor PDOK automatisch in kleinere rechthoeken verdeeld. De resultaten worden per kaart samengevoegd tot één DWG.

De tekenregels zijn afgeleid van een InfraCAD-referentie en gebruiken de daarin aangetroffen NLCS-lagen, lijnpatronen en blokdefinities. Voor de weergave van de complexe lijntypen en de NLCS-tekststijl moet AutoCAD `NLCS.SHX` en `NLCS-ISO.ttf` kunnen vinden. Het AutoCAD-opdrachtvenster meldt objecten waarvoor geen tekenregel beschikbaar is.

## Bouwen

Gebruik Windows en een .NET 10 SDK. Voer `Build.ps1` uit om de AutoCAD-bundle en het installatiearchief opnieuw op te bouwen. De API-referenties komen uit het NuGet-pakket AutoCAD.NET.
