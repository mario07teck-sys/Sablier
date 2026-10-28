# Compile Sablier.exe avec le compilateur C# fourni avec Windows (aucune installation requise).
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"

# Icône : sablier sombre sur un carré arrondi dégradé (16, 32, 48 et 256 px, PNG dans un .ico)
if (-not (Test-Path Sablier.ico)) {
    Add-Type -AssemblyName System.Drawing
    $images = foreach ($s in 16, 32, 48, 256) {
        $bmp = New-Object Drawing.Bitmap $s, $s
        $g = [Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = 'AntiAlias'
        $d = [float]($s * 0.44); $e = [float]($s - 1 - $d)
        $forme = New-Object Drawing.Drawing2D.GraphicsPath
        $forme.AddArc(0, 0, $d, $d, 180, 90); $forme.AddArc($e, 0, $d, $d, 270, 90)
        $forme.AddArc($e, $e, $d, $d, 0, 90); $forme.AddArc(0, $e, $d, $d, 90, 90); $forme.CloseFigure()
        $fond = New-Object Drawing.Drawing2D.LinearGradientBrush (New-Object Drawing.PointF 0, 0), (New-Object Drawing.PointF $s, $s), ([Drawing.Color]::FromArgb(34, 211, 238)), ([Drawing.Color]::FromArgb(167, 139, 250))
        $g.FillPath($fond, $forme)
        $encre = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(10, 14, 23))
        $t = [Math]::Max(1.5, $s * 0.075)
        $g.FillRectangle($encre, [float]($s * 0.25), [float]($s * 0.2), [float]($s * 0.5), [float]$t)
        $g.FillRectangle($encre, [float]($s * 0.25), [float]($s * 0.8 - $t), [float]($s * 0.5), [float]$t)
        $g.FillPolygon($encre, [Drawing.PointF[]]@((New-Object Drawing.PointF ($s * 0.31), ($s * 0.2 + $t)), (New-Object Drawing.PointF ($s * 0.69), ($s * 0.2 + $t)), (New-Object Drawing.PointF ($s * 0.5), ($s * 0.5))))
        $g.FillPolygon($encre, [Drawing.PointF[]]@((New-Object Drawing.PointF ($s * 0.5), ($s * 0.5)), (New-Object Drawing.PointF ($s * 0.69), ($s * 0.8 - $t)), (New-Object Drawing.PointF ($s * 0.31), ($s * 0.8 - $t))))
        $g.Dispose()
        $ms = New-Object IO.MemoryStream
        $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
        , @($s, $ms.ToArray())
    }
    $out = New-Object IO.BinaryWriter ([IO.File]::Create("$PSScriptRoot\Sablier.ico"))
    $out.Write([uint16]0); $out.Write([uint16]1); $out.Write([uint16]$images.Count)
    $offset = 6 + 16 * $images.Count
    foreach ($img in $images) {
        $taille = if ($img[0] -ge 256) { 0 } else { $img[0] }
        $out.Write([byte]$taille); $out.Write([byte]$taille); $out.Write([byte]0); $out.Write([byte]0)
        $out.Write([uint16]1); $out.Write([uint16]32); $out.Write([uint32]$img[1].Length); $out.Write([uint32]$offset)
        $offset += $img[1].Length
    }
    foreach ($img in $images) { $out.Write([byte[]]$img[1]) }
    $out.Close()
}

& "$fw\csc.exe" /nologo /target:winexe /optimize+ /out:Sablier.exe /win32icon:Sablier.ico `
    /r:"$fw\WPF\PresentationFramework.dll" /r:"$fw\WPF\PresentationCore.dll" /r:"$fw\WPF\WindowsBase.dll" `
    /r:"$fw\System.Xaml.dll" /r:"$fw\Microsoft.CSharp.dll" `
    /resource:Theme.xaml,Theme.xaml /resource:Main.xaml,Main.xaml /resource:Alerte.xaml,Alerte.xaml /resource:Setup.xaml,Setup.xaml `
    Sablier.cs
if ($LASTEXITCODE -ne 0) { throw "Échec de la compilation" }
# Le même exe sert d'installeur : c'est ce fichier qu'on partage
New-Item -ItemType Directory -Force dist | Out-Null
Copy-Item Sablier.exe dist\Sablier-Setup.exe -Force
Write-Host "OK : $PSScriptRoot\dist\Sablier-Setup.exe"
