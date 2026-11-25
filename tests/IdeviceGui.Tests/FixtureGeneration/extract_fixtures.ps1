#Requires -Version 7.0
<#
.SYNOPSIS
  从测试 IPA 生成 B6 golden fixture：tests\IdeviceGui.Tests\fixtures\<slug>\{Info.plist,executable-head.bin,icon-slice.bin,expected.json}

.DESCRIPTION
  用途：单元测试直接用仓库里的 fixture 字节断言 Plist / IpaReader，不依赖磁盘上的 IPA。

  前置条件：
  1. 先构建主程序（脚本用主程序自己的 Plist 解析器读 Info.plist，避免在 PowerShell 里重写
     一套 bplist 解析，双重实现会漂移）：
         cd IdeviceGui\prototype\IdeviceGui; dotnet build -c Release
  2. 用 PowerShell 7+ 运行（`pwsh -File extract_fixtures.ps1`）。
     Windows PowerShell 5.1 跑不了：它是 .NET Framework 宿主，`Assembly.LoadFrom` 加载不了
     主程序的 net10.0-windows 程序集（实测报 FileLoadException 0x80131515）。

  可重复运行：每次先清空 fixtures 目录再重建。

  R3（2026-10-29）起：
  - expected.json 附带可追溯性字段：源 IPA 的 SHA256、每个 fixture 文件的 SHA256、Mach-O 魔数、
    生成方式说明（不写时间戳，重跑产物逐字节一致）。
  - 只删本脚本生成过的子目录（含 expected.json 的），**保留** fixtures\readback\ ——
    那是真机 `ideviceinstaller list --xml` 原文 golden，不是本脚本产物。

  注意（本项目踩过的坑）：
  - 不要用 `Get-ChildItem -Filter 'idevice*.exe'` 这类通配符：Windows 通配符不区分大小写。
    本脚本只用 -LiteralPath / -eq（大小写不敏感仅用于文件名前缀映射，不影响文件选择）。
  - 本文件存成 UTF-8 带 BOM（与 probe\*.ps1 同约定；5.1 按 ANSI 读中文会解析失败）。

.EXAMPLE
  pwsh -File extract_fixtures.ps1
  pwsh -File extract_fixtures.ps1 -IpaDir '.\ipas' -OutDir ..\fixtures
