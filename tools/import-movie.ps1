# Добавление 3D-фильма в библиотеку (папка movies).
# - определяет формат SBS / OU (по имени файла или пропорциям, иначе спрашивает)
# - выбирает русскую звуковую дорожку
# - MKV / AC3 / DTS / TrueHD перепаковывает в MP4 + AAC (видео копируется без потери качества)
# Исходный файл не изменяется.
param([Parameter(ValueFromRemainingArguments = $true)] [string[]]$Files)
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$ffmpeg = Join-Path $PSScriptRoot 'ffmpeg\ffmpeg.exe'
$ffprobe = Join-Path $PSScriptRoot 'ffmpeg\ffprobe.exe'
$Movies = Join-Path $Root 'movies'
New-Item -ItemType Directory -Force $Movies | Out-Null

if (-not $Files) {
  Add-Type -AssemblyName System.Windows.Forms
  $dlg = New-Object Windows.Forms.OpenFileDialog
  $dlg.Title = 'Выберите 3D-фильм (SBS или OU)'
  $dlg.Filter = 'Видео|*.mkv;*.mp4;*.m4v;*.avi;*.ts;*.m2ts;*.mov;*.webm|Все файлы|*.*'
  $dlg.Multiselect = $true
  if ($dlg.ShowDialog() -ne 'OK') { exit 0 }
  $Files = $dlg.FileNames
}

$okVideo = 'h264', 'hevc', 'av1', 'vp9'
$okAudio = 'aac', 'mp3', 'opus', 'flac'

# вопрос пользователю; без консоли (или по Enter) — вариант по умолчанию
function Ask([string]$q) { try { Read-Host $q } catch { '' } }

function LayoutFromName([string]$n) {
  $n = $n.ToLower()
  if ($n -match '(^|[^a-z])(h-?ou|half-?ou|ou|tab|h-?tab|top-?bottom|over-?under|abl|tb|3d-?ou)([^a-z]|$)') { return 'OU' }
  if ($n -match '(^|[^a-z])(h-?sbs|half-?sbs|f-?sbs|full-?sbs|sbs|side-?by-?side|lr|3d-?sbs)([^a-z]|$)') { return 'SBS' }
  return $null
}

