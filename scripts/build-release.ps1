param([ValidateSet('jellyfin10', 'jellyfin12', 'emby', 'all')][string]$Platform = 'all')
$ErrorActionPreference = 'Stop'
$amaneRoot = Split-Path $PSScriptRoot -Parent
$amaneTargets = @{
    jellyfin10 = @{ Project = 'src/Amane.Jellyfin/Jellyfin.Plugin.Amane.csproj'; Output = 'src/Amane.Jellyfin/bin/Release/net9.0'; Dll = 'Jellyfin.Plugin.Amane.dll'; Zip = 'Jellyfin.Plugin.Amane.zip' }
    jellyfin12 = @{ Project = 'src/Amane.Jellyfin12/Amane.Jellyfin12.csproj'; Output = 'src/Amane.Jellyfin12/bin/Release/net10.0'; Dll = 'Jellyfin.Plugin.Amane.dll'; Zip = 'Jellyfin.Plugin.Amane.12.zip' }
    emby = @{ Project = 'src/Amane.Emby/Amane.Emby.csproj'; Output = 'src/Amane.Emby/bin/Release/net8.0'; Dll = 'Amane.Emby.dll'; Zip = 'Amane.Emby.zip' }
}
New-Item -ItemType Directory -Path (Join-Path $amaneRoot 'dist') -Force | Out-Null
$amaneSelected = if ($Platform -eq 'all') { @('jellyfin10', 'jellyfin12', 'emby') } else { @($Platform) }
foreach ($amaneName in $amaneSelected) {
    $amaneTarget = $amaneTargets[$amaneName]
    dotnet build (Join-Path $amaneRoot $amaneTarget.Project) -c Release
    if ($LASTEXITCODE -ne 0) { throw "构建失败: $amaneName" }
    $amaneOutput = Join-Path $amaneRoot $amaneTarget.Output
    $amaneFiles = @((Join-Path $amaneOutput $amaneTarget.Dll))
    foreach ($amaneFile in $amaneFiles) { if (!(Test-Path -LiteralPath $amaneFile)) { throw "缺少插件程序集: $amaneFile" } }
    $amaneZip = Join-Path $amaneRoot ('dist/' + $amaneTarget.Zip)
    Compress-Archive -LiteralPath $amaneFiles -DestinationPath $amaneZip -Force
    $amaneArchive = [System.IO.Compression.ZipFile]::OpenRead($amaneZip)
    try {
        $amaneEntries = @($amaneArchive.Entries | ForEach-Object { $_.FullName })
        if ($amaneEntries.Count -ne 1 -or $amaneEntries[0] -ne $amaneTarget.Dll) { throw "打包内容不正确: $amaneZip" }
    } finally { $amaneArchive.Dispose() }
    $amaneChecksum = (Get-FileHash -LiteralPath $amaneZip -Algorithm MD5).Hash.ToLowerInvariant()
    Set-Content -LiteralPath ($amaneZip + '.md5') -Value $amaneChecksum
    Write-Output "$amaneZip md5=$amaneChecksum"
}
