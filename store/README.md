# Microsoft Store 提出準備

Taskmetry は既存の署名済み Velopack `EXE` を使う Win32 アプリとして提出します。MSIX への変更や Partner Center 上の製品作成は、このリポジトリ設定には含めません。

## 提出前チェック

1. Partner Center で製品名 `Taskmetry` を予約し、製品 ID と公開者情報を確定する。
2. `scripts/release-local.ps1` で署名済みリリースを Cloudflare R2 へ配信する。
3. Store の Installer URL には固定名ではなく、不変のバージョン付き URL を指定する。
   - `https://taskmetry.kagayoi.com/Taskmetry-{version}-win-Setup.exe`
4. [ja-JP/listing.md](ja-JP/listing.md) の原稿を Partner Center へ転記する。
5. 実機で撮影した 16:9 スクリーンショットを最低 1 枚登録し、現在の Partner Center が示す画像要件を満たす。
6. Store logo には `src/Taskmetry/icon/app_icon.png` の 1024×1024 原本から生成した画像を使う。
7. プライバシー URL に `https://taskmetry.kagayoi.com/privacy` を指定する。
8. Installer parameters に `--silent` を指定する。Velopack 1.2.0 の Setup.exe が対応しているが、終了コードとアンインストール動作を提出対象の署名済み EXE で再確認する。

固定名 `Taskmetry-win-Setup.exe` は公式サイトの常時最新ダウンロード用です。Store 審査中に内容が変わらないよう、Store では必ずバージョン付き URL を使います。

## 未確定の外部情報

- Microsoft Store product ID / package identity
- Partner Center の予約名と公開者表示名
- Store 公開 URL
- 実機スクリーンショット

これらは Partner Center 上で確定した値だけを正本とし、推測値をリポジトリへ置きません。
