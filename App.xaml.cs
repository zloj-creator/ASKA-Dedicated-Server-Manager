using System;
using System.IO;
using System.Windows;
using System.Threading;

namespace AskaServerManager
{
    public partial class App : System.Windows.Application
    {
        private static Mutex? _appMutex;

        public static AppSettings? Settings { get; set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            bool createdNew;
            _appMutex = new Mutex(true, @"Global\ASKA_Server_Manager_SingleInstance", out createdNew);
            if (!createdNew)
            {
                System.Windows.MessageBox.Show("ASKA Server Manager is already running!", "Warning",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                Environment.Exit(0);
                return;
            }

            base.OnStartup(e);

            string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
            if (!Directory.Exists(logDir))
                Directory.CreateDirectory(logDir);
        }

        // Опционально: освободить мьютекс при закрытии приложения
        protected override void OnExit(ExitEventArgs e)
        {
            _appMutex?.ReleaseMutex();
            _appMutex?.Dispose();
            base.OnExit(e);
        }
    }

    public class AppSettings
    {
        public string ServerDirectory { get; set; } = @"";
        public string ServerExecutable { get; set; } = "AskaServer.exe";
        public string PropertiesFileName { get; set; } = "";
        public string BackupDirectory { get; set; } = @"";
        public int SteamAppId { get; set; } = 1898300;
        public int BackupIntervalMinutes { get; set; } = 120;
        public int MaxBackupCount { get; set; } = 10;
        public string QueryIP { get; set; } = "127.0.0.1";
        public int QueryPort { get; set; } = 27016;
        public int MaxLogSizeMB { get; set; } = 2;
        public int MaxLogFiles { get; set; } = 100;
        public bool BackupOnStop { get; set; } = false;
        public bool LoadDailyLogOnStart { get; set; } = false;
        public bool ShowServerLog { get; set; } = false;
        public bool SaveServerLog { get; set; } = false;
        public bool CheckForUpdatesAtStart { get; set; } = false;

    }
}