foreach ($in in $Files) {
  Write-Host ''
  Write-Host "=== $in" -ForegroundColor Cyan
  if (-not (Test-Path -LiteralPath $in)) { Write-Host 'Файл не найден.' -ForegroundColor Red; continue }
  $src = Get-Item -LiteralPath $in
  $j = (& $ffprobe -v error -show_entries 'stream=index,codec_type,codec_name,profile,width,height,channels:stream_disposition=attached_pic:stream_tags=language,title' -of json $src.FullName) -join "`n" | ConvertFrom-Json
  $vids = @($j.streams | Where-Object { $_.codec_type -eq 'video' -and -not ($_.disposition -and $_.disposition.attached_pic) })
  $auds = @($j.streams | Where-Object { $_.codec_type -eq 'audio' })
  if (-not $vids) { Write-Host 'В файле нет видео.' -ForegroundColor Red; continue }
  $v = $vids[0]
  Write-Host ("Видео: {0} {1}x{2} {3}" -f $v.codec_name, $v.width, $v.height, $v.profile)
  if ("$($v.profile)" -match 'Stereo|MVC') {
    Write-Host 'Похоже на 3D Blu-ray MVC: браузер покажет только 2D-вид. Нужна версия SBS / OU.' -ForegroundColor Yellow
  }

  # --- формат стерео ---
  $layout = LayoutFromName $src.BaseName
  if (-not $layout) {
    $r = [double]$v.width / [double]$v.height
    if ($r -ge 3) { $layout = 'SBS' } elseif ($r -le 1.1) { $layout = 'OU' }
  }
  if (-not $layout) {
    Write-Host 'По имени не понять формат. Откройте файл в любом плеере и посмотрите:' -ForegroundColor Yellow
    Write-Host '  1 = две картинки РЯДОМ (SBS / HSBS)   2 = картинки ДРУГ НАД ДРУГОМ (OU / TAB)   3 = обычное 2D'
    $ans = Ask 'Выберите 1/2/3 [1]'
    $layout = switch ($ans) { '2' { 'OU' } '3' { '2D' } default { 'SBS' } }
  }
  Write-Host "Формат стерео: $layout"

  # --- звук: русская дорожка, если есть ---
  $a = $null
  if ($auds) {
    $i = 0
    foreach ($s in $auds) { $i++; Write-Host ("  Звук {0}: {1} {2}кан. [{3}] {4}" -f $i, $s.codec_name, $s.channels, $s.tags.language, $s.tags.title) }
    $a = $auds | Where-Object { $_.tags.language -match '^(rus|ru)$' } | Select-Object -First 1
    if (-not $a) { $a = $auds[0] }
    if ($auds.Count -gt 1) {
      $def = [array]::IndexOf($auds, $a) + 1
      $ans = Ask "Какую дорожку взять? [$def]"
      if ($ans -match '^\d+$' -and [int]$ans -ge 1 -and [int]$ans -le $auds.Count) { $a = $auds[[int]$ans - 1] }
    }
  }

  $copyVideo = $okVideo -contains $v.codec_name
  $copyAudio = (-not $a) -or ($okAudio -contains $a.codec_name)
  $tag = if (-not (LayoutFromName $src.BaseName)) { " [$layout]" } else { '' }
  $out = Join-Path $Movies ($src.BaseName + $tag + '.mp4')
  if (Test-Path -LiteralPath $out) { Write-Host "Уже есть в библиотеке: $out" -ForegroundColor Green; continue }

  # Простое копирование, если файл уже совместим
  if ($src.Extension -match '^\.(mp4|m4v)$' -and $copyVideo -and $copyAudio -and $auds.Count -le 1) {
    Write-Host 'Файл уже совместим, копирую...'
    Copy-Item -LiteralPath $src.FullName -Destination $out
    Write-Host "Готово: $out" -ForegroundColor Green
    continue
  }

  $map = @('-map', "0:$($v.index)")
  if ($a) { $map += '-map', "0:$($a.index)" }
  $acodec = if (-not $a) { @() } elseif ($copyAudio) { @('-c:a', 'copy') } else { @('-c:a', 'aac', '-b:a', '256k', '-ac', '2') }
  $tmp = $out + '.part.mp4'

  if ($copyVideo) {
    Write-Host 'Перепаковка: видео без перекодирования, звук -> AAC (несколько минут)...'
    $vcodecs = @(, @('-c:v', 'copy'))
  } else {
    Write-Host "Видео $($v.codec_name) браузер не играет — перекодирую в H.264 (это долго)." -ForegroundColor Yellow
    $vcodecs = @(
      @('-c:v', 'h264_nvenc', '-preset', 'p5', '-cq', '19', '-pix_fmt', 'yuv420p'),
      @('-c:v', 'h264_qsv', '-global_quality', '20', '-pix_fmt', 'nv12'),
      @('-c:v', 'h264_amf', '-quality', 'quality', '-pix_fmt', 'yuv420p'),
      @('-c:v', 'libx264', '-preset', 'veryfast', '-crf', '19', '-pix_fmt', 'yuv420p'))
  }

  $done = $false
  foreach ($vc in $vcodecs) {
    $fa = @('-hide_banner', '-loglevel', 'error', '-stats', '-y', '-i', $src.FullName) + $map + $vc + $acodec +
          @('-sn', '-dn', '-movflags', '+faststart', $tmp)
    # ffmpeg пишет прогресс в stderr; в PowerShell 5.1 это не должно считаться ошибкой
    $ErrorActionPreference = 'Continue'
    & $ffmpeg @fa 2>&1 | ForEach-Object { Write-Host "`r$_" -NoNewline }
    Write-Host ''
    $ErrorActionPreference = 'Stop'
    if ($LASTEXITCODE -eq 0) { $done = $true; break }
    Write-Host "Не получилось с $($vc[1]), пробую следующий способ..." -ForegroundColor Yellow
  }
  if (-not $done) { Remove-Item -LiteralPath $tmp -ErrorAction SilentlyContinue; Write-Host 'Ошибка конвертации.' -ForegroundColor Red; continue }
  Move-Item -LiteralPath $tmp -Destination $out
  Write-Host "Готово: $out" -ForegroundColor Green
}
Write-Host ''
Write-Host 'Запустите START-3D-CINEMA.cmd — фильм появится в списке.'
