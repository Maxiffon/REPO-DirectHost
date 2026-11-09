# Builds the standalone installer: one .exe that carries BepInExPack 5.4.2305 (taken from the r2modman cache, the
# same files r2modman installs) and the released DirectHost plugin zip from the Desktop.
# Output: Desktop\REPO_DirectHost-<version>-installer.exe
param([string]$Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$desktop = [Environment]::GetFolderPath('Desktop')
if (-not $Version) { $Version = ([xml](Get-Content "$root\src\RepoDirectHost.csproj")).Project.PropertyGroup.Version | Select-Object -First 1 }

$release = Join-Path $desktop "REPO_DirectHost-$Version-r2modman.zip"
$pack = "$env:APPDATA\r2modmanPlus-local\REPO\cache\BepInEx-BepInExPack\5.4.2305\BepInExPack"
foreach ($p in $release, "$pack\BepInEx\core\BepInEx.Preloader.dll", "$pack\winhttp.dll") { if (-not (Test-Path $p)) { throw "missing: $p" } }

$obj = Join-Path $PSScriptRoot 'obj'
$stage = Join-Path $obj 'payload'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force "$stage\bepinex", "$stage\plugin" | Out-Null

# BepInEx pack, exactly as in the cache (incl. .doorstop_version)
Get-ChildItem $pack -Force | Copy-Item -Destination "$stage\bepinex" -Recurse -Force

# plugin files from the released r2modman zip
Add-Type -AssemblyName System.IO.Compression.FileSystem
$z = [IO.Compression.ZipFile]::OpenRead($release)
try {
    foreach ($e in $z.Entries) {
        if ($e.Name -in 'RepoDirectHost.dll', 'README.md', 'THIRD-PARTY-NOTICES.md') {
            [IO.Compression.ZipFileExtensions]::ExtractToFile($e, (Join-Path "$stage\plugin" $e.Name), $true)
        }
        if ($e.FullName -eq 'icon.png') { [IO.Compression.ZipFileExtensions]::ExtractToFile($e, (Join-Path $obj 'icon.png'), $true) }
    }
} finally { $z.Dispose() }
$dllVer = (Get-Item "$stage\plugin\RepoDirectHost.dll").VersionInfo.FileVersion
if ($dllVer -ne "$Version.0") { throw "plugin DLL in $release is $dllVer, expected $Version.0" }

# notices: the plugin's own + the BepInEx pack components with their license texts
$n = Get-Content "$stage\plugin\THIRD-PARTY-NOTICES.md" -Raw
$n += "`r`n" + (Get-Content "$PSScriptRoot\BEPINEX-NOTICES.md" -Raw)
foreach ($l in 'BepInEx', 'HarmonyX', 'MonoMod', 'Mono.Cecil', 'UnityDoorstop') {
    $n += "`r`n## $l`r`n`r`n" + '```' + "`r`n" + (Get-Content "$PSScriptRoot\licenses\$l-LICENSE.txt" -Raw).TrimEnd() + "`r`n" + '```' + "`r`n"
}
Set-Content "$stage\plugin\THIRD-PARTY-NOTICES.md" $n -Encoding utf8NoBOM

$payload = Join-Path $obj 'payload.zip'
if (Test-Path $payload) { Remove-Item $payload -Force }
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $payload, [IO.Compression.CompressionLevel]::Optimal, $false)

# exe icon: the plugin icon as one PNG-compressed .ico entry (Windows Vista+ reads those)
$png = [IO.File]::ReadAllBytes((Join-Path $obj 'icon.png'))
$w = [int]$png[16] * 16777216 + [int]$png[17] * 65536 + [int]$png[18] * 256 + [int]$png[19]
$h = [int]$png[20] * 16777216 + [int]$png[21] * 65536 + [int]$png[22] * 256 + [int]$png[23]
if ($w -gt 256 -or $h -gt 256) { throw "icon.png is ${w}x${h}; an .ico entry can be at most 256x256" }
$ico = New-Object IO.MemoryStream
$bw = New-Object IO.BinaryWriter($ico)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]1)                 # header: reserved, type=icon, 1 image
$bw.Write([byte]($w % 256)); $bw.Write([byte]($h % 256)); $bw.Write([byte]0); $bw.Write([byte]0) # 256 is stored as 0
$bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$png.Length); $bw.Write([uint32]22)
$bw.Write($png); $bw.Flush()
[IO.File]::WriteAllBytes((Join-Path $obj 'icon.ico'), $ico.ToArray())

dotnet build "$PSScriptRoot\DirectHostInstaller.csproj" -c Release -nologo -v q "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw "build failed" }
$exe = "$PSScriptRoot\bin\Release\net48\DirectHostInstaller.exe"
$out = Join-Path $desktop "REPO_DirectHost-$Version-installer.exe"
Copy-Item $exe $out -Force
$f = Get-Item $out
"{0}  {1:N0} bytes  version {2}" -f $f.FullName, $f.Length, $f.VersionInfo.FileVersion
