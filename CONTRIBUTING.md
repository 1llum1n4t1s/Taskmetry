# 開発ガイド

## 技術スタック

- .NET 10 / C#
- Avalonia 12
- CommunityToolkit.Mvvm
- xUnit v3
- Win32 API（タスクバー配置、CPU・メモリ計測）

Windows 専用です。画面状態は ViewModel、OS・ファイル操作は Services、表示は Views に分離しています。

## ビルドとテスト

```powershell
dotnet restore Taskmetry.slnx
dotnet build Taskmetry.slnx -c Debug --no-restore
dotnet test Taskmetry.slnx -c Debug --no-restore --no-build
```

Release 検証:

```powershell
dotnet build Taskmetry.slnx -c Release
dotnet test Taskmetry.slnx -c Release --no-build
```

## 実装上の注意

- タスクバーへ DLL を注入せず、`WS_EX_NOACTIVATE` のトップレベルウィンドウを空き領域へ配置します。
- `Shell_TrayWnd`、`ReBarWindow32`、`TrayNotifyWnd` の矩形を定期的に再取得し、Explorer 再起動やDPI変更へ追従します。
- セッション記録は末尾だけを読み、会話本文を保持・表示しません。
- 新しい CLI 記録形式へ対応するときは、実データ本文をテストへ含めず使用量フィールドだけを最小サンプル化します。
