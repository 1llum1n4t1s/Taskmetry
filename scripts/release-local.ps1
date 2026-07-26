# Taskmetry のローカル署名付き Velopack リリース。
# SimplySign Desktop で署名証明書を接続してから実行する。

[CmdletBinding()]
param(
    [switch]$SkipUpload
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$VpkVersion = '1.2.0'
$WranglerVersion = '4.112.0'
$Bucket = 'taskmetry-updates'
$BaseUrl = 'https://taskmetry.kagayoi.com'
$ZoneName = 'kagayoi.com'
$AccountId = '10901bfadbf1005164774a7350082985'
$SecretsPath = 'C:\Users\IMT\dev\Secret\secrets.json'
$CertificateSubject = 'Open Source Developer Yuichiro Shinozaki'
$SignParams = "/n `"$CertificateSubject`" /fd SHA256 /td SHA256 /tr http://time.certum.pl"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$WorkDirectory = Join-Path $RepoRoot 'local-release'
$PublishDirectory = Join-Path $WorkDirectory 'publish-win-x64'
$ArtifactsDirectory = Join-Path $WorkDirectory 'artifacts'
Set-Location $RepoRoot

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

    $zoneResponse = Invoke-RestMethod -Uri "https://api.cloudflare.com/client/v4/zones?name=$ZoneName" -Headers $cloudflareHeaders -TimeoutSec 30
    if (-not $zoneResponse.success -or @($zoneResponse.result).Count -eq 0) {
        throw "Cloudflare zone '$ZoneName' を確認できません。"
    }
    $zoneId = $zoneResponse.result[0].id

    $bucketResponse = Invoke-RestMethod -Uri "https://api.cloudflare.com/client/v4/accounts/$AccountId/r2/buckets" -Headers $cloudflareHeaders -TimeoutSec 30
    if (-not $bucketResponse.success -or -not @($bucketResponse.result.buckets | Where-Object name -eq $Bucket)) {
        throw "Cloudflare R2 bucket '$Bucket' がありません。先に web/README.md の初回設定を行ってください。"
    }
}

if (Test-Path -LiteralPath $WorkDirectory) {
    $resolvedWorkDirectory = [System.IO.Path]::GetFullPath($WorkDirectory)
    $resolvedRepoRoot = [System.IO.Path]::GetFullPath($RepoRoot)
    if (-not $resolvedWorkDirectory.StartsWith($resolvedRepoRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "作業ディレクトリがリポジトリ外です: $resolvedWorkDirectory"
    }
    Remove-Item -LiteralPath $resolvedWorkDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $ArtifactsDirectory -Force | Out-Null

Write-Host "== publish & pack: v$version ==" -ForegroundColor Cyan
Invoke-Native 'lock file の検証' { dotnet restore Taskmetry.slnx --locked-mode }
Invoke-Native 'win-x64 自己完結 publish' {
    dotnet publish src/Taskmetry/Taskmetry.csproj -c Release -r win-x64 --self-contained --no-restore -o $PublishDirectory
}
if (-not (Test-Path -LiteralPath (Join-Path $PublishDirectory 'Taskmetry.exe'))) {
    throw 'publish 出力に Taskmetry.exe がありません。'
}

Invoke-Native 'Velopack パッケージ作成と署名' {
    vpk pack `
        --packId Taskmetry `
        --packVersion $version `
        --packTitle 'Taskmetry' `
        --packAuthors 'ゆろち' `
        --mainExe Taskmetry.exe `
        --icon (Join-Path 'src' 'Taskmetry' 'icon' 'app.ico') `
        --packDir $PublishDirectory `
        --outputDir $ArtifactsDirectory `
        --channel win `
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
$payloads = @($files | Where-Object { $_.Name -notlike 'releases.*.json' })

# manifest を最後に置き換え、クライアントから未アップロード nupkg が見える時間を作らない。
foreach ($file in @($payloads + $manifests)) {
    Write-Host "  ↑ $($file.Name)"
    $cacheControl = if ($file.Name -like '*.nupkg' -or $file.Name -eq $versionedSetupName) {
        'public, max-age=31536000, immutable'
    } else {
        'no-cache, must-revalidate'
    }
    Invoke-Native "R2 upload: $($file.Name)" {
        pnpm dlx "wrangler@$WranglerVersion" r2 object put "$Bucket/$($file.Name)" `
            --file $file.FullName --cache-control $cacheControl --remote
    }
}

Write-Host '== 固定 URL の CDN キャッシュをパージ ==' -ForegroundColor Cyan
$purgeUrls = @($files |
    Where-Object { $_.Name -notlike '*.nupkg' -and $_.Name -ne $versionedSetupName } |
    ForEach-Object { "$BaseUrl/$($_.Name)" })
if ($purgeUrls.Count -gt 0) {
    $purgeBody = @{ files = $purgeUrls } | ConvertTo-Json -Compress
    $purgeResponse = Invoke-RestMethod -Method Post `
        -Uri "https://api.cloudflare.com/client/v4/zones/$zoneId/purge_cache" `
        -Headers $cloudflareHeaders `
        -ContentType 'application/json' `
        -Body $purgeBody `
        -TimeoutSec 30
    if (-not $purgeResponse.success) {
        throw 'Cloudflare CDN キャッシュのパージに失敗しました。'
    }
}

Write-Host '== 配信確認 ==' -ForegroundColor Cyan
foreach ($relativePath in @('releases.win.json', 'Taskmetry-win-Setup.exe', $versionedSetupName)) {
    $response = Invoke-WebRequest -Uri "$BaseUrl/$relativePath" -Method Head -TimeoutSec 30 -MaximumRetryCount 3 -RetryIntervalSec 5
    if ($response.StatusCode -ne 200) {
        throw "配信確認に失敗しました: $BaseUrl/$relativePath (HTTP $($response.StatusCode))"
    }
    Write-Host "  ✅ $relativePath"
}

Write-Host "🎉 Taskmetry v$version を $BaseUrl へ配信しました。" -ForegroundColor Green
