using VelopackUpdateDialog;

namespace Taskmetry.Services;

/// <summary>自動更新ダイアログを Taskmetry の日本語 UI に合わせる。</summary>
internal sealed class JapaneseUpdateDialogStrings : IUpdateDialogStrings
{
    public static JapaneseUpdateDialogStrings Instance { get; } = new();

    private JapaneseUpdateDialogStrings()
    {
    }

    public string Title => "更新の確認";
    public string AvailableHeader => "新しいバージョンがあります";
    public string DownloadAndInstall => "ダウンロードしてインストール";
    public string IgnoreThisVersion => "このバージョンを無視";
    public string UpToDateMessage => "現在のバージョンが最新版です。";
    public string ErrorHeader => "更新に失敗しました";
    public string Close => "閉じる";
    public string CheckingMessage => "更新を確認中…";
}
