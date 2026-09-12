# build-sendspin.ps1
#
# Rebuilds MusicAssistant\Assets\bridge\sendspin.js, the vendored browser bundle
# of @sendspin/sendspin-js (Apache-2.0) and its dependencies, which the hidden
# WebView2 bridge page uses to make this PC a Music Assistant player.
# The bundle is committed, so the app build itself needs no Node.js.
#
# Depends on Node.js 20+ with npm: https://nodejs.org
#
# Usage: .\tools\build-sendspin.ps1 [-Version 5.0.0]

param([string]$Version = "5.0.0")

$work = Join-Path ([IO.Path]::GetTempPath()) "ma-sendspin-build"
New-Item -ItemType Directory -Force $work | Out-Null
Push-Location $work
try {
    '{ "name": "ma-sendspin-bundle", "private": true }' | Set-Content package.json
    npm install --silent --no-audit --no-fund "@sendspin/sendspin-js@$Version" esbuild@0.25.4
    'export * from "@sendspin/sendspin-js";' | Set-Content entry.js
    npx --yes esbuild entry.js --bundle --format=iife --global-name=Sendspin --platform=browser --target=es2022 --minify --legal-comments=inline --outfile=sendspin.js
    $dest = Join-Path $PSScriptRoot "..\MusicAssistant\Assets\bridge"
    Copy-Item sendspin.js (Join-Path $dest "sendspin.js") -Force
    Copy-Item "node_modules\@sendspin\sendspin-js\LICENSE" (Join-Path $dest "sendspin-LICENSE.txt") -Force
    "sendspin.js $Version written to $dest"
}
finally {
    Pop-Location
}
