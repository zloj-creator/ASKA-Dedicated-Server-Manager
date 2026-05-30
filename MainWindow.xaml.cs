using Microsoft.VisualBasic.FileIO;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;

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
    private bool _serverStarting = false;
    private bool _authFailedHandled = false; // флаг, чтобы не обрабатывать ошибку многократно
    private int _currentBackupInterval = 0;
    private bool serverStartedLogged = false;
    private Process? _serverProcess;
    private bool _autoScroll = true;
    private string currentSaveId = "";
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
    private readonly DispatcherTimer logRotationTimer;

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

    // Автоматический перезапуск
    private int _stuckCounter = 0;
    private double _lastGameTimeRaw = 0;
    private bool _isAutoRestarting = false;
    private int _stuckThreshold = 10; // будет вычисляться из StuckDetectionSeconds / 2
    private DateTime _lastAutoRestartTime = DateTime.MinValue;
    private int _autoRestartCountInWindow = 0;
    private DateTime _serverStartTime = DateTime.MinValue;
    // Автоматический перезапуск по интервалу (с ожиданием игроков)
    private DispatcherTimer? _intervalRestartTimer;
    private bool _intervalRestartPending = false;
    private DateTime _nextRestartTime = DateTime.MinValue;

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
        $"ShowServerLog = {(App.Settings?.ShowServerLog ?? false ? "true" : "false")}",
        $"SaveServerLog = {(settings.SaveServerLog ? "true" : "false")}",
        $"CheckForUpdatesAtStart = {(settings.CheckForUpdatesAtStart ? "true" : "false")}",
        $"AutoRestartOnStuck = {(settings.AutoRestartOnStuck ? "true" : "false")}",
        $"StuckDetectionSeconds = {settings.StuckDetectionSeconds}",
        // Новые параметры
        $"AutoRestartIntervalEnabled = {(settings.AutoRestartIntervalEnabled ? "true" : "false")}",
        $"AutoRestartIntervalMinutes = {settings.AutoRestartIntervalMinutes}"
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
        {
            Dispatcher.Invoke(() => Log(e.Data, "SERVER"));

            // Проверка на ошибку аутентификации токена
            if (!_authFailedHandled && e.Data.Contains("Disconnected with reason: CustomAuthenticationFailed"))
            {
                _authFailedHandled = true;

                // Вывод сообщений об ошибке в UI
                Dispatcher.Invoke(() =>
                {
                    Log("", "ERROR");
                    Log("=== AUTHENTICATION FAILURE ===", "ERROR");
                    Log("The server could not authenticate with Steam because", "ERROR");
                    Log("the 'authentication token' is missing or invalid.", "ERROR");
                    Log("Please follow these steps to create a token:", "ERROR");
                    Log("1. Visit: https://steamcommunity.com/dev/managegameservers", "ERROR");
                    Log("2. Log in with your Steam account.", "ERROR");
                    Log("3. Create a new token using App ID: 1898300 (name ASKA server).", "ERROR");
                    Log("4. Copy the generated token.", "ERROR");
                    Log("5. Open menu Server - Edit Configuration and paste token:", "ERROR");
                    Log("6. Click Save and Start server again.", "ERROR");
                    Log(" ", "ERROR");
                    Log("The server will now be forcefully terminated to prevent restart loops.", "ERROR");
                });

                // Принудительное завершение процесса сервера (Kill), чтобы предотвратить автоперезапуск
                _ = Task.Run(() =>
                {
                    try
                    {
                        // Небольшая задержка, чтобы сообщения успели отобразиться
                        Task.Delay(500).Wait();

                        var proc = GetServerProcess() ?? _serverProcess;
                        if (proc != null && !proc.HasExited)
                        {
                            Log("Forcefully terminating the server process...", "WARN");
                            proc.Kill();
                            proc.WaitForExit(3000);
                            Log("Server process terminated.", "INFO");
                            _serverStarting = false;
                        }

                        // Очистка ресурсов
                        if (_serverProcess != null)
                        {
                            _serverProcess.OutputDataReceived -= OnServerOutput;
                            _serverProcess.ErrorDataReceived -= OnServerError;
                            _serverProcess.Dispose();
                            _serverProcess = null;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Error during forced termination: {ex.Message}", "ERROR");
                    }
                    finally
                    {
                        // Обновляем состояние UI
                        Dispatcher.Invoke(() =>
                        {
                            TxtServerStatus.Text = "=== Server offline ===";
                            BackupProgress.IsIndeterminate = false;
                            BackupProgress.Value = 100;
                            serverWasRunning = false;
                            isStoppingManually = false;
                            UpdateServerInfo();
                            UpdateMenuState();
                            StatusBarText.Text = "Ready";
                        });
                    }
                });
            }
        }
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
        LoadSettings(false, false);
        UpdateMenuState();
        MenuShowServerLog.IsChecked = App.Settings?.ShowServerLog ?? false;
        UpdateServerLogMenuItemColor();
        if (App.Settings?.CheckForUpdatesAtStart == true && isConfigured)
            _ = CheckForUpdatesAsync(false);
        if (GetServerProcess() != null && _serverProcess == null)
        {
            Log("Server already running but not started by Manager. Server Log will not be shown. Command RESTART to restart server.", "WARN");
        }

        if (App.Settings?.LoadDailyLogOnStart == true)
            LoadTodayLog();

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
        LogBox.Document.PageWidth = LogBox.ActualWidth;
        LogBox.SizeChanged += (s, e) => LogBox.Document.PageWidth = LogBox.ActualWidth;
    }

    // Начало

    public void ResetAuthFailedFlag()
    {
        _authFailedHandled = false;
        //Log("Authentication failure flag reset. You can now start the server.", "INFO");
    }
    private void UpdateServerLogMenuItemColor()
    {
        if (MenuShowServerLog == null) return;
        bool show = App.Settings?.ShowServerLog ?? false;
        MenuShowServerLog.Foreground = show ? Brushes.White : (SolidColorBrush)FindResource("ForegroundBrush");
    }

    private void PlayJoinSound()
    {
        // Нам больше не нужно проверять _soundsAvailable, так как звуки зашиты в EXE
        PlayInternalSound("join.wav");
    }

    private void PlayLeaveSound()
    {
        PlayInternalSound("leave.wav");
    }

    // НОВЫЙ ВСПОМОГАТЕЛЬНЫЙ МЕТОД (добавь его ниже)
    private void PlayInternalSound(string fileName)
    {
        try
        {
            var uri = new Uri($"pack://application:,,,/{fileName}");
            var streamInfo = System.Windows.Application.GetResourceStream(uri);

            if (streamInfo != null)
            {
                using (var player = new System.Media.SoundPlayer(streamInfo.Stream))
                {
                    player.Play();
                }
            }
        }
        catch (Exception ex)
        {
            // Логируем в DEBUG, чтобы не пугать пользователя в обычном логе
            System.Diagnostics.Debug.WriteLine($"[Sound Error] {fileName}: {ex.Message}");
        }
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

        // Отображение списка игроков (всегда из лога)
        if (serverRunning && previousPlayers.Count > 0)
            TxtPlayerNames.Text = "Ingame: " + string.Join(", ", previousPlayers);
        else if (serverRunning)
            TxtPlayerNames.Text = "";
        else
            TxtPlayerNames.Text = "";

        if (!serverRunning)
        {
            //TxtStats.Text = "Players N/A | Time: N/A | Season: N/A | Villagers: N/A | Days Survived: N/A";
            TxtStats.Text = "Players N/A | Time: N/A | Season: N/A | Days Survived: N/A";
            return;
        }

        // Проверяем, есть ли данные от плагина (значимые значения)
        bool hasPluginData = _serverGameTimeRaw > 0 || (_lastSeason != null && _lastSeason != "N/A") || _lastVillagers > 0;

        if (hasPluginData)
        {
            if (previousPlayers.Count == 0)
            {
                // Нет игроков – показываем ожидание
                TxtStats.Text = "Waiting for players";
            }
            else
            {
                // Полная информация: игроки, время, сезон, дни
                string playersPart = $"Players {_lastPlayersCount}/4";
                double raw = _smoothTime % 24;
                int hour = (int)raw;
                int minute = (int)((raw - hour) * 60);
                if (minute >= 60) { minute = 0; hour++; }
                string timeStr = $"{hour:D2}:{minute:D2}";
                TxtStats.Text = $"{playersPart} | Time: {timeStr} | Season: {_lastSeason} | Days Survived: {_lastDaysSurvived}";
            }
        }
        else
        {
            // Плагин не установлен или не отвечает
            if (previousPlayers.Count > 0)
            {
                TxtStats.Text = $"Players {_lastPlayersCount}/4 | Ingame: {string.Join(", ", previousPlayers)}";
            }
            else
            {
                TxtStats.Text = "Waiting for players";
            }
        }
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

    public Process? GetServerProcess() => Process.GetProcessesByName("AskaServer").FirstOrDefault();

    private void UpdateServerRam()
    {
        var proc = GetServerProcess();
        if (proc != null && !proc.HasExited)
        {
            try
            {
                // 🔥 CRITICAL: Update the process info from the OS
                proc.Refresh();

                // Use PrivateMemorySize64 for accurate game world allocation
                long ramMB = proc.WorkingSet64 / (1024 * 1024);
                Dispatcher.Invoke(() => TxtServerRam.Text = $"RAM: {ramMB} MB");
            }
            catch { /* Process might have closed during read */ }
        }
        else
        {
            Dispatcher.Invoke(() => TxtServerRam.Text = "");
        }
    }
    private void IntervalRestartTimer_Tick(object? sender, EventArgs e)
    {
        _nextRestartTime = DateTime.MinValue;
        //_intervalRestartTimer?.Stop();
        _intervalRestartPending = true;
        Log($"Scheduled restart interval reached. Waiting for players to leave before restarting.", "INFO");
        // Если игроков нет сейчас – перезапускаем немедленно
        if (previousPlayers.Count == 0 && !isStoppingManually)
        {
            Log("No players online. Restarting now.", "INFO");
            RestartServer();
            _intervalRestartPending = false;
        }
    }

    internal void Log(string message, string prefix)
    {
        bool isServerLog = (prefix == "SERVER" || prefix == "SERVER ERR");
        bool saveServerLog = App.Settings?.SaveServerLog == true;   // true = сохранять в файл

        // Сохраняем в файл, ТОЛЬКО если НЕ серверный лог ИЛИ разрешено сохранять серверный лог
        if (!isServerLog || saveServerLog)
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
                _isAutoRestarting = false; // сбрасываем флаг после успешного запуска
                _serverStarting = false;
                serverStartedLogged = true;
                Log("Server started successfully!", "CMD");
                Dispatcher.Invoke(() =>
                {
                    StatusBarText.Text = "Ready";
                    // Сброс индикации запуска
                    TxtServerStatus.Visibility = Visibility.Collapsed;
                    BackupProgress.IsIndeterminate = false;
                    BackupProgress.Value = 100; // восстановить нормальный вид
                    BackupProgress.Visibility = Visibility.Visible; // будет виден, так как сервер работает
                                                                    // Показать элементы статистики
                    TxtStats.Visibility = Visibility.Visible;
                    TxtPlayerNames.Visibility = Visibility.Visible;
                    TxtServerRam.Visibility = Visibility.Visible;
                    TxtBackupTimer.Visibility = Visibility.Visible;
                    TxtBackupInfo.Visibility = Visibility.Visible;

                    // Запуск таймеров (перенесены сюда)
                    secondsLeft = backupIntervalMinutes * 60;
                    TxtBackupTimer.Text = $"Until backup: {backupIntervalMinutes:D2}:00";
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

                    // Обновить отображение (чтобы показать RAM и т.д.)
                    UpdateServerInfo();
                    Log($"Backup timer activated ({backupIntervalMinutes} minutes).", "CONFIG");
                    isStoppingManually = false;
                    UpdateMenuState();
                    _serverStartTime = DateTime.Now;
                    RecreateIntervalRestartTimer();
                });
            }
        }

        // фильтруем показ серверных логов (!isImportant)
        if (isServerLog && !(App.Settings?.ShowServerLog ?? false) && !isImportant)
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
                "JOIN" => Brushes.Yellow,
                "LEAVE" => Brushes.GreenYellow,
                "BACKUP" => Brushes.Goldenrod,
                "TIMER" => Brushes.DarkCyan,
                "CONFIG" => Brushes.LightSeaGreen,
                "SERVER" => Brushes.CornflowerBlue,
                "SERVER ERR" => Brushes.Red,
                "DEBUG" => Brushes.Gray,
                "STEAMCMD" => Brushes.Cyan,
                _ => Brushes.LightGray
            };
            var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = 1, TextAlignment = TextAlignment.Left };
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

        // Отключение: "<имя> disconnected!"
        int disconnectIdx = trimmed.IndexOf(" disconnected!", StringComparison.OrdinalIgnoreCase);
        if (disconnectIdx > 0)
        {
            string playerName = trimmed.Substring(0, disconnectIdx).Trim();
            if (previousPlayers.Remove(playerName))
            {
                Log($"{playerName} disconnected", "LEAVE");
                PlayLeaveSound();
            }
            UpdatePlayerCountUI();
            return;
        }

        // Подключение: "<имя> connected!"
        int connectIdx = trimmed.IndexOf(" connected!", StringComparison.OrdinalIgnoreCase);
        if (connectIdx > 0)
        {
            string playerName = trimmed.Substring(0, connectIdx).Trim();
            if (!string.IsNullOrEmpty(playerName) && !previousPlayers.Contains(playerName))
            {
                previousPlayers.Add(playerName);
                Log($"{playerName} connected", "JOIN");
                PlayJoinSound();
            }
            UpdatePlayerCountUI();
            return;
        }
    }

    // Вынес обновление UI в отдельный метод, чтобы не дублировать код
    private void UpdatePlayerCountUI()
    {
        int oldCount = _lastPlayersCount;
        _lastPlayersCount = previousPlayers.Count;

        // Реагируем только на изменение количества игроков
        if (oldCount != _lastPlayersCount)
        {
            ResetStuckDetection();

            // Случай: последний игрок вышел, и ожидается перезапуск по интервальному таймеру
            if (oldCount > 0 && _lastPlayersCount == 0 && _intervalRestartPending && !isStoppingManually)
            {
                Log("All players have left. Executing delayed restart.", "INFO");
                _intervalRestartPending = false;
                RestartServer();
                return;
            }
            // Удалён блок, который отменял перезапуск при подключении игрока
        }

        Dispatcher.Invoke(() =>
        {
            TxtPlayerNames.Text = previousPlayers.Count > 0 ? "In game: " + string.Join(", ", previousPlayers) : "";
            UpdateGameTimeDisplay();
        });
    }

    private void ResetStuckDetection()
    {
        _stuckCounter = 0;
        _lastGameTimeRaw = 0;
        DebugLog("Stuck detection reset due to player count change.");
    }

    // ---- Settings handling ----
    private void LoadSettings(bool silent = false, bool isReloading = false)
    {
        try
        {
            validationErrors.Clear();

            // Если файла настроек нет – создаём дефолтный и выходим (App.Settings останется null)
            if (!File.Exists(settingsPath))
            {
                ResetToUnconfiguredState();
                CreateDefaultSettingsFile();
                Log("Please open Settings (⚙️) and specify your Aska server folder.", "WARN");
                return;
            }

            var cfg = ParseCfgFile(settingsPath);

            // 1. Создаём временный объект и заполняем ВСЕ настройки из cfg
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
            settings.SaveServerLog = cfg.GetValueOrDefault("SaveServerLog", "false").Equals("true", StringComparison.OrdinalIgnoreCase);
            settings.CheckForUpdatesAtStart = cfg.GetValueOrDefault("CheckForUpdatesAtStart", "false").Equals("true", StringComparison.OrdinalIgnoreCase);
            settings.AutoRestartOnStuck = cfg.GetValueOrDefault("AutoRestartOnStuck", "true").Equals("true", StringComparison.OrdinalIgnoreCase);
            settings.StuckDetectionSeconds = int.TryParse(cfg.GetValueOrDefault("StuckDetectionSeconds", "300"), out int stuck) ? stuck : 300;
            settings.AutoRestartIntervalEnabled = cfg.GetValueOrDefault("AutoRestartIntervalEnabled", "false").Equals("true", StringComparison.OrdinalIgnoreCase);
            settings.AutoRestartIntervalMinutes = int.TryParse(cfg.GetValueOrDefault("AutoRestartIntervalMinutes", "120"), out int intervalMin) ? intervalMin : 120;

            // UI-настройка ShowServerLog (отдельное свойство в AppSettings)
            bool showServerLogValue = cfg.GetValueOrDefault("ShowServerLog", "false").Equals("true", StringComparison.OrdinalIgnoreCase);

            // 2. Валидируем пути (без изменений в App.Settings)
            ValidateSettings(settings);
            bool pathsValid = validationErrors.Count == 0;

            // 3. Всегда присваиваем App.Settings
            if (App.Settings == null) App.Settings = new AppSettings();
            App.Settings = settings;
            App.Settings.ShowServerLog = showServerLogValue; // добавляем UI-настройку

            // Инициализация параметров авто-перезапуска (после того как App.Settings полностью заполнен)
            _stuckThreshold = App.Settings.StuckDetectionSeconds / 2;
            if (_stuckThreshold < 1) _stuckThreshold = 1;
            
            // 4. Применяем UI-настройки сразу (меню, цвет)
            MenuShowServerLog.IsChecked = App.Settings.ShowServerLog;
            UpdateServerLogMenuItemColor();
                        
            // 5. Устанавливаем флаг isConfigured...
            isConfigured = pathsValid;

            if (!pathsValid)
            {
                Log("ERRORS IN SETTINGS (paths are invalid):", "ERROR");
                foreach (var err in validationErrors) Log($"  - {err}", "ERROR");
                // Не возвращаемся, а показываем панель ошибки. App.Settings уже загружен.
                UnconfiguredPanel.Visibility = Visibility.Visible;
                if (ServerMissingPanel != null) ServerMissingPanel.Visibility = Visibility.Collapsed;
                ServerInfoPanel.Visibility = Visibility.Collapsed;
                EnableServerControls(false);
                // Остальное (таймеры и т.д.) не инициализируем, так как пути невалидны
                return;
            }

            // --- Валидация успешна ---
            int newInterval = App.Settings.BackupIntervalMinutes;
            bool intervalChanged = (newInterval != _currentBackupInterval) && (_currentBackupInterval != 0);
            _currentBackupInterval = newInterval;

            UpdateSettingsWithDefaults(App.Settings);
            backupIntervalMinutes = App.Settings.BackupIntervalMinutes;
            maxBackupCount = App.Settings.MaxBackupCount;

            // Вычисление путей
            string localLowPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData).Replace("Local", "LocalLow"));
            savePath = Path.Combine(localLowPath, "Sand Sailor Studio", "Aska", "data", "server");
            serverExe = Path.Combine(App.Settings.ServerDirectory, App.Settings.ServerExecutable);
            propertiesFilePath = Path.Combine(App.Settings.ServerDirectory, App.Settings.PropertiesFileName);
            backupDir = App.Settings.BackupDirectory;
            if (string.IsNullOrWhiteSpace(backupDir))
            {
                backupDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ASKA_Server_Backup");
                App.Settings.BackupDirectory = backupDir;
                Log($"Backup folder not specified. Default set to: {backupDir}", "INFO");
                if (!Directory.Exists(backupDir))
                    Directory.CreateDirectory(backupDir);
                SaveSettingsToCfg(App.Settings);
            }

            if (!Path.IsPathRooted(App.Settings.BackupDirectory))
            {
                App.Settings.BackupDirectory = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, App.Settings.BackupDirectory));
                Log($"Backup directory: {App.Settings.BackupDirectory}", "INFO");
            }

            if (!string.IsNullOrEmpty(App.Settings.QueryIP)) queryIP = App.Settings.QueryIP;
            if (App.Settings.QueryPort > 0) queryPort = App.Settings.QueryPort;

            EnableServerControls(true);

            // Обновление панелей
            bool serverInstalled = File.Exists(Path.Combine(App.Settings.ServerDirectory, "AskaServer.exe"));
            if (serverInstalled)
            {
                UnconfiguredPanel.Visibility = Visibility.Collapsed;
                if (ServerMissingPanel != null) ServerMissingPanel.Visibility = Visibility.Collapsed;
                ServerInfoPanel.Visibility = Visibility.Visible;
            }
            else
            {
                UnconfiguredPanel.Visibility = Visibility.Collapsed;
                if (ServerMissingPanel != null) ServerMissingPanel.Visibility = Visibility.Visible;
                ServerInfoPanel.Visibility = Visibility.Collapsed;
            }

            UpdateBackupInfo();

            var serverProc = GetServerProcess();
            if (serverProc != null)
            {
                if (isReloading && !intervalChanged)
                {
                    if (!backupTimer.IsEnabled) backupTimer.Start();
                    if (!uiTimer.IsEnabled) uiTimer.Start();
                    if (!pluginTimer.IsEnabled) pluginTimer.Start();
                }
                else
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
            }

            currentSaveId = GetCurrentSaveId();
            if (!string.IsNullOrEmpty(currentSaveId))
            {
                string worldFolder = Path.Combine(savePath, $"savegame_{currentSaveId}");
                if (Directory.Exists(worldFolder))
                    lastBackupWriteTime = Directory.GetLastWriteTime(worldFolder);
            }

            UpdateServerHeader();
            UpdateMenuState();

            if (!silent)
            {
                string fullSavePath = string.IsNullOrEmpty(currentSaveId) ? savePath : Path.Combine(savePath, $"savegame_{currentSaveId}");
                Log($"Savegame folder: {fullSavePath}", "INFO");
            }
        }
        catch (Exception ex)
        {
            Log($"Error loading settings: {ex.Message}", "ERROR");
            ResetToUnconfiguredState();
        }
    }

    private void ValidateSettings(AppSettings settings)
    {
        // Определяем, установлен ли сервер
        string serverExePath = Path.Combine(settings.ServerDirectory, "AskaServer.exe");
        bool serverInstalled = File.Exists(serverExePath);

        if (string.IsNullOrEmpty(settings.ServerDirectory))
            validationErrors.Add("(⚙️) Specify ASKA server directory");
        else if (!Directory.Exists(settings.ServerDirectory))
            validationErrors.Add($"Server directory does not exist: {settings.ServerDirectory}");

        // Проверка конфигурационного файла ТОЛЬКО если сервер установлен
        if (serverInstalled)
        {
            if (string.IsNullOrEmpty(settings.PropertiesFileName))
                validationErrors.Add("(⚙️) Specify server config file path");
            else if (!File.Exists(Path.Combine(settings.ServerDirectory, settings.PropertiesFileName)))
                validationErrors.Add($"Config file not found: {settings.PropertiesFileName}");
        }

        if (settings.BackupIntervalMinutes < 1 || settings.BackupIntervalMinutes > 1440)
            validationErrors.Add($"BackupIntervalMinutes must be between 1 and 1440.");
        if (settings.MaxBackupCount < 1 || settings.MaxBackupCount > 100)
            validationErrors.Add($"MaxBackupCount must be between 1 and 100");
        if (settings.MaxLogSizeMB < 1 || settings.MaxLogSizeMB > 100)
            validationErrors.Add($"MaxLogSizeMB must be between 1 and 100");
        if (settings.MaxLogFiles < 1 || settings.MaxLogFiles > 1000)
            validationErrors.Add($"MaxLogFiles must be between 1 and 1000");
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
            "SaveServerLog = false"
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

        // Обновляем панели
        UnconfiguredPanel.Visibility = Visibility.Visible;
        if (ServerMissingPanel != null)
            ServerMissingPanel.Visibility = Visibility.Collapsed;
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

    private void ReloadSettings()
    {
        // Сохраняем старые значения для интервального перезапуска и бэкапа
        bool oldIntervalEnabled = App.Settings?.AutoRestartIntervalEnabled ?? false;
        int oldIntervalMinutes = App.Settings?.AutoRestartIntervalMinutes ?? 120;
        int oldBackupInterval = backupIntervalMinutes;

        // Перезагружаем настройки из файла
        LoadSettings(true, true);

        // Обновление панелей
        if (isConfigured && App.Settings != null)
        {
            bool serverInstalled = File.Exists(Path.Combine(App.Settings.ServerDirectory, "AskaServer.exe"));
            if (serverInstalled)
            {
                UnconfiguredPanel.Visibility = Visibility.Collapsed;
                if (ServerMissingPanel != null) ServerMissingPanel.Visibility = Visibility.Collapsed;
                ServerInfoPanel.Visibility = Visibility.Visible;
            }
            else
            {
                UnconfiguredPanel.Visibility = Visibility.Collapsed;
                if (ServerMissingPanel != null) ServerMissingPanel.Visibility = Visibility.Visible;
                ServerInfoPanel.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            UnconfiguredPanel.Visibility = Visibility.Visible;
            if (ServerMissingPanel != null) ServerMissingPanel.Visibility = Visibility.Collapsed;
            ServerInfoPanel.Visibility = Visibility.Collapsed;
        }

        // --- Таймер бэкапа (только если изменился интервал) ---
        if (isConfigured && GetServerProcess() != null)
        {
            if (oldBackupInterval != backupIntervalMinutes)
            {
                Log($"Backup interval changed: {oldBackupInterval} -> {backupIntervalMinutes} minutes", "CONFIG");
                uiTimer.Stop();
                backupTimer.Stop();
                backupTimer.Interval = TimeSpan.FromMinutes(backupIntervalMinutes);
                backupTimer.Start();
                uiTimer.Start();
                secondsLeft = backupIntervalMinutes * 60;
                TxtBackupTimer.Text = $"Until backup: {backupIntervalMinutes:D2}:00";
                BackupProgress.Value = 100;
                Log("Backup timer restarted with new interval.", "CONFIG");
            }
        }

        // --- Интервальный таймер (только если изменились его настройки) ---
        if (isConfigured && GetServerProcess() != null && App.Settings != null)
        {
            bool newIntervalEnabled = App.Settings.AutoRestartIntervalEnabled;
            int newIntervalMinutes = App.Settings.AutoRestartIntervalMinutes;

            if (oldIntervalEnabled != newIntervalEnabled || oldIntervalMinutes != newIntervalMinutes)
            {
                Log($"Scheduled restart configuration changed: enabled={oldIntervalEnabled}->{newIntervalEnabled}, interval={oldIntervalMinutes}->{newIntervalMinutes} minutes", "CONFIG");
                RecreateIntervalRestartTimer();
            }
        }
    }

    private void RecreateIntervalRestartTimer()
    {

        if (!isConfigured || GetServerProcess() == null) return;
        bool enabled = App.Settings?.AutoRestartIntervalEnabled ?? false;
        int minutes = App.Settings?.AutoRestartIntervalMinutes ?? 120;

        _intervalRestartTimer?.Stop();
        _intervalRestartPending = false;
        _nextRestartTime = DateTime.MinValue;

        if (enabled)
        {
            _intervalRestartTimer = new DispatcherTimer();
            _intervalRestartTimer.Interval = TimeSpan.FromMinutes(minutes);
            _intervalRestartTimer.Tick += IntervalRestartTimer_Tick;
            _intervalRestartTimer.Start();
            _nextRestartTime = DateTime.Now.AddMinutes(minutes);
            Log($"Auto-restart timer started with interval {minutes} minutes.", "CONFIG");
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
        _isAutoRestarting = false;
        _stuckCounter = 0;
        _lastGameTimeRaw = 0;
        _intervalRestartPending = false;
        _intervalRestartTimer?.Stop();

        // Блокировка запуска, если была ошибка аутентификации
        if (_authFailedHandled)
        {
            Log("Server start blocked due to previous authentication failure.", "WARN");
            Log("Please fix the 'authentication token' in the configuration and try to start server.", "WARN");
            return;
        }

        // Расширенная диагностика (без изменений)
        if (!isConfigured || App.Settings == null)
        {
            if (App.Settings == null || string.IsNullOrEmpty(App.Settings.ServerDirectory))
                Log("Settings not configured. Click (⚙️) Settings and specify ASKA server directory.", "WARN");
            else if (!File.Exists(Path.Combine(App.Settings.ServerDirectory, "AskaServer.exe")))
                Log("Server not installed. Use 'Install Server' from menu first.", "WARN");
            else if (string.IsNullOrEmpty(App.Settings.PropertiesFileName) || !File.Exists(Path.Combine(App.Settings.ServerDirectory, App.Settings.PropertiesFileName)))
                Log("Configuration file missing. Check your settings.", "WARN");
            else
                Log("Configuration error. Please verify settings.", "WARN");
            return;
        }

        try
        {
            if (!File.Exists(serverExe)) { Log($"Error: server file not found: {NormalizePathForDisplay(serverExe)}", "ERROR"); return; }
            if (!File.Exists(propertiesFilePath)) { Log($"Error: config file not found: {NormalizePathForDisplay(propertiesFilePath)}", "ERROR"); return; }

            // Индикация запуска
            Dispatcher.Invoke(() =>
            {
                TxtServerStatus.Text = "=== Starting Server ===";
                TxtServerStatus.Visibility = Visibility.Visible;
                BackupProgress.IsIndeterminate = true;
                BackupProgress.Visibility = Visibility.Visible;
                // Скрыть статистику, пока сервер не запустился
                TxtStats.Visibility = Visibility.Collapsed;
                TxtPlayerNames.Visibility = Visibility.Collapsed;
                TxtServerRam.Visibility = Visibility.Collapsed;
                TxtBackupTimer.Visibility = Visibility.Collapsed;
                TxtBackupInfo.Visibility = Visibility.Collapsed;
                StatusBarText.Text = "Starting server...";
            });

            _serverStarting = true;

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
                // Сброс индикации при ошибке
                Dispatcher.Invoke(() =>
                {
                    TxtServerStatus.Text = "=== Server offline ===";
                    TxtServerStatus.Visibility = Visibility.Visible;
                    BackupProgress.IsIndeterminate = false;
                    BackupProgress.Visibility = Visibility.Collapsed;
                    StatusBarText.Text = "Ready";
                    UpdateServerInfo(); // покажет оффлайн состояние
                });
                return;
            }

            // Таймеры больше не запускаем здесь. Они будут запущены после успешного старта (в Log)
            // Не вызываем await Task.Delay(10000) – убрано
            // secondsLeft, BackupProgress и т.д. будут установлены при старте
        }
        catch (Exception ex)
        {
            Log($"Error starting server: {ex.Message}", "ERROR");
            Dispatcher.Invoke(() =>
            {
                TxtServerStatus.Text = "=== Server offline ===";
                TxtServerStatus.Visibility = Visibility.Visible;
                BackupProgress.IsIndeterminate = false;
                BackupProgress.Visibility = Visibility.Collapsed;
                StatusBarText.Text = "Ready";
                UpdateServerInfo();
            });
        }
    }

    private async Task StopServer(Process process)
    {
        // Защита от повторного вызова
        if (isStoppingManually)
        {
            Log("StopServer already in progress, skipping.", "DEBUG");
            return;
        }
        _serverStarting = false;
        isStoppingManually = true;
        serverStartedLogged = false;

        Dispatcher.Invoke(() => StatusBarText.Text = "Stopping server...");
        serverWasRunning = false;
        _lastManualStopTime = DateTime.Now;

        if (App.Settings?.BackupOnStop == true)
        {
            Log("Creating backup before stopping server...", "BACKUP");
            await Task.Run(() => MakeBackup());
        }

        // Отписываемся от событий и закрываем процесс
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

        // Проверяем, не завершён ли уже процесс
        if (process.HasExited)
        {
            Log("Process already exited.", "INFO");
            isStoppingManually = false;
            Dispatcher.Invoke(() => StatusBarText.Text = "Ready");
            Dispatcher.Invoke(() => UpdateMenuState());
            return;
        }

        // Пытаемся закрыть окно процесса (если есть)
        try
        {
            List<IntPtr> windows = GetProcessWindows(process.Id);
            if (windows.Count == 0)
                process.CloseMainWindow();
            else
            {
                foreach (IntPtr hWnd in windows)
                    WinApi.SendMessage(hWnd, 0x0010, IntPtr.Zero, IntPtr.Zero);
            }
        }
        catch (Exception ex)
        {
            Log($"Error sending close message: {ex.Message}", "WARN");
        }

        // Асинхронное ожидание завершения с таймаутом (20 секунд)
        const int timeoutMs = 20000;
        int elapsed = 0;
        while (!process.HasExited && elapsed < timeoutMs)
        {
            await Task.Delay(100);
            process.Refresh();
            elapsed += 100;
        }

        if (!process.HasExited)
        {
            Log("Process did not exit gracefully, forcing kill...", "WARN");
            try
            {
                process.Kill();
                await Task.Delay(500);
            }
            catch (Exception ex)
            {
                Log($"Error killing process: {ex.Message}", "ERROR");
            }
        }

        // Очистка состояния (данные)
        previousPlayers.Clear();
        _lastPlayersCount = 0;
        _lastSeason = "N/A";
        _lastVillagers = 0;
        _lastDaysSurvived = 0;

        // Все UI-обновления – в одном блоке Dispatcher
        await Dispatcher.InvokeAsync(() =>
        {
            TxtPlayerNames.Text = "";
            UpdateGameTimeDisplay();
            TxtBackupTimer.Text = $"Until backup: {backupIntervalMinutes:D2}:00";
            BackupProgress.Value = 100;
            TxtServerStatus.Text = "=== Server offline ===";
            TxtServerStatus.Visibility = Visibility.Visible;
            BackupProgress.IsIndeterminate = false;
            BackupProgress.Visibility = Visibility.Collapsed;
            StatusBarText.Text = "Ready";
            UpdateServerInfo();      // обновит все остальные UI-элементы
            UpdateMenuState();       // обновит состояние меню
        });

        Log("Server stopped.", "CMD");

        // Сброс флагов и остановка таймеров (без UI)
        _isAutoRestarting = false;
        _stuckCounter = 0;
        _lastGameTimeRaw = 0;
        _intervalRestartTimer?.Stop();
        _nextRestartTime = DateTime.MinValue;
        _intervalRestartPending = false;
        _serverStartTime = DateTime.MinValue;
        backupTimer.Stop();
        uiTimer.Stop();
        Log("Backup timer stopped", "TIMER");
        secondsLeft = backupIntervalMinutes * 60;

        pluginTimer.Stop();
        _smoothTime = 0;
        _gameTimeTimer?.Stop();
        _serverGameTimeRaw = 0;
        _lastTimerTickTime = DateTime.MinValue;

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
        Dispatcher.Invoke(() => StatusBarText.Text = "Restarting server...");
        Log("Restarting server...", "INFO");
        await StopServer(process);   // корректно останавливает с сохранением
        Log("Server stopped. Starting in 3 seconds...", "INFO");
        await Task.Delay(3000);
        StartServerAsync();

        _intervalRestartPending = false;
        _intervalRestartTimer?.Stop();

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

            if (App.Settings?.AutoRestartOnStuck == true &&
            GetServerProcess() != null &&
            !_serverStarting &&
            !isStoppingManually &&
            !_isAutoRestarting &&
            previousPlayers.Count > 0 &&
            _serverGameTimeRaw > 0)   // ← ключевое условие: время должно идти
            {
                double currentRaw = _serverGameTimeRaw;
                if (_lastGameTimeRaw > 0)
                {
                    double delta = currentRaw - _lastGameTimeRaw;
                    if (delta <= 0.001)
                    {
                        _stuckCounter++;
                        if (_stuckCounter >= _stuckThreshold)
                        {
                            // защита от частых перезапусков
                            if ((DateTime.Now - _lastAutoRestartTime).TotalMinutes > 5)
                            {
                                _autoRestartCountInWindow = 0;
                                _lastAutoRestartTime = DateTime.Now;
                            }
                            _autoRestartCountInWindow++;

                            if (_autoRestartCountInWindow <= 3)
                            {
                                Log("Server appears stuck (game time not advancing). Automatic restart triggered.", "WARN");
                                _isAutoRestarting = true;
                                _stuckCounter = 0;
                                _ = Task.Run(() => RestartServer());
                            }
                            else
                            {
                                Log("Too many automatic restarts in a short time. Auto-restart disabled temporarily.", "ERROR");
                                App.Settings.AutoRestartOnStuck = false;
                                SaveSettingsToCfg(App.Settings);
                            }
                        }
                    }
                    else
                    {
                        _stuckCounter = 0;
                    }
                }
                _lastGameTimeRaw = currentRaw;
            }
            else
            {
                // Если нет игроков или время не идёт – сбрасываем счётчик и последнее значение
                _stuckCounter = 0;
                _lastGameTimeRaw = 0;
            }


            UpdateGameTimeDisplay();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"TryReadPluginDataAsync error: {ex.Message}");
        }
    }
    // ========== Обработчики меню ==========
    private async void MenuInstall_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteInstallAsync();
    }

    private async void MenuUpdate_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteUpdateAsync();
    }

    private async void MenuValidate_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteValidateAsync();
    }

    private async void MenuCheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        await CheckForUpdatesAsync(true);
    }

    private void MenuEditConfig_Click(object sender, RoutedEventArgs e)
    {
        if (App.Settings == null || string.IsNullOrEmpty(App.Settings.ServerDirectory))
        {
            Log("Settings not loaded or server directory not configured.", "ERROR");
            return;
        }
        if (!isConfigured)
        {
            Log("Settings are not properly configured. Please check settings.", "WARN");
            return;
        }
        string configFile = Path.Combine(App.Settings.ServerDirectory, App.Settings.PropertiesFileName ?? "");
        if (string.IsNullOrEmpty(App.Settings.PropertiesFileName) || !File.Exists(configFile))
        {
            Log($"Configuration file not found: {configFile}. Please check settings.", "WARN");
            var result = System.Windows.MessageBox.Show("Configuration file not found.\nDo you want to open settings?", "File Missing", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
                OpenSettingsWindow();
            return;
        }
        var win = new ConfigEditorWindow();
        win.Owner = this;
        win.ShowDialog();
        UpdateServerHeader();
    }

    private async Task ExecuteInstallAsync()
    {
        if (App.Settings == null)
        {
            Log("Settings not loaded. Cannot install server.", "ERROR");
            return;
        }
        if (string.IsNullOrWhiteSpace(App.Settings.ServerDirectory))
        {
            Log("Server directory not configured. Please set it in settings.", "ERROR");
            return;
        }

        string serverExePath = Path.Combine(App.Settings.ServerDirectory, "AskaServer.exe");
        if (File.Exists(serverExePath))
        {
            Log("Server already installed. Use 'update' to refresh.", "WARN");
            return;
        }

        await RunSteamCmdAsync($"+force_install_dir \"{App.Settings.ServerDirectory}\" +login anonymous +app_update 3246670 +quit", "install");
    }

    private async Task ExecuteUpdateAsync()
    {
        if (App.Settings == null)
        {
            Log("Settings not loaded. Cannot update server.", "ERROR");
            return;
        }
        if (!File.Exists(Path.Combine(App.Settings.ServerDirectory, "AskaServer.exe")))
        {
            Log("Server not installed. Use 'install' first.", "WARN");
            return;
        }
        await CheckForUpdatesAsync(true);
        // Если есть обновление (или всегда обновляем), запускаем steamcmd
        await RunSteamCmdAsync($"+force_install_dir \"{App.Settings.ServerDirectory}\" +login anonymous +app_update 3246670 +quit", "update");
    }

    private async Task ExecuteValidateAsync()
    {
        if (App.Settings == null)
        {
            Log("Settings not loaded. Cannot validate server.", "ERROR");
            return;
        }
        if (!File.Exists(Path.Combine(App.Settings.ServerDirectory, "AskaServer.exe")))
        {
            Log("Server not installed. Use 'install' first.", "WARN");
            return;
        }
        await RunSteamCmdAsync($"+force_install_dir \"{App.Settings.ServerDirectory}\" +login anonymous +app_update 3246670 validate +quit", "validate");
    }

    private void ExecuteEditConfig()
    {
        if (App.Settings == null)
        {
            Log("Settings not loaded. Cannot edit configuration.", "ERROR");
            return;
        }
        if (!isConfigured || string.IsNullOrEmpty(App.Settings.ServerDirectory))
        {
            Log("Server directory not configured.", "WARN");
            return;
        }
        string configFile = Path.Combine(App.Settings.ServerDirectory, App.Settings.PropertiesFileName ?? "server_config.txt");
        if (!File.Exists(configFile))
        {
            Log($"Config file not found: {configFile}. Run install first.", "WARN");
            return;
        }
        var win = new ConfigEditorWindow();
        win.Owner = this;
        win.ShowDialog();
    }

    private void UpdateMenuState()
    {
        if (App.Settings == null || string.IsNullOrEmpty(App.Settings.ServerDirectory))
        {
            // Настройки не заданы – показываем только Install (но он неактивен, пока нет папки)
            MenuInstall.Visibility = Visibility.Visible;
            MenuInstall.IsEnabled = false;
            MenuUpdate.Visibility = Visibility.Collapsed;
            MenuValidate.Visibility = Visibility.Collapsed;
            MenuCheckUpdate.Visibility = Visibility.Collapsed;
            MenuEditConfig.Visibility = Visibility.Collapsed;
            MenuStartServer.Visibility = Visibility.Collapsed;
            MenuStopServer.Visibility = Visibility.Collapsed;
            MenuRestartServer.Visibility = Visibility.Collapsed;
            return;
        }

        string serverExePath = Path.Combine(App.Settings.ServerDirectory, "AskaServer.exe");
        bool serverExeExists = File.Exists(serverExePath);
        bool serverRunning = GetServerProcess() != null;

        // Install Server: виден только если сервер не установлен
        MenuInstall.Visibility = serverExeExists ? Visibility.Collapsed : Visibility.Visible;
        MenuInstall.IsEnabled = !string.IsNullOrEmpty(App.Settings.ServerDirectory);

        // Остальные пункты видны только если сервер установлен
        bool showServerMenus = serverExeExists;
        MenuUpdate.Visibility = showServerMenus ? Visibility.Visible : Visibility.Collapsed;
        MenuValidate.Visibility = showServerMenus ? Visibility.Visible : Visibility.Collapsed;
        //MenuCheckUpdate.Visibility = showServerMenus ? Visibility.Visible : Visibility.Collapsed;
        MenuEditConfig.Visibility = showServerMenus ? Visibility.Visible : Visibility.Collapsed;
        MenuStartServer.Visibility = showServerMenus ? Visibility.Visible : Visibility.Collapsed;
        MenuStopServer.Visibility = showServerMenus ? Visibility.Visible : Visibility.Collapsed;
        MenuRestartServer.Visibility = showServerMenus ? Visibility.Visible : Visibility.Collapsed;

        if (serverExeExists)
        {
            MenuStartServer.IsEnabled = !serverRunning;
            MenuStopServer.IsEnabled = serverRunning;
            MenuRestartServer.IsEnabled = serverRunning;
        }
        else
        {
            MenuStartServer.IsEnabled = false;
            MenuStopServer.IsEnabled = false;
            MenuRestartServer.IsEnabled = false;
        }

        MenuUpdate.IsEnabled = serverExeExists && !serverRunning;
        MenuValidate.IsEnabled = serverExeExists && !serverRunning;
        MenuCheckUpdate.IsEnabled = serverExeExists;
        MenuEditConfig.IsEnabled = serverExeExists;
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

        if (command.Equals("install", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteInstallAsync();
            ConsoleInput.Clear();
            return;
        }
        if (command.Equals("update", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteUpdateAsync();
            ConsoleInput.Clear();
            return;
        }
        if (command.Equals("validate", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteValidateAsync();
            ConsoleInput.Clear();
            return;
        }
        if (command.Equals("checkupdate", StringComparison.OrdinalIgnoreCase))
        {
            await CheckForUpdatesAsync(true);
            ConsoleInput.Clear();
            return;
        }
        if (command.Equals("editconfig", StringComparison.OrdinalIgnoreCase))
        {
            ConsoleInput.Clear();
            ExecuteEditConfig();
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
        if (command.Equals("timers", StringComparison.OrdinalIgnoreCase))
        {
            Log("=== Active Timers ===", "CMD");
            // Таймер бэкапа
            if (backupTimer.IsEnabled && isConfigured && GetServerProcess() != null)
            {
                int mins = secondsLeft / 60;
                int secs = secondsLeft % 60;
                Log($"  Backup: {mins:D2}:{secs:D2} remaining", "CMD");
            }
            else
            {
                Log("  Backup: not active (server not running or backup disabled)", "CMD");
            }

            // Таймер планового перезапуска
            if (App.Settings?.AutoRestartIntervalEnabled == true && _intervalRestartTimer?.IsEnabled == true && GetServerProcess() != null)
            {
                if (_nextRestartTime != DateTime.MinValue)
                {
                    var remaining = _nextRestartTime - DateTime.Now;
                    if (remaining.TotalSeconds > 0)
                    {
                        int totalSeconds = (int)remaining.TotalSeconds;
                        int hours = totalSeconds / 3600;
                        int minutes = (totalSeconds % 3600) / 60;
                        int seconds = totalSeconds % 60;
                        Log($"  Scheduled restart: {hours:D2}:{minutes:D2}:{seconds:D2} remaining", "CMD");
                    }
                    else
                        Log("  Scheduled restart: pending (waiting for players)", "CMD");
                }
                
            }
            else
            {
                Log("  Scheduled restart: not active", "CMD");
            }

            ConsoleInput.Clear();
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
        if (command.Equals("settings", StringComparison.OrdinalIgnoreCase)) { ConsoleInput.Clear(); OpenSettingsWindow(); return; }
        if (command.Equals("start", StringComparison.OrdinalIgnoreCase))
        {
            if (GetServerProcess() != null) { Log("Server already running", "WARN"); ConsoleInput.Clear(); return; }
            ConsoleInput.Clear(); StartServerAsync(); return;
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
            Log("=== Available commands ===", "CMD");
            Log("  info          - show server information", "CMD");
            Log("  settings      - open manager settings window", "CMD");
            Log("  editconfig    - edit server configuration", "CMD");
            Log("  start         - start server", "CMD");
            Log("  stop          - stop server", "CMD");
            Log("  restart       - restart server", "CMD");
            Log("  backup        - create manual backup of game world", "CMD");
            Log("  install       - install dedicated server via SteamCMD", "CMD");
            Log("  update        - update server if newer version available", "CMD");
            Log("  validate      - verify integrity of server files", "CMD");
            Log("  checkupdate   - manually check for server updates", "CMD");
            Log("  list          - show this list", "CMD");
            Log("  clear         - clear log (saves old log first)", "CMD");
            Log("  timers        - show remaining time for backup/restart timers", "CMD");
            Log("  version       - show current Manager version", "CMD");
            Log("  /cmd, /c      - switch to local command mode", "CMD");
            Log("  status        - show server status via plugin (if installed)", "PLUGIN");
            Log("  get:<key>     - query plugin for specific info", "PLUGIN");
            Log("  /plugin, /p   - switch to plugin command mode", "PLUGIN");
            //Log("  /rcon, /r     - switch to RCON mode (not implemented)", "RCON");
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

    private async Task RunSteamCmdAsync(string arguments, string operationName, bool waitForNetwork = true)
    {
        if (App.Settings == null) throw new InvalidOperationException("Settings not loaded");
        if (!isConfigured || string.IsNullOrWhiteSpace(App.Settings?.ServerDirectory))
        {
            Log("Server directory not configured. Please set it in settings.", "ERROR");
            return;
        }

        if (GetServerProcess() != null)
        {
            Log($"Cannot {operationName} while server is running. Stop server first.", "ERROR");
            return;
        }

        // 1. Проверяем/скачиваем steamcmd
        string steamCmdPath = EnsureSteamCmd();
        if (string.IsNullOrEmpty(steamCmdPath))
        {
            Log("Failed to initialize SteamCMD. Check internet connection and firewall.", "ERROR");
            return;
        }

        // 2. Запуск с таймаутом для обнаружения проблем фаервола
        DispatcherTimer timeoutTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        bool completed = false;
        bool timeoutReached = false;
        Process? steamProcess = null;
        int zeroPercentCount = 0;

        try
        {
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = steamCmdPath,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(steamCmdPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            steamProcess = Process.Start(psi);
            if (steamProcess == null) throw new Exception("Failed to start SteamCMD process.");

            StatusBarText.Text = $"{operationName}...";
            Log($"Starting SteamCMD for {operationName}...", "STEAMCMD");

            steamProcess.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    // Фильтруем вводящую в заблуждение строку
                    if (!e.Data.Contains("type 'quit' to exit"))
                    {
                        Dispatcher.BeginInvoke(new Action(() => Log(e.Data, "STEAMCMD")));
                    }
                    // Обнаружение застревания на 0%
                    if (e.Data.Contains("0%") && (e.Data.Contains("Downloading") || e.Data.Contains("Connecting")))
                    {
                        zeroPercentCount++;
                        if (zeroPercentCount >= 5 && !completed)
                        {
                            Dispatcher.BeginInvoke(new Action(() => { timeoutReached = true; steamProcess?.Kill(); }));
                        }
                    }
                    else if (e.Data.Contains("Success!") || e.Data.Contains("Login Ok") || (e.Data.Contains("Downloading") && e.Data.Contains("%") && !e.Data.Contains("0%")))
                    {
                        zeroPercentCount = 0;
                    }
                }
            };
            steamProcess.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    Dispatcher.Invoke(() => Log(e.Data, "STEAMCMD ERR"));
            };
            steamProcess.BeginOutputReadLine();
            steamProcess.BeginErrorReadLine();

            timeoutTimer.Tick += (sender, args) =>
            {
                if (!completed && steamProcess != null && !steamProcess.HasExited)
                {
                    timeoutTimer.Stop();
                    timeoutReached = true;
                    Log($"SteamCMD operation timed out (15 sec with no progress). Possible firewall block. Please allow steamcmd.exe and retry.", "ERROR");
                    try { steamProcess.Kill(); } catch { }
                }
            };
            timeoutTimer.Start();

            await Task.Run(() => steamProcess.WaitForExit());
            timeoutTimer.Stop();
            completed = true;

            if (timeoutReached)
            {
                Log($"{operationName} aborted due to network/firewall issues.", "ERROR");
                return;
            }

            if (steamProcess.ExitCode == 0)
            {
                Log($"{operationName} completed successfully.", "STEAMCMD");
                if (operationName == "install")
                    OnInstallSuccess();
                else if (operationName == "update")
                    await OnUpdateSuccess();
                else if (operationName == "validate")
                    Log("File validation finished.", "STEAMCMD");
            }
            else if (steamProcess.ExitCode == 7)
            {
                Log("SteamCMD has been updated. Please repeat the operation (Update / Install / Validate).", "INFO");
            }
            else
            {
                Log($"{operationName} failed with exit code {steamProcess.ExitCode}.", "ERROR");
            }
        }
        catch (Exception ex)
        {
            Log($"Error during {operationName}: {ex.Message}", "ERROR");
        }
        finally
        {
            timeoutTimer.Stop();
            StatusBarText.Text = "Ready";
            steamProcess?.Dispose();
        }
    }

    private string EnsureSteamCmd()
    {
        string toolsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "SteamCmd");
        string exePath = Path.Combine(toolsDir, "steamcmd.exe");
        if (File.Exists(exePath)) return exePath;

        Log("SteamCMD not found. Downloading...", "STEAMCMD");
        try
        {
            Directory.CreateDirectory(toolsDir);
            string zipPath = Path.Combine(toolsDir, "steamcmd.zip");
            using (var client = new System.Net.Http.HttpClient())
            {
                var downloadTask = client.GetByteArrayAsync("https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip");
                downloadTask.Wait();
                File.WriteAllBytes(zipPath, downloadTask.Result);
            }
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, toolsDir, true);
            File.Delete(zipPath);
            Log("SteamCMD downloaded and extracted.", "STEAMCMD");
            return exePath;
        }
        catch (Exception ex)
        {
            Log($"Failed to download SteamCMD: {ex.Message}", "ERROR");
            return "";
        }
    }

    private void OnInstallSuccess()
    {
        if (App.Settings == null) return;

        string serverDir = App.Settings.ServerDirectory;
        string defaultConfig = Path.Combine(serverDir, "server properties.txt");
        string userConfig = Path.Combine(serverDir, "server_config.txt");

        // Если имя конфига не задано – устанавливаем стандартное
        if (string.IsNullOrEmpty(App.Settings.PropertiesFileName))
        {
            App.Settings.PropertiesFileName = "server properties.txt";
            Log("Configuration file name was empty, set to default: server properties.txt", "CONFIG");
        }

        // Если имя конфига равно стандартному "server properties.txt", копируем в server_config.txt и переключаем (сохранение обратной совместимости)
        if (App.Settings.PropertiesFileName.Equals("server properties.txt", StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(defaultConfig) && !File.Exists(userConfig))
            {
                File.Copy(defaultConfig, userConfig);
                App.Settings.PropertiesFileName = "server_config.txt";
                Log($"Copied default config to user config: {userConfig}", "CONFIG");
            }
        }
        else
        {
            // Пользователь указал своё имя – проверяем, существует ли файл, если нет – предупреждение
            string customConfig = Path.Combine(serverDir, App.Settings.PropertiesFileName);
            if (!File.Exists(customConfig) && File.Exists(defaultConfig))
            {
                Log($"Warning: specified config '{App.Settings.PropertiesFileName}' not found. Default config 'server properties.txt' exists but will not be auto-switched.", "WARN");
            }
        }

        // Сохраняем настройки
        SaveSettingsToCfg(App.Settings);

        // Перезагружаем настройки и обновляем UI
        ReloadSettings();
        Log("Server installation finished. You can now edit configuration and start the server.", "CMD");
        UpdateMenuState();
    }

    private async Task OnUpdateSuccess()
    {
        // После успешного обновления сбросить жёлтый LED
        await CheckForUpdatesAsync(showLog: true);
    }

    private async Task CheckForUpdatesAsync(bool showLog = false)
    {
        // 1. Проверка настроек и пути к серверу
        if (App.Settings == null || string.IsNullOrEmpty(App.Settings.ServerDirectory))
        {
            if (showLog) Log("Server directory not configured.", "WARN");
            return;
        }
        string serverExePath = Path.Combine(App.Settings.ServerDirectory, "AskaServer.exe");
        if (!File.Exists(serverExePath))
        {
            if (showLog) Log("Server not installed. Cannot check updates.", "WARN");
            return;
        }

        // 2. Определяем локальный build ID из appmanifest_3246670.acf
        long localBuildId = 0;
        string steamCmdDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "SteamCmd");
        string manifestPath = Path.Combine(App.Settings.ServerDirectory, "steamapps", "appmanifest_3246670.acf");
        if (File.Exists(manifestPath))
        {
            try
            {
                string content = File.ReadAllText(manifestPath);
                // Ищем строку "buildid"		"12345678"
                var match = System.Text.RegularExpressions.Regex.Match(content, "\"buildid\"\\s*\"(\\d+)\"");
                if (match.Success && long.TryParse(match.Groups[1].Value, out long id))
                    localBuildId = id;
            }
            catch (Exception ex)
            {
                if (showLog) Log($"Failed to read local buildid: {ex.Message}", "ERROR");
            }
        }

        if (localBuildId == 0)
        {
            if (showLog) Log("Cannot determine local server build ID. Ensure SteamCMD has installed the server at least once.", "WARN");
            return;
        }

        // 3. Получаем удалённый build ID через API
        long remoteBuildId = 0;
        try
        {
            using var client = new System.Net.Http.HttpClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            string url = "https://api.steamcmd.net/v1/info/3246670";
            string response = await client.GetStringAsync(url);
            // Ищем "buildid": 12345678
            var match = System.Text.RegularExpressions.Regex.Match(response, "\"buildid\"\\s*:\\s*(\\d+)");
            if (match.Success && long.TryParse(match.Groups[1].Value, out long id))
                remoteBuildId = id;
        }
        catch (Exception ex)
        {
            if (showLog) Log($"Failed to fetch remote build ID: {ex.Message}", "ERROR");
            return;
        }

        // 4. Сравнение
        if (localBuildId < remoteBuildId)
        {
            Log($"Update available! Local build: {localBuildId}, latest: {remoteBuildId}. Use 'update' command.", "INFO");
            StatusLed.Fill = Brushes.Gold;
        }
        else
        {
            if (showLog)
                Log($"Server is up to date (build {localBuildId}).", "INFO");
            else
                Log("Server is up to date.", "INFO");   // добавляем эту строку
            if (StatusLed.Fill == Brushes.Gold)
                UpdateServerInfo();
        }
    }

    // ---- Backup ----
    public void MakeBackup()
    {
        if (!isConfigured)
        {
            if (App.Settings == null || string.IsNullOrEmpty(App.Settings?.ServerDirectory))
                Log("Backup cancelled: settings not configured.", "WARN");
            else if (!File.Exists(Path.Combine(App.Settings.ServerDirectory, "AskaServer.exe")))
                Log("Backup cancelled: server not installed.", "WARN");
            else if (string.IsNullOrEmpty(App.Settings.PropertiesFileName) || !File.Exists(Path.Combine(App.Settings.ServerDirectory, App.Settings.PropertiesFileName)))
                Log("Backup cancelled: configuration file missing.", "WARN");
            else
                Log("Backup cancelled: unknown configuration error.", "WARN");
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
            TxtServerStatus.Visibility = Visibility.Collapsed;
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
                //TxtServerStatus.Visibility = Visibility.Visible;
                UpdateServerInfo();
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

        var serverProcess = GetServerProcess();
        bool isRunning = (serverProcess != null && !serverProcess.HasExited);

        MenuStartServer.IsEnabled = !isRunning;
        MenuStopServer.IsEnabled = isRunning;
        MenuRestartServer.IsEnabled = isRunning;

        if (isRunning && isConfigured)
        {
            if (_serverStarting)
            {
                // Сервер в процессе запуска: показываем только индикацию, скрываем статистику
                StatusLed.Fill = Brushes.Gold;
                TxtPlayerNames.Visibility = Visibility.Collapsed;
                TxtBackupTimer.Visibility = Visibility.Collapsed;
                TxtServerRam.Visibility = Visibility.Collapsed;
                TxtStats.Visibility = Visibility.Collapsed;
                TxtBackupInfo.Visibility = Visibility.Collapsed;
                TxtServerStatus.Visibility = Visibility.Visible;
                TxtServerStatus.Text = "=== Starting Server ===";
                BackupProgress.Visibility = Visibility.Visible;
                BackupProgress.IsIndeterminate = true;
            }
            else
            {
                // Сервер полностью запущен и готов
                try
                {
                    serverProcess!.Refresh();
                    long ramMb = serverProcess.PrivateMemorySize64 / (1024 * 1024);
                    TxtServerRam.Text = $"RAM: {ramMb} MB";
                }
                catch { }

                StatusLed.Fill = Brushes.LimeGreen;
                TxtPlayerNames.Visibility = Visibility.Visible;
                TxtBackupTimer.Visibility = Visibility.Visible;
                BackupProgress.Visibility = Visibility.Visible;
                TxtServerRam.Visibility = Visibility.Visible;
                TxtStats.Visibility = Visibility.Visible;
                TxtBackupInfo.Visibility = Visibility.Visible;
                TxtServerStatus.Visibility = Visibility.Collapsed;
                // Убедимся, что прогресс-бар не в indeterminate режиме
                BackupProgress.IsIndeterminate = false;
                if (secondsLeft > 0)
                    BackupProgress.Value = ((double)secondsLeft / (backupIntervalMinutes * 60)) * 100;
                else
                    BackupProgress.Value = 100;
            }
        }
        else
        {
            // Сервер не работает
            _serverStarting = false;
            StatusLed.Fill = Brushes.Red;
            TxtPlayerNames.Visibility = Visibility.Collapsed;
            TxtBackupTimer.Visibility = Visibility.Collapsed;
            BackupProgress.Visibility = Visibility.Collapsed;
            TxtServerRam.Visibility = Visibility.Collapsed;
            TxtStats.Visibility = Visibility.Collapsed;
            TxtServerStatus.Text = "=== Server offline ===";
            TxtServerStatus.Visibility = Visibility.Visible;
            TxtBackupInfo.Visibility = Visibility.Collapsed;
            BackupProgress.IsIndeterminate = false;
            BackupProgress.Value = 100;
        }
    }
    
    private void CheckServerProcess()
    {
        // Если сервер не был запущен через менеджер, не отслеживаем
        UpdateMenuState();
        if (_serverProcess == null) return;

        bool isRunning = !_serverProcess.HasExited;

        if (isConfigured && !isRunning && !isStoppingManually && serverWasRunning)
        {
            // Игнорируем, если ручная остановка была меньше 15 секунд назад
            if ((DateTime.Now - _lastManualStopTime).TotalSeconds < 15)
            {
                UpdateMenuState();
                serverWasRunning = false;
                return;
            }

            // Если была ошибка аутентификации – не перезапускаем, просто очищаем ресурсы
            if (_authFailedHandled)
            {
                Log("Server stopped due to authentication failure. Auto-restart disabled.", "WARN");
                serverWasRunning = false;
                _serverProcess.Dispose();
                _serverProcess = null;
                UpdateMenuState();
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
            UpdateMenuState();
            _serverProcess.Dispose();
            _serverProcess = null;
        }
        else if (isRunning)
        {
            UpdateMenuState();
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
            Log($"  PID: {proc.Id}", "INFO");
            Log($"  Memory: {proc.WorkingSet64 / (1024 * 1024)} MB", "INFO");
            Log($"  Start time: {proc.StartTime}", "INFO");
            Log($"  Uptime: {(DateTime.Now - proc.StartTime):hh\\h\\ mm\\m}", "INFO");
        }
        else
        {
            Log("Server not running", "INFO");
        }

        // Дополнительная диагностика
        if (_authFailedHandled)
            Log("⚠️ Authentication failure flag is set. Server start is blocked.\nPlease check Token in World settings.", "WARN");

        if (!string.IsNullOrEmpty(propertiesFilePath) && File.Exists(propertiesFilePath))
        {
            Log("=== SERVER CONFIGURATION ===", "INFO");
            var config = ReadServerConfig();
            if (config.Count == 0)
                Log("Configuration file is empty or unreadable.", "WARN");
            else
            {
                foreach (var kvp in config)
                    Log($"  {kvp.Key} = {kvp.Value}", "INFO");
            }
        }
        else
        {
            Log("Configuration file not found or not set.", "WARN");
            Log($"Expected path: {propertiesFilePath}", "WARN");
        }

        UpdateServerInfo();
    }

    private Dictionary<string, string> ReadServerConfig()
    {
        var dict = new Dictionary<string, string>();
        if (!File.Exists(propertiesFilePath)) return dict;

        // Набор ключей, которые нужно игнорировать
        var ignoredKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "mode",
        "terrain aspect",
        "terrain height",
        "starting season",
        "year length",
        "precipitation",
        "day length",
        "structure decay",
        "clothing decay",
        "invasion dificulty",
        "monster density",
        "monster population",
        "wulfar population",
        "herbivore population",
        "bear population"
    };

        foreach (var line in File.ReadAllLines(propertiesFilePath))
        {
            if (line.TrimStart().StartsWith("//") || string.IsNullOrWhiteSpace(line)) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq].Trim();
            if (ignoredKeys.Contains(key)) continue; // пропускаем игнорируемые ключи
            string value = line[(eq + 1)..].Trim();
            dict[key] = value;
        }
        return dict;
    }

    public void UpdateServerHeader()
    {
        if (!isConfigured || string.IsNullOrEmpty(propertiesFilePath) || !File.Exists(propertiesFilePath))
        {
            TxtDisplayName.Text = "CONFIG MISSING";
            TxtServerName.Text = "Open settings to fix";
            TxtDisplayName.Foreground = Brushes.Red;
            TxtServerName.Foreground = Brushes.Red;
            return;
        }
        var cfg = ReadServerConfig();
        TxtDisplayName.Visibility = Visibility.Visible;
        TxtServerName.Visibility = Visibility.Visible;
        string displayName = cfg.TryGetValue("display name", out string? dn) ? dn.ToUpper() : "DISPLAY NAME";
        string serverName = cfg.TryGetValue("server name", out string? sn) ? sn.ToUpper() : "SERVER NAME";
        TxtDisplayName.Text = displayName;
        TxtServerName.Text = serverName;
        TxtDisplayName.Foreground = (SolidColorBrush)FindResource("ForegroundBrush");
        TxtServerName.Foreground = (SolidColorBrush)FindResource("ForegroundBrush");
    }

    // ---- Menu actions ----
    private void OpenSavesFolder()
    {
        try
        {
            string localLow = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData).Replace("Local", "LocalLow"));
            string path = Path.Combine(localLow, "Sand Sailor Studio", "Aska", "data", "server");

            if (Directory.Exists(path))
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true // Это задействует системный проводник напрямую
                };

                using (System.Diagnostics.Process? p = System.Diagnostics.Process.Start(psi))
                {
                    // Запустили и забыли: using очистит ресурсы в Манагере
                }
            }
            else
            {
                Log($"Savegame folder not found: {path}", "WARN");
            }
        }
        catch (Exception ex)
        {
            Log($"Error opening folder: {ex.Message}", "ERROR");
        }
    }


    private void OpenBackupsFolder()
    {
        if (string.IsNullOrEmpty(backupDir))
        {
            Log("Backup folder is not configured.", "WARN");
            return;
        }
        if (!Directory.Exists(backupDir))
        {
            try
            {
                Directory.CreateDirectory(backupDir);
                Log($"Backup folder created: {backupDir}", "INFO");
            }
            catch (Exception ex)
            {
                Log($"Cannot create backup folder: {ex.Message}", "ERROR");
                return;
            }
        }
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = backupDir,
            UseShellExecute = true
        };
        using (System.Diagnostics.Process.Start(psi)) { }
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Directory.Exists(logDirectory)) Directory.CreateDirectory(logDirectory);
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = logDirectory,
                UseShellExecute = true
            };
            using (System.Diagnostics.Process.Start(psi)) { }
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
        if (App.Settings == null)
        {
            // Создаём временные настройки по умолчанию
            App.Settings = new AppSettings();
        }
        App.Settings.ShowServerLog = MenuShowServerLog.IsChecked;
        SaveSettingsToCfg(App.Settings);
        UpdateServerLogMenuItemColor();
        Log($"Show server log: {(App.Settings.ShowServerLog ? "ON" : "OFF")}", "CONFIG");
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
                Dispatcher.Invoke(() => StatusBarText.Text = "Shootdown server and Exit...");
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