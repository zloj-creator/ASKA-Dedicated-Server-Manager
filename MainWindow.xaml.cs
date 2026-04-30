using Microsoft.VisualBasic.FileIO;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using Media = System.Windows.Media;

namespace AskaServerManager;

public static class NativeMethods
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public static void SetDarkTitleBar(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
        var trueValue = 1;
        _ = DwmSetWindowAttribute(hwnd, 20, ref trueValue, sizeof(int));
    }
}


public partial class MainWindow : Window
{
    [Conditional("DEBUG")]
    public void DebugLog(string message)
    {
        Log(message, "DEBUG");
    }
    // command history
    private readonly List<string> _history = [];
    private int _historyIdx = -1;
    private enum ConsoleMode { CMD, PLUGIN, RCON }
    private ConsoleMode _currentMode = ConsoleMode.CMD;



    // Fields
    private bool serverStartedLogged = false;
    private bool showServerLog = false;   // по умолчанию не показывать логи сервера
    private Process? _serverProcess;
    private bool _autoScroll = true;
    private string currentSaveId = "";
    private System.Media.SoundPlayer? _joinPlayer;
    private System.Media.SoundPlayer? _leavePlayer;
    private List<string> previousPlayers = [];
    private readonly string logDirectory;
    private string serverExe = "";
    private string savePath = "";
    private string backupDir = "";
    private string propertiesFilePath = "";
    private readonly string settingsPath;
    private int backupIntervalMinutes = 30;
    private int maxBackupCount = 10;
    private bool isConfigured = false;
    private readonly List<string> validationErrors = [];

    private bool isStoppingManually = false;
    private bool isBackupInProgress = false;
    private bool serverWasRunning = false;
    private DateTime lastBackupWriteTime = DateTime.MinValue;
    private DateTime _lastManualStopTime = DateTime.MinValue;

    private string queryIP = "127.0.0.1";
    private int queryPort = 27016;

    private readonly DispatcherTimer backupTimer;
    private readonly DispatcherTimer uiTimer;
    private readonly DispatcherTimer pluginTimer;
    private int secondsLeft = 1800;
    private FileSystemWatcher? settingsWatcher;
    private DispatcherTimer? settingsChangedTimer;
    private readonly DispatcherTimer logRotationTimer;
    private bool _soundsAvailable = true;

    // Для плавного обновления времени
    private double _smoothTime = 0;
    private readonly DispatcherTimer _gameTimeTimer;
    // Новые поля для автономного хода времени
    private double _serverGameTimeRaw = 0;      // текущее игровое время (часы, дробные)
    private double _gameTimeMultiplier = 1.0;   // множитель из day length
    private DateTime _lastTimerTickTime = DateTime.MinValue; // для расчёта дельты

    // Данные, отображаемые в InfoPanel (обновляются из плагинов)
    private int _lastPlayersCount = 0;
    private string _lastSeason = "N/A";
    private int _lastVillagers = 0;
    private int _lastDaysSurvived = 0;

