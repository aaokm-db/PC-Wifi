$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$icoScript = Join-Path $root "GenerateIcon.ps1"
$icoPath = Join-Path $root "app.ico"
$srcPath = Join-Path $root "PCWifi.cs"
$outExe = Join-Path $root "PCWifi.exe"

if (-not (Test-Path $icoPath)) {
    & powershell -ExecutionPolicy Bypass -File $icoScript
}

$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$fw = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
$wpf = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF"
$wmd = "C:\Windows\System32\WinMetadata"

Write-Host "Compiling PCWifi.exe..." -ForegroundColor Cyan

& $csc /nologo /target:winexe /optimize+ `
    /win32icon:"$icoPath" `
    /out:"$outExe" `
    /r:"$fw\System.dll" `
    /r:"$fw\System.Core.dll" `
    /r:"$fw\System.Drawing.dll" `
    /r:"$fw\System.Windows.Forms.dll" `
    /r:"$fw\System.Xaml.dll" `
    /r:"$fw\System.Runtime.dll" `
    /r:"$fw\System.Runtime.InteropServices.WindowsRuntime.dll" `
    /r:"$wpf\WindowsBase.dll" `
    /r:"$wpf\PresentationCore.dll" `
    /r:"$wpf\PresentationFramework.dll" `
    /r:"$wmd\Windows.Foundation.winmd" `
    /r:"$wmd\Windows.Devices.winmd" `
    /r:"$wmd\Windows.Networking.winmd" `
    "$srcPath"

if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE"
}

$item = Get-Item $outExe
Write-Host "Successfully built: $($item.FullName) ($([Math]::Round($item.Length / 1KB, 1)) KB)" -ForegroundColor Green
