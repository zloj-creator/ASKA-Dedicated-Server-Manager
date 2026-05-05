using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AskaMonitor
{
    [BepInPlugin("com.aska.monitor", "AskaMonitor", "2.2.3")]
    public class AskaMonitorPlugin : BasePlugin
    {
        public static AskaMonitorPlugin Instance;
        public string _statusFile;
        public string _staticFile;
        public string _commandFile;
        public string _lastCommandId = "0";
        public string _statusDetails = "";
        private const BindingFlags flags = (BindingFlags)62;

        public override void Load()
        {
            Instance = this;
            _statusFile = Path.Combine(Paths.BepInExRootPath, "AskaServerStatus.txt");
            _commandFile = Path.Combine(Paths.BepInExRootPath, "AskaCommand.txt");

            AddComponent<MonitorComponent>();
            Log.LogInfo("AskaMonitor v2.2.3 Loaded.");
        }

        // --- ДИНАМИКА (Timeraw, Season, Days Survived) ---
        public void WriteStatus()
        {
            var s = new JObject();
            s["lastCommandId"] = _lastCommandId;
            s["statusDetails"] = _statusDetails;

            const BindingFlags instFlags = flags | BindingFlags.Instance;

            try
            {
                var w = Type.GetType("SSSGame.Weather.WeatherSystem, Assembly-CSharp");
                var wi = w?.GetMethod("get_Instance", flags)?.Invoke(null, null);
                if (wi != null)
                {
                    s["gameTimeRaw"] = w.GetMethod("get_TimeOfDay", instFlags)?.Invoke(wi, null)?.ToString();
                    s["daysPassed"] = w.GetMethod("GetDaysPassed", instFlags)?.Invoke(wi, null)?.ToString();
                    s["season"] = w.GetMethod("get_SeasonName", instFlags)?.Invoke(wi, null)?.ToString();
                }
            }
            catch { }

            // Запись с разрешением на чтение для Манагера
            try
            {
                using (var stream = new FileStream(_statusFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                using (var writer = new StreamWriter(stream))
                {
                    writer.Write(s.ToString(Formatting.Indented));
                }
            }
            catch { }
        }

        // --- КОМАНДЫ (idTime с исправленным парсингом) ---
        public void ReadCommand()
        {
            try
            {
                if (!File.Exists(_commandFile)) return;
                string raw = File.ReadAllText(_commandFile);
                if (string.IsNullOrEmpty(raw)) return;

                var cmdObj = JObject.Parse(raw);
                string id = cmdObj["id"]?.ToString();
                string cmd = cmdObj["command"]?.ToString();

                if (id != null && id != _lastCommandId)
                {
                    _lastCommandId = id;
                    if (cmd.StartsWith("get:")) ExecuteGet(cmd.Replace("get:", ""));
                    else _statusDetails = "Received: " + cmd;
                    WriteStatus();
                }
            }
            catch { }
        }

        // --- УНИВЕРСАЛЬНЫЙ ГЕТТЕР (Properties + Fields) ---
        private void ExecuteGet(string path)
        {
            try
            {
                string cleanPath = path.Trim();
                int lastDot = cleanPath.LastIndexOf('.');
                if (lastDot == -1) { _statusDetails = "Err: Invalid Path"; return; }

                string typeName = cleanPath.Substring(0, lastDot);
                string memberName = cleanPath.Substring(lastDot + 1);
                var type = Type.GetType(typeName + ", Assembly-CSharp");

                if (type == null) { _statusDetails = "Err: Type Not Found"; return; }

                // 1. Пытаемся найти точку входа (Instance)
                var instanceProp = type.GetProperty("Instance", flags)
                                ?? type.GetProperty("instance", flags);
                var target = instanceProp?.GetValue(null);

                // 2. Ищем значение (у инстанса или статически)
                object result = null;
                if (target != null)
                {
                    // Работаем с живым объектом (добавляем BindingFlags.Instance)
                    result = type.GetProperty(memberName, flags | BindingFlags.Instance)?.GetValue(target)
                          ?? type.GetField(memberName, flags | BindingFlags.Instance)?.GetValue(target)
                          ?? type.GetMethod(memberName, flags | BindingFlags.Instance)?.Invoke(target, null);
                }
                else
                {
                    // Работаем как раньше (статика)
                    result = type.GetProperty(memberName, flags)?.GetValue(null)
                          ?? type.GetField(memberName, flags)?.GetValue(null);
                }

                _statusDetails = result?.ToString() ?? "null";
            }
            catch (Exception ex) { _statusDetails = "Err: " + ex.Message; }
        }


        public class MonitorComponent : MonoBehaviour
        {
            private float _timer = 0f;
            void Update()
            {
                _timer += Time.deltaTime;
                if (_timer >= 2.0f)
                {
                    _timer = 0f;
                    Instance.ReadCommand();
                    Instance.WriteStatus();
                }
            }
        }
    }
}
