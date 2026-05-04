using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace AskaServerManager
{
    public partial class ConfigEditorWindow : Window
    {
        private Dictionary<string, string> _config = null!;
        private string _configPath = null!;
        private bool _worldExists;
        private System.Windows.Controls.ComboBox? _modeCombo = null;
        private List<System.Windows.FrameworkElement> _dependentControlsList = new List<System.Windows.FrameworkElement>();

        // Словарь выпадающих списков
        private static readonly Dictionary<string, List<string>> _options = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            { "region", new List<string> { "default", "asia", "japan", "europe", "south america", "south korea", "usa east", "usa west", "australia", "canada east", "hong kong", "india", "turkey", "united arab emirates", "usa south central" } },
            { "terrain aspect", new List<string> { "smooth", "normal", "rocky" } },
            { "terrain height", new List<string> { "flat", "normal", "varied" } },
            { "starting season", new List<string> { "spring", "summer", "autumn", "winter" } },
            { "year length", new List<string> { "minimum", "reduced", "default", "extended", "maximum" } },
            { "precipitation", new List<string> { "0", "1", "2", "3", "4", "5", "6" } },
            { "day length", new List<string> { "minimum", "reduced", "default", "extended", "maximum" } },
            { "structure decay", new List<string> { "low", "medium", "high" } },
            { "clothing decay", new List<string> { "low", "medium", "high" } },
            { "invasion dificulty", new List<string> { "off", "easy", "normal", "hard" } },
            { "monster density", new List<string> { "off", "low", "medium", "high" } },
            { "monster population", new List<string> { "low", "medium", "high" } },
            { "wulfar population", new List<string> { "low", "medium", "high" } },
            { "herbivore population", new List<string> { "low", "medium", "high" } },
            { "bear population", new List<string> { "low", "medium", "high" } },
            { "autosave style", new List<string> { "every morning", "disabled", "every 5 minutes", "every 10 minutes", "every 15 minutes", "every 20 minutes" } },
            { "mode", new List<string> { "normal", "custom" } },
            { "keep server world alive", new List<string> { "true", "false" } }
        };

        private static readonly HashSet<string> EditableAlways = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "password", "server name", "display name", "steam game port", "steam query port",
            "authentication token", "region", "keep server world alive", "autosave style"
        };

        private static readonly HashSet<string> DependentParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "terrain aspect", "terrain height", "starting season", "year length", "precipitation", "day length",
            "structure decay", "clothing decay", "invasion dificulty", "monster density",
            "monster population", "wulfar population", "herbivore population", "bear population"
        };

        private Dictionary<string, System.Windows.FrameworkElement> _dependentControls = new Dictionary<string, System.Windows.FrameworkElement>();
        private bool _isProcessingFolder;

        public ConfigEditorWindow()
        {
            InitializeComponent();
            this.Owner = System.Windows.Application.Current.MainWindow;
            NativeMethods.SetDarkTitleBar(this);

            if (App.Settings == null || string.IsNullOrEmpty(App.Settings.ServerDirectory) || string.IsNullOrEmpty(App.Settings.PropertiesFileName))
            {
                ErrorDialog.Show("Server directory or config file name not set in settings.", "Configuration Error");
                this.Close();
                return;
            }
            LoadConfig();
        }

        private void LoadConfig()
        {
            if (App.Settings == null || string.IsNullOrEmpty(App.Settings.ServerDirectory) || string.IsNullOrEmpty(App.Settings.PropertiesFileName))
            {
                ErrorDialog.Show("Server directory or config file name not set in settings.", "Configuration Error");
                this.Close();
                return;
            }

            _configPath = Path.Combine(App.Settings.ServerDirectory, App.Settings.PropertiesFileName);
            if (!System.IO.File.Exists(_configPath))
            {
                ErrorDialog.Show($"Config file not found:\n{_configPath}", "File Not Found");
                this.Close();
                return;
            }

            _config = ReadServerConfig(_configPath);

            string saveId = _config.GetValueOrDefault("save id", "");
            if (!string.IsNullOrEmpty(saveId))
            {
                string localLow = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData).Replace("Local", "LocalLow"),
                    "Sand Sailor Studio", "Aska", "data", "server");
                string worldFolder = Path.Combine(localLow, $"savegame_{saveId}");
                _worldExists = System.IO.Directory.Exists(worldFolder);
            }
            else
            {
                _worldExists = false;
            }

            BuildUI();
        }

        private Dictionary<string, string> ReadServerConfig(string filePath)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!System.IO.File.Exists(filePath)) return result;
            foreach (string rawLine in System.IO.File.ReadAllLines(filePath))
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

        private void BuildUI()
        {
            ContentPanel.Children.Clear();
            _modeCombo = null;
            _dependentControlsList.Clear();

            if (_config.TryGetValue("save id", out string? saveId))
            {
                ContentPanel.Children.Add(CreateSaveIdRow(saveId));
            }

            var groups = new Dictionary<string, List<string>>
    {
        { "🛡️ Server settings", new List<string> { "display name", "server name", "password", "keep server world alive", "autosave style" } },
        { "🌐 Network", new List<string> { "authentication token", "steam game port", "steam query port", "region" } },
        { "🌍 World & Environment", new List<string> {
            "seed", "---separator---", "mode", "starting season", "year length", "precipitation",
            "day length", "terrain aspect", "terrain height", "structure decay",
            "clothing decay", "invasion dificulty", "monster density",
            "monster population", "wulfar population", "herbivore population", "bear population"
        } }
    };

            foreach (var group in groups)
            {
                var keysInConfig = group.Value.Where(k => k == "---separator---" || _config.Keys.Any(ck => ck.Equals(k, StringComparison.OrdinalIgnoreCase))).ToList();
                if (keysInConfig.Count == 0) continue;

                var groupBox = new System.Windows.Controls.GroupBox
                {
                    Header = group.Key,
                    Margin = new System.Windows.Thickness(0, 0, 0, 15),
                    Padding = new System.Windows.Thickness(10, 10, 10, 5),
                    BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(212, 160, 23)),
                    BorderThickness = new System.Windows.Thickness(1)
                };

                var grid = new System.Windows.Controls.Grid();
                grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(180) });
                grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });

                int rowIndex = 0;
                foreach (var keyName in keysInConfig)
                {
                    grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });

                    if (keyName == "---separator---")
                    {
                        var line = new System.Windows.Shapes.Rectangle { Height = 1, Fill = System.Windows.Media.Brushes.Gray, Opacity = 0.3, Margin = new System.Windows.Thickness(0, 10, 0, 10) };
                        System.Windows.Controls.Grid.SetRow(line, rowIndex);
                        System.Windows.Controls.Grid.SetColumnSpan(line, 2);
                        grid.Children.Add(line);
                    }
                    else
                    {
                        string originalKey = _config.Keys.First(k => k.Equals(keyName, StringComparison.OrdinalIgnoreCase));
                        var label = CreateLabel(originalKey);
                        var control = CreateControl(originalKey, _config[originalKey]);

                        System.Windows.Controls.Grid.SetRow(label, rowIndex);
                        System.Windows.Controls.Grid.SetColumn(label, 0);
                        System.Windows.Controls.Grid.SetRow(control, rowIndex);
                        System.Windows.Controls.Grid.SetColumn(control, 1);

                        grid.Children.Add(label);
                        grid.Children.Add(control);
                    }
                    rowIndex++;
                }
                groupBox.Content = grid;
                ContentPanel.Children.Add(groupBox);
            }

            UpdateDependencyStates();
            if (_modeCombo != null) _modeCombo.SelectionChanged += (s, e) => UpdateDependencyStates();
        }



        private System.Windows.FrameworkElement CreateSaveIdRow(string value)
        {
            var grid = new System.Windows.Controls.Grid { Margin = new System.Windows.Thickness(0, 0, 0, 10) };
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(180) });
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });

            var label = CreateLabel("save id");
            var panel = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = System.Windows.VerticalAlignment.Center };

            var tb = new System.Windows.Controls.TextBox
            {
                Text = value,
                IsEnabled = false,
                Width = 150,
                Height = 28,
                VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(50, 50, 50)),
                Foreground = System.Windows.Media.Brushes.LightGray,
                Margin = new System.Windows.Thickness(0, 0, 8, 8)
            };
            var btn = new System.Windows.Controls.Button
            {
                Content = "📂 Open Savegame",
                Width = 150,
                Height = 28,
                IsEnabled = !string.IsNullOrWhiteSpace(value),
                VerticalAlignment = System.Windows.VerticalAlignment.Top
            };
            btn.Click += (s, e) => OpenWorldFolder(value);

            panel.Children.Add(tb);
            panel.Children.Add(btn);

            System.Windows.Controls.Grid.SetColumn(label, 0);
            System.Windows.Controls.Grid.SetColumn(panel, 1);
            grid.Children.Add(label);
            grid.Children.Add(panel);
            return grid;
        }

        private System.Windows.FrameworkElement CreateControl(string key, string value)
        {
            bool canEdit = !_worldExists;
            if (EditableAlways.Contains(key)) canEdit = true;

            if (key.Equals("seed", StringComparison.OrdinalIgnoreCase))
            {
                var panel = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = System.Windows.VerticalAlignment.Center };
                var tb = new System.Windows.Controls.TextBox
                {
                    Text = value,
                    IsEnabled = canEdit,
                    Width = 340,
                    Height = 28,
                    VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
                    Background = (System.Windows.Media.Brush)FindResource("ControlBackgroundBrush"),
                    Margin = new System.Windows.Thickness(0, 0, 8, 8)
                };
                var btn = new System.Windows.Controls.Button { Content = "📋", Width = 30, Height = 28, VerticalAlignment = System.Windows.VerticalAlignment.Top };
                btn.Click += (s, e) => System.Windows.Clipboard.SetText(tb.Text);

                tb.TextChanged += (s, e) => _config[key] = tb.Text;
                panel.Children.Add(tb);
                panel.Children.Add(btn);
                return panel;
            }

            if (_options.ContainsKey(key))
            {
                var combo = new System.Windows.Controls.ComboBox
                {
                    IsEditable = false,
                    IsEnabled = canEdit,
                    Margin = new System.Windows.Thickness(0, 0, 0, 8),
                    Background = canEdit ? (System.Windows.Media.Brush)FindResource("ControlBackgroundBrush") : System.Windows.Media.Brushes.DimGray
                };
                foreach (string opt in _options[key]) combo.Items.Add(opt);
                combo.SelectedIndex = _options[key].FindIndex(x => x.Equals(value, StringComparison.OrdinalIgnoreCase));
                if (combo.SelectedIndex < 0) combo.SelectedIndex = 0;

                combo.SelectionChanged += (s, e) => { if (combo.SelectedItem != null) _config[key] = combo.SelectedItem.ToString()!; };

                if (key.Equals("mode", StringComparison.OrdinalIgnoreCase)) _modeCombo = combo;
                else if (DependentParameters.Contains(key)) _dependentControlsList.Add(combo);

                return combo;
            }
            else
            {
                var textBox = new System.Windows.Controls.TextBox
                {
                    Text = value,
                    IsEnabled = canEdit,
                    Margin = new System.Windows.Thickness(0, 0, 0, 8),
                    VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
                    Background = canEdit ? (System.Windows.Media.Brush)FindResource("ControlBackgroundBrush") : System.Windows.Media.Brushes.DimGray
                };

                if (key.Contains("port", StringComparison.OrdinalIgnoreCase))
                    textBox.PreviewTextInput += (s, e) => e.Handled = !char.IsDigit(e.Text, 0);

                if (key.Equals("display name", StringComparison.OrdinalIgnoreCase)) textBox.MaxLength = 24;
                if (key.Equals("server name", StringComparison.OrdinalIgnoreCase)) textBox.MaxLength = 22;

                textBox.TextChanged += (s, e) => { _config[key] = textBox.Text; };
                if (DependentParameters.Contains(key)) _dependentControlsList.Add(textBox);
                return textBox;
            }
        }


        


        private TextBlock CreateLabel(string key)
        {
            return new TextBlock
            {
                Text = char.ToUpper(key[0]) + key.Substring(1),
                Margin = new Thickness(0, 8, 8, 4),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = System.Windows.Media.Brushes.Orange
            };
        }



        private void OpenWorldFolder(string saveId)
        {
            if (_isProcessingFolder) return;
            _isProcessingFolder = true;

            try
            {
                string localLow = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData).Replace("Local", "LocalLow"),
                    "Sand Sailor Studio", "Aska", "data", "server", $"savegame_{saveId}");

                if (Directory.Exists(localLow))
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = localLow,
                        UseShellExecute = true
                    };
                    using (System.Diagnostics.Process.Start(psi)) { }
                }
                else
                    System.Windows.MessageBox.Show($"Folder not found:\n{localLow}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { /* игнорируем */ }
            finally
            {
                Task.Delay(500).ContinueWith(_ => _isProcessingFolder = false);
            }
        }


        private void UpdateDependencyStates()
        {
            if (_modeCombo == null) return;

            // Блок Environment доступен только если Mode = Custom И мир не создан
            bool isCustom = _modeCombo.SelectedItem?.ToString() == "custom";
            bool canEditEnvironment = isCustom && !_worldExists;

            foreach (var control in _dependentControlsList)
            {
                if (control != null)
                    control.IsEnabled = canEditEnvironment;
            }
        }

        private void Save_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            try
            {
                string backup = _configPath + ".bak";
                if (System.IO.File.Exists(backup)) System.IO.File.Delete(backup);
                System.IO.File.Copy(_configPath, backup);

                var lines = new List<string>();
                foreach (string rawLine in System.IO.File.ReadAllLines(_configPath))
                {
                    string trimmed = rawLine.Trim();
                    if (trimmed.StartsWith('#') || trimmed.StartsWith("//") || string.IsNullOrWhiteSpace(trimmed) || !trimmed.Contains('='))
                    {
                        lines.Add(rawLine);
                        continue;
                    }
                    int eqPos = rawLine.IndexOf('=');
                    string key = rawLine[..eqPos].Trim();
                    if (_config.TryGetValue(key, out string? newValue))
                        lines.Add($"{key} = {newValue}");
                    else
                        lines.Add(rawLine);
                }
                System.IO.File.WriteAllLines(_configPath, lines);

                if (this.Owner is MainWindow main)
                {
                    main.UpdateServerHeader();
                    main.ResetAuthFailedFlag();
                }

                this.Close();
            }
            catch (Exception ex)
            {
                ErrorDialog.Show($"Failed to save configuration:\n{ex.Message}", "Save Error");
            }
        }

        private void Cancel_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            this.Close();
        }
    }
}