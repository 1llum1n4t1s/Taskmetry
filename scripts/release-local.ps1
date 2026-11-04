# Taskmetry のローカル署名付き Velopack リリース。
# SimplySign Desktop で署名証明書を接続してから実行する。

[CmdletBinding()]
param(
    [switch]$SkipUpload,
    [string]$WorkDirectory,
    [string]$DotnetRoot
)


# R2 への書込みは同梱の PowerShell/.NET 実装で行う。
. (Join-Path $PSScriptRoot 'R2Distribution.Common.ps1')

function Send-ReleaseR2Object {
    param([Parameter(Mandatory)][System.IO.FileInfo]$File)
    # バージョン付き成果物は不変、固定 URL とメタデータは毎回再検証する。
    $immutable = $File.Extension -eq '.nupkg' -or $File.Name -match '\d+\.\d+\.\d+'
    $cacheControl = if ($immutable) { 'public, max-age=31536000, immutable' } else { 'no-cache, must-revalidate' }
    $contentType = switch ($File.Extension.ToLowerInvariant()) {
        '.json' { 'application/json'; break }
        '.exe' { 'application/vnd.microsoft.portable-executable'; break }
        '.zip' { 'application/zip'; break }
        default { if ($File.Name -like 'RELEASES*') { 'text/plain; charset=utf-8' } else { 'application/octet-stream' } }
    }
    $null = Publish-R2Object -AccountId $AccountId -Bucket $Bucket -FilePath $File.FullName `
        -ApiToken $env:CLOUDFLARE_API_TOKEN -ContentType $contentType -CacheControl $cacheControl -Immutable:$immutable
}

# 通常の公開URLを照合し、実証された欠損・古い内容だけをパージする。
function Assert-PublicReleaseFile {
    param([Parameter(Mandatory)][System.IO.FileInfo]$File)
    $url = "$BaseUrl/$([Uri]::EscapeDataString($File.Name))"
    $expectedHash = (Get-FileHash -LiteralPath $File.FullName -Algorithm SHA256).Hash
    for ($attempt = 0; $attempt -lt 2; $attempt++) {
        $response = Invoke-WebRequest -Uri $url -Headers @{ 'Accept-Encoding' = 'identity' } -SkipHttpErrorCheck -TimeoutSec 180
        if ($response.StatusCode -eq 200) {
            $stream = $response.RawContentStream
            if ($stream.Length -eq $File.Length -and (Get-R2DistributionHash $stream) -eq $expectedHash) {
                Write-Host "  ✅ 公開バイト一致: $($File.Name)"
                return
            }
        } elseif ($response.StatusCode -ne 404) {
            throw "公開GETに失敗しました: $url (HTTP $($response.StatusCode))"
        }
        if ($attempt -eq 1) { throw "公開バイトが一致しません: $url (HTTP $($response.StatusCode))" }
        $purge = Invoke-RestMethod -Method Post -Uri "https://api.cloudflare.com/client/v4/zones/$zoneId/purge_cache" `
            -Headers $cloudflareHeaders -ContentType 'application/json' -Body (@{ files = @($url) } | ConvertTo-Json -Compress) -TimeoutSec 30 -MaximumRedirection 0 -ErrorAction Stop
        if (-not $purge.success) { throw "Cloudflare CDN キャッシュのパージに失敗しました: $url" }
    }
}

