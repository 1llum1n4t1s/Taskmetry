# Taskmetry

![Taskmetry](src/Taskmetry/icon/app_icon.png)

Taskmetry は、Windows のタスクバーに CPU・メモリ・AI CLI のコンテキスト使用状況を常時表示する軽量メーターです。

紹介用のランディングページは [landing/index.html](landing/index.html) にあります。

中央のタスクアイコン群と通知領域の間にある実際の空きスペースを検出し、既存アイコンを覆わない幅へ自動調整します。空きが 260px 未満ならタスクバー外側へ退避し、Explorer が再起動した場合も再検出して追従します。

タスクバーの上・下・左・右を自動判定し、向きに合わせて横型／縦型メーターを切り替えます。通常はクリック透過の「固定表示モード」で誤操作を防ぎ、設定から「レイアウト編集モード」にした場合だけドラッグできます。

## 表示できる情報

| 項目 | 内容 |
|---|---|
| CPU | システム全体の CPU 使用率 |
| RAM | 物理メモリの使用率と使用量 |
| Codex | 直近セッションのコンテキスト使用率。ツールチップに利用枠も表示 |
| Claude | 直近セッションのコンテキスト使用率 |
| Gemini | 直近セッションのコンテキスト使用率 |

AI の値は各 CLI がローカル保存したセッション記録から読み取ります。

- Codex: `%USERPROFILE%\.codex\sessions\**\*.jsonl`
- Claude Code: `%USERPROFILE%\.claude\projects\**\*.jsonl`
- Gemini CLI: `%USERPROFILE%\.gemini\tmp\<project_hash>\chats\session-*`

API キー、認証情報、会話本文は外部へ送信しません。Taskmetry 自体はネットワーク通信を行いません。

## 使い方

1. Taskmetry を起動します。
2. 初回に開く設定画面で、表示項目・幅・更新間隔を選びます。
3. 通知領域の Taskmetry アイコンから、いつでも設定画面を開けます。
4. 位置を変える場合は「レイアウト編集モード」を ON にしてレールをドラッグし、調整後に OFF へ戻します。

Claude と Gemini はモデルやプランによってコンテキスト上限が異なるため、設定画面で上限を調整できます。CLI をまだ使っていない場合や記録が存在しない場合は `—` と表示します。

## 動作環境

- Windows 10 以降
- x64
- ソースから実行する場合は .NET 10 SDK

現時点ではメインタスクバーを対象にしています。将来の Windows 11 で上下左右配置が提供された場合も、タスクバー矩形とモニター端の関係から追従する設計です。

## ソースから起動

```powershell
dotnet run --project src/Taskmetry/Taskmetry.csproj
```

配布用の自己完結版を作る場合:

```powershell
dotnet publish src/Taskmetry/Taskmetry.csproj -c Release -r win-x64 --self-contained -o publish
```

詳しい開発コマンドは [CONTRIBUTING.md](CONTRIBUTING.md) を参照してください。

## ライセンス

[MIT License](LICENSE)
