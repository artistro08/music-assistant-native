<#
    Install-Msix.ps1

    Installs the Music Assistant MSIX package from the folder this script sits in.
    Windows only installs packages whose signing certificate it trusts, so the
    script first imports the .cer next to it into the machine's Trusted People
    store (that needs administrator rights, so it re-launches itself elevated),
    then installs the .msix for the current user.

    Used standalone (right-click, Run with PowerShell) and by the setup exe that
    tools\make-setup.ps1 builds, which extracts the package and runs this script.

    Depends on the built-in Add-AppxPackage cmdlet:
    https://learn.microsoft.com/powershell/module/appx/add-appxpackage
#>

$ErrorActionPreference = 'Stop'
$here  = Split-Path -Parent $MyInvocation.MyCommand.Path
$shell = New-Object -ComObject WScript.Shell

# Elevate (wait, so a calling installer does not clean up the folder too early)
$is_admin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $is_admin) {
    try {
        Start-Process powershell.exe -Verb RunAs -Wait -WindowStyle Hidden -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$($MyInvocation.MyCommand.Path)`""
    }
    catch {
        $shell.Popup('Music Assistant needs administrator approval to install.', 0, 'Music Assistant Setup', 48) | Out-Null
    }
    return
}

# Find Package and Certificate
$msix = Get-ChildItem $here -Filter *.msix | Select-Object -First 1
$cer  = Get-ChildItem $here -Filter *.cer  | Select-Object -First 1
if (-not $msix) {
    $shell.Popup('No .msix file found next to this script.', 0, 'Music Assistant Setup', 16) | Out-Null
    return
}

# Trust Certificate and Install
try {
    if ($cer) {
        Import-Certificate -FilePath $cer.FullName -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
    }
    Add-AppxPackage -Path $msix.FullName
    $shell.Popup('Music Assistant is installed. Find it in the Start Menu.', 0, 'Music Assistant Setup', 64) | Out-Null
}
catch {
    $shell.Popup("Install failed:`n`n$($_.Exception.Message)", 0, 'Music Assistant Setup', 16) | Out-Null
}
