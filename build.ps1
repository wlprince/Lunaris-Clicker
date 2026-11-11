param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'build'))
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Install .NET Framework 4.8 (Windows x64).' }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
Add-Type -AssemblyName System.Drawing
$bitmap = New-Object System.Drawing.Bitmap 64,64
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = 'AntiAlias'
$graphics.Clear([System.Drawing.Color]::Transparent)
$fill = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(120,96,232))
$shape = New-Object System.Drawing.Drawing2D.GraphicsPath
$shape.AddArc(2,2,26,26,180,90)
$shape.AddArc(36,2,26,26,270,90)
$shape.AddArc(36,36,26,26,0,90)
$shape.AddArc(2,36,26,26,90,90)
$shape.CloseFigure()
$graphics.FillPath($fill,$shape)
$points = [System.Drawing.Point[]]@((New-Object System.Drawing.Point 22,15),(New-Object System.Drawing.Point 22,43),(New-Object System.Drawing.Point 30,36),(New-Object System.Drawing.Point 37,51),(New-Object System.Drawing.Point 43,48),(New-Object System.Drawing.Point 36,34),(New-Object System.Drawing.Point 47,33))
$graphics.FillPolygon([System.Drawing.Brushes]::White,$points)
$icon = [System.Drawing.Icon]::FromHandle($bitmap.GetHicon())
$iconPath = Join-Path $OutputDirectory 'MellowClick.ico'
$stream = [System.IO.File]::Create($iconPath)
$icon.Save($stream)
$stream.Dispose()
$graphics.Dispose(); $fill.Dispose(); $shape.Dispose(); $bitmap.Dispose()
$target = Join-Path $OutputDirectory 'Lunaris.exe'
$arguments = @('/nologo','/target:winexe','/platform:x64','/optimize+','/utf8output',"/out:$target",("/win32icon:" + $iconPath),("/win32manifest:" + (Join-Path $PSScriptRoot 'app.manifest')))
foreach ($reference in @('System.dll','System.Core.dll','System.Xml.dll','System.Xml.Linq.dll','WPF/WindowsBase.dll','WPF/PresentationCore.dll','WPF/PresentationFramework.dll','System.Xaml.dll')) { $arguments += '/reference:' + (Join-Path $framework $reference) }
foreach ($resource in @('MainWindow.xaml','TestWindow.xaml')) { $arguments += '/resource:' + (Join-Path $PSScriptRoot $resource) + ',' + $resource }
$arguments += Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' | ForEach-Object { $_.FullName }
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'MellowClick.exe.config') -Destination ($target + '.config') -Force
Write-Output $target

