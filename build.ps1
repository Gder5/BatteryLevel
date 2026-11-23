# 使用 Windows 自带的 .NET Framework 4.x C# 编译器构建，无需安装任何 SDK。
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$src  = Join-Path $root 'src'
$out  = $root   # 产物输出到仓库根目录，随源码一起提交
$ico  = Join-Path $root 'assets\app.ico'
$csc  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw '未找到 .NET Framework 4.x 编译器 csc.exe' }

New-Item -ItemType Directory -Force (Split-Path $ico) | Out-Null

# 正在运行的 exe 会被系统锁定，无法覆盖，先关闭它
$exe = Join-Path $out 'BatteryLevel.exe'
$running = Get-Process BatteryLevel -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }
if ($running) {
    Write-Host '检测到 BatteryLevel 正在运行，先将其关闭…'
    $running | Stop-Process -Force
    $running | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue
}

# 生成应用图标（PNG 压缩的多尺寸 ICO）
if (-not (Test-Path $ico)) {
    Add-Type -AssemblyName System.Drawing
    Add-Type -Path (Join-Path $src 'Theme.cs') -ReferencedAssemblies System.Drawing, System.Windows.Forms
    $sizes = 256, 48, 32, 16
    $pngs = foreach ($s in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap $s, $s
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = 'AntiAlias'
        $bg = [BatteryLevel.Theme]::RoundRect((New-Object System.Drawing.RectangleF 0, 0, $s, $s), $s * 0.22)
        $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0x23, 0x26, 0x2E))), $bg)
        $inner = [int]($s * 0.78)
        $bat = [BatteryLevel.IconFactory]::DrawBattery(80, $inner, [System.Drawing.Color]::White)
        $off = [int](($s - $inner) / 2)
        $g.DrawImage($bat, $off, $off, $inner, $inner)
        $g.Dispose()
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose(); $bat.Dispose()
        ,$ms.ToArray()
    }
    $fs = [System.IO.File]::Create($ico)
    $bw = New-Object System.IO.BinaryWriter $fs
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $d = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
        $bw.Write([byte]$d); $bw.Write([byte]$d); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
        $offset += $pngs[$i].Length
    }
    foreach ($p in $pngs) { $bw.Write($p) }
    $bw.Close()
    Write-Host "已生成图标 $ico"
}

$files = Get-ChildItem $src -Filter *.cs | ForEach-Object { $_.FullName }
& $csc /nologo /target:winexe /optimize+ /platform:anycpu /codepage:65001 `
    /out:"$out\BatteryLevel.exe" /win32icon:"$ico" /win32manifest:"$src\app.manifest" `
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll $files
if ($LASTEXITCODE -ne 0) { throw "编译失败 ($LASTEXITCODE)" }

$size = (Get-Item "$out\BatteryLevel.exe").Length
Write-Host ("构建完成：{0}\BatteryLevel.exe ({1:N1} KB)" -f $out, ($size / 1KB))
