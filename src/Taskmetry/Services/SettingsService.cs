using System.Text.Json;
using Taskmetry.Models;

namespace Taskmetry.Services;

public enum SettingsLoadStatus
{
    Success,
    NotFound,
    Corrupt,
    IoError,
    AccessDenied,
}

public sealed record SettingsLoadResult(
    AppSettings Settings,
    SettingsLoadStatus Status,
    bool CanSave,
    bool RecoveryCopyCreated = false);

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _settingsPath;
    private bool _canSave = true;

    public SettingsService(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Taskmetry",
            "settings.json");
    }

    public AppSettings Current { get; private set; } = new();
    public SettingsLoadResult LastLoadResult { get; private set; } = new(
        new AppSettings(),
        SettingsLoadStatus.NotFound,
        CanSave: true);

    public event EventHandler<AppSettings>? SettingsChanged;

    public SettingsLoadResult Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return CompleteLoad(new AppSettings(), SettingsLoadStatus.NotFound, canSave: true);
            }

            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath), JsonOptions)
                ?? throw new JsonException("設定JSONのルートがnullです。");
            return CompleteLoad(settings, SettingsLoadStatus.Success, canSave: true);
        }
        catch (JsonException)
        {
            var recoveryCopyCreated = TryCreateRecoveryCopy();
            return CompleteLoad(
                new AppSettings(),
                SettingsLoadStatus.Corrupt,
                canSave: recoveryCopyCreated,
                recoveryCopyCreated);
        }
        catch (IOException)
        {
            return CompleteLoad(new AppSettings(), SettingsLoadStatus.IoError, canSave: false);
        }
        catch (UnauthorizedAccessException)
        {
            return CompleteLoad(new AppSettings(), SettingsLoadStatus.AccessDenied, canSave: false);
        }
        catch (System.Security.SecurityException)
        {
            return CompleteLoad(new AppSettings(), SettingsLoadStatus.AccessDenied, canSave: false);
        }
    }

    public void Save(AppSettings settings)
    {
        if (!_canSave)
        {
            throw new IOException("設定ファイルの読み込みに失敗したため、既存設定を保護して保存を停止しています。アプリを再起動してください。");
        }

        settings.Sanitize();
        var directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{_settingsPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(tempPath, _settingsPath, overwrite: true);
            Current = settings.Clone();
            LastLoadResult = new SettingsLoadResult(Current.Clone(), SettingsLoadStatus.Success, CanSave: true);
            SettingsChanged?.Invoke(this, Current);
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception ex) when (IsPersistenceException(ex))
            {
                // 保存結果を変えない後始末なので、次回の一時ファイル名で自然に回避する。
            }
        }
    }

    public static bool IsPersistenceException(Exception exception)
        => exception is IOException or UnauthorizedAccessException or System.Security.SecurityException;

    private SettingsLoadResult CompleteLoad(
        AppSettings settings,
        SettingsLoadStatus status,
        bool canSave,
        bool recoveryCopyCreated = false)
    {
        settings.Sanitize();
        Current = settings;
        _canSave = canSave;
        LastLoadResult = new SettingsLoadResult(Current.Clone(), status, canSave, recoveryCopyCreated);
        return LastLoadResult;
    }

    private bool TryCreateRecoveryCopy()
    {
        try
        {
            var recoveryPath = $"{_settingsPath}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
            File.Copy(_settingsPath, recoveryPath, overwrite: false);
            return true;
        }
        catch (Exception ex) when (IsPersistenceException(ex))
        {
            return false;
        }
    }
}
