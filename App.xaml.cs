using System;
using System.IO;
using System.Windows;

namespace AskaServerManager
{
    public partial class App : System.Windows.Application
    {
        public static AppSettings? Settings { get; set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
            if (!Directory.Exists(logDir))
                Directory.CreateDirectory(logDir);
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
        public bool LoadDailyLogOnStart { get; set; } = true;
        public bool ShowServerLog { get; set; } = false;
        public bool DontSaveServerLog { get; set; } = true;

    }
}