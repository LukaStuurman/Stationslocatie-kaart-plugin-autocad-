param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\Stationslocaties.csproj'
$stage = Join-Path $PSScriptRoot 'dist'
$bundle = Join-Path $stage 'Stationslocaties.bundle'
foreach ($series in @('R25', 'R26')) {
    $target = Join-Path $bundle "Contents\$series"
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    & $Dotnet build $project -c Release "-p:ApiSeries=$series" --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build $series mislukt." }
    $framework = if ($series -eq 'R25') { 'net8.0-windows' } else { 'net10.0-windows' }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "src\bin\Release\$framework\Stationslocaties.dll") -Destination $target -Force
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PackageContents.xml') -Destination $bundle -Force
Compress-Archive -LiteralPath $bundle,(Join-Path $PSScriptRoot 'Installeren.cmd'),(Join-Path $PSScriptRoot 'README.md') -DestinationPath (Join-Path $stage 'Stationslocaties-AutoCAD.zip') -Force
