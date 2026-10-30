param([string]$InstallDir = (Join-Path $env:USERPROFILE '3D-Cinema'), [switch]$SkipShortcuts)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$source = Split-Path -Parent $PSScriptRoot
$target = $InstallDir
if (-not (Test-Path (Join-Path $source 'Cinema3D.exe'))) {
    throw 'Cinema3D.exe is missing. Download the release ZIP, not the source-code ZIP.'
}
if ([IO.Path]::GetFullPath($source).TrimEnd('\') -eq [IO.Path]::GetFullPath($target).TrimEnd('\')) {
    throw 'The app is already in the installation folder.'
}
Write-Host "Copying application to $target ..."
New-Item -ItemType Directory -Path $target -Force | Out-Null
& robocopy $source $target /E /R:2 /W:2 /NFL /NDL /NP /XD (Join-Path $source 'native\Cinema3D\bin') (Join-Path $source 'native\Cinema3D\obj') (Join-Path $source 'native\Cinema3D\publish') /XF Cinema3D.log Cinema3D.exe.bak *.pdb
if ($LASTEXITCODE -gt 7) { throw "Copy failed: robocopy exit code $LASTEXITCODE" }

$ffDir = Join-Path $target 'tools\ffmpeg'
if (-not (Test-Path (Join-Path $ffDir 'ffmpeg.exe')) -or -not (Test-Path (Join-Path $ffDir 'ffprobe.exe'))) {
    Write-Host 'Downloading FFmpeg from GyanD official release...'
    $temp = Join-Path $env:TEMP ('cinema3d-ffmpeg-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temp -Force | Out-Null
    $archive = Join-Path $temp 'ffmpeg.zip'
    Invoke-WebRequest -UseBasicParsing -Uri 'https://github.com/GyanD/codexffmpeg/releases/download/8.1.2/ffmpeg-8.1.2-essentials_build.zip' -OutFile $archive
    Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $temp 'unpacked')
    New-Item -ItemType Directory -Path $ffDir -Force | Out-Null
    foreach ($name in @('ffmpeg.exe', 'ffprobe.exe')) {
        $found = Get-ChildItem (Join-Path $temp 'unpacked') -Recurse -File -Filter $name | Select-Object -First 1
        if (-not $found) { throw "FFmpeg archive does not contain $name" }
        Copy-Item -LiteralPath $found.FullName -Destination (Join-Path $ffDir $name) -Force
    }
}
$ytDir = Join-Path $target 'tools\yt-dlp'
if (-not (Test-Path (Join-Path $ytDir 'yt-dlp.exe'))) {
    Write-Host 'Downloading yt-dlp from its official release...'
    New-Item -ItemType Directory -Path $ytDir -Force | Out-Null
    Invoke-WebRequest -UseBasicParsing -Uri 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe' -OutFile (Join-Path $ytDir 'yt-dlp.exe')
}
if (-not $SkipShortcuts) { & (Join-Path $target 'tools\create-shortcut.ps1') }
Write-Host "Installed: $target"
