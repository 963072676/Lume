Add-Type -AssemblyName System.Drawing
$images = @()
foreach($size in @(16,24,32,48,64,128,256)) {
 $bitmap = [Drawing.Bitmap]::new($size,$size)
 $g=[Drawing.Graphics]::FromImage($bitmap); $g.SmoothingMode='AntiAlias'; $g.Clear([Drawing.Color]::Transparent)
 $green=[Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#456F5D')); $cream=[Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#F2F6E9'))
 $g.FillEllipse($green,0,0,$size-1,$size-1)
 $g.FillRectangle($cream,[single]($size*.29),[single]($size*.22),[single]($size*.16),[single]($size*.56))
 $g.FillRectangle($cream,[single]($size*.29),[single]($size*.62),[single]($size*.44),[single]($size*.16))
 $g.FillEllipse($cream,[single]($size*.58),[single]($size*.23),[single]($size*.14),[single]($size*.14))
 $stream=[IO.MemoryStream]::new(); $bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png); $images+=,@($size,$stream.ToArray())
 if($size -eq 256){$bitmap.Save((Join-Path $PWD 'src/Lume.Desktop/Assets/lume.png'),[Drawing.Imaging.ImageFormat]::Png)}
 $stream.Dispose();$g.Dispose();$bitmap.Dispose();$green.Dispose();$cream.Dispose()
}
$out=[IO.File]::Create((Join-Path $PWD 'src/Lume.Desktop/Assets/lume.ico'));$writer=[IO.BinaryWriter]::new($out)
$writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$images.Count);$offset=6+16*$images.Count
foreach($entry in $images){$dimension=if($entry[0] -eq 256){0}else{$entry[0]};$writer.Write([byte]$dimension);$writer.Write([byte]$dimension);$writer.Write([byte]0);$writer.Write([byte]0);$writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$entry[1].Length);$writer.Write([uint32]$offset);$offset+=$entry[1].Length}
foreach($entry in $images){$writer.Write([byte[]]$entry[1])};$writer.Dispose()
