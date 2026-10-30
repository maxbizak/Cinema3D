# Запуск 3D Кинотеатра: ищет проектор, собирает список фильмов, открывает плеер
# в отдельном профиле Chrome/Edge на весь экран проектора.
# Безопасно: видеорежимы, драйверы и настройки Windows не меняет.
param(
  [ValidateSet('dlp', 'anaglyph', '2d', 'sbs')] [string]$Mode = 'dlp',
  [switch]$Test,          # сразу открыть тест очков
  [string]$Open = '',     # имя файла из библиотеки, который открыть сразу
  [int]$Monitor = 0,      # 0 = авто (монитор ~120 Гц), иначе номер экрана 1..N
  [switch]$Log,           # писать диагностику в app\profile\chrome_debug.log
  [ValidateSet('', 'on', 'off')] [string]$PairLock = ''   # точные пары L/R: on / off (лёгкий режим)
)
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$App = $PSScriptRoot

Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class C3D {
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] public struct DEVMODE {
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
    public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public int dmFields;
    public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
    public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
    public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
    public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight; }
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplaySettings(string n, int i, ref DEVMODE d);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  public static DEVMODE Current(string n) { var d = new DEVMODE(); d.dmSize = (short)Marshal.SizeOf(d); EnumDisplaySettings(n, -1, ref d); return d; }
}
"@
[C3D]::SetProcessDPIAware() | Out-Null

# ---------- экраны ----------
$screens = @([Windows.Forms.Screen]::AllScreens | ForEach-Object {
  $dm = [C3D]::Current($_.DeviceName)
  [pscustomobject]@{ Name = $_.DeviceName; Primary = $_.Primary; X = $dm.dmPositionX; Y = $dm.dmPositionY
    W = $dm.dmPelsWidth; H = $dm.dmPelsHeight; Hz = $dm.dmDisplayFrequency }
})
$i = 0; foreach ($s in $screens) { $i++; Write-Host ("  Экран {0}: {1} {2}x{3} @ {4} Гц{5}" -f $i, $s.Name, $s.W, $s.H, $s.Hz, $(if ($s.Primary) { ' (основной)' } else { '' })) }

if ($Monitor -ge 1 -and $Monitor -le $screens.Count) { $target = $screens[$Monitor - 1] }
else {
  $target = $screens | Where-Object { $_.Hz -ge 100 -and $_.Hz -le 130 -and -not $_.Primary } | Select-Object -First 1
  if (-not $target) { $target = $screens | Where-Object { $_.Hz -ge 100 -and $_.Hz -le 130 } | Select-Object -First 1 }
  if (-not $target) { $target = $screens | Where-Object { -not $_.Primary } | Select-Object -First 1 }
  if (-not $target) { $target = $screens[0] }
}
Write-Host ("Проектор: {0} {1}x{2} @ {3} Гц" -f $target.Name, $target.W, $target.H, $target.Hz)
if ($Mode -eq 'dlp' -and ($target.Hz -lt 110 -or $target.Hz -gt 130)) {
  Write-Host ""
  Write-Host "ВНИМАНИЕ: на проекторе $($target.Hz) Гц. Для DLP-очков нужно ~120 Гц." -ForegroundColor Yellow
  Write-Host "Параметры Windows -> Дисплей -> (проектор) -> Дополнительные параметры дисплея -> 120 Гц, разрешение 1280x720." -ForegroundColor Yellow
  Write-Host ""
}

# ---------- библиотека ----------
$ffprobe = Join-Path $Root 'tools\ffmpeg\ffprobe.exe'
if (-not (Test-Path $ffprobe)) { $c = Get-Command ffprobe -ErrorAction SilentlyContinue; $ffprobe = if ($c) { $c.Source } else { $null } }

