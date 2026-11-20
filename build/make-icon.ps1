# 生成多尺寸 app.ico（程序 / 窗口 / 托盘共用）。零外部依赖，仅 System.Drawing。
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root "src\AiPassportVoice.App\Assets"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$outFile = Join-Path $outDir "app.ico"

function New-IconPng([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [single]($s * 0.045)
    $w = [single]($s - 2 * $pad)
    $radius = [single]($s * 0.24)
    $d = [single]($radius * 2)

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $x = [single]$pad; $y = [single]$pad
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc([single]($x + $w - $d), $y, $d, $d, 270, 90)
    $path.AddArc([single]($x + $w - $d), [single]($y + $w - $d), $d, $d, 0, 90)
    $path.AddArc($x, [single]($y + $w - $d), $d, $d, 90, 90)
    $path.CloseFigure()

    $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(12, 68, 124))
    $g.FillPath($bg, $path)

    $accent = [System.Drawing.Color]::FromArgb(96, 205, 255)
    $brush = New-Object System.Drawing.SolidBrush $accent

    $cx = [single]($s / 2)
    $capW = [single]($s * 0.235)
    $capH = [single]($s * 0.35)
    $capX = [single]($cx - $capW / 2)
    $capY = [single]($s * 0.155)

    $g.FillEllipse($brush, $capX, $capY, $capW, $capW)
    $g.FillEllipse($brush, $capX, [single]($capY + $capH - $capW), $capW, $capW)
    $g.FillRectangle($brush, $capX, [single]($capY + $capW / 2), $capW, [single]($capH - $capW))

    $penW = [single][Math]::Max(1.4, $s * 0.062)
    $pen = New-Object System.Drawing.Pen $accent, $penW
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

    $arcW = [single]($s * 0.46)
    $arcH = [single]($s * 0.30)
    $arcX = [single]($cx - $arcW / 2)
    $arcY = [single]($s * 0.415)
    $g.DrawArc($pen, $arcX, $arcY, $arcW, $arcH, 0, 180)
    $g.DrawLine($pen, $cx, [single]($s * 0.705), $cx, [single]($s * 0.81))

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    $pen.Dispose(); $brush.Dispose(); $bg.Dispose(); $path.Dispose(); $g.Dispose(); $bmp.Dispose()
    return ,@($s, $bytes)
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = @()
foreach ($sz in $sizes) { $images += ,(New-IconPng $sz) }

$fs = [System.IO.File]::Create($outFile)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]$images.Count)

$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $sz = [int]$img[0]
    $data = [byte[]]$img[1]
    $dim = if ($sz -ge 256) { 0 } else { $sz }
    $bw.Write([Byte]$dim)
    $bw.Write([Byte]$dim)
    $bw.Write([Byte]0)
    $bw.Write([Byte]0)
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]$data.Length)
    $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($img in $images) { $bw.Write([byte[]]$img[1]) }
$bw.Flush(); $bw.Close(); $fs.Dispose()

Write-Output "已生成: $outFile"
Get-Item $outFile | Select-Object Name, Length
