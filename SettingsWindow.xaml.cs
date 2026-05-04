using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Forms; // FolderBrowserDialog
using System.Windows.Input;
using WinForms = System.Windows.Forms;

namespace AskaServerManager
{
    public partial class SettingsWindow : Window
    {
        private bool serverRunning;
        private string? _originalServerDir;
        private string? _originalPropertiesFile;
        private string? _originalBackupDir;
        private int _originalInterval;
        private int _originalMaxBackup;
        private int _originalLogSize;
        private int _originalLogFiles;
        private bool _originalBackupOnStop;
        private bool _originalLoadDailyLog;
        private bool _originalSaveServerLog;
        private bool _originalCheckUpdates;

        public SettingsWindow(bool isServerRunning)
        {
            InitializeComponent();
            NativeMethods.SetDarkTitleBar(this);
            serverRunning = isServerRunning;
            LoadSettings(); // Загружает значения в поля и чекбоксы

        }

        private void LoadSettings()
        {
            if (App.Settings != null)
            {
                TxtServerDirectory.Text = App.Settings.ServerDirectory;
                TxtServerExecutable.Text = App.Settings.ServerExecutable;
                TxtPropertiesFileName.Text = App.Settings.PropertiesFileName;
                TxtBackupDirectory.Text = App.Settings.BackupDirectory;
                TxtBackupIntervalMinutes.Text = App.Settings.BackupIntervalMinutes.ToString();
                TxtMaxBackupCount.Text = App.Settings.MaxBackupCount.ToString();
                TxtMaxLogSizeMB.Text = App.Settings.MaxLogSizeMB.ToString();
                TxtMaxLogFiles.Text = App.Settings.MaxLogFiles.ToString();
                ChkBackupOnStop.IsChecked = App.Settings.BackupOnStop;
                ChkLoadDailyLog.IsChecked = App.Settings.LoadDailyLogOnStart;
                ChkSaveServerLog.IsChecked = App.Settings.SaveServerLog;
                ChkCheckUpdatesAtStart.IsChecked = App.Settings.CheckForUpdatesAtStart;
            }

            // Сохраняем оригинальные значения ПОСЛЕ загрузки (для отслеживания изменений)
            _originalServerDir = TxtServerDirectory.Text;
            _originalPropertiesFile = TxtPropertiesFileName.Text;
            _originalBackupDir = TxtBackupDirectory.Text;
            _originalInterval = int.TryParse(TxtBackupIntervalMinutes.Text, out int interval) ? interval : 120;
            _originalMaxBackup = int.TryParse(TxtMaxBackupCount.Text, out int maxBackup) ? maxBackup : 10;
            _originalLogSize = int.TryParse(TxtMaxLogSizeMB.Text, out int logSize) ? logSize : 2;
            _originalLogFiles = int.TryParse(TxtMaxLogFiles.Text, out int logFiles) ? logFiles : 100;
            _originalBackupOnStop = ChkBackupOnStop.IsChecked == true;
            _originalLoadDailyLog = ChkLoadDailyLog.IsChecked == true;
            _originalSaveServerLog = ChkSaveServerLog.IsChecked == true;
            _originalCheckUpdates = ChkCheckUpdatesAtStart.IsChecked == true;
        }

        private void NumberValidationTextBox(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !char.IsDigit(e.Text, 0);
        }

        private void PastingHandler(object sender, DataObjectPastingEventArgs e)
        {
            if (e.DataObject.GetDataPresent(typeof(string)))
            {
                string text = (string)e.DataObject.GetData(typeof(string));
                if (!text.All(char.IsDigit)) e.CancelCommand();
            }
            else
            {
                e.CancelCommand();
            }
        }

        private void BrowsePropertiesFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog();
            dialog.Title = "Select server configuration file";
            dialog.Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*";
            dialog.CheckFileExists = true;

