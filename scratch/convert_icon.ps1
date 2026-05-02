
Add-Type -AssemblyName System.Drawing
$pngPath = "d:\Git\recents\src\Recents.App\Resources\Icons\AppIcon.png"
$icoPath = "d:\Git\recents\src\Recents.App\Resources\Icons\AppIcon.ico"

$bmp = [System.Drawing.Bitmap]::FromFile($pngPath)
$hIcon = $bmp.GetHicon()
$icon = [System.Drawing.Icon]::FromHandle($hIcon)

$fileStream = New-Object System.IO.FileStream($icoPath, [System.IO.FileMode]::Create)
$icon.Save($fileStream)
$fileStream.Close()

$icon.Dispose()
$bmp.Dispose()
# Note: DestroyIcon should be called via P/Invoke but for a scratch script we'll just let it be.
