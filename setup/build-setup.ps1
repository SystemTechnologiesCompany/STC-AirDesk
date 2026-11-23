param([string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'build\setup'))

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$output = New-Item -ItemType Directory -Path $OutputDirectory -Force
$installer = Join-Path $output.FullName 'STC-AirDesk-Setup.exe'
$staging = New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) ('AirDesk-Setup-' + [guid]::NewGuid().ToString('N')))
$payload = Join-Path $staging.FullName 'STC-AirDesk.exe'

try {
    & (Join-Path $project 'build.ps1') -OutputDirectory $staging.FullName
    & $compiler /nologo /target:winexe /optimize+ "/out:$installer" `
        "/win32manifest:$(Join-Path $PSScriptRoot 'setup.manifest')" `
        "/win32icon:$(Join-Path $project 'airdesk-icon.ico')" "/resource:$payload,STC-AirDesk.exe" `
        "/resource:$(Join-Path $project 'build\logo-source\logo only head.png'),stc-head.png" `
        "/resource:$(Join-Path $project 'airdesk-logo.png'),airdesk-logo.png" `
        /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
        /reference:System.Runtime.Serialization.dll /reference:Microsoft.CSharp.dll `
        (Join-Path $PSScriptRoot 'SetupWizard.cs') (Join-Path $PSScriptRoot 'InstallationRegistration.cs') `
        (Join-Path $project 'AirDeskUpdates.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Setup compilation failed.' }
    Get-Item -LiteralPath $installer
}
finally {
    if (Test-Path -LiteralPath $payload -PathType Leaf) { Remove-Item -LiteralPath $payload }
    if (!(Get-ChildItem -LiteralPath $staging.FullName -Force)) { Remove-Item -LiteralPath $staging.FullName }
}
