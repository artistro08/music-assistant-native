<#
    make-setup.ps1

    Wraps the built MSIX, its test certificate and Install-Msix.ps1 into one
    self-extracting MusicAssistant-Setup.exe in dist\. Double-clicking that exe
    trusts the certificate (one UAC prompt) and installs the package, so nobody
    has to touch certificate stores by hand.

    Uses IExpress, which ships with Windows:
    https://learn.microsoft.com/windows/win32/msi/iexpress
#>

param(
    [string] $Version = '1.0.0.0'
)

$ErrorActionPreference = 'Stop'

# Paths
$root    = Split-Path -Parent $PSScriptRoot
$dist    = Join-Path $root 'dist'
$package = Join-Path $dist "MusicAssistant_${Version}_x64_Test"
$staging = Join-Path $dist 'setup-staging'
$target  = Join-Path $dist 'MusicAssistant-Setup.exe'
$sed     = Join-Path $staging 'setup.sed'

# Stage Inputs
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory $staging | Out-Null
Copy-Item (Join-Path $package "MusicAssistant_${Version}_x64.msix") $staging
Copy-Item (Join-Path $package "MusicAssistant_${Version}_x64.cer")  $staging
Copy-Item (Join-Path $PSScriptRoot 'Install-Msix.ps1')               $staging
if (Test-Path $target) { Remove-Item $target -Force }

# IExpress Directive
@"
[Version]
Class=IEXPRESS
SEDVersion=3
[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=0
HideExtractAnimation=0
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=%InstallPrompt%
DisplayLicense=%DisplayLicense%
FinishMessage=%FinishMessage%
TargetName=%TargetName%
FriendlyName=%FriendlyName%
AppLaunched=%AppLaunched%
PostInstallCmd=%PostInstallCmd%
AdminQuietInstCmd=%AdminQuietInstCmd%
UserQuietInstCmd=%UserQuietInstCmd%
SourceFiles=SourceFiles
[Strings]
InstallPrompt=
DisplayLicense=
FinishMessage=
TargetName=$target
FriendlyName=Music Assistant Setup
AppLaunched=powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File Install-Msix.ps1
PostInstallCmd=<None>
AdminQuietInstCmd=
UserQuietInstCmd=
FILE0="MusicAssistant_${Version}_x64.msix"
FILE1="MusicAssistant_${Version}_x64.cer"
FILE2="Install-Msix.ps1"
[SourceFiles]
SourceFiles0=$staging\
[SourceFiles0]
%FILE0%=
%FILE1%=
%FILE2%=
"@ | Set-Content $sed -Encoding ASCII

# Build
& "$env:WINDIR\System32\iexpress.exe" /N /Q $sed | Out-Null
Remove-Item $staging -Recurse -Force
if (-not (Test-Path $target)) { throw 'IExpress did not produce the setup exe.' }
"Built {0} ({1:N0} MB)" -f $target, ((Get-Item $target).Length / 1MB)
