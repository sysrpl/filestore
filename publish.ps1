#Requires -Version 5.1
$ErrorActionPreference = "Stop"

$repoDir = $PSScriptRoot
$appName = "S3 File Explorer"
$publishDir = "$env:LOCALAPPDATA\Programs\$appName"
$exePath = "$publishDir\filestore.exe"
$pngPath = "$repoDir\resources\icon.png"
$icoPath = "$repoDir\resources\icon.ico"

# Windows has no native PNG icon support, so wrap the 256x256 PNG in a
# Vista-style ICO container (a single PNG-compressed frame).
$pngBytes = [System.IO.File]::ReadAllBytes($pngPath)
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([UInt16]0)                 # reserved
$bw.Write([UInt16]1)                 # type = icon
$bw.Write([UInt16]1)                 # image count
$bw.Write([Byte]0)                   # width (0 = 256)
$bw.Write([Byte]0)                   # height (0 = 256)
$bw.Write([Byte]0)                   # color count
$bw.Write([Byte]0)                   # reserved
$bw.Write([UInt16]1)                 # planes
$bw.Write([UInt16]32)                # bit count
$bw.Write([UInt32]$pngBytes.Length)  # size of image data
$bw.Write([UInt32]22)                # offset of image data (6 + 16 header bytes)
$bw.Write($pngBytes)
$bw.Flush()
[System.IO.File]::WriteAllBytes($icoPath, $ms.ToArray())
$bw.Close(); $ms.Close()

dotnet publish "$repoDir\filestore.csproj" -c Release -r win-x64 --self-contained -o "$publishDir"

$startMenuDir = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs"
$shortcutPath = Join-Path $startMenuDir "$appName.lnk"
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exePath
$shortcut.WorkingDirectory = $publishDir
$shortcut.IconLocation = "$exePath,0"
$shortcut.Description = "Browse and manage S3 files"
$shortcut.Save()

Write-Host "Published to: $publishDir"
Write-Host "Start Menu shortcut: $shortcutPath"
