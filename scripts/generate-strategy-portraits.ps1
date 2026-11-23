param([string]$Source = 'docs/research/refs/upstream/data/icons/strategies')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
# 将已有策略图标转成圆内灰度特征；运行时不需要解码 WebP，也不包含水印与圆框。
$portraits = [ordered]@{}
foreach ($file in Get-ChildItem -LiteralPath $Source -Filter '*.png' | Sort-Object Name) {
    $image = [Drawing.Bitmap]::FromFile($file.FullName)
    $small = [Drawing.Bitmap]::new(32, 32)
    $graphics = [Drawing.Graphics]::FromImage($small)
    $graphics.DrawImage($image, 0, 0, 32, 32)
    $values = @()
    for ($y = 0; $y -lt 32; $y++) { for ($x = 0; $x -lt 32; $x++) {
        if (($x - 15.5) * ($x - 15.5) + ($y - 15.5) * ($y - 15.5) -ge 144) { continue }
        $c = $small.GetPixel($x, $y)
        $values += [int][Math]::Round(.299 * $c.R + .587 * $c.G + .114 * $c.B)
    } }
    $portraits[$file.BaseName] = $values
    $graphics.Dispose(); $small.Dispose(); $image.Dispose()
}
[IO.File]::WriteAllText((Join-Path (Get-Location) 'data/strategy_portraits.json'), ($portraits | ConvertTo-Json -Depth 3 -Compress) + "`n")
Write-Output "输出 $($portraits.Count) 个策略头像特征。"