function Assert-ReleaseBucket {
    $bucketResponse = Invoke-RestMethod -Uri "https://api.cloudflare.com/client/v4/accounts/$AccountId/r2/buckets/$([Uri]::EscapeDataString($Bucket))" `
        -Headers $cloudflareHeaders -TimeoutSec 30 -MaximumRedirection 0 -ErrorAction Stop
    if (-not $bucketResponse.success -or $bucketResponse.result.name -cne $Bucket) {
        throw "Cloudflare R2 bucket '$Bucket' を確認できません。既存の bucket と接続を確認してください。"
    }
}

function New-ReleaseBuildProps {
    param([Parameter(Mandatory)][string]$RepoRoot, [Parameter(Mandatory)][string]$WorkDirectory)
    $buildDirectory = Join-Path $WorkDirectory 'build'
    $null = New-Item -ItemType Directory -Path $buildDirectory
    $originalProps = [Security.SecurityElement]::Escape((Join-Path $RepoRoot 'Directory.Build.props'))
    $buildRoot = [Security.SecurityElement]::Escape($buildDirectory.Replace('\', '/'))
    $wrapper = Join-Path $buildDirectory 'Directory.Build.props'
    # MSBuild のプロパティ参照は PowerShell で展開しない。
    $xml = '<Project><Import Project="' + $originalProps + '" /><PropertyGroup>' +
        '<BaseOutputPath>' + $buildRoot + '/bin/$(MSBuildProjectName)/</BaseOutputPath>' +
        '<OutputPath>$(BaseOutputPath)$(Configuration)/</OutputPath>' +
        '<BaseIntermediateOutputPath>' + $buildRoot + '/obj/$(MSBuildProjectName)/</BaseIntermediateOutputPath>' +
        '<IntermediateOutputPath>$(BaseIntermediateOutputPath)$(Configuration)/</IntermediateOutputPath>' +
        '<MSBuildProjectExtensionsPath>$(BaseIntermediateOutputPath)</MSBuildProjectExtensionsPath>' +
        '<DefaultItemExcludes>$(DefaultItemExcludes);$(MSBuildProjectDirectory)/bin/**;$(MSBuildProjectDirectory)/obj/**</DefaultItemExcludes>' +
        '<UseSharedCompilation>false</UseSharedCompilation></PropertyGroup></Project>'
    Set-Content -LiteralPath $wrapper -Value $xml -Encoding utf8NoBOM
    return $wrapper
}

function Assert-ReleaseSdk {
    param([Parameter(Mandatory)][string]$RepoRoot)
    $sdkVersion = (& dotnet --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw '選択した dotnet SDK の確認に失敗しました。' }
    $sdkEntry = @(& dotnet --list-sdks) | Where-Object { $_ -match ('^' + [regex]::Escape($sdkVersion) + ' \[') } | Select-Object -First 1
    if ($LASTEXITCODE -ne 0 -or -not $sdkEntry -or $sdkEntry -notmatch '\[(.+)\]$') { throw '選択した SDK の配置先を確認できません。' }
    $sdkProps = Join-Path $Matches[1] "$sdkVersion/Microsoft.NETCoreSdk.BundledVersions.props"
    $sdkXml = [xml](Get-Content -LiteralPath $sdkProps -Raw)
    $compiler = $sdkXml.SelectSingleNode('//KnownILCompilerPack[@TargetFramework="net10.0"]')
    $compilerVersion = if ($compiler) { $compiler.ILCompilerPackVersion } else { '不明' }
    $lock = Get-Content -LiteralPath (Join-Path $RepoRoot 'src/Taskmetry/packages.lock.json') -Raw | ConvertFrom-Json
    $lockedCompilers = @($lock.dependencies.PSObject.Properties | ForEach-Object {
        $_.Value.PSObject.Properties | Where-Object Name -eq 'Microsoft.DotNet.ILCompiler' | ForEach-Object { $_.Value.resolved }
    } | Sort-Object -Unique)
    if (-not $compiler -or $lockedCompilers.Count -ne 1 -or $compiler.ILCompilerPackVersion -ne $lockedCompilers[0]) {
        throw "SDK $sdkVersion の NativeAOT compiler ($compilerVersion) と lock ($($lockedCompilers -join ', ')) が不一致です。lock に対応した SDK を用意し、-DotnetRoot で配置先を指定してください。"
    }
    Write-Host "SDK $sdkVersion / NativeAOT $($lockedCompilers[0])"
}

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$VpkVersion = '1.2.161'
$Bucket = 'taskmetry-updates'
$BaseUrl = 'https://taskmetry.kagayoi.com'
$ZoneName = 'kagayoi.com'
$AccountId = '10901bfadbf1005164774a7350082985'
$SecretsPath = 'D:\Dev\Secret\secrets.json'
$CertificateSubject = 'Open Source Developer Yuichiro Shinozaki'
$SignParams = "/n `"$CertificateSubject`" /fd SHA256 /td SHA256 /tr http://time.certum.pl"

$RepoRoot = Split-Path -Parent $PSScriptRoot
if ($DotnetRoot) {
    if (-not [IO.Path]::IsPathFullyQualified($DotnetRoot)) { throw 'DotnetRoot は絶対パスで指定してください。' }
    $DotnetRoot = [IO.Path]::GetFullPath($DotnetRoot)
    if (-not (Test-Path -LiteralPath (Join-Path $DotnetRoot 'dotnet.exe') -PathType Leaf)) { throw 'DotnetRoot に dotnet.exe がありません。' }
    $env:DOTNET_ROOT = $DotnetRoot
    $env:PATH = $DotnetRoot + [IO.Path]::PathSeparator + $env:PATH
}
if (-not $WorkDirectory) { $WorkDirectory = Join-Path ([IO.Path]::GetTempPath()) ('Taskmetry-release-' + [guid]::NewGuid().ToString('N')) }
if (-not [IO.Path]::IsPathFullyQualified($WorkDirectory)) { throw 'WorkDirectory は絶対パスで指定してください。' }
$WorkDirectory = [IO.Path]::GetFullPath($WorkDirectory)
$workAncestor = $WorkDirectory
while ($workAncestor) {
    if ((Test-Path -LiteralPath $workAncestor) -and ((Get-Item -LiteralPath $workAncestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "作業パスに reparse point があります: $workAncestor" }
    $workAncestor = Split-Path $workAncestor -Parent
}
$PublishDirectory = Join-Path $WorkDirectory 'publish-win-x64'
$ArtifactsDirectory = Join-Path $WorkDirectory 'artifacts'
Set-Location $RepoRoot
Assert-ReleaseSdk -RepoRoot $RepoRoot

function Invoke-Native {
    param(
        [Parameter(Mandatory)] [string]$Description,
        [Parameter(Mandatory)] [scriptblock]$Command
    )

    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Description が失敗しました (exit $LASTEXITCODE)"
    }
}

Write-Host '== プリフライト ==' -ForegroundColor Cyan
$versionNode = ([xml](Get-Content -LiteralPath 'Directory.Build.props' -Raw)).SelectSingleNode('/Project/PropertyGroup/Version')
$version = if ($versionNode) { $versionNode.InnerText.Trim() } else { $null }
if (-not $version) {
    throw 'Directory.Build.props から Version を取得できません。'
}

$certificate = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -like "CN=$CertificateSubject*" -and $_.NotAfter -gt (Get-Date) } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1
if (-not $certificate) {
    throw "署名証明書 (CN=$CertificateSubject) が見つかりません。SimplySign Desktop を確認してください。"
}

$installedVpk = dotnet tool list --global |
    Select-String -Pattern '^vpk\s+' |
    ForEach-Object { ($_.Line -split '\s+')[1] } |
    Select-Object -First 1
if ($installedVpk -ne $VpkVersion) {
    if ($installedVpk) {
        Invoke-Native 'vpk の更新' { dotnet tool update --global vpk --version $VpkVersion }
    } else {
        Invoke-Native 'vpk のインストール' { dotnet tool install --global vpk --version $VpkVersion }
    }
}

$cloudflareHeaders = $null
$zoneId = $null
if (-not $SkipUpload) {
    if (-not (Test-Path -LiteralPath $SecretsPath)) {
        throw "Cloudflare secrets が見つかりません: $SecretsPath"
    }
    $secrets = Get-Content -LiteralPath $SecretsPath -Raw | ConvertFrom-Json
    if (-not $secrets.cloudflare.api_token) {
        throw 'secrets.json に cloudflare.api_token がありません。'
    }
    $env:CLOUDFLARE_API_TOKEN = $secrets.cloudflare.api_token
    $env:CLOUDFLARE_ACCOUNT_ID = $AccountId
    $cloudflareHeaders = @{ Authorization = "Bearer $($env:CLOUDFLARE_API_TOKEN)" }

    $zoneResponse = Invoke-RestMethod -Uri "https://api.cloudflare.com/client/v4/zones?name=$ZoneName" -Headers $cloudflareHeaders -TimeoutSec 30 -MaximumRedirection 0 -ErrorAction Stop
    if (-not $zoneResponse.success -or @($zoneResponse.result).Count -eq 0) {
        throw "Cloudflare zone '$ZoneName' を確認できません。"
    }
    $zoneId = $zoneResponse.result[0].id

    Assert-ReleaseBucket
}

if (Test-Path -LiteralPath $WorkDirectory) { throw "既存の作業ディレクトリは保持します。未使用の -WorkDirectory を指定してください: $WorkDirectory" }
New-Item -ItemType Directory -Path $ArtifactsDirectory -Force | Out-Null
# 新規作業先を確保できたことを確認し、子プロセスの一時出力も分離する。
$env:TEMP = $WorkDirectory
$env:TMP = $WorkDirectory
$buildPropsPath = New-ReleaseBuildProps -RepoRoot $RepoRoot -WorkDirectory $WorkDirectory
$buildPropsArgument = "-p:DirectoryBuildPropsPath=$buildPropsPath"

Write-Host "== publish & pack: v$version ==" -ForegroundColor Cyan
Invoke-Native 'lock file の検証' { dotnet restore Taskmetry.slnx --locked-mode -p:OS=Windows_NT $buildPropsArgument }
Invoke-Native 'win-x64 自己完結 publish' {
    dotnet publish src/Taskmetry/Taskmetry.csproj -c Release -r win-x64 --self-contained --no-restore -p:OS=Windows_NT $buildPropsArgument -o $PublishDirectory
}
if (-not (Test-Path -LiteralPath (Join-Path $PublishDirectory 'Taskmetry.exe'))) {
    throw 'publish 出力に Taskmetry.exe がありません。'
}

Invoke-Native 'Velopack パッケージ作成と署名' {
    vpk pack `
        --packId Taskmetry `
        --packVersion $version `
        --packTitle 'Taskmetry' `
        --packAuthors 'Kagayoi' `
        --mainExe Taskmetry.exe `
        --icon (Join-Path 'src' 'Taskmetry' 'icon' 'app.ico') `
        --packDir $PublishDirectory `
        --outputDir $ArtifactsDirectory `
        --channel win `
        --runtime win-x64 `
        --shortcuts StartMenuRoot `
        --signParams $SignParams
}

$setupPath = Join-Path $ArtifactsDirectory 'Taskmetry-win-Setup.exe'
if (-not (Test-Path -LiteralPath $setupPath)) {
    throw 'Velopack の Setup.exe が見つかりません。'
}
$versionedSetupName = "Taskmetry-$version-win-Setup.exe"
Copy-Item -LiteralPath $setupPath -Destination (Join-Path $ArtifactsDirectory $versionedSetupName)

Write-Host '== Authenticode 署名検証 ==' -ForegroundColor Cyan
foreach ($executable in Get-ChildItem -LiteralPath $ArtifactsDirectory -Filter '*.exe' -File) {
    $signature = Get-AuthenticodeSignature -LiteralPath $executable.FullName
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike "CN=$CertificateSubject*") {
        throw "署名検証に失敗しました: $($executable.Name) ($($signature.Status))"
    }
    Write-Host "  ✅ $($executable.Name)"
}

