<#
    Install-Msix.ps1

    Installs the Music Assistant MSIX package from the folder this script sits in.
    Windows only installs packages whose signing certificate it trusts, so the
    script first imports the .cer next to it into the machine's Trusted People
    store (that needs administrator rights, so it re-launches itself elevated),
    then installs the .msix for the current user.

    Usage: put Install-Msix.ps1, the .cer and the .msix in one folder, then
    right-click the script and choose "Run with PowerShell".

    Depends on the built-in Add-AppxPackage cmdlet:
    https://learn.microsoft.com/powershell/module/appx/add-appxpackage
#>

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

# Elevate
$is_admin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $is_admin) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-ExecutionPolicy Bypass -File `"$($MyInvocation.MyCommand.Path)`""
    return
}

# Find Package and Certificate
$msix = Get-ChildItem $here -Filter *.msix | Select-Object -First 1
$cer  = Get-ChildItem $here -Filter *.cer  | Select-Object -First 1
if (-not $msix) { Write-Host 'No .msix file found next to this script.'; Read-Host 'Press Enter to close'; return }

# Trust Certificate
if ($cer) {
    Import-Certificate -FilePath $cer.FullName -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
    Write-Host "Trusted certificate $($cer.Name)"
}

# Install
try {
    Add-AppxPackage -Path $msix.FullName
    Write-Host "Installed $($msix.Name). Music Assistant is in the Start Menu."
}
catch {
    Write-Host "Install failed: $($_.Exception.Message)"
}

Read-Host 'Press Enter to close'
