using System.Globalization;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Waigoma.ClipboardHistory.Capture;

namespace Waigoma.ClipboardHistory.Extension;

// The settings file lives beside the log, never under the data root, so moving the data root cannot lose the
// settings that point at it. Current is rebuilt only when a setting changes, so readers never parse text.
internal sealed class SettingsManager : JsonSettingsManager
{
    private const long MegaByte = 1024L * 1024;

    private const int DefaultRetentionDays = 90;
    private const int DefaultSequenceSeconds = 60;
    private const int DefaultMaxFormatMb = 50;
    private const int DefaultMaxTotalMb = 2048;
    private const int MinMaxFormatMb = 1;
    private const int MaxMaxFormatMb = 512;
    private const int MinMaxTotalMb = 10;
    private const int MaxMaxTotalMb = 102400;

    private const string CopyChoice = "copy";
    private const string PasteChoice = "paste";

    private readonly string _localFolder;

    private readonly ChoiceSetSetting _retention = new(
        "retention",
        "保存期間",
        "最終コピー日時がこの期間を過ぎた項目を消します。ピン留めは消しません。",
        [
            new ChoiceSetSetting.Choice("1", "1 日"),
            new ChoiceSetSetting.Choice("7", "1 週"),
            new ChoiceSetSetting.Choice("30", "1 か月"),
            new ChoiceSetSetting.Choice("90", "3 か月"),
            new ChoiceSetSetting.Choice("180", "6 か月"),
            new ChoiceSetSetting.Choice("365", "1 年"),
            new ChoiceSetSetting.Choice("0", "無期限"),
        ]);

    private readonly ChoiceSetSetting _primaryAction = new(
        "primaryAction",
        "主操作",
        "Enter で行う操作です。もう一方は Ctrl+Enter になります。",
        [
            new ChoiceSetSetting.Choice(PasteChoice, "貼り付け"),
            new ChoiceSetSetting.Choice(CopyChoice, "コピー"),
        ]);

    private readonly ToggleSetting _preferPlainText = new(
        "preferPlainText",
        "プレーンテキストを優先",
        "書式付きの内容でも、テキストだけを貼り付けます。",
        false);

    private readonly ToggleSetting _moveToTop = new(
        "moveToTop",
        "操作後に履歴の先頭へ移動",
        "貼り付けとコピーの後、その項目を一覧の先頭へ移します。",
        true);

    private readonly ToggleSetting _ocr = new(
        "ocr",
        "画像の文字認識",
        "画像を記録したあとに文字を読み取ります。",
        true);

    private readonly TextSetting _excludedApps = new(
        "excludedApps",
        "除外アプリ",
        "記録しないアプリの実行ファイル名を 1 行に 1 つ書きます (例: keepass.exe)。",
        string.Empty)
    {
        Multiline = true,
    };

    private readonly ChoiceSetSetting _sequenceTimeout = new(
        "sequenceTimeout",
        "連続貼り付けの制限時間",
        "この時間を過ぎると、次の連続貼り付けは最新から始まります。",
        [
            new ChoiceSetSetting.Choice("30", "30 秒"),
            new ChoiceSetSetting.Choice("60", "1 分"),
            new ChoiceSetSetting.Choice("180", "3 分"),
        ]);

    private readonly ToggleSetting _sequenceNewline = new(
        "sequenceNewline",
        "連続貼り付けで改行を付ける",
        "テキストの後に改行を付けて貼り付けます。",
        true);

    private readonly TextSetting _maxFormatMb = new(
        "maxFormatMb",
        "1 形式の上限 (MB)",
        "1 つの形式の大きさの上限です。1 から 512 の整数で指定します。",
        DefaultMaxFormatMb.ToString(CultureInfo.InvariantCulture));

    private readonly TextSetting _maxTotalMb = new(
        "maxTotalMb",
        "全体の上限 (MB)",
        "履歴全体の大きさの上限です。10 から 102400 の整数で指定します。",
        DefaultMaxTotalMb.ToString(CultureInfo.InvariantCulture));

    private readonly TextSetting _dataRoot = new(
        "dataRoot",
        "保存先",
        "履歴を保存するフォルダーのパスです。空のときは既定の場所を使います。変更は CmdPal を再起動したあとに有効になり、既存の履歴は移動しません。既定の保存先は拡張の削除で消えます。",
        string.Empty);

    private HistoryOptions _current = new();

