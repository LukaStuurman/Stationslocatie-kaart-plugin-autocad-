# Stationlocaties voor AutoCAD

Zelfstandige AutoCAD-plug-in voor het plaatsen van trafolocaties en het maken van A3-detailbladen en een overzicht. De plug-in draait zonder Techbase Blocks.

## Installatie

Download `Stationslocaties-AutoCAD.zip` uit deze repo, pak het bestand uit, sluit AutoCAD en voer `Installeren.cmd` uit. Start AutoCAD 2025, 2026 of 2027 en voer `TBSTATIONLOCATIES` uit. Gebruik één installatie van dit commando. Als je Techbase Blocks 1.20.0 met Stationlocaties hebt geïnstalleerd, verwijder die versie of gebruik een installatie zonder dat commando.

## Locaties maken

1. Open een project-DWG met de gewenste achtergrond in modelruimte en werk in meters.
2. Kies **Locatie plaatsen**. Selecteer bij het eerste gebruik een voorbeeld-DWG met de layouts `Trafo 1 VL` en `Totaal`, het blok `Onderhoek Spitters` en het trafoblok `TRAFO` uit het oorspronkelijke sjabloon. Deze repo levert de project-DWG en kaartachtergronden niet mee.
3. Vul de projectgegevens in, kies het trafonummer en VL of AL en prik het midden en de draairichting. De plug-in maakt een detailblad op 1:250 en werkt Totaal bij.
4. Voeg de alternatieve locaties toe. Elk detailblad toont de eigen trafolocatie. Totaal toont alle locaties.

## Bestaande bladen bijwerken

- Open **Alle bladen bewerken** voor de tabel met Totaal en alle VL- en AL-bladen. Wijzig datums, adressen, opmerkingen, tekeningnummers en fotopaden in één sessie. Dubbelklik in een fotocel om een bestand te kiezen. Maak de fotocel leeg om de foto te verwijderen.
- Gebruik **Vandaag voor alle bladen** om alle datums in de tabel te vullen. De wijzigingen worden pas toegepast met **Opslaan**.
- Gebruik **Projectgegevens** voor gedeelde velden zoals de projectnaam, gemeente en tekenaarsinitialen. Deze worden in alle gegenereerde onderhoeken bijgewerkt.
- Via **Geselecteerd blad** kun je één locatie bewerken of de kaart opnieuw centreren na het verplaatsen van een blok.

Foto’s en logo’s blijven rasterreferenties. De projectgegevens blijven in de DWG. Gebruik bij overdracht ook de bijbehorende referentiebestanden.

## Bouwen

Gebruik Windows en een .NET 10 SDK. Voer `Build.ps1` uit om de AutoCAD-bundle en het installatiearchief opnieuw op te bouwen. De API-referenties komen uit het NuGet-pakket AutoCAD.NET.
