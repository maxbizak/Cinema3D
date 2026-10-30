# Создаёт на рабочем столе значки «3D Кинотеатр» и «Добавить 3D-фильм».
# Запускать после распаковки папки на новом компьютере (CREATE-SHORTCUT.cmd).
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$desk = [Environment]::GetFolderPath('Desktop')
$ps = Join-Path $env:windir 'System32\WindowsPowerShell\v1.0\powershell.exe'
$shell = New-Object -ComObject WScript.Shell

function MakeLink([string]$name, [string]$script, [string]$extra, [bool]$hidden, [string]$desc) {
  $lnk = $shell.CreateShortcut((Join-Path $desk "$name.lnk"))
  $lnk.TargetPath = $ps
  $lnk.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle $(if ($hidden) { 'Hidden' } else { 'Normal' }) -File `"$script`" $extra"
  $lnk.WorkingDirectory = $Root
  $lnk.IconLocation = (Join-Path $Root 'app\icon.ico') + ',0'
  $lnk.WindowStyle = $(if ($hidden) { 7 } else { 1 })   # 7 = свёрнуто, без чёрного окна
  $lnk.Description = $desc
  $lnk.Save()
  Write-Host "Создан значок: $name"
}

$exe = Join-Path $Root 'Cinema3D.exe'
if (Test-Path $exe) {
  # основной плеер — отдельная программа с очередью кадров
  $lnk = $shell.CreateShortcut((Join-Path $desk '3D Кинотеатр.lnk'))
  $lnk.TargetPath = $exe
  $lnk.Arguments = ''
  $lnk.WorkingDirectory = $Root
  $lnk.IconLocation = "$exe,0"
  $lnk.WindowStyle = 1
  $lnk.Description = 'Открыть 3D-кинотеатр'
  $lnk.Save()
  Write-Host 'Создан значок: 3D Кинотеатр'
  MakeLink '3D Кинотеатр (браузер)' (Join-Path $Root 'app\launch.ps1') '-Mode dlp' $true 'Запасной плеер в браузере'
} else {
  MakeLink '3D Кинотеатр' (Join-Path $Root 'app\launch.ps1') '-Mode dlp' $true 'Открыть 3D-плеер на проекторе'
}
MakeLink 'Добавить 3D-фильм' (Join-Path $Root 'tools\import-movie.ps1') '' $false 'Добавить фильм SBS/OU в библиотеку'
Write-Host 'Готово. Значки на рабочем столе.'
