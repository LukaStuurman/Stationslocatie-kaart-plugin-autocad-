# Stationlocaties voor AutoCAD

Zelfstandige AutoCAD-plug-in voor het plaatsen van trafolocaties en het maken van A3-detailbladen en een overzicht. De plug-in draait zonder Techbase Blocks.

## Installatie

Download `Stationslocaties-AutoCAD.zip` uit deze repo, pak het bestand uit, sluit AutoCAD en voer `Installeren.cmd` uit. Start AutoCAD 2025, 2026 of 2027 en voer `STATIONLOCATIES` uit. Het eigen commando kan naast Techbase Blocks worden gebruikt.

## Locaties maken

1. Open een project-DWG met de gewenste achtergrond in modelruimte en werk in meters.
2. Kies **Locatie plaatsen**. De plug-in bevat een leeg A3-sjabloon, de onderhoek, het fotovak en het trafoblok.
3. Vul de projectgegevens in, kies het trafonummer en VL of AL en prik het midden en de draairichting. De plug-in maakt een detailblad op 1:250 en werkt Totaal bij.
4. Laat **Adres / locatie** leeg om met PDOK het dichtstbijzijnde BAG-adres binnen 1 km in te vullen. De optie gebruikt het aangeklikte punt in RD New (EPSG:28992) en kan per plaatsing worden uitgezet. Een handmatig opgegeven adres krijgt voorrang. Bij een onbereikbare dienst kun je de locatie gewoon plaatsen.
5. Voeg de alternatieve locaties toe. Elk detailblad toont de eigen trafolocatie. Totaal toont alle locaties.

## Bestaande bladen bijwerken

- Open **Alle bladen bewerken** voor de tabel met Totaal en alle VL- en AL-bladen. Wijzig datums, adressen, opmerkingen, tekeningnummers en fotopaden in één sessie. Dubbelklik in een fotocel om een bestand te kiezen. Maak de fotocel leeg om de foto te verwijderen.
- Gebruik **Vandaag voor alle bladen** om alle datums in de tabel te vullen. De wijzigingen worden pas toegepast met **Opslaan**.
- Projectnaam, deelgebied, gemeente, beide projectnummers en tekenaarsinitialen worden uit bestaande onderhoeken ingevuld. Sla een wijziging op vanuit **Geselecteerd blad** of **Projectgegevens** om de gedeelde waarden in alle onderhoeken bij te werken.
- De laatst gebruikte fotomap wordt bij het opslaan in de project-DWG onthouden. De fotokiezer opent die map bij de volgende locatie.
- Gebruik **PDOK** naast **Adres / locatie** in **Geselecteerd blad** om het adres van een bestaande locatie opnieuw op te zoeken.
- Via **Geselecteerd blad** kun je één locatie bewerken of de kaart opnieuw centreren na het verplaatsen van een blok.

Locatiefoto’s blijven rasterreferenties. Neem ze mee bij overdracht van de DWG. Het ingebouwde sjabloon bevat de A3-indeling en de vectoronderdelen. Projectfoto’s, logo’s en kaartachtergronden komen uit het eigen project.

## Trafonummer en VL/AL wijzigen

Kies een locatie en open **Geselecteerd blad**. Wijzig het trafonummer of locatietype en klik op **Opslaan**. Als je een bestaande alternatieve locatie tot voorkeur maakt terwijl de trafo al een VL heeft, vraagt de plug-in om bevestiging en zet de oude VL om naar een AL.

## Foto’s en PDF’s

Gebruik **Bladeren** of **Plakken** bij de locatiefoto. De foto vult het vaste vak met een centrale uitsnede. Een geplakte afbeelding wordt bij het opslaan als PNG naast de DWG bewaard in `Stationlocaties-fotos`. Bij een onopgeslagen DWG of een map zonder schrijfrechten gebruikt de plug-in een lokale fotomap.

Kies **Bladen plotten**, vink de gewenste layouts aan en stel de PDF-plotter, het papierformaat, de plotstijl, de afdrukstand, de schaal en de uitvoermap één keer in. Elk blad krijgt een afzonderlijke PDF met de layoutnaam. Bij een bestaande bestandsnaam krijgt de nieuwe PDF een volgnummer.

## Bouwen

Gebruik Windows en een .NET 10 SDK. Voer `Build.ps1` uit om de AutoCAD-bundle en het installatiearchief opnieuw op te bouwen. De API-referenties komen uit het NuGet-pakket AutoCAD.NET.