function FileUrl([string]$p) {
  $parts = ($p -replace '\\', '/').Split('/')
  'file:///' + (@($parts[0]) + @($parts | Select-Object -Skip 1 | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/')
}
$okAudio = 'aac', 'mp3', 'opus', 'vorbis', 'flac'
$okVideo = 'h264', 'vp9', 'av1', 'hevc', 'vp8'
$items = New-Object System.Collections.ArrayList
foreach ($folder in 'movies', 'media') {
  $dir = Join-Path $Root $folder
  if (-not (Test-Path $dir)) { continue }
  Get-ChildItem $dir -File | Where-Object { $_.Extension -match '^\.(mp4|m4v|mkv|webm|mov)$' } | Sort-Object Name | ForEach-Object {
    $it = [ordered]@{ name = $_.Name; url = (FileUrl $_.FullName); size = $_.Length; folder = $folder; w = 0; h = 0; warn = '' }
    if ($ffprobe) {
      try {
        $j = (& $ffprobe -v error -show_entries stream=codec_type,codec_name,width,height -of json $_.FullName) -join "`n" | ConvertFrom-Json
        $v = $j.streams | Where-Object { $_.codec_type -eq 'video' } | Select-Object -First 1
        $a = @($j.streams | Where-Object { $_.codec_type -eq 'audio' })
        if ($v) { $it.w = [int]$v.width; $it.h = [int]$v.height }
        $warn = @()
        if ($v -and $okVideo -notcontains $v.codec_name) { $warn += "видео $($v.codec_name)" }
        if ($a.Count -and -not ($a | Where-Object { $okAudio -contains $_.codec_name })) { $warn += "звук $($a[0].codec_name) не играет" }
        if ($_.Extension -notmatch '^\.(mp4|m4v|webm)$') { $warn += 'контейнер ' + $_.Extension.TrimStart('.') }
        if ($warn) { $it.warn = ($warn -join ', ') + ' — пропустите через ADD-MOVIE.cmd' }
      } catch {}
    }
    [void]$items.Add([pscustomobject]$it)
  }
}
$json = if ($items.Count) { ConvertTo-Json -InputObject @($items) -Depth 3 -Compress } else { '[]' }
[IO.File]::WriteAllText((Join-Path $App 'library.js'), "window.LIBRARY = $json;`n", (New-Object Text.UTF8Encoding $false))
Write-Host "Фильмов в библиотеке: $($items.Count)"

# ---------- браузер ----------
$browsers = @(
  "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
  "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
  "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe",
  "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
  "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe")
$exe = $browsers | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $exe) { throw 'Не найден Chrome или Edge.' }

$profileDir = Join-Path $App 'profile'
# закрыть прошлый экземпляр плеера (только наш профиль, обычный браузер не трогаем)
Get-CimInstance Win32_Process -Filter "Name='chrome.exe' OR Name='msedge.exe'" |
  Where-Object { $_.CommandLine -and $_.CommandLine.Contains($profileDir) } |
  ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Milliseconds 500

$hash = "mode=$Mode"
if ($Test) { $hash += '&test=1' }
if ($Open) { $hash += '&open=' + [Uri]::EscapeDataString($Open) }
if ($Log) { $hash += '&log=1' }
if ($PairLock) { $hash += "&pairlock=$PairLock" }
$url = (FileUrl (Join-Path $App 'index.html')) + '#' + $hash

$bargs = @(
  "--user-data-dir=`"$profileDir`"",
  "--app=`"$url`"",
  "--window-position=$($target.X),$($target.Y)",
  "--window-size=$($target.W),$($target.H)",
  '--start-fullscreen',
  '--allow-file-access-from-files',          # нужно, чтобы WebGL мог читать кадры локального видео
  '--autoplay-policy=no-user-gesture-required',
  '--disable-background-timer-throttling', '--disable-renderer-backgrounding', '--disable-backgrounding-occluded-windows',
  '--disable-features=CalculateNativeWinOcclusion,Translate',
  '--no-first-run', '--no-default-browser-check', '--disable-sync'
)
if ($Log) { $bargs += '--enable-logging', '--v=0' }
Write-Host "Браузер: $exe"
Start-Process -FilePath $exe -ArgumentList $bargs | Out-Null
Write-Host 'Плеер открыт на проекторе. Tab/M — меню, I — диагностика (частота должна быть ~120 Гц).'