    // ---- File helpers ----
    private static Dictionary<string, string> ParseCfgFile(string filePath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(filePath)) return result;
        foreach (string rawLine in File.ReadAllLines(filePath))
        {
            string line = rawLine.Trim();
            if (string.IsNullOrEmpty(line)) continue;
            if (line.StartsWith('#') || line.StartsWith("//")) continue;
            int eqPos = line.IndexOf('=');
            if (eqPos <= 0) continue;
            string key = line[..eqPos].Trim();
            string value = line[(eqPos + 1)..].Trim();
            result[key] = value;
        }
        return result;
    }

    public void SaveSettingsToCfg(AppSettings settings)
    {
        var lines = new List<string>
        {
            "# ASKA Server Manager configuration file",
            $"ServerDirectory = {settings.ServerDirectory}",
            $"ServerExecutable = {settings.ServerExecutable}",
            $"PropertiesFileName = {settings.PropertiesFileName}",
            $"BackupDirectory = {settings.BackupDirectory}",
            $"SteamAppId = {settings.SteamAppId}",
            $"BackupIntervalMinutes = {settings.BackupIntervalMinutes}",
            $"MaxBackupCount = {settings.MaxBackupCount}",
            $"QueryIP = {settings.QueryIP}",
            $"QueryPort = {settings.QueryPort}",
            $"MaxLogSizeMB = {settings.MaxLogSizeMB}",
            $"MaxLogFiles = {settings.MaxLogFiles}",
            $"BackupOnStop = {(settings.BackupOnStop ? "true" : "false")}",
            $"LoadDailyLogOnStart = {(settings.LoadDailyLogOnStart ? "true" : "false")}",
            $"ShowServerLog = {(showServerLog ? "true" : "false")}",
            $"DontSaveServerLog = {(settings.DontSaveServerLog ? "true" : "false")}"
        };
        File.WriteAllLines(settingsPath, lines);
    }

    private static string? ExtractJsonStringValue(string json, string key)
    {
        string pattern = $"\"{key}\"\\s*:\\s*\"([^\"]*)\"";
        var match = Regex.Match(json, pattern);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static int ExtractJsonIntValue(string json, string key)
    {
        // Ищем число без кавычек: "key": 123
        string pattern = $"\"{key}\"\\s*:\\s*(\\d+)";
        Match match = Regex.Match(json, pattern);
        if (match.Success && int.TryParse(match.Groups[1].Value, out int val))
            return val;

        // Ищем число в кавычках: "key": "123"
        pattern = $"\"{key}\"\\s*:\\s*\"(\\d+)\"";
        match = Regex.Match(json, pattern);
        if (match.Success && int.TryParse(match.Groups[1].Value, out val))
            return val;

        // Ищем число с плавающей точкой (на случай gameTimeRaw, но для дней не нужно)
        pattern = $"\"{key}\"\\s*:\\s*([\\d.]+)";
        match = Regex.Match(json, pattern);
        if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double dval))
            return (int)dval;

        return 0;
    }

    private void OnServerOutput(object sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Data))
            Dispatcher.Invoke(() => Log(e.Data, "SERVER"));
    }

    private void OnServerError(object sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Data))
            Dispatcher.Invoke(() => Log(e.Data, "SERVER ERR"));
    }
    private static string? ExtractJsonRawValue(string json, string key)
    {
        string pattern = $"\"{key}\"\\s*:\\s*\"?([\\d\\.]+)\"?";
        Match match = Regex.Match(json, pattern);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static List<string> ExtractJsonStringArray(string json, string key)
    {
        var result = new List<string>();
        int start = json.IndexOf($"\"{key}\":", StringComparison.Ordinal);
        if (start < 0) return result;
        int arrStart = json.IndexOf('[', start);
        int arrEnd = json.IndexOf(']', arrStart);
        if (arrStart < 0 || arrEnd < 0) return result;
        string block = json.Substring(arrStart + 1, arrEnd - arrStart - 1);
        var matches = Regex.Matches(block, "\"([^\"]*)\"");
        foreach (Match m in matches)
            result.Add(m.Groups[1].Value);
        return result;
    }

    // ---- Window state ----
    private void LoadWindowState()
    {
        if (!File.Exists(settingsPath)) return;
        var cfg = ParseCfgFile(settingsPath);
        if (cfg.TryGetValue("WindowPos", out string? posStr) && !string.IsNullOrEmpty(posStr))
        {
            string[] parts = posStr.Split(',');
            if (parts.Length == 4 &&
                int.TryParse(parts[0], out int left) &&
                int.TryParse(parts[1], out int top) &&
                int.TryParse(parts[2], out int width) &&
                int.TryParse(parts[3], out int height))
            {
                Left = left;
                Top = top;
                Width = width;
                Height = height;
                WindowStartupLocation = WindowStartupLocation.Manual;
            }
        }
    }

    private void SaveWindowState()
    {
        if (WindowState == WindowState.Maximized) return;
        string newLine = $"WindowPos = {Left},{Top},{Width},{Height}";
        if (!File.Exists(settingsPath))
        {
            File.WriteAllText(settingsPath, newLine + Environment.NewLine);
            return;
        }
        var lines = File.ReadAllLines(settingsPath).ToList();
        bool replaced = false;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].TrimStart().StartsWith("WindowPos", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = newLine;
                replaced = true;
                break;
            }
        }
        if (!replaced) lines.Add(newLine);
        File.WriteAllLines(settingsPath, lines);
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, true);
        }
        foreach (string subDir in Directory.GetDirectories(sourceDir))
        {
            string destSubDir = Path.Combine(destDir, Path.GetFileName(subDir));
            CopyDirectory(subDir, destSubDir);
        }
    }

    // CONSTRUCTOR
    [SupportedOSPlatform("windows")]
    public MainWindow()
    {
        InitializeComponent();
        UpdateServerLogMenuItemColor();
        TxtDisplayName.Visibility = Visibility.Collapsed;
        TxtServerName.Visibility = Visibility.Collapsed;

        this.PreviewKeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter)
            {
                if (!ConsoleInput.IsFocused)
                {
                    ConsoleInput.Focus();
                    ConsoleInput.CaretIndex = ConsoleInput.Text.Length;
                    e.Handled = true;
                }
            }
        };

        logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
        settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.cfg");

        // Dark titlebar
        NativeMethods.SetDarkTitleBar(this);

        // Sound initialization
        string soundsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Sounds");
        string joinPath = Path.Combine(soundsDir, "join.wav");
        string leavePath = Path.Combine(soundsDir, "leave.wav");
        if (File.Exists(joinPath) && File.Exists(leavePath))
        {
            _joinPlayer = new System.Media.SoundPlayer(joinPath);
            _leavePlayer = new System.Media.SoundPlayer(leavePath);
            _joinPlayer.Load();
            _leavePlayer.Load();
        }
        else
        {
            _soundsAvailable = false;
            Log("Sound files missing in Sounds folder. Sound notifications disabled.", "WARN");
        }

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName()?.Version;
        string versionString = version != null ? $"v{version.ToString(3)}" : "v0.0.0";
        string versionString1 = version != null ? version.ToString() : "v0.0.0";
        Title += " " + versionString;
        VersionText.Text = versionString1;

        LoadWindowState();
        if (!Directory.Exists(logDirectory)) Directory.CreateDirectory(logDirectory);
        LogScrollViewer.ScrollChanged += (s, e) =>
        {
            if (e.ExtentHeightChange == 0) return;
            if (LogScrollViewer.VerticalOffset + LogScrollViewer.ViewportHeight < LogScrollViewer.ExtentHeight - 5)
                _autoScroll = false;
            else
                _autoScroll = true;
        };

        backupTimer = new DispatcherTimer();
        backupTimer.Tick += (s, e) => Task.Run(() => MakeBackup());
        uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        uiTimer.Tick += (s, e) => UpdateVisualTimer();

        pluginTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        pluginTimer.Tick += async (s, e) => await TryReadPluginDataAsync();

        logRotationTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        logRotationTimer.Tick += (s, e) => RotateLogsIfNeeded();
        logRotationTimer.Start();

        // Load settings
        LoadSettings();
        if (GetServerProcess() != null && _serverProcess == null)
        {
            Log("Server already running but not started by Manager. Log output will not be captured. Command RESTART to restart server.", "WARN");
        }

        if (App.Settings?.LoadDailyLogOnStart == true)
            LoadTodayLog();
        else
            Log("Daily log loading disabled in settings.", "INFO");

        BtnSendCommand.Click += async (s, e) => await SendCommandToPlugin();
        ConsoleInput.KeyDown += async (s, e) => { if (e.Key == Key.Enter) await SendCommandToPlugin(); };
        BtnOpenSavesFolder.Click += (s, e) => OpenSavesFolder();
        BtnOpenBackupsFolder.Click += (s, e) => OpenBackupsFolder();
        BtnSettings.Click += (s, e) => OpenSettingsWindow();
        StatusLed.MouseDown += (s, e) => CheckServerStatusManual();
        StatusLed.Cursor = Cursors.Hand;

        DispatcherTimer statusTimer = new() { Interval = TimeSpan.FromSeconds(10) };
        statusTimer.Tick += (s, e) => { UpdateServerInfo(); CheckServerProcess(); UpdateServerRam(); };
        statusTimer.Start();
        _gameTimeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _gameTimeTimer.Tick += (s, e) => OnGameTimeTick();
        UpdateServerInfo();
    }

    // Начало

    private void UpdateServerLogMenuItemColor()
    {
        if (MenuShowServerLog == null) return;
        MenuShowServerLog.Foreground = showServerLog ? Brushes.White : (SolidColorBrush)FindResource("ForegroundBrush");
    }

    private void OnGameTimeTick()
    {
        if (GetServerProcess() == null) return;

        DateTime now = DateTime.UtcNow;
        if (_lastTimerTickTime == DateTime.MinValue)
        {
            _lastTimerTickTime = now;
            return;
        }

        double deltaRealSeconds = (now - _lastTimerTickTime).TotalSeconds;
        _lastTimerTickTime = now;

        // Защита от зависаний: не больше 0.2 сек реальных
        if (deltaRealSeconds > 0.2) deltaRealSeconds = 0.2;

        // Инициализация _smoothTime
        if (_smoothTime <= 0 && _serverGameTimeRaw > 0)
            _smoothTime = _serverGameTimeRaw;

        // Приращение игрового времени в часах
        double deltaGameHours = deltaRealSeconds / 3600.0 * _gameTimeMultiplier;

        // Жёсткое ограничение: максимум 1 игровая секунда за тик
        const double maxDeltaGameHours = 1.0 / 3600.0; // 1 секунда
        if (deltaGameHours > maxDeltaGameHours)
            deltaGameHours = maxDeltaGameHours;

        _smoothTime += deltaGameHours;

        // Коррекция только при сильном расхождении (> 2 минут)
        double error = _serverGameTimeRaw - _smoothTime;
        if (Math.Abs(error) > 2.0 / 60.0)
            _smoothTime += error * 0.02; // очень медленная коррекция

        UpdateGameTimeDisplay();
    }

    private void UpdateGameTimeDisplay()
    {
        bool serverRunning = GetServerProcess() != null;

        if (serverRunning)
        {
            TxtPlayerNames.Text = previousPlayers.Count > 0 ? "In game: " + string.Join(", ", previousPlayers) : "";
        }
        else
        {
            TxtPlayerNames.Text = "";
        }

        if (!serverRunning)
        {
            TxtStats.Text = "Players N/A | Time: N/A | Season: N/A | Villagers: N/A | Days Survived: N/A";
            return;
        }

        // Всегда показываем количество игроков из лога
        string playersPart = $"Players {_lastPlayersCount}/4";

        // Данные от плагина всегда считаем валидными (файл свежий)
        double raw = _smoothTime % 24;
        int hour = (int)raw;
        int minute = (int)((raw - hour) * 60);
        if (minute >= 60) { minute = 0; hour++; }
        string timeStr = $"{hour:D2}:{minute:D2}";
        TxtStats.Text = $"{playersPart} | Time: {timeStr} | Season: {_lastSeason} | Villagers: {_lastVillagers} | Days Survived: {_lastDaysSurvived}";
    }

    private void SetConsoleMode(ConsoleMode mode)
    {
        _currentMode = mode;
        if (ModeLabel == null) return;
        switch (mode)
        {
            case ConsoleMode.CMD:
                ModeLabel.Text = "[cmd] >";
                ModeLabel.Foreground = Brushes.LightGreen;
                ConsoleInput.Foreground = Brushes.LightGreen;
                break;
            case ConsoleMode.PLUGIN:
                ModeLabel.Text = "[plugin] >";
                ModeLabel.Foreground = Brushes.Cyan;
                ConsoleInput.Foreground = Brushes.Cyan;
                break;
            case ConsoleMode.RCON:
                ModeLabel.Text = "[rcon] >";
                ModeLabel.Foreground = Brushes.Orange;
                ConsoleInput.Foreground = Brushes.Orange;
                break;
        }
        ConsoleInput.Focus();
    }

    private string GetSaveIdFromConfig()
    {
        try
        {
            if (!File.Exists(propertiesFilePath)) return "";
            var lines = File.ReadAllLines(propertiesFilePath);
            foreach (string line in lines)
            {
                if (line.Trim().StartsWith("save id", StringComparison.OrdinalIgnoreCase))
                {
                    int eq = line.IndexOf('=');
                    if (eq == -1) continue;
                    string value = line[(eq + 1)..].Trim();
                    return value;
                }
            }
        }
        catch (Exception ex) { Log($"Error reading config: {ex.Message}", "ERROR"); }
        return "";
    }

    private string GetCurrentSaveId() => GetSaveIdFromConfig();

    private void RotateLogsIfNeeded()
    {
        try
        {
            string todayLog = Path.Combine(logDirectory, $"Aska_Server_Log_{DateTime.Now:yyyy-MM-dd}.txt");
            if (!File.Exists(todayLog)) return;
            FileInfo fi = new(todayLog);
            long maxSize = (App.Settings?.MaxLogSizeMB ?? 10) * 1024 * 1024;
            if (fi.Length > maxSize)
            {
                string timestamp = DateTime.Now.ToString("HHmmss");
                string newName = Path.Combine(logDirectory, $"Aska_Server_Log_{DateTime.Now:yyyy-MM-dd}_{timestamp}.txt");
                File.Move(todayLog, newName);
                File.WriteAllText(todayLog, "");
                Log($"Log exceeded limit, new log created: {Path.GetFileName(todayLog)}", "INFO");
            }
            var allLogs = Directory.GetFiles(logDirectory, "Aska_Server_Log_*.txt")
                .Where(f => !f.Equals(todayLog, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f).ToList();
            int maxFiles = App.Settings?.MaxLogFiles ?? 100;
            for (int i = maxFiles; i < allLogs.Count; i++)
                File.Delete(allLogs[i]);
        }
        catch (Exception ex) { Log($"Error rotating logs: {ex.Message}", "ERROR"); }
    }

    private void LoadTodayLog()
    {
        if (string.IsNullOrEmpty(logDirectory)) return;
        string todayLog = Path.Combine(logDirectory, $"Aska_Server_Log_{DateTime.Now:yyyy-MM-dd}.txt");
        if (!File.Exists(todayLog)) return;
        var lines = File.ReadAllLines(todayLog);
        foreach (string line in lines)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = 1 };
            paragraph.Inlines.Add(new Run(line) { Foreground = Brushes.White });
            LogBox.Document.Blocks.Add(paragraph);
        }
        LogScrollViewer.ScrollToEnd();
        Log($"Loaded current day's log: {Path.GetFileName(todayLog)}", "CMD");
    }

    private void ConsoleInput_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Up)
        {
            if (_historyIdx > 0)
            {
                _historyIdx--;
                ConsoleInput.Text = _history[_historyIdx];
                ConsoleInput.CaretIndex = ConsoleInput.Text.Length;
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            if (_historyIdx < _history.Count - 1)
            {
                _historyIdx++;
                ConsoleInput.Text = _history[_historyIdx];
                ConsoleInput.CaretIndex = ConsoleInput.Text.Length;
            }
            else
            {
                _historyIdx = _history.Count;
                ConsoleInput.Clear();
            }
            e.Handled = true;
        }
    }

    private Process? GetServerProcess() => Process.GetProcessesByName("AskaServer").FirstOrDefault();

    private void UpdateServerRam()
    {
        if (_serverProcess != null && !_serverProcess.HasExited)
        {
            long ramMB = _serverProcess.WorkingSet64 / (1024 * 1024);
            Dispatcher.Invoke(() => TxtServerRam.Text = $"RAM: {ramMB} MB");
        }
        else Dispatcher.Invoke(() => TxtServerRam.Text = "");
    }

    private void PlayJoinSound()
    {
        if (!_soundsAvailable) return;
        try { _joinPlayer?.Play(); }
        catch (Exception ex) { Log($"Join sound error: {ex.Message}", "ERROR"); }
    }

    private void PlayLeaveSound()
    {
        if (!_soundsAvailable) return;
        try { _leavePlayer?.Play(); }
        catch (Exception ex) { Log($"Leave sound error: {ex.Message}", "ERROR"); }
    }

    internal void Log(string message, string prefix)
    {
        bool isServerLog = (prefix == "SERVER" || prefix == "SERVER ERR");
        bool dontSaveServerLog = App.Settings?.DontSaveServerLog == true;

        // Сохраняем в файл, только если НЕ серверный лог ИЛИ разрешено сохранять (dontSaveServerLog == false)
        if (!(isServerLog && dontSaveServerLog))
        {
            try
            {
                string todayLog = Path.Combine(logDirectory, $"Aska_Server_Log_{DateTime.Now:yyyy-MM-dd}.txt");
                File.AppendAllText(todayLog, $"[{DateTime.Now:HH:mm:ss}] [{prefix}] {message}\n");
            }
            catch { }
        }

        // парсим подключения/отключения из лога сервера (даже если скрыт)
        if (prefix == "SERVER")
            ParseServerLogForPlayers(message);

        // всегда показываем важные сообщения сервера
        bool isImportant = false;
        if (prefix == "SERVER")
        {
            string lowerMsg = message.ToLower();
            isImportant = lowerMsg.Contains("loading game world") ||
                          lowerMsg.Contains("connected to steam successfully") ||
                          lowerMsg.Contains("the session is now open");

            if (lowerMsg.Contains("the session is now open") && !serverStartedLogged)
            {
                serverStartedLogged = true;
                Log("Server started successfully!", "CMD");
            }
        }

        // фильтруем показ серверных логов (!isImportant)
        if (isServerLog && !showServerLog && !isImportant)
            return;

        // отображение в UI
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            var brush = prefix switch
            {
                "ERROR" => Brushes.Red,
                "WARN" => Brushes.Orange,
                "PLUGIN" => Brushes.Cyan,
                "RCON" => Brushes.Magenta,
                "CMD" => Brushes.LightGreen,
                "JOIN" => Brushes.LightBlue,
                "LEAVE" => Brushes.Yellow,
                "BACKUP" => Brushes.Goldenrod,
                "TIMER" => Brushes.DarkCyan,
                "CONFIG" => Brushes.LightSeaGreen,
                "SERVER" => Brushes.CornflowerBlue,
                "SERVER ERR" => Brushes.Red,
                "DEBUG" => Brushes.Gray,
                _ => Brushes.LightGray
            };
            var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = 1 };
            paragraph.Inlines.Add(new Run($"[{DateTime.Now:HH:mm:ss}] ") { Foreground = Brushes.White });
            paragraph.Inlines.Add(new Run($"[{prefix}] ") { Foreground = brush });
            paragraph.Inlines.Add(new Run(message) { Foreground = brush });
            LogBox.Document.Blocks.Add(paragraph);
            if (_autoScroll) LogScrollViewer.ScrollToEnd();
        }));
    }

    private void ParseServerLogForPlayers(string line)
    {
        string trimmed = line.Trim();
        int idxDisconnected = trimmed.IndexOf("disconnected", StringComparison.OrdinalIgnoreCase);
        if (idxDisconnected >= 0)
        {
            string playerName = trimmed.Substring(0, idxDisconnected).Trim();
            if (previousPlayers.Remove(playerName))
            {
                Log($"{playerName} disconnected", "LEAVE");
                PlayLeaveSound();
            }
        }
        else
        {
            int idxConnected = trimmed.IndexOf("connected", StringComparison.OrdinalIgnoreCase);
            if (idxConnected >= 0)
            {
                string playerName = trimmed.Substring(0, idxConnected).Trim();
                if (!string.IsNullOrEmpty(playerName) && !previousPlayers.Contains(playerName))
                {
                    previousPlayers.Add(playerName);
                    Log($"{playerName} connected", "JOIN");
                    PlayJoinSound();
                }
            }
        }
        _lastPlayersCount = previousPlayers.Count;
        Dispatcher.Invoke(() =>
        {
            TxtPlayerNames.Text = previousPlayers.Count > 0 ? "In game: " + string.Join(", ", previousPlayers) : "";
            UpdateGameTimeDisplay(); // принудительное обновление инфопанели
        });
    }

    // ---- Settings handling ----
    private void LoadSettings(bool silent = false)
    {
        try
        {
            validationErrors.Clear();
            if (!File.Exists(settingsPath))
            {
                ResetToUnconfiguredState();
                CreateDefaultSettingsFile();
                Log("Settings file not found. Default created. Please configure server paths.", "WARN");
                return;
            }

            var cfg = ParseCfgFile(settingsPath);
            var settings = new AppSettings();

            settings.ServerDirectory = cfg.GetValueOrDefault("ServerDirectory", "");
            settings.ServerExecutable = cfg.GetValueOrDefault("ServerExecutable", "AskaServer.exe");
            settings.PropertiesFileName = cfg.GetValueOrDefault("PropertiesFileName", "");
            settings.BackupDirectory = cfg.GetValueOrDefault("BackupDirectory", "");
            settings.SteamAppId = int.TryParse(cfg.GetValueOrDefault("SteamAppId", "1898300"), out int sid) ? sid : 1898300;
            settings.BackupIntervalMinutes = int.TryParse(cfg.GetValueOrDefault("BackupIntervalMinutes", "120"), out int bi) ? bi : 120;
            settings.MaxBackupCount = int.TryParse(cfg.GetValueOrDefault("MaxBackupCount", "10"), out int mb) ? mb : 10;
            settings.QueryIP = cfg.GetValueOrDefault("QueryIP", "127.0.0.1");
            settings.QueryPort = int.TryParse(cfg.GetValueOrDefault("QueryPort", "27016"), out int qp) ? qp : 27016;
            settings.MaxLogSizeMB = int.TryParse(cfg.GetValueOrDefault("MaxLogSizeMB", "2"), out int ml) ? ml : 2;
            settings.MaxLogFiles = int.TryParse(cfg.GetValueOrDefault("MaxLogFiles", "100"), out int mf) ? mf : 100;
            settings.BackupOnStop = cfg.GetValueOrDefault("BackupOnStop", "false").Equals("true", StringComparison.OrdinalIgnoreCase);
            settings.LoadDailyLogOnStart = cfg.GetValueOrDefault("LoadDailyLogOnStart", "false").Equals("true", StringComparison.OrdinalIgnoreCase);
            showServerLog = cfg.GetValueOrDefault("ShowServerLog", "false").Equals("true", StringComparison.OrdinalIgnoreCase);
            settings.DontSaveServerLog = cfg.GetValueOrDefault("DontSaveServerLog", "true").Equals("true", StringComparison.OrdinalIgnoreCase);

            ValidateSettings(settings);
            if (validationErrors.Count > 0)
            {
                Log("ERRORS IN SETTINGS:", "ERROR");
                foreach (var err in validationErrors) Log($"  - {err}", "ERROR");
                ResetToUnconfiguredState();
                return;
            }

            App.Settings = settings;
            showServerLog = App.Settings.ShowServerLog;
            MenuShowServerLog.IsChecked = showServerLog;
            UpdateServerLogMenuItemColor();
            UpdateSettingsWithDefaults(settings);
            backupIntervalMinutes = settings.BackupIntervalMinutes;
            maxBackupCount = settings.MaxBackupCount;
            isConfigured = true;

            string localLowPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData).Replace("Local", "LocalLow"));
            savePath = Path.Combine(localLowPath, "Sand Sailor Studio", "Aska", "data", "server");
            serverExe = Path.Combine(settings.ServerDirectory, settings.ServerExecutable);
            propertiesFilePath = Path.Combine(settings.ServerDirectory, settings.PropertiesFileName);
            backupDir = settings.BackupDirectory;
            if (string.IsNullOrWhiteSpace(backupDir))
            {
                backupDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ASKA_Server_Backup");
                settings.BackupDirectory = backupDir;
                Log($"Backup folder not specified. Default set to: {backupDir}", "INFO");
                SaveSettingsToCfg(settings);
            }

            if (!Path.IsPathRooted(settings.BackupDirectory))
            {
                settings.BackupDirectory = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, settings.BackupDirectory));
                Log($"Backup directory: {settings.BackupDirectory}", "INFO");
            }

            if (!string.IsNullOrEmpty(settings.QueryIP)) queryIP = settings.QueryIP;
            if (settings.QueryPort > 0) queryPort = settings.QueryPort;

            EnableServerControls(true);
            UnconfiguredPanel.Visibility = Visibility.Collapsed;
            ServerInfoPanel.Visibility = Visibility.Visible;
            WatchSettingsFile();

            //TxtBackupTimer.Visibility = Visibility.Visible;
            //TxtBackupInfo.Visibility = Visibility.Visible;
            UpdateBackupInfo();

            secondsLeft = backupIntervalMinutes * 60;
            TxtBackupTimer.Text = $"Until backup: {backupIntervalMinutes:D2}:00";
            BackupProgress.Value = 100;

            if (!silent)
            {
                Log("Manager settings loaded successfully.", "CMD");
                Log($"  Server: {NormalizePathForDisplay(serverExe)}", "INFO");
                Log($"  Config: {NormalizePathForDisplay(propertiesFilePath)}", "INFO");
                Log($"  Backups: {NormalizePathForDisplay(backupDir)}", "INFO");
                Log($"  Backup interval: {backupIntervalMinutes} minutes", "INFO");
                Log($"  Keep backups: {maxBackupCount}", "INFO");
                Log($"  Max log size: {App.Settings.MaxLogSizeMB} MB, files: {App.Settings.MaxLogFiles}", "INFO");
            }

            if (GetServerProcess() != null)
            {
                backupTimer.Interval = TimeSpan.FromMinutes(backupIntervalMinutes);
                backupTimer.Start();
                uiTimer.Start();
                pluginTimer.Start();
                if (_gameTimeTimer != null)
                {
                    _serverGameTimeRaw = 0;
                    _lastTimerTickTime = DateTime.UtcNow;
                    _gameTimeTimer.Start();
                }
                secondsLeft = backupIntervalMinutes * 60;
                TxtBackupTimer.Text = $"Until backup: {backupIntervalMinutes:D2}:00";
                BackupProgress.Value = 100;
                Log("Backup and plugin timers activated.", "INFO");
            }

            if (!string.IsNullOrEmpty(currentSaveId))
            {
                string worldFolder = Path.Combine(savePath, $"savegame_{currentSaveId}");
                if (Directory.Exists(worldFolder))
                    lastBackupWriteTime = Directory.GetLastWriteTime(worldFolder);
            }

            UpdateServerHeader();

        }
        catch (Exception ex)
        {
            Log($"Error loading settings: {ex.Message}", "ERROR");
            ResetToUnconfiguredState();
        }
        currentSaveId = GetCurrentSaveId();
        string fullSavePath = string.IsNullOrEmpty(currentSaveId) ? savePath : Path.Combine(savePath, $"savegame_{currentSaveId}");
        if (!silent)
            Log($"  Savegame folder: {fullSavePath}", "INFO");
    }

    private void ValidateSettings(AppSettings settings)
    {
        if (string.IsNullOrEmpty(settings.ServerDirectory)) validationErrors.Add("(⚙️) Specify ASKA server directory");
        else if (!Directory.Exists(settings.ServerDirectory)) validationErrors.Add($"Server directory does not exist: {settings.ServerDirectory}");
        if (string.IsNullOrEmpty(settings.PropertiesFileName)) validationErrors.Add("(⚙️) Specify server config file path");
        else if (!File.Exists(Path.Combine(settings.ServerDirectory, settings.PropertiesFileName))) validationErrors.Add($"Config file not found: {settings.PropertiesFileName}");
        if (settings.BackupIntervalMinutes < 1 || settings.BackupIntervalMinutes > 1440) validationErrors.Add($"BackupIntervalMinutes must be between 1 and 1440.");
        if (settings.MaxBackupCount < 1 || settings.MaxBackupCount > 100) validationErrors.Add($"MaxBackupCount must be between 1 and 100");
        if (settings.MaxLogSizeMB < 1 || settings.MaxLogSizeMB > 100) validationErrors.Add($"MaxLogSizeMB must be between 1 and 100");
        if (settings.MaxLogFiles < 1 || settings.MaxLogFiles > 1000) validationErrors.Add($"MaxLogFiles must be between 1 and 1000");
    }

    private void UpdateSettingsWithDefaults(AppSettings settings)
    {
        bool needSave = false;
        if (settings.MaxLogSizeMB == 0) { settings.MaxLogSizeMB = 2; needSave = true; }
        if (settings.MaxLogFiles == 0) { settings.MaxLogFiles = 100; needSave = true; }
        if (needSave) SaveSettingsToCfg(settings);
    }

    private void CreateDefaultSettingsFile()
    {
        var lines = new List<string>
        {
            "# ASKA Server Manager configuration file",
            "ServerDirectory =",
            "ServerExecutable = AskaServer.exe",
            "PropertiesFileName =",
            "BackupDirectory =",
            "SteamAppId = 1898300",
            "BackupIntervalMinutes = 120",
            "MaxBackupCount = 10",
            "QueryIP = 127.0.0.1",
            "QueryPort = 27016",
            "MaxLogSizeMB = 2",
            "MaxLogFiles = 100",
            "BackupOnStop = false",
            "LoadDailyLogOnStart = false",
            "ShowServerLog = false",
            "DontSaveServerLog = true"
        };
        File.WriteAllLines(settingsPath, lines);
        Log($"Settings file created: {settingsPath}", "INFO");
        UnconfiguredPanel.Visibility = Visibility.Visible;
    }

    private void ResetToUnconfiguredState()
    {
        isConfigured = false;
        backupDir = "";
        serverExe = "";
        savePath = "";
        propertiesFilePath = "";
        backupTimer?.Stop();
        uiTimer?.Stop();
        EnableServerControls(false);
        UnconfiguredPanel.Visibility = Visibility.Visible;
        ServerInfoPanel.Visibility = Visibility.Collapsed;
        TxtBackupTimer.Visibility = Visibility.Collapsed;
        TxtBackupInfo.Visibility = Visibility.Collapsed;
        TxtStats.Visibility = Visibility.Collapsed;
    }

    private void EnableServerControls(bool enabled) { }

    private void EnsureLogDirectory()
    {
        if (!Directory.Exists(logDirectory)) Directory.CreateDirectory(logDirectory);
    }

    private void WatchSettingsFile()
    {
        settingsWatcher?.Dispose();
        settingsWatcher = new FileSystemWatcher();
        settingsWatcher.Path = AppDomain.CurrentDomain.BaseDirectory;
        settingsWatcher.Filter = "settings.cfg";
        settingsWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName;
        settingsWatcher.Deleted += (s, e) => Dispatcher.Invoke(() => { ResetToUnconfiguredState(); CreateDefaultSettingsFile(); });
        settingsWatcher.Changed += OnSettingsChanged;
        settingsWatcher.EnableRaisingEvents = true;
    }

    private void OnSettingsChanged(object sender, FileSystemEventArgs e)
    {
        if (settingsChangedTimer == null)
        {
            settingsChangedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            settingsChangedTimer.Tick += (s, ev) =>
            {
                settingsChangedTimer.Stop();
                Dispatcher.Invoke(() =>
                {
                    if (File.Exists(settingsPath))
                    {
                        Log("Detected settings file change. Applying...", "INFO");
                        ReloadSettings();
                    }
                    else ResetToUnconfiguredState();
                });
            };
        }
        settingsChangedTimer.Stop();
        settingsChangedTimer.Start();
    }

    private void ReloadSettings()
    {
        int oldInterval = backupIntervalMinutes;
        LoadSettings(true);
        if (isConfigured && GetServerProcess() != null)
        {
            if (oldInterval != backupIntervalMinutes)
            {
                Log($"Backup interval changed: {oldInterval} -> {backupIntervalMinutes} minutes", "CONFIG");
                uiTimer.Stop();
                backupTimer.Stop();
                backupTimer.Interval = TimeSpan.FromMinutes(backupIntervalMinutes);
                backupTimer.Start();
                uiTimer.Start();
                secondsLeft = backupIntervalMinutes * 60;
                TxtBackupTimer.Text = $"Until backup: {backupIntervalMinutes:D2}:00";
                BackupProgress.Value = 100;
                Log("Backup timer restarted with new interval.", "INFO");
            }
        }
    }

    private void OpenSettingsWindow()
    {
        var win = new SettingsWindow(GetServerProcess() != null);
        win.Owner = this;
        if (win.ShowDialog() == true)
            ReloadSettings();
    }

    // ---- Server management ----
    private async void StartServerAsync()
    {
        if (!isConfigured) { Log("Click (⚙️) Settings and specify ASKA server directory.", "WARN"); return; }
        try
        {
            if (!File.Exists(serverExe)) { Log($"Error: server file not found: {NormalizePathForDisplay(serverExe)}", "ERROR"); return; }
            if (!File.Exists(propertiesFilePath)) { Log($"Error: config file not found: {NormalizePathForDisplay(propertiesFilePath)}", "ERROR"); return; }

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = serverExe,
                Arguments = $"-propertiesPath \"{App.Settings?.PropertiesFileName ?? "server properties.txt"}\"",
                WorkingDirectory = Path.GetDirectoryName(serverExe),
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.EnvironmentVariables["SteamAppId"] = App.Settings?.SteamAppId.ToString() ?? "1898300";

            Log($"Starting server with config: {Path.GetFileName(propertiesFilePath)}", "CMD");
            _serverProcess = Process.Start(psi);
            if (_serverProcess == null) throw new Exception("Failed to start process");

            if (!_serverProcess.HasExited)
            {
                _serverProcess.OutputDataReceived += OnServerOutput;
                _serverProcess.ErrorDataReceived += OnServerError;
                _serverProcess.BeginOutputReadLine();
                _serverProcess.BeginErrorReadLine();
            }
            else
            {
                Log("Server process exited immediately after start!", "ERROR");
                _serverProcess = null;
                return;
            }

            await Task.Delay(10000);
            secondsLeft = backupIntervalMinutes * 60;
            BackupProgress.Value = 100;
            TxtBackupTimer.Text = $"Until backup: {backupIntervalMinutes:D2}:00";
            backupTimer.Interval = TimeSpan.FromMinutes(backupIntervalMinutes);
            backupTimer.Start();
            uiTimer.Start();
            UpdateServerInfo();
            Log($"Backup timer activated ({backupIntervalMinutes} minutes).", "CONFIG");
            serverWasRunning = true;
            pluginTimer.Start();
            isStoppingManually = false;
        }
        catch (Exception ex) { Log($"Error starting server: {ex.Message}", "ERROR"); }
    }

    private async Task StopServer(Process process)
    {
        serverStartedLogged = false;
        Dispatcher.Invoke(() => StatusBarText.Text = "Stopping server...");
        isStoppingManually = true;
        serverWasRunning = false;
        _lastManualStopTime = DateTime.Now;

        if (App.Settings?.BackupOnStop == true)
        {
            Log("Creating backup before stopping server...", "BACKUP");
            await Task.Run(() => MakeBackup());
        }
        if (_serverProcess != null)
        {
            _serverProcess.OutputDataReceived -= OnServerOutput;
            _serverProcess.ErrorDataReceived -= OnServerError;
            _serverProcess.CancelOutputRead();
            _serverProcess.CancelErrorRead();
            _serverProcess.Close();
            _serverProcess = null;
        }
        Log("Stopping server...", "INFO");

        await Task.Run(async () =>
        {
            try
            {
                List<IntPtr> windows = GetProcessWindows(process.Id);
                if (windows.Count == 0)
                    process.CloseMainWindow();
                else
                    foreach (IntPtr hWnd in windows)
                        WinApi.SendMessage(hWnd, 0x0010, IntPtr.Zero, IntPtr.Zero);

                while (!process.HasExited)
                {
                    await Task.Delay(100);
                    process.Refresh();
                }
            }
            catch (Exception ex) { Dispatcher.Invoke(() => Log($"Error during stop: {ex.Message}", "ERROR")); }
        });

        previousPlayers.Clear();
        _lastPlayersCount = 0;
        Dispatcher.Invoke(() => TxtPlayerNames.Text = "");
        _lastSeason = "N/A";
        _lastVillagers = 0;
        _lastDaysSurvived = 0;
        UpdateGameTimeDisplay();
        Log("Server stopped.", "CMD");
        backupTimer.Stop();
        uiTimer.Stop();
        Log($"Backup timer stopped", "TIMER");
        secondsLeft = backupIntervalMinutes * 60;
        TxtBackupTimer.Text = $"Until backup: {backupIntervalMinutes:D2}:00";
        BackupProgress.Value = 100;
        UpdateServerInfo();
        pluginTimer.Stop();
        _smoothTime = 0;
        _gameTimeTimer?.Stop();
        _serverGameTimeRaw = 0;
        _lastTimerTickTime = DateTime.MinValue;
        Dispatcher.Invoke(() => StatusBarText.Text = "Ready");
        serverWasRunning = false;
        isStoppingManually = false;

    }

    private async void RestartServer()
    {
        var process = GetServerProcess();
        if (process == null || process.HasExited)
        {
            Log("Server not running.", "WARN");
            return;
        }
        Log("Restarting server...", "INFO");
        await StopServer(process);   // корректно останавливает с сохранением
        Log("Server stopped. Starting in 3 seconds...", "INFO");
        await Task.Delay(3000);
        StartServerAsync();
    }

    private static List<IntPtr> GetProcessWindows(int processId)
    {
        var windows = new List<IntPtr>();
        WinApi.EnumWindows((hWnd, lParam) =>
        {
            _ = WinApi.GetWindowThreadProcessId(hWnd, out var wp);
            if (wp == processId) windows.Add(hWnd);
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    // ---- Plugin communication (AskaMonitor) ----
    private async Task SendCommandToPluginAsync(string command)
    {
        if (App.Settings == null || string.IsNullOrEmpty(App.Settings.ServerDirectory))
        {
            Log("Error: Server path not configured!", "ERROR");
            return;
        }

        string commandFile = Path.GetFullPath(Path.Combine(App.Settings.ServerDirectory, "BepInEx", "AskaCommand.txt"));
        string statusFile = Path.GetFullPath(Path.Combine(App.Settings.ServerDirectory, "BepInEx", "AskaServerStatus.txt"));

        try
        {
            // Генерируем подпись (время с миллисекундами)
            string correlationId = DateTime.Now.ToString("HHmmssfff");
            // Отправляем команду с полями command, id и time
            string json = $"{{\"command\": \"{command.Replace("\"", "\\\"")}\", \"id\": \"{correlationId}\", \"time\": \"{correlationId}\"}}";
            await File.WriteAllTextAsync(commandFile, json);
            Log($"Command '{command}' sent to plugin (id: {correlationId}).", "CMD");

            int timeout = 10; // секунд
            int elapsed = 0;
            bool responseReceived = false;

            while (elapsed < timeout && !responseReceived)
            {
                await Task.Delay(1000);
                elapsed++;

                if (File.Exists(statusFile))
                {
                    try
                    {
                        // Открываем файл с общим доступом на чтение и запись
                        using var stream = new FileStream(statusFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var reader = new StreamReader(stream);
                        string statusJson = await reader.ReadToEndAsync();

                        string? lastResponseId = ExtractJsonStringValue(statusJson, "lastCommandId");
                        if (lastResponseId == correlationId)
                        {
                            string? details = ExtractJsonStringValue(statusJson, "statusDetails");
                            Log(string.IsNullOrEmpty(details) ? "Response received." : details, "PLUGIN");
                            responseReceived = true;
                            break;
                        }
                    }
                    catch (IOException)
                    {
                        // Если файл временно заблокирован, просто продолжим и попробуем на следующей секунде
                    }
                }
            }

            if (!responseReceived)
                Log($"Timeout: server did not respond within {timeout} sec.", "WARN");
        }
        catch (Exception ex)
        {
            Log($"Error: {ex.Message}", "ERROR");
        }
    }

    private async Task TryReadPluginDataAsync()
    {
        if (App.Settings == null) return;
        if (GetServerProcess() == null) return; // сервер не работает – не читаем (опционально)

        string statusFilePath = Path.Combine(App.Settings.ServerDirectory, "BepInEx", "AskaServerStatus.txt");
        if (!File.Exists(statusFilePath)) return;

        try
        {
            string json = await File.ReadAllTextAsync(statusFilePath, Encoding.UTF8);


            // Парсинг времени
            string rawTimeStr = ExtractJsonRawValue(json, "gameTimeRaw") ?? "0";
            double.TryParse(rawTimeStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double rawTime);
            if (rawTime < 0) rawTime = 0;

            // Сезон
            string season = ExtractJsonStringValue(json, "season") ?? "N/A";
            _lastSeason = season;

            // Дни
            int daysPassed = ExtractJsonIntValue(json, "daysPassed");

            // Рассвет
            int dawnHour = 6, dawnMinute = 35;
            switch (season.ToLower())
            {
                case "spring": dawnHour = 6; dawnMinute = 35; break;
                case "summer": dawnHour = 5; dawnMinute = 0; break;
                case "autumn": dawnHour = 5; dawnMinute = 15; break;
                case "winter": dawnHour = 7; dawnMinute = 45; break;
            }

            int currentHour = (int)rawTime;
            int currentMinute = (int)((rawTime - currentHour) * 60);
            bool isSunrisePassed = currentHour > dawnHour || (currentHour == dawnHour && currentMinute >= dawnMinute);
            _lastDaysSurvived = isSunrisePassed ? daysPassed : (daysPassed > 0 ? daysPassed - 1 : 0);

            // Синхронизация времени
            if (_serverGameTimeRaw == 0 && rawTime > 0)
            {
                _serverGameTimeRaw = rawTime;
                _smoothTime = rawTime;
                _lastTimerTickTime = DateTime.UtcNow;
            }
            else if (rawTime > 0)
            {
                _serverGameTimeRaw = rawTime;
                double error = _serverGameTimeRaw - _smoothTime;
                if (Math.Abs(error) > 5.0 / 60.0)
                    _smoothTime += error * 0.7;
            }


            UpdateGameTimeDisplay();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"TryReadPluginDataAsync error: {ex.Message}");
        }
    }


    // ---- Console commands ----
    private async Task SendCommandToPlugin()
    {
        string command = ConsoleInput.Text.Trim();
        if (string.IsNullOrEmpty(command))
        {
            LogBox.Focus();
            ConsoleInput.Clear();
            return;
        }

        _history.Add(command);
        _historyIdx = _history.Count;

        // Mode switching
        if (command.Equals("/cmd", StringComparison.OrdinalIgnoreCase) || command.Equals("/c", StringComparison.OrdinalIgnoreCase))
        { SetConsoleMode(ConsoleMode.CMD); ConsoleInput.Clear(); return; }
        if (command.Equals("/plugin", StringComparison.OrdinalIgnoreCase) || command.Equals("/p", StringComparison.OrdinalIgnoreCase))
        { SetConsoleMode(ConsoleMode.PLUGIN); ConsoleInput.Clear(); return; }
        if (command.Equals("/rcon", StringComparison.OrdinalIgnoreCase) || command.Equals("/r", StringComparison.OrdinalIgnoreCase))
        { SetConsoleMode(ConsoleMode.RCON); ConsoleInput.Clear(); return; }

        if (command.StartsWith("plugin ", StringComparison.OrdinalIgnoreCase))
        {
            string subCommand = "get:" + command[7..];
            await SendCommandToPluginAsync(subCommand);
            ConsoleInput.Clear();
            return;
        }

        if (command.StartsWith("get:"))
        {
            if (GetServerProcess() == null)
            {
                Log("Server is not running. Command cannot be executed.", "WARN");
                ConsoleInput.Clear();
                return;
            }
            ConsoleInput.Clear(); // Clear immediately
            await SendCommandToPluginAsync(command);
            return;
        }

        if (_currentMode == ConsoleMode.PLUGIN && !command.StartsWith("get:"))
        {
            command = "get:" + command;
            ConsoleInput.Clear();
            await SendCommandToPluginAsync(command);
            return;
        }
        if (_currentMode == ConsoleMode.RCON)
        {
            Log($"Sending: {command}", "RCON");
            ConsoleInput.Clear();
            return;
        }

        // Local manager commands
        if (command.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            if (App.Settings == null || string.IsNullOrEmpty(App.Settings.ServerDirectory))
            {
                Log("Server directory not configured.", "WARN");
                ConsoleInput.Clear();
                return;
            }
            string path = Path.Combine(App.Settings.ServerDirectory, "BepInEx", "AskaServerStatus.txt");
            if (File.Exists(path))
            {
                string content = File.ReadAllText(path);
                Log("=== CURRENT SERVER STATUS ===", "INFO");
                Log(content, "DEBUG");
            }
            else
            {
                Log($"Status file not found: {path}", "WARN");
            }
            ConsoleInput.Clear();
            return;
        }

        if (command.Equals("version", StringComparison.OrdinalIgnoreCase) || command.Equals("ver", StringComparison.OrdinalIgnoreCase))
        {
            Log("=== ASKA Dedicated Server Manager ===", "CMD");
            Log($"Version: {VersionText.Text}", "CMD");
            Log("Enjoy the game, Viking!", "CMD");
            ConsoleInput.Clear(); return;
        }
        if (command.Equals("settings", StringComparison.OrdinalIgnoreCase)) { OpenSettingsWindow(); ConsoleInput.Clear(); return; }
        if (command.Equals("start", StringComparison.OrdinalIgnoreCase))
        {
            if (GetServerProcess() != null) { Log("Server already running", "WARN"); ConsoleInput.Clear(); return; }
            StartServerAsync(); ConsoleInput.Clear(); return;
        }
        if (command.Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            var dialog = new ConfirmDialog("Stop server?");
            dialog.ShowDialog();
            if (!dialog.Result) { ConsoleInput.Clear(); return; }
            var proc = GetServerProcess();
            if (proc != null) await StopServer(proc);
            ConsoleInput.Clear(); return;
        }
        if (command.Equals("restart", StringComparison.OrdinalIgnoreCase))
        {
            var dialog = new ConfirmDialog("Restart server?");
            dialog.ShowDialog();
            if (dialog.Result) RestartServer();
            ConsoleInput.Clear(); return;
        }
        if (command.Equals("info", StringComparison.OrdinalIgnoreCase)) { CheckServerStatusManual(); ConsoleInput.Clear(); return; }
        if (command.Equals("backup", StringComparison.OrdinalIgnoreCase))
        {
            _ = Task.Run(() => MakeBackup());
            ConsoleInput.Clear();
            return;
        }
        if (command.Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            Log("  Available commands:", "CMD");
            Log("  info      - show process status", "CMD");
            Log("  settings  - open settings window", "CMD");
            Log("  start     - start server", "CMD");
            Log("  stop      - stop server", "CMD");
            Log("  restart   - restart server", "CMD");
            Log("  backup    - create manual backup", "CMD");
            Log("  list      - show this list", "CMD");
            Log("  clear     - clear log (with save)", "CMD");
            Log("  version   - show current Manager version", "CMD");
            Log("  status    - show server status (plugin)", "PLUGIN");
            ConsoleInput.Clear(); return;
        }
        if (command.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            ClearLog_Click(null, null);
            ConsoleInput.Clear();
            return;
        }

        Log($">>> Unknown command: {command}", "CMD");
        ConsoleInput.Clear();
    }

    // ---- Backup ----
    private void MakeBackup()
    {
        if (!isConfigured)
        {
            Log("Backup cancelled: settings not configured.", "WARN");
            return;
        }

        if (!Directory.Exists(backupDir))
        {
            Log($"Backup folder does not exist. Creating: {backupDir}", "INFO");
            Directory.CreateDirectory(backupDir);
        }

        string saveId = currentSaveId;
        if (string.IsNullOrEmpty(saveId))
        {
            saveId = GetCurrentSaveId();
            if (!string.IsNullOrEmpty(saveId))
                currentSaveId = saveId;
        }

        if (string.IsNullOrEmpty(saveId))
        {
            Log("Backup impossible: savegame directory not determined.", "WARN");
            return;
        }

        string worldFolderName = $"savegame_{saveId}";
        string worldFolderPath = Path.Combine(savePath, worldFolderName);
        if (!Directory.Exists(worldFolderPath))
        {
            Log($"Backup cancelled: world folder not found: {worldFolderPath}", "WARN");
            return;
        }

        DateTime currentWriteTime = Directory.GetLastWriteTime(worldFolderPath);
        if (currentWriteTime <= lastBackupWriteTime && lastBackupWriteTime != DateTime.MinValue)
        {
            Log("World hasn't changed since last backup. Backup postponed.", "INFO");
            secondsLeft = backupIntervalMinutes * 60;
            Dispatcher.Invoke(() =>
            {
                TxtBackupTimer.Text = $"Until backup: {backupIntervalMinutes:D2}:00";
                BackupProgress.Value = 100;
                BackupProgress.IsIndeterminate = false;
                BackupProgress.Foreground = Brushes.DodgerBlue;
                uiTimer.Start();
            });
            return;
        }

        if (isBackupInProgress)
        {
            Log("Backup already in progress, skipping duplicate request.", "WARN");
            return;
        }

        Dispatcher.Invoke(() =>
        {
            TxtBackupTimer.Text = "=== Creating backup ===";
            TxtServerOff.Text = "";
            TxtBackupTimer.Visibility = Visibility.Visible;
            TxtBackupInfo.Visibility = Visibility.Collapsed;
            BackupProgress.Visibility = Visibility.Visible;
            BackupProgress.IsIndeterminate = true;
            BackupProgress.Foreground = Brushes.Gold;
            StatusBarText.Text = "Creating backup...";
        });

        isBackupInProgress = true;
        Log($"Creating backup for world {saveId}...", "INFO");

        try
        {
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
            string fileName = Path.Combine(backupDir, $"Aska_Backup-{stamp}.zip");
            string tempDir = Path.Combine(Path.GetTempPath(), $"AskaBackup_{Guid.NewGuid()}");
            Directory.CreateDirectory(tempDir);
            try
            {
                CopyDirectory(worldFolderPath, Path.Combine(tempDir, worldFolderName));
                ZipFile.CreateFromDirectory(tempDir, fileName);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }

            long sizeMB = new FileInfo(fileName).Length / (1024 * 1024);
            Log($"Backup created: {Path.GetFileName(fileName)} ({sizeMB} MB)", "INFO");

            var files = new DirectoryInfo(backupDir).GetFiles("*.zip").OrderByDescending(f => f.CreationTime).Skip(maxBackupCount);
            foreach (var f in files)
            {
                FileSystem.DeleteFile(f.FullName, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                Log($"Deleted old backup: {f.Name}", "INFO");
            }

            lastBackupWriteTime = currentWriteTime;
            secondsLeft = backupIntervalMinutes * 60;

            Dispatcher.Invoke(() =>
            {
                TxtBackupTimer.Visibility = Visibility.Collapsed;
                TxtBackupTimer.Text = $"Until backup: {backupIntervalMinutes:D2}:00";
                //TxtBackupInfo.Visibility = Visibility.Visible;
                UpdateBackupInfo();
                BackupProgress.Value = 100;
                BackupProgress.IsIndeterminate = false;
                BackupProgress.Foreground = Brushes.DodgerBlue;
                StatusBarText.Text = "Ready";
                TxtServerOff.Text = "=== Server offline ===";
            });
        }
        catch (Exception ex)
        {
            Log($"Backup error: {ex.Message}", "ERROR");
            Dispatcher.Invoke(() =>
            {
                TxtBackupTimer.Text = $"Until backup: {backupIntervalMinutes:D2}:00";
                TxtBackupInfo.Visibility = Visibility.Visible;
                UpdateBackupInfo();
                BackupProgress.Value = 100;
                BackupProgress.IsIndeterminate = false;
                BackupProgress.Foreground = Brushes.DodgerBlue;
                StatusBarText.Text = "Backup error";
            });
        }
        finally
        {
            isBackupInProgress = false;
            Dispatcher.Invoke(() => BackupProgress.Visibility = GetServerProcess() != null ? Visibility.Visible : Visibility.Collapsed);
        }
    }

    private void UpdateBackupInfo()
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                if (!Directory.Exists(backupDir) || !Directory.EnumerateFiles(backupDir, "*.zip").Any())
                {
                    TxtBackupInfo.Text = "No backups";
                    return;
                }
                var files = Directory.GetFiles(backupDir, "*.zip");
                var last = files.OrderByDescending(f => new FileInfo(f).CreationTime).First();
                long lastSizeKB = new FileInfo(last).Length / 1024;
                long totalSizeMB = files.Sum(f => new FileInfo(f).Length) / (1024 * 1024);
                TxtBackupInfo.Text = $"{lastSizeKB} KB / {totalSizeMB} MB";
                TxtBackupInfo.ToolTip = $"Last backup: {Path.GetFileName(last)}\nTotal size: {totalSizeMB} MB";
            }
            catch (Exception ex) { TxtBackupInfo.Text = "Error"; Log($"Error getting backup info: {ex.Message}", "ERROR"); }
        });
    }

    private void UpdateVisualTimer()
    {
        if (isBackupInProgress) return;
        if (secondsLeft > 0 && TxtBackupTimer.Visibility == Visibility.Visible && isConfigured)
        {
            secondsLeft--;
            int mins = secondsLeft / 60;
            int secs = secondsLeft % 60;
            TxtBackupTimer.Text = $"Until backup: {mins:D2}:{secs:D2}";
            BackupProgress.Value = ((double)secondsLeft / (backupIntervalMinutes * 60)) * 100;
        }
    }

    private void UpdateServerInfo()
    {
        if (isBackupInProgress) return; // не трогаем UI, пока идёт бэкап

        bool isRunning = GetServerProcess() != null;
        MenuStartServer.IsEnabled = !isRunning;
        MenuStopServer.IsEnabled = isRunning;
        MenuRestartServer.IsEnabled = isRunning;

        if (isRunning && isConfigured)
        {
            StatusLed.Fill = Brushes.LimeGreen;
            TxtPlayerNames.Visibility = Visibility.Visible;
            TxtBackupTimer.Visibility = Visibility.Visible;
            BackupProgress.Visibility = Visibility.Visible;
            TxtServerRam.Visibility = Visibility.Visible;
            TxtStats.Visibility = Visibility.Visible;
            TxtBackupInfo.Visibility = Visibility.Visible;
            TxtServerOff.Visibility = Visibility.Collapsed;
        }
        else
        {
            StatusLed.Fill = Brushes.Red;
            TxtPlayerNames.Visibility = Visibility.Collapsed;
            TxtBackupTimer.Visibility = Visibility.Collapsed;
            BackupProgress.Visibility = Visibility.Collapsed;
            TxtServerRam.Visibility = Visibility.Collapsed;
            TxtStats.Visibility = Visibility.Collapsed;
            TxtServerOff.Visibility = Visibility.Visible;
            TxtBackupInfo.Visibility = Visibility.Collapsed;

        }
    }

    private void CheckIfServerAlreadyRunning()
    {
        var proc = GetServerProcess();
        if (proc != null)
        {
            Log("=== SERVER INFORMATION ===", "CMD");
            Log($"Detected running process: {proc.ProcessName} (PID: {proc.Id})", "INFO");
            Log($"Server uptime: {DateTime.Now - proc.StartTime:hh\\:mm\\:ss}", "INFO");
            Log("Server process detected. Please configure paths in settings to enable monitoring.", "INFO");
            UpdateServerInfo();
            UpdateServerHeader();
            currentSaveId = GetCurrentSaveId();
        }
    }

    private void CheckServerProcess()
    {
        // Если сервер не был запущен через менеджер, не отслеживаем
        if (_serverProcess == null) return;

        bool isRunning = !_serverProcess.HasExited;

        if (isConfigured && !isRunning && !isStoppingManually && serverWasRunning)
        {
            // Игнорируем, если ручная остановка была меньше 15 секунд назад
            if ((DateTime.Now - _lastManualStopTime).TotalSeconds < 15)
            {
                serverWasRunning = false;
                return;
            }

            Log("⚠️ WARNING: Server unexpectedly stopped!", "WARN");
            backupTimer.Stop();
            uiTimer.Stop();
            serverStartedLogged = false;
            previousPlayers.Clear();
            _lastPlayersCount = 0;
            Dispatcher.Invoke(() => TxtPlayerNames.Text = "");
            secondsLeft = backupIntervalMinutes * 60;
            TxtBackupTimer.Text = $"Until backup: {backupIntervalMinutes:D2}:00";
            BackupProgress.Value = 100;
            UpdateServerInfo();
            serverWasRunning = false;

            _serverProcess.Dispose();
            _serverProcess = null;
        }
        else if (isRunning)
        {
            serverWasRunning = true;
        }
        else
        {
            serverWasRunning = false;
        }
    }

    private void CheckServerStatusManual()
    {
        Log("=== SERVER INFORMATION ===", "INFO");
        var proc = GetServerProcess();
        if (proc != null && !proc.HasExited)
        {
            Log($"PID: {proc.Id}", "INFO");
            Log($"Memory: {proc.WorkingSet64 / (1024 * 1024)} MB", "INFO");
            Log($"Start time: {proc.StartTime}", "INFO");
            Log($"Uptime: {(DateTime.Now - proc.StartTime):hh\\h\\ mm\\m}", "INFO");
            var config = ReadServerConfig();
            if (config.TryGetValue("save id", out string? saveId)) Log($"Save ID: {saveId}", "INFO");
            if (config.TryGetValue("server name", out string? serverName)) Log($"Server name: {serverName}", "INFO");
            if (config.TryGetValue("display name", out string? displayName)) Log($"Display name: {displayName}", "INFO");
            if (config.TryGetValue("seed", out string? seed)) Log($"Seed: {seed}", "INFO");
            if (config.TryGetValue("region", out string? region)) Log($"Region: {region}", "INFO");
            if (config.TryGetValue("steam game port", out string? gamePort)) Log($"Game port: {gamePort}", "INFO");
            if (config.TryGetValue("steam query port", out string? queryPort)) Log($"Query Port: {queryPort}", "INFO");
            if (config.TryGetValue("keep server world alive", out string? keepAlive))
                Log($"Keep server world alive: {(keepAlive == "true" ? "True" : "False")}", "INFO");
            serverWasRunning = true;
        }
        else
        {
            Log("Server not running", "INFO");
            serverWasRunning = false;
        }
        UpdateServerInfo();
    }

    private Dictionary<string, string> ReadServerConfig()
    {
        var dict = new Dictionary<string, string>();
        if (!File.Exists(propertiesFilePath)) return dict;
        foreach (var line in File.ReadAllLines(propertiesFilePath))
        {
            if (line.TrimStart().StartsWith("//") || string.IsNullOrWhiteSpace(line)) continue;
            int eq = line.IndexOf('=');
            if (eq > 0) dict[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return dict;
    }

    private void UpdateServerHeader()
    {
        var cfg = ReadServerConfig();
        TxtDisplayName.Visibility = Visibility.Visible;
        TxtServerName.Visibility = Visibility.Visible;
        TxtDisplayName.Text = cfg.TryGetValue("display name", out string? displayName) ? displayName.ToUpper() : "DISPLAY NAME";
        TxtServerName.Text = cfg.TryGetValue("server name", out string? serverName) ? serverName.ToUpper() : "SERVER NAME";
    }

    // ---- Menu actions ----
    private void OpenSavesFolder()
    {
        try
        {
            string localLow = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData).Replace("Local", "LocalLow"));
            string path = Path.Combine(localLow, "Sand Sailor Studio", "Aska", "data", "server");
            if (Directory.Exists(path)) Process.Start("explorer.exe", path);
            else Log($"Savegame folder not found: {path}", "WARN");
        }
        catch (Exception ex) { Log($"Error opening folder: {ex.Message}", "ERROR"); }
    }

    private void OpenBackupsFolder()
    {
        if (!string.IsNullOrEmpty(backupDir) && Directory.Exists(backupDir))
            Process.Start("explorer.exe", backupDir);
        else Log("Backup folder not configured or does not exist.", "WARN");
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Directory.Exists(logDirectory)) Directory.CreateDirectory(logDirectory);
            Process.Start("explorer.exe", logDirectory);
            Log($"Opened log folder: {logDirectory}", "INFO");
        }
        catch (Exception ex) { Log($"Error opening log folder: {ex.Message}", "ERROR"); }
    }

    private void SaveLogToFile(object? sender, RoutedEventArgs? e)
    {
        EnsureLogDirectory();
        try
        {
            TextRange range = new(LogBox.Document.ContentStart, LogBox.Document.ContentEnd);
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
            string path = Path.Combine(logDirectory, $"Log_{stamp}.txt");
            File.WriteAllText(path, range.Text);
            Log($"Log saved to file: {path}", "INFO");
        }
        catch (Exception ex) { Log($"Error saving log: {ex.Message}", "ERROR"); }
    }

    private void SaveLogAs(object sender, RoutedEventArgs e)
    {
        EnsureLogDirectory();
        try
        {
            string text = LogBox.Selection.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                TextRange range = new(LogBox.Document.ContentStart, LogBox.Document.ContentEnd);
                text = range.Text;
            }
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = $"Log_{DateTime.Now:yyyy-MM-dd_HHmmss}",
                DefaultExt = ".txt",
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                InitialDirectory = logDirectory
            };
            if (dlg.ShowDialog() == true)
            {
                File.WriteAllText(dlg.FileName, text);
                Log($"Text saved to file: {dlg.FileName}", "INFO");
            }
        }
        catch (Exception ex) { Log($"Error saving: {ex.Message}", "ERROR"); }
    }

    private void CopyLogToClipboard(object sender, RoutedEventArgs e)
    {
        try
        {
            string selected = LogBox.Selection.Text;
            if (!string.IsNullOrWhiteSpace(selected))
            {
                System.Windows.Clipboard.SetText(selected);
                Log("Selected text copied to clipboard.", "INFO");
            }
            else
            {
                TextRange range = new(LogBox.Document.ContentStart, LogBox.Document.ContentEnd);
                System.Windows.Clipboard.SetText(range.Text);
                Log("Entire log copied to clipboard.", "INFO");
            }
        }
        catch (Exception ex) { Log($"Error copying: {ex.Message}", "ERROR"); }
    }

    private void ClearLog_Click(object? sender, RoutedEventArgs? e)
    {
        SaveLogToFile(sender, e);
        LogBox.Document.Blocks.Clear();
        Log("Log cleared (previous saved)", "INFO");
    }

    private void Faq_Click(object sender, RoutedEventArgs e)
    {
        var faqWindow = new FaqWindow();
        faqWindow.Owner = this;
        faqWindow.ShowDialog();
    }

    private void About_Click(object sender, RoutedEventArgs e) => new AboutWindow().ShowDialog();

    private void ToggleServerLog_Click(object sender, RoutedEventArgs e)
    {
        showServerLog = MenuShowServerLog.IsChecked;
        if (App.Settings != null)
        {
            App.Settings.ShowServerLog = showServerLog;
            SaveSettingsToCfg(App.Settings);
        }
        Log($"Show server log: {(showServerLog ? "ON" : "OFF")}", "CONFIG");
        UpdateServerLogMenuItemColor();
    }

    private void MenuStartServer_Click(object sender, RoutedEventArgs e) => StartServerAsync();
    private async void MenuStopServer_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ConfirmDialog("Stop server?");
        dialog.ShowDialog();
        if (!dialog.Result) return;
        var proc = GetServerProcess();
        if (proc != null) await StopServer(proc);
    }
    private void MenuRestartServer_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ConfirmDialog("Restart server?");
        dialog.ShowDialog();
        if (dialog.Result) RestartServer();
    }

    private static string NormalizePathForDisplay(string path) => path?.Replace(@"\\", @"\") ?? "";

    private async void Window_Closing(object sender, CancelEventArgs e)
    {
        var runningProcess = GetServerProcess();
        if (runningProcess != null && !isStoppingManually)
        {
            var dialog = new ConfirmDialog("Server is running. Manager will shutdown server?");
            dialog.ShowDialog();
            if (dialog.Result)
            {
                e.Cancel = true;
                await StopServer(runningProcess);
                // После остановки сервера удаляем временные файлы
                DeletePluginTempFiles();
                await Task.Delay(2000);
                SaveWindowState();
                Environment.Exit(0);
                return;
            }
            else
            {
                e.Cancel = true;
                return;
            }
        }

        // Если сервер не был запущен или уже остановлен – удаляем файлы и выходим
        DeletePluginTempFiles();
        SaveWindowState();
        Environment.Exit(0);
    }

    private void DeletePluginTempFiles()
    {
        try
        {
            if (App.Settings != null && !string.IsNullOrEmpty(App.Settings.ServerDirectory))
            {
                string bepinDir = Path.Combine(App.Settings.ServerDirectory, "BepInEx");
                string statusFile = Path.Combine(bepinDir, "AskaServerStatus.txt");
                string commandFile = Path.Combine(bepinDir, "AskaCommand.txt");
                if (File.Exists(statusFile)) File.Delete(statusFile);
                if (File.Exists(commandFile)) File.Delete(commandFile);
            }
        }
        catch (Exception ex)
        {
            Log($"Failed to delete temporary plugin files: {ex.Message}", "WARN");
        }
    }

    private static class WinApi
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    }
}