param([string]$OutputDirectory = $PSScriptRoot)

$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) {
    throw 'The Windows .NET Framework C# compiler could not be found.'
}

$directory = New-Item -ItemType Directory -Path $OutputDirectory -Force
$executable = Join-Path $directory.FullName 'STC-AirDesk.exe'
Push-Location -LiteralPath $PSScriptRoot
try {
    & $compiler /nologo /target:winexe /optimize+ "/out:$executable" /win32icon:airdesk-icon.ico /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Runtime.Serialization.dll /resource:index.html /resource:styles.css /resource:tracker.js /resource:mouse-mode.js /resource:mouse-training.js /resource:settings.js /resource:updates.js /resource:mouse-circle-svgrepo-com.svg /resource:airdesk-logo.png /resource:airdesk-icon.ico Launcher.cs AppServer.cs MouseControl.cs FloatingKeyboard.cs AppSettings.cs AirDeskUpdates.cs
    if ($LASTEXITCODE -ne 0) {
        throw 'Compilation failed.'
    }
}
finally {
    Pop-Location
}
