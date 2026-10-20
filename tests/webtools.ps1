# Runs every Omebly Tools tool inside Omebly Explorer on a real file and checks that it saves a result.
# Each tool opens in its own window for a moment (it presses the main button and Download by itself).
#   powershell -File tests\webtools.ps1 [exe] [samples folder]
param(
  [string]$Exe = "D:\my latest project\Omebly Explorer\publish\test-x64\OmeblyExplorer.exe",
  [string]$Samples = "D:\oxtest\viewers",
  [string]$Only = ""
)
$root = "D:\oxtest\webtools"
$tools = Get-Content (Join-Path (Split-Path $Exe) "WebTools\tools.json") -Raw | ConvertFrom-Json
if (Test-Path $root) { Remove-Item $root -Recurse -Force }
New-Item -ItemType Directory $root | Out-Null
$env:OMEBLY_TOOLTEST = "1"
$env:OMEBLY_SETTINGS_DIR = "$env:TEMP\omebly-webtools-test-settings"
$pass = 0; $fail = 0; $skip = 0
foreach ($t in $tools) {
  if ($Only -and $t.slug -ne $Only) { continue }
  $acc = $t.accept -join ','
  $inputs = @()
  if ($acc -match 'pdf') { $inputs = @("three pages.pdf") ; if ($t.multiple) { $inputs += "three pages.pdf" } }
  elseif ($acc -match 'docx') { $inputs = @("letter.docx") }
  elseif ($acc -match 'xlsx') { $inputs = @("sheet.xlsx") }
  elseif ($acc -match 'pptx') { $inputs = @() }
  elseif ($acc -match 'image') { $inputs = @("photo 1.jpg", "pattern.png") }
  elseif ($acc -match 'html') { $inputs = @("page.html") }
  elseif ($acc -match 'markdown|\.md') { $inputs = @("readme.md") }
  elseif ($acc -match 'epub') { $inputs = @() }
  if ($inputs.Count -eq 0 -or -not (Test-Path (Join-Path $Samples $inputs[0]))) { "SKIP $($t.slug) (no sample file)"; $skip++; continue }
  $dir = Join-Path $root $t.slug; New-Item -ItemType Directory $dir | Out-Null
  $paths = @(); $i = 0
  foreach ($f in $inputs) { $i++; $dst = Join-Path $dir ("in$i" + [IO.Path]::GetExtension($f)); Copy-Item (Join-Path $Samples $f) $dst; $paths += "`"$dst`"" }
  $names = @(Get-ChildItem $dir | ForEach-Object { $_.Name })
  $p = Start-Process -FilePath $Exe -ArgumentList (@('--webtool', $t.slug) + $paths) -PassThru
  $sw = [Diagnostics.Stopwatch]::StartNew(); $made = $null
  while ($sw.Elapsed.TotalSeconds -lt 90) {
    Start-Sleep -Milliseconds 700
    $now = @(Get-ChildItem $dir | Where-Object { $names -notcontains $_.Name })
    if ($now.Count -gt 0) { Start-Sleep -Milliseconds 1500; $made = $now; break }
    if ($p.HasExited) { break }
  }
  if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
  if ($made) { "PASS $($t.slug) -> $(($made | ForEach-Object { "$($_.Name) ($($_.Length) bytes)" }) -join ', ')"; $pass++ }
  else { "FAIL $($t.slug) (nothing saved in $([int]$sw.Elapsed.TotalSeconds) s)"; $fail++ }
}
""
"$pass passed, $fail failed, $skip skipped"
