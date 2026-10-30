$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'native\Cinema3D\Cinema3D.csproj'
$output = Join-Path $root 'native\Cinema3D\publish'
$target = Join-Path $root 'Cinema3D.exe'
$portable = Join-Path $root 'development\dotnet\dotnet.exe'
$installed = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = if (Test-Path $portable) { $portable } elseif ($installed) { $installed.Source } else { $null }
if (-not $dotnet) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $installer = Join-Path $env:TEMP 'cinema3d-dotnet-install.ps1'
    Write-Host 'Downloading the official .NET 10 SDK installer...'
    Invoke-WebRequest -UseBasicParsing -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
    New-Item -ItemType Directory -Path (Split-Path $portable -Parent) -Force | Out-Null
    & $installer -Channel 10.0 -InstallDir (Split-Path $portable -Parent)
    if (-not (Test-Path $portable)) { throw '.NET SDK installation failed.' }
    $dotnet = $portable
}
if (Test-Path (Join-Path $root 'development\nuget')) {
    $env:NUGET_PACKAGES = Join-Path $root 'development\nuget'
}
if (Get-Process Cinema3D -ErrorAction SilentlyContinue) {
    throw 'Закройте 3D Кинотеатр перед сборкой.'
}
Write-Host 'Собираю приложение из исходников...'
& $dotnet publish $project -c Release -o $output --nologo --ignore-failed-sources
if ($LASTEXITCODE -ne 0) { throw "dotnet publish: код $LASTEXITCODE" }
$built = Join-Path $output 'Cinema3D.exe'
if (-not (Test-Path $built)) { throw 'Новый Cinema3D.exe не создан.' }
$staged = "$target.new"
Copy-Item -LiteralPath $built -Destination $staged -Force
if (Test-Path $target) { Copy-Item -LiteralPath $target -Destination "$target.bak" -Force }
Move-Item -LiteralPath $staged -Destination $target -Force
Write-Host "Обновлено: $target"
