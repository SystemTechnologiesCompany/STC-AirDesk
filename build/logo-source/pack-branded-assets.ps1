$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$root=(Get-Location).Path
$source=[Drawing.Bitmap]::new((Join-Path $root 'build\logo-source\AirDesk-approved-logo.png'))
function Resize-Art($image, [Drawing.Rectangle]$area, [int]$width,[int]$height) {
  $out=[Drawing.Bitmap]::new($width,$height)
  $gfx=[Drawing.Graphics]::FromImage($out)
  try {
    $gfx.CompositingMode=[Drawing.Drawing2D.CompositingMode]::SourceCopy
    $gfx.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $gfx.PixelOffsetMode=[Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $gfx.DrawImage($image,[Drawing.Rectangle]::new(0,0,$width,$height),$area,[Drawing.GraphicsUnit]::Pixel)
  } finally { $gfx.Dispose() }
  return $out
}
try {
  if ($source.Width -ne 2172 -or $source.Height -ne 724) { throw 'Unexpected logo dimensions.' }
  $logo=Resize-Art $source ([Drawing.Rectangle]::new(92,156,1990,411)) 1000 207
  $logo.Save((Join-Path $root 'airdesk-logo.png'),[Drawing.Imaging.ImageFormat]::Png)
  $logo.Dispose()
  $size=378
  $area=[Drawing.Rectangle]::new(180,176,$size,$size)
  $crop=$source.Clone($area,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $tile=[Drawing.Bitmap]::new($size,$size)
  $g=[Drawing.Graphics]::FromImage($tile)
  $border=[Drawing.Drawing2D.GraphicsPath]::new()
  $brush=[Drawing.TextureBrush]::new($crop,[Drawing.Drawing2D.WrapMode]::Clamp)
  try {
    $arc=154
    $g.SmoothingMode=[Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $border.AddArc(0,0,$arc,$arc,180,90)
    $border.AddArc(($size-$arc),0,$arc,$arc,270,90)
    $border.AddArc(($size-$arc),($size-$arc),$arc,$arc,0,90)
    $border.AddArc(0,($size-$arc),$arc,$arc,90,90)
    $border.CloseFigure()
    $g.FillPath($brush,$border)
  } finally { $brush.Dispose(); $border.Dispose(); $g.Dispose(); $crop.Dispose() }
  $frames=[Collections.Generic.List[byte[]]]::new()
  $sizes=@(16,24,32,48,64,128,256)
  foreach ($scale in $sizes) {
    $frame=Resize-Art $tile ([Drawing.Rectangle]::new(0,0,$size,$size)) $scale $scale
    $buffer=[IO.MemoryStream]::new()
    try {
      $frame.Save($buffer,[Drawing.Imaging.ImageFormat]::Png)
      $frames.Add($buffer.ToArray())
    } finally { $frame.Dispose(); $buffer.Dispose() }
  }
  $tile.Dispose()
  $writer=[IO.BinaryWriter]::new([IO.File]::Create((Join-Path $root 'airdesk-icon.ico')))
  try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)
    $offset=6 + 16*$frames.Count
    for ($i=0;$i -lt $frames.Count;$i++) {
      $width=[byte]($sizes[$i] % 256)
      $writer.Write($width)
      $writer.Write($width)
      $writer.Write([uint16]0)
      $writer.Write([uint16]1)
      $writer.Write([uint16]32)
      $writer.Write([uint32]$frames[$i].Length)
      $writer.Write([uint32]$offset)
      $offset += $frames[$i].Length
    }
    foreach ($frameBytes in $frames) { $writer.Write([byte[]]$frameBytes) }
  } finally { $writer.Dispose() }
} finally { $source.Dispose() }