            if (!string.IsNullOrEmpty(TxtPropertiesFileName.Text))
            {
                string dir = TxtServerDirectory.Text;
                if (!string.IsNullOrEmpty(dir))
                {
                    string fullPath = Path.Combine(dir, TxtPropertiesFileName.Text);
                    if (File.Exists(fullPath))
                        dialog.FileName = fullPath;
                }
            }

            if (dialog.ShowDialog() == true)
            {
                string fileName = Path.GetFileName(dialog.FileName) ?? "";
                TxtPropertiesFileName.Text = fileName;

                string? dir = Path.GetDirectoryName(dialog.FileName);
                if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "AskaServer.exe")))
                {
                    TxtServerDirectory.Text = dir;
                }
                TxtPropertiesFileName.Text = Path.GetFileName(dialog.FileName);
            }
        }

        private void BrowseServerDir_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new WinForms.FolderBrowserDialog())
            {
                dialog.Description = "Please select ASKA dedicated server folder";
                dialog.ShowNewFolderButton = true;
                if (!string.IsNullOrEmpty(TxtServerDirectory.Text))
                    dialog.SelectedPath = TxtServerDirectory.Text;

                if (dialog.ShowDialog() == WinForms.DialogResult.OK)
                {
                    TxtServerDirectory.Text = dialog.SelectedPath;
                }
            }
        }

        private void BrowseBackupDir_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new WinForms.FolderBrowserDialog())
            {
                dialog.Description = "Please select folder to save backups";
                dialog.ShowNewFolderButton = true;
                if (!string.IsNullOrEmpty(TxtBackupDirectory.Text))
                    dialog.SelectedPath = TxtBackupDirectory.Text;

                if (dialog.ShowDialog() == WinForms.DialogResult.OK)
                {
                    TxtBackupDirectory.Text = dialog.SelectedPath;
                }
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            // --- Валидация ---
            if (string.IsNullOrWhiteSpace(TxtServerDirectory.Text) || !Directory.Exists(TxtServerDirectory.Text))
            {
                new ErrorDialog("Select a valid server folder.", "Validation Error") { Owner = this }.ShowDialog();
                return;
            }

            string serverDir = TxtServerDirectory.Text;
            string serverExePath = Path.Combine(serverDir, "AskaServer.exe");
            bool serverInstalled = File.Exists(serverExePath);

            // --- Проверка конфигурационного файла (только если сервер установлен) ---
            if (serverInstalled)
            {
                if (string.IsNullOrWhiteSpace(TxtPropertiesFileName.Text))
                {
                    new ErrorDialog("Select a configuration file\n(e.g. server properties.txt).", "Validation Error") { Owner = this }.ShowDialog();
                    return;
                }
                string fullConfig = Path.Combine(serverDir, TxtPropertiesFileName.Text);
                if (!File.Exists(fullConfig))
                {
                    new ErrorDialog($"Configuration file not found:\n{fullConfig}", "Validation Error") { Owner = this }.ShowDialog();
                    return;
                }
            }
            else
            {
                // Сервер не установлен – автоматически подставляем имя конфига, если пусто
                if (string.IsNullOrWhiteSpace(TxtPropertiesFileName.Text))
                {
                    TxtPropertiesFileName.Text = "server properties.txt";
                }
            }

            // --- Валидация числовых параметров ---
            if (!int.TryParse(TxtBackupIntervalMinutes.Text, out int interval) || interval < 1 || interval > 1440)
            {
                new ErrorDialog("Backup interval must be a number from 1 to 1440.", "Error") { Owner = this }.ShowDialog();
                return;
            }
            if (!int.TryParse(TxtMaxBackupCount.Text, out int maxBackup) || maxBackup < 1 || maxBackup > 100)
            {
                new ErrorDialog("Number of backups must be from 1 to 100.", "Error") { Owner = this }.ShowDialog();
                return;
            }
            if (!int.TryParse(TxtMaxLogSizeMB.Text, out int logSize) || logSize < 1 || logSize > 100)
            {
                new ErrorDialog("Log size (MB) must be from 1 to 100.", "Error") { Owner = this }.ShowDialog();
                return;
            }
            if (!int.TryParse(TxtMaxLogFiles.Text, out int logFiles) || logFiles < 1 || logFiles > 1000)
            {
                new ErrorDialog("Number of log files must be from 1 to 1000.", "Error") { Owner = this }.ShowDialog();
                return;
            }

            // --- Сбор изменений ---
            var changes = new System.Collections.Generic.List<string>();
            if (TxtServerDirectory.Text != _originalServerDir)
                changes.Add($"Server directory: '{_originalServerDir}' -> '{TxtServerDirectory.Text}'");
            if (TxtPropertiesFileName.Text != _originalPropertiesFile)
                changes.Add($"Config file: '{_originalPropertiesFile}' -> '{TxtPropertiesFileName.Text}'");
            if (TxtBackupDirectory.Text != _originalBackupDir)
                changes.Add($"Backup directory: '{_originalBackupDir}' -> '{TxtBackupDirectory.Text}'");
            if (interval != _originalInterval)
                changes.Add($"Backup interval: {_originalInterval} -> {interval} minutes");
            if (maxBackup != _originalMaxBackup)
                changes.Add($"Max backups: {_originalMaxBackup} -> {maxBackup}");
            if (logSize != _originalLogSize)
                changes.Add($"Max log size: {_originalLogSize} -> {logSize} MB");
            if (logFiles != _originalLogFiles)
                changes.Add($"Max log files: {_originalLogFiles} -> {logFiles}");
            bool backupOnStop = ChkBackupOnStop.IsChecked == true;
            if (backupOnStop != _originalBackupOnStop)
                changes.Add($"Backup on stop: {(_originalBackupOnStop ? "true" : "false")} -> {(backupOnStop ? "true" : "false")}");
            bool loadDailyLog = ChkLoadDailyLog.IsChecked == true;
            if (loadDailyLog != _originalLoadDailyLog)
                changes.Add($"Load daily log on start: {(_originalLoadDailyLog ? "true" : "false")} -> {(loadDailyLog ? "true" : "false")}");
            bool saveServerLog = ChkSaveServerLog.IsChecked == true;
            if (saveServerLog != _originalSaveServerLog)
                changes.Add($"Save server log: {(_originalSaveServerLog ? "true" : "false")} -> {(saveServerLog ? "true" : "false")}");
            bool checkUpdates = ChkCheckUpdatesAtStart.IsChecked == true;
            if (checkUpdates != _originalCheckUpdates)
                changes.Add($"Check for updates at startup: {_originalCheckUpdates} -> {checkUpdates}");

            if (changes.Count == 0)
            {
                DialogResult = false;
                Close();
                return;
            }

            // --- Вывод изменений в лог ---
            var mainWin = (MainWindow)Owner;
            mainWin.Log("Settings changed:", "CONFIG");
            foreach (var change in changes)
                mainWin.Log($"  {change}", "CONFIG");

            // --- Обновление App.Settings ---
            if (App.Settings == null) App.Settings = new AppSettings();
            App.Settings.ServerDirectory = TxtServerDirectory.Text;
            App.Settings.ServerExecutable = TxtServerExecutable.Text;
            App.Settings.PropertiesFileName = TxtPropertiesFileName.Text;
            App.Settings.BackupDirectory = TxtBackupDirectory.Text;
            App.Settings.BackupIntervalMinutes = interval;
            App.Settings.MaxBackupCount = maxBackup;
            App.Settings.MaxLogSizeMB = logSize;
            App.Settings.MaxLogFiles = logFiles;
            App.Settings.SteamAppId = 1898300;
            App.Settings.BackupOnStop = backupOnStop;
            App.Settings.LoadDailyLogOnStart = loadDailyLog;
            App.Settings.SaveServerLog = ChkSaveServerLog.IsChecked == true;
            App.Settings.CheckForUpdatesAtStart = checkUpdates;

            // --- Сохранение в файл ---
            mainWin.SaveSettingsToCfg(App.Settings);

            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}