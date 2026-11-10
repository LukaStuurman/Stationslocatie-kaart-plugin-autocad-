@echo off
setlocal
tasklist /FI "IMAGENAME eq acad.exe" /NH | findstr /I "acad.exe" >nul
if not errorlevel 1 (
    echo Sluit AutoCAD en probeer opnieuw.
    pause
    exit /b 1
)
if not exist "%~dp0Stationslocaties.bundle\PackageContents.xml" (
    echo Pak eerst het volledige installatiearchief uit.
    pause
    exit /b 1
)
robocopy "%~dp0Stationslocaties.bundle" "%APPDATA%\Autodesk\ApplicationPlugins\Stationslocaties.bundle" /E /R:1 /W:1 >nul
if errorlevel 8 (
    echo De installatie is mislukt.
    pause
    exit /b 1
)
echo Geinstalleerd. Start AutoCAD en typ STATIONLOCATIES.
pause
