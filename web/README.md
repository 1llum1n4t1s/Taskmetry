# Cloudflare 配信構成

`taskmetry.kagayoi.com` でランディングページと Velopack 更新ファイルを同居させます。

- R2 bucket: `taskmetry-updates`
- Custom Domain: `taskmetry.kagayoi.com`
- Worker: `taskmetry-landing`
- Zone: `kagayoi.com`
- Worker Route: `taskmetry.kagayoi.com/*`

Worker Route が `/`、`/privacy`、CSS、画像だけを `landing/` の静的アセットから返し、それ以外は R2 Custom Domain へ委譲します。Velopack の Range、ETag、Content-Type は R2 側の応答を維持します。

## 初回だけ必要な外部設定

1. Cloudflare アカウントで R2 bucket `taskmetry-updates` を作成する。
2. bucket の Custom Domain に `taskmetry.kagayoi.com` を接続する。
3. GitHub Actions secrets に次の 2 件を登録する。
   - `CLOUDFLARE_API_TOKEN`（Workers Scripts:Edit を含む最小権限 token）
   - `CLOUDFLARE_ACCOUNT_ID`
4. `Deploy Landing Page` workflow を手動実行し、Worker Route を作成する。

秘密値はリポジトリへ保存しません。ローカルの署名付き配信では `C:\Users\IMT\dev\Secret\secrets.json` の `cloudflare.api_token` だけを実行時に読みます。

## ローカル検証

```powershell
Push-Location web
pnpm dlx wrangler@4.112.0 deploy --dry-run
Pop-Location
```

本番デプロイや bucket/domain 作成は、この dry-run には含まれません。