if ($SkipUpload) {
    Write-Host "✅ 署名済み成果物を作成しました: $ArtifactsDirectory" -ForegroundColor Green
    return
}

Write-Host '== Cloudflare R2 へアップロード ==' -ForegroundColor Cyan
$files = @(Get-ChildItem -LiteralPath $ArtifactsDirectory -File)
$manifests = @($files | Where-Object { $_.Name -like 'releases.*.json' })
if ($manifests.Count -eq 0) {
    throw 'Velopack manifest (releases.*.json) がありません。'
}
$metadata = @($files | Where-Object { $_.Name -like 'releases.*.json' -or $_.Name -like 'assets.*.json' -or $_.Name -like 'RELEASES*' } | Sort-Object @{ Expression = { if ($_.Name -like 'releases.*.json') { 3 } elseif ($_.Name -like 'RELEASES*') { 2 } elseif ($_.Name -like 'assets.*.json') { 1 } else { 0 } } }, Name)
$payloads = @($files | Where-Object { $_ -notin $metadata })

# manifest を最後に置き換え、クライアントから未アップロード nupkg が見える時間を作らない。
foreach ($file in $payloads) {
    Write-Host "  ↑ $($file.Name)"
    Send-ReleaseR2Object -File $file
}
Write-Host '== manifest 公開前に配布物の公開バイトを照合 ==' -ForegroundColor Cyan
foreach ($file in $payloads) { Assert-PublicReleaseFile -File $file }
foreach ($file in $metadata) {
    Write-Host "  ↑ $($file.Name)"
    Send-ReleaseR2Object -File $file
    Assert-PublicReleaseFile -File $file
}

Write-Host "🎉 Taskmetry v$version を $BaseUrl へ配信しました。" -ForegroundColor Green
