# bridge-selftest.ps1
#
# Runs the remote-access bridge self-test (certificate pinning) in headless
# Microsoft Edge and prints one PASS/FAIL line per check. Exits 1 on failure.
#
# Depends on Microsoft Edge (present on every supported Windows):
#   https://www.microsoft.com/edge
# Edge is a GUI process, so its stdout only reaches a file through cmd redirection.
#
# Usage: .\tools\bridge-selftest.ps1

$edge = @("$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe", "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { Write-Error "Microsoft Edge not found"; exit 1 }

$page = (Resolve-Path (Join-Path $PSScriptRoot "..\MusicAssistant\Assets\bridge\selftest.html")).Path -replace '\\', '/'
$dump = Join-Path ([IO.Path]::GetTempPath()) "ma-bridge-selftest.html"

cmd /c "`"$edge`" --headless=new --disable-gpu --dump-dom `"file:///$page`" > `"$dump`" 2>nul"
$out = [regex]::Match((Get-Content -Raw $dump), '(?s)<pre id="out">(.*?)</pre>').Groups[1].Value
$out = [System.Net.WebUtility]::HtmlDecode($out)
Remove-Item $dump -ErrorAction SilentlyContinue

$out
if ($out -notmatch 'SELFTEST OK') { exit 1 }
