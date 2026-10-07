<#
  Installs or updates AnyPortProxy from .\publish (same as pressing "Install & start" in the app).
  Copies to C:\Program Files\AnyPortProxy, registers the auto-start service, firewall rule,
  Start menu/desktop shortcuts and the "apx" command, then starts it. Your settings are kept.
#>
#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'publish\AnyPortProxy.exe'
if (-not (Test-Path $exe)) { throw "Run build.ps1 first" }
& $exe install
exit $LASTEXITCODE
