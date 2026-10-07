<#
  Builds everything:
    publish\AnyPortProxyGui.exe            the app
    publish\AnyPortProxy.exe               the background service + "apx" terminal command
    dist\AnyPortProxySetup-<version>.exe   the installer (one file, contains everything above + the WinDivert driver)
  Downloads WinDivert from its official GitHub release the first time.
#>
param(
    [string]$WinDivertVersion = "2.2.2"
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$lib = Join-Path $root 'lib\WinDivert'
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version

# 1. WinDivert driver
if (-not (Test-Path (Join-Path $lib 'WinDivert64.sys'))) {
    $name = "WinDivert-$WinDivertVersion-A"
    $url = "https://github.com/basil00/WinDivert/releases/download/v$WinDivertVersion/$name.zip"
    $zip = Join-Path $env:TEMP "$name.zip"
    $extract = Join-Path $env:TEMP $name
    Write-Host "Downloading $url"
    Invoke-WebRequest $url -OutFile $zip -UseBasicParsing
    Expand-Archive $zip -DestinationPath $extract -Force
    $x64 = Get-ChildItem $extract -Recurse -Directory -Filter x64 | Select-Object -First 1
    New-Item -ItemType Directory -Force $lib | Out-Null
    Copy-Item (Join-Path $x64.FullName 'WinDivert.dll'), (Join-Path $x64.FullName 'WinDivert64.sys') $lib -Force
    Remove-Item $zip, $extract -Recurse -Force
}

# 2. The app + service
$publish = Join-Path $root 'publish'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
foreach ($proj in 'src\AnyPortProxy\AnyPortProxy.csproj', 'src\AnyPortProxy.Gui\AnyPortProxy.Gui.csproj') {
    dotnet publish (Join-Path $root $proj) -c Release -o $publish
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $proj" }
}
Get-ChildItem $publish -Filter *.pdb | Remove-Item
$payloadFiles = 'AnyPortProxy.exe', 'AnyPortProxyGui.exe', 'WinDivert.dll', 'WinDivert64.sys'
foreach ($f in $payloadFiles) {
    if (-not (Test-Path (Join-Path $publish $f))) { throw "missing $f in publish output" }
}

# 3. The installer: zip the app into the setup exe as an embedded resource
$artifacts = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Force $artifacts | Out-Null
$payload = Join-Path $artifacts 'payload.zip'
if (Test-Path $payload) { Remove-Item $payload }
Compress-Archive -Path ($payloadFiles | ForEach-Object { Join-Path $publish $_ }) -DestinationPath $payload -CompressionLevel Optimal

$setupOut = Join-Path $artifacts 'setup'
dotnet publish (Join-Path $root 'src\AnyPortProxy.Setup\AnyPortProxy.Setup.csproj') -c Release -o $setupOut "-p:PayloadZip=$payload"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for the installer" }

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$setup = Join-Path $dist "AnyPortProxySetup-$version.exe"
Copy-Item (Join-Path $setupOut 'AnyPortProxySetup.exe') $setup -Force
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash

Write-Host ""
Write-Host "Built AnyPortProxy $version" -ForegroundColor Green
Write-Host "  App:        $publish"
Write-Host "  Installer:  $setup  ($([math]::Round((Get-Item $setup).Length / 1MB)) MB)"
Write-Host "  SHA-256:    $hash"