    public SettingsManager(string localFolder)
    {
        _localFolder = localFolder;
        FilePath = Path.Combine(localFolder, "settings.json");

        _retention.Value = DefaultRetentionDays.ToString(CultureInfo.InvariantCulture);
        _primaryAction.Value = PasteChoice;
        _sequenceTimeout.Value = DefaultSequenceSeconds.ToString(CultureInfo.InvariantCulture);

        Settings.Add(_retention);
        Settings.Add(_primaryAction);
        Settings.Add(_preferPlainText);
        Settings.Add(_moveToTop);
        Settings.Add(_ocr);
        Settings.Add(_excludedApps);
        Settings.Add(_sequenceTimeout);
        Settings.Add(_sequenceNewline);
        Settings.Add(_maxFormatMb);
        Settings.Add(_maxTotalMb);
        Settings.Add(_dataRoot);

        try
        {
            LoadSettings();
        }
        catch (Exception ex)
        {
            // A damaged file must not stop the palette; the defaults apply until the user saves again.
            Log.Error("SettingsManager.Load", ex);
        }

        _current = BuildOptions();
        DataRoot = ResolveDataRoot(_dataRoot.Value);

        Settings.SettingsChanged += (_, _) => OnSettingsChanged();
    }

    // Immutable snapshot, so a caller can hold it for the duration of one operation.
    public HistoryOptions Current => Volatile.Read(ref _current);

    // Resolved once at startup: the store opened from this root is the one the worker and DIB writer must use.
    public string DataRoot { get; }

    public event Action? Changed;

    // Adds an executable name to the excluded list. Returns true when the name is in the saved list afterwards, which
    // includes the case where it was already there. Never throws: a failure is logged and reported as false.
    public bool AddExcludedApp(string exe)
    {
        try
        {
            string name = Path.GetFileName((exe ?? string.Empty).Trim().Trim('"')).ToLowerInvariant();
            if (name.Length == 0)
            {
                return false;
            }

            string current = _excludedApps.Value ?? string.Empty;
            if (CaptureFilter.ParseExcludedApps(current).Contains(name))
            {
                return true;
            }

            string next = current.Length == 0 || current.EndsWith('\n')
                ? current + name
                : current + "\n" + name;
            _excludedApps.Value = next;
            SaveAndApply();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("SettingsManager.AddExcludedApp", ex);
            return false;
        }
    }

    private void OnSettingsChanged()
    {
        try
        {
            SaveAndApply();
        }
        catch (Exception ex)
        {
            Log.Error("SettingsManager.SettingsChanged", ex);
        }
    }

    private void SaveAndApply()
    {
        SaveSettings();
        Volatile.Write(ref _current, BuildOptions());
        Changed?.Invoke();
    }

    private HistoryOptions BuildOptions() => new()
    {
        Retention = ParseRetention(_retention.Value),
        PasteIsPrimary = _primaryAction.Value != CopyChoice,
        PreferPlainText = _preferPlainText.Value,
        MoveToTop = _moveToTop.Value,
        Ocr = _ocr.Value,
        ExcludedApps = CaptureFilter.ParseExcludedApps(_excludedApps.Value),
        SequenceTimeout = TimeSpan.FromSeconds(ParseInt(_sequenceTimeout.Value, DefaultSequenceSeconds, 1, int.MaxValue)),
        SequenceNewline = _sequenceNewline.Value,
        MaxFormatBytes = ParseInt(_maxFormatMb.Value, DefaultMaxFormatMb, MinMaxFormatMb, MaxMaxFormatMb) * MegaByte,
        MaxTotalBytes = ParseInt(_maxTotalMb.Value, DefaultMaxTotalMb, MinMaxTotalMb, MaxMaxTotalMb) * MegaByte,
    };

    // Zero means "keep forever", which the spec shows as 無期限. An unknown value keeps the default.
    private static TimeSpan? ParseRetention(string? value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int days))
        {
            return TimeSpan.FromDays(DefaultRetentionDays);
        }

        return days == 0 ? null : TimeSpan.FromDays(days);
    }

    // Text that does not parse falls back to the default; numbers outside the range are clamped to it.
    private static long ParseInt(string? text, int fallback, int min, int max)
    {
        if (!int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            value = fallback;
        }

        return Math.Clamp(value, min, max);
    }

    // The default root sits beside the log, so a fresh install never writes outside the package.
    private string ResolveDataRoot(string? configured)
    {
        string fallback = Path.Combine(_localFolder, "data");
        string requested = configured?.Trim() ?? string.Empty;
        if (requested.Length == 0)
        {
            return fallback;
        }

        try
        {
            string full = Path.GetFullPath(requested);
            Directory.CreateDirectory(full);

            // Creating and deleting a probe proves the folder is writable, which CreateDirectory alone does not.
            string probe = Path.Combine(full, "write-probe-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return full;
        }
        catch (Exception ex)
        {
            Log.Error("SettingsManager.ResolveDataRoot", ex);
            return fallback;
        }
    }
}