#>
[CmdletBinding()]
param(
    [string]$IpaDir = '.\ipas',
    [string]$OutDir,
    [string]$MainDll
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# PS 5.1 绑定 param 默认值时 $PSScriptRoot 还没赋值，所以默认路径在 param 块之后补。
if (-not $OutDir) {
    $OutDir = Join-Path $PSScriptRoot '..\fixtures'
}
if (-not $MainDll) {
    $MainDll = Join-Path $PSScriptRoot '..\..\..\IdeviceGui\prototype\IdeviceGui\bin\Release\net10.0-windows\IdeviceGui.dll'
}

$OutDir = [System.IO.Path]::GetFullPath($OutDir)
$MainDll = [System.IO.Path]::GetFullPath($MainDll)

Add-Type -AssemblyName System.IO.Compression.FileSystem

if (-not (Test-Path -LiteralPath $MainDll)) {
    throw "找不到主程序程序集：$MainDll`n请先构建：cd IdeviceGui\prototype\IdeviceGui; dotnet build -c Release"
}
if (-not (Test-Path -LiteralPath $IpaDir)) {
    throw "找不到 IPA 目录：$IpaDir"
}

# ── 6 个已知测试包 → fixture slug（按文件名前缀映射；ASCII 安全名） ──────────────
$KnownSlugs = @(
    @{ Prefix = 'DOOM';       Slug = 'doom' },
    @{ Prefix = 'Flappy';     Slug = 'flappy' },
    @{ Prefix = 'Gyroblox';   Slug = 'gyroblox' },
    @{ Prefix = 'Line Art';   Slug = 'lineart' },
    @{ Prefix = 'MemoryInfo'; Slug = 'memoryinfo' },
    @{ Prefix = 'Ziggy';      Slug = 'ziggy' }
)

function Get-FixtureSlug([string]$fileName) {
    $stem = [System.IO.Path]::GetFileNameWithoutExtension($fileName)
    foreach ($known in $KnownSlugs) {
        if ($stem.StartsWith($known.Prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $known.Slug
        }
    }

    # 未知包兜底：取第一个纯 ASCII 字母数字片段，小写化。
    $first = ($stem -split '[^A-Za-z0-9]+' | Where-Object { $_ } | Select-Object -First 1)
    if (-not $first) { $first = 'ipa' }
    return $first.ToLowerInvariant()
}

# ── 借主程序的 Plist 解析器读 Info.plist（脚本不重复实现 bplist） ──────────────
$assembly = [System.Reflection.Assembly]::LoadFrom($MainDll)
$plistType = $assembly.GetType('IdeviceGui.Plist', $true)
$parseMethod = $plistType.GetMethod('Parse', [System.Reflection.BindingFlags]'Public, NonPublic, Static')
if ($null -eq $parseMethod) {
    throw "主程序集 $MainDll 里找不到 internal static Plist.Parse(byte[])。"
}

function ConvertFrom-PlistBytes([byte[]]$Bytes) {
    try {
        return $parseMethod.Invoke($null, @(, $Bytes))
    }
    catch {
        $inner = $_.Exception.InnerException
        $detail = if ($null -ne $inner) { "$($inner.GetType().Name): $($inner.Message)" } else { $_.Exception.Message }
        throw "Plist 解析失败：$detail"
    }
}

function Get-PlistString($dict, [string]$key) {
    if ($null -eq $dict) { return '' }
    if (-not $dict.ContainsKey($key)) { return '' }
    $value = $dict[$key]
    if ($value -is [string]) { return $value }
    return ''
}

# ── zip 读取小工具 ────────────────────────────────────────────────────────────
function Read-ZipEntryBytes($entry, [int]$maxBytes = 0) {
    $stream = $entry.Open()
    try {
        $ms = New-Object System.IO.MemoryStream
        if ($maxBytes -le 0) {
            $stream.CopyTo($ms)
            return $ms.ToArray()
        }

        $buffer = New-Object byte[] 8192
        while ([int]$ms.Length -lt $maxBytes) {
            $want = [Math]::Min($buffer.Length, $maxBytes - [int]$ms.Length)
            $read = $stream.Read($buffer, 0, $want)
            if ($read -le 0) { break }
            $ms.Write($buffer, 0, $read)
        }
        return $ms.ToArray()
    }
    finally {
        $stream.Dispose()
    }
}

# ── Mach-O 头 → 架构名（给 M3 用；本轮只记录不断言） ───────────────────────────
function Read-U32([byte[]]$bytes, [int]$offset, [bool]$littleEndian) {
    if ($offset + 4 -gt $bytes.Length) { return 0 }
    if ($littleEndian) {
        return ([uint32]$bytes[$offset]) -bor
               ([uint32]$bytes[$offset + 1] -shl 8) -bor
               ([uint32]$bytes[$offset + 2] -shl 16) -bor
               ([uint32]$bytes[$offset + 3] -shl 24)
    }
    return ([uint32]$bytes[$offset] -shl 24) -bor
           ([uint32]$bytes[$offset + 1] -shl 16) -bor
           ([uint32]$bytes[$offset + 2] -shl 8) -bor
           ([uint32]$bytes[$offset + 3])
}

$ArmSubtypes = @{
    0  = 'arm'
    3  = 'armv4t'
    4  = 'armv4'
    5  = 'armv4'
    6  = 'armv6'
    7  = 'armv5'
    9  = 'armv7'
    10 = 'armv7f'
    11 = 'armv7s'
    12 = 'armv7k'
    13 = 'armv8'
    14 = 'armv6m'
    15 = 'armv7m'
    16 = 'armv7em'
}

function ConvertFrom-CpuType([uint32]$cpuType, [uint32]$cpuSubType) {
    $sub = $cpuSubType -band 0x00FFFFFF
    switch ($cpuType) {
        12        { if ($ArmSubtypes.ContainsKey([int]$sub)) { return $ArmSubtypes[[int]$sub] } return "arm-sub$sub" }
        0x0100000C { if ($sub -eq 2) { return 'arm64e' } return 'arm64' }
        7         { return 'i386' }
        0x01000007 { return 'x86_64' }
        default   { return "cpu$cpuType-sub$sub" }
    }
}

function Get-ArchFromMachO([byte[]]$head) {
    if ($head.Length -lt 32) { return '' }

    $magic = '{0:X2}{1:X2}{2:X2}{3:X2}' -f $head[0], $head[1], $head[2], $head[3]
    $littleEndian = $false
    $isFat = $false
    $fatEntrySize = 20

    switch ($magic) {
        'CAFEBABE'  { $isFat = $true }
        'CAFE BABF' { $isFat = $true; $fatEntrySize = 32 }
        'CAFEBABF'  { $isFat = $true; $fatEntrySize = 32 }
        'BEBAFECA'  { $isFat = $true; $littleEndian = $true }
        'BFBAFECA'  { $isFat = $true; $littleEndian = $true; $fatEntrySize = 32 }
        'FEEDFACE'  { }
        'CEFAEDFE'  { $littleEndian = $true }
        'FEEDFACF'  { }
        'CFFAEDFE'  { $littleEndian = $true }
        default     { return "unknown-magic-$magic" }
    }

    $archs = New-Object System.Collections.Generic.List[string]
    if ($isFat) {
        $count = [int](Read-U32 $head 4 $littleEndian)
        if ($count -le 0 -or $count -gt 16) { return "bad-fat-count-$count" }
        for ($i = 0; $i -lt $count; $i++) {
            $offset = 8 + ($i * $fatEntrySize)
            if ($offset + 8 -gt $head.Length) { break }
            $cpuType = Read-U32 $head $offset $littleEndian
            $cpuSub = Read-U32 $head ($offset + 4) $littleEndian
            $archs.Add((ConvertFrom-CpuType $cpuType $cpuSub))
        }
    }
    else {
        $cpuType = Read-U32 $head 4 $littleEndian
        $cpuSub = Read-U32 $head 8 $littleEndian
        $archs.Add((ConvertFrom-CpuType $cpuType $cpuSub))
    }

    if ($archs.Count -eq 0) { return '' }
    return ($archs -join ',')
}

function Test-PngHasCgbi([byte[]]$slice) {
    if ($slice.Length -lt 16) { return $false }
    $signature = [byte[]](0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)
    for ($i = 0; $i -lt 8; $i++) {
        if ($slice[$i] -ne $signature[$i]) { return $false }
    }
    return [System.Text.Encoding]::ASCII.GetString($slice).Contains('CgBI')
}

# ── 重建 fixtures 目录 ────────────────────────────────────────────────────────
# 只删"本脚本生成过"的目录（含 expected.json 的子目录），保留手工/别的来源的目录，
# 例如 fixtures\readback\（真机 list --xml 原文 golden，R3 加）。
if (Test-Path -LiteralPath $OutDir) {
    Get-ChildItem -LiteralPath $OutDir -Directory | Where-Object {
        Test-Path -LiteralPath (Join-Path $_.FullName 'expected.json')
    } | ForEach-Object {
        Remove-Item -LiteralPath $_.FullName -Recurse -Force
    }
}
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

function Get-Sha256Hex([string]$path) {
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$ipaFiles = @(Get-ChildItem -LiteralPath $IpaDir -File | Where-Object { $_.Extension -eq '.ipa' } | Sort-Object Name)
if ($ipaFiles.Count -eq 0) {
    throw "IPA 目录里没有 .ipa 文件：$IpaDir"
}

$totalBytes = 0
$summary = New-Object System.Collections.Generic.List[string]

foreach ($ipa in $ipaFiles) {
    $slug = Get-FixtureSlug $ipa.Name
    $target = Join-Path $OutDir $slug
    New-Item -ItemType Directory -Path $target -Force | Out-Null

    $zip = [System.IO.Compression.ZipFile]::OpenRead($ipa.FullName)
    try {
        $infoEntry = $zip.Entries | Where-Object { $_.FullName -match '^Payload/[^/]+\.app/Info\.plist$' } | Select-Object -First 1
        if ($null -eq $infoEntry) {
            throw "$($ipa.Name)：包内没有 Payload/*.app/Info.plist"
        }

        $plistBytes = Read-ZipEntryBytes $infoEntry
        $appDirectory = ($infoEntry.FullName -split '/')[1]
        $dict = ConvertFrom-PlistBytes $plistBytes
        if ($null -eq $dict) {
            throw "$($ipa.Name)：Info.plist 解析结果不是字典"
        }

        $bundleId = Get-PlistString $dict 'CFBundleIdentifier'
        $shortVersion = Get-PlistString $dict 'CFBundleShortVersionString'
        $buildVersion = Get-PlistString $dict 'CFBundleVersion'
        $version = if ($shortVersion.Length -gt 0) { $shortVersion } else { $buildVersion }
        $displayName = Get-PlistString $dict 'CFBundleDisplayName'
        $bundleName = Get-PlistString $dict 'CFBundleName'
        $name = if ($displayName.Length -gt 0) { $displayName } elseif ($bundleName.Length -gt 0) { $bundleName } else { $appDirectory }
        $minOs = Get-PlistString $dict 'MinimumOSVersion'
        $executableName = Get-PlistString $dict 'CFBundleExecutable'

        # 1) Info.plist 原字节（保留原始格式：xml / bplist00）
        $plistPath = Join-Path $target 'Info.plist'
        [System.IO.File]::WriteAllBytes($plistPath, $plistBytes)
        $plistFormat = if ($plistBytes.Length -ge 6 -and [System.Text.Encoding]::ASCII.GetString($plistBytes, 0, 6) -eq 'bplist') { 'bplist' } else { 'xml' }

        # 2) 可执行文件前 4096 字节（按 CFBundleExecutable 找，不能按"最大文件"）
        $execHead = New-Object byte[] 0
        if ($executableName.Length -gt 0) {
            $execFullName = 'Payload/' + $appDirectory + '/' + $executableName
            $execEntry = $zip.Entries | Where-Object { $_.FullName -eq $execFullName } | Select-Object -First 1
            if ($null -eq $execEntry) {
                throw "$($ipa.Name)：按 CFBundleExecutable=$executableName 找不到 $execFullName"
            }
            $execHead = Read-ZipEntryBytes $execEntry 4096
        }
        $execPath = Join-Path $target 'executable-head.bin'
        [System.IO.File]::WriteAllBytes($execPath, $execHead)

        # 3) 任一 PNG 的前 512 字节（优先含 CgBI chunk 的；找不到就写空文件）
        $iconSlice = New-Object byte[] 0
        $hasCgbi = $false
        $pngEntries = @($zip.Entries | Where-Object { $_.FullName -match '^Payload/[^/]+\.app/.*\.png$' } | Sort-Object FullName)
        $fallbackIcon = $null
        foreach ($png in $pngEntries) {
            $slice = Read-ZipEntryBytes $png 512
            if (Test-PngHasCgbi $slice) {
                $iconSlice = $slice
                $hasCgbi = $true
                break
            }
            if ($null -eq $fallbackIcon) { $fallbackIcon = $slice }
        }
        if (-not $hasCgbi -and $null -ne $fallbackIcon) { $iconSlice = $fallbackIcon }
        $iconPath = Join-Path $target 'icon-slice.bin'
        [System.IO.File]::WriteAllBytes($iconPath, $iconSlice)

        # 4) expected.json
        #    arch / hasCgbi 是给 M3 留的，本轮只记录不断言；
        #    R3 起附带可追溯性字段：源 IPA 的 SHA256、每个 fixture 文件的 SHA256、Mach-O 魔数。
        #    sha256 让"换了素材但没更新期望"和"fixture 被手改"都会被测出来；
        #    本文件不写时间戳，保证重跑产物逐字节一致。
        $machoMagic = ''
        if ($execHead.Length -ge 4) {
            $machoMagic = '{0:X2}{1:X2}{2:X2}{3:X2}' -f $execHead[0], $execHead[1], $execHead[2], $execHead[3]
        }

        $expected = [ordered]@{
            bundleId          = $bundleId
            version           = $version
            name              = $name
            minOs             = $minOs
            appDirectory      = $appDirectory
            plistFormat       = $plistFormat
            executableName    = $executableName
            arch              = Get-ArchFromMachO $execHead
            archSource        = '脚本内独立的 PowerShell 实现（Read-U32/Get-ArchFromMachO）；M3 用生产解析器复核，本轮不断言'
            machoMagic        = $machoMagic
            hasCgbi           = $hasCgbi
            fileSize          = $ipa.Length
            sourceIpa         = $ipa.Name
            sourceIpaSha256   = Get-Sha256Hex $ipa.FullName
            generatedBy       = 'FixtureGeneration/extract_fixtures.ps1（R3：+sha256/来源指纹/machoMagic）'
            filesSha256       = [ordered]@{
                'Info.plist'          = Get-Sha256Hex $plistPath
                'executable-head.bin' = Get-Sha256Hex $execPath
                'icon-slice.bin'      = Get-Sha256Hex $iconPath
            }
        }
        $expectedPath = Join-Path $target 'expected.json'
        $json = $expected | ConvertTo-Json -Depth 4
        [System.IO.File]::WriteAllText($expectedPath, $json, $utf8NoBom)

        $dirSize = 0
        Get-ChildItem -LiteralPath $target -File | ForEach-Object { $dirSize += $_.Length }
        $totalBytes += $dirSize
        $summary.Add(('{0,-11} {1,-6} arch={2,-14} cgbi={3,-5} plist={4,5}B exec={5,4}B icon={6,3}B dir={7,6}B  {8}' -f `
            $slug, $plistFormat, $expected.arch, $hasCgbi, $plistBytes.Length, $execHead.Length, $iconSlice.Length, $dirSize, $ipa.Name))
    }
    finally {
        $zip.Dispose()
    }
}

$summary | ForEach-Object { Write-Output $_ }
Write-Output ('--- fixtures 总数 {0} 个目录，总大小 {1} 字节（{2:N1} KB）---' -f $ipaFiles.Count, $totalBytes, ($totalBytes / 1024))
if ($totalBytes -gt 200 * 1024) {
    throw "fixture 总大小 $totalBytes 字节超过 200 KB 上限，请缩小切片/检查 Info.plist。"
}
