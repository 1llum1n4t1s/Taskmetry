# Taskmetry — ja-JP ストア原稿

## 製品名

Taskmetry

## 短い説明

CPU・メモリとAIサービスの公式使用枠を、Windows タスクバーの空きスペースへ見やすく常時表示します。

## 説明

Taskmetry は、作業中の状態を別ウィンドウへ切り替えずに確認できる Windows タスクバー常駐メーターです。

CPU と物理メモリの使用率に加え、CodexはOpenAIのWeb認証と公式APIから、Claudeはユーザーが手動入力したSession Tokenを使ってclaude.aiの公式Web応答から利用枠を取得し、新幹線の速度計を思わせる大きな数値と段階色で表示します。Geminiは個人向け公式APIが提供されるまで、推測せず取得不能理由を表示します。

中央のピン留めアプリと通知領域の間にある空きスペースを検出し、利用可能な幅へ自動調整します。空きが足りない場合はタスクバーの外側へ退避するため、既存アイコンの操作を妨げません。タスクバーが上・下・左・右のどこに置かれても、辺を判定して横型または縦型へ切り替える設計です。

通常はクリックを透過する固定表示モードで、誤操作による移動を防ぎます。配置を変えたいときだけレイアウト編集モードへ切り替えてドラッグできます。

ローカルの会話、セッションログ、CLI出力は読み取りません。Codex接続時はOpenAI公式の認証画面と使用率APIへ通信し、認証トークンはCodexが管理します。ClaudeのSession Tokenは接続確認後にWindows資格情報マネージャーへ保存し、claude.aiへの使用率取得だけに送信します。CPU・メモリの計測値、会話本文は外部へ送信しません。

## 主な機能

- CPU とメモリのリアルタイム表示
- Codex公式利用枠とClaude公式Web使用率の表示、Geminiの公式API提供状況表示
- ピン留めアプリ数に合わせた幅の自動調整
- タスクバー上下左右への追従
- 固定表示モードとレイアウト編集モード
- Explorer 再起動、DPI、モニター構成変更後の再配置
- 署名付き自動更新

## カテゴリ候補

ユーティリティ & ツール

## 検索語候補

- タスクバー
- CPU メーター
- メモリ使用率
- Codex
- Claude Code
- Codex App Server
- システムモニター

## URL

- Web サイト: `https://taskmetry.kagayoi.com/`
- サポート: `https://github.com/1llum1n4t1s/Taskmetry/issues`
- プライバシー: `https://taskmetry.kagayoi.com/privacy`

## システム要件

- Windows 10 バージョン 2004（build 19041）以降
- x64 プロセッサ
- Codex使用率を表示する場合は、Codex App Serverを利用できるCodexのインストール

## リリースノート原稿

Taskmetry の Microsoft Store 初回公開です。CPU・メモリとCodexの公式使用枠を、タスクバーの空き領域へ安全に表示できます。
