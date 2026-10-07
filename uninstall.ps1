<#
  Removes AnyPortProxy: service, firewall rules, router forwards it created, shortcuts, the apx command and program files.
  Add -Purge to also delete your settings and logs (C:\ProgramData\AnyPortProxy).
#>
#Requires -RunAsAdministrator
param([switch]$Purge)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $env:ProgramFiles 'AnyPortProxy\AnyPortProxy.exe'
if (-not (Test-Path $exe)) { $exe = Join-Path $PSScriptRoot 'publish\AnyPortProxy.exe' }
if (-not (Test-Path $exe)) { throw "AnyPortProxy.exe not found" }
$args = @('uninstall', '--yes')
if ($Purge) { $args += '--purge' }
& $exe @args
exit $LASTEXITCODE
