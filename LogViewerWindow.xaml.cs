using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;

namespace AskaServerManager
{
    public partial class LogViewerWindow : Window
    {
        private List<string> _allLines = new List<string>();
        private List<FilterRow> _filterRows = new List<FilterRow>();  // stores all filter rows
        private const int MAX_FILTERS = 4;

        public LogViewerWindow()
        {
            InitializeComponent();
            Owner = System.Windows.Application.Current.MainWindow;
            BtnAddFilter.IsEnabled = true;
        }

        // Class for storing a filter row (ComboBox + "-" button)
        private class FilterRow
        {
            public StackPanel Panel { get; set; } = default!;
            public System.Windows.Controls.ComboBox Combo { get; set; } = default!;
            public System.Windows.Controls.Button RemoveButton { get; set; } = default!;
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog()
            {
                Filter = "Log files (*.txt)|*.txt|All files (*.*)|*.*",
                InitialDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs")
            };
            if (dlg.ShowDialog() == true)
            {
                _allLines = File.ReadAllLines(dlg.FileName).ToList();
                // Clear all filters when loading a new log
                ClearAllFilters();
                ApplyFilters();
            }
        }

        private void AddFilter_Click(object sender, RoutedEventArgs e)
        {
            if (_filterRows.Count >= MAX_FILTERS)
            {
                BtnAddFilter.IsEnabled = false;
                return;
            }

            var row = CreateFilterRow();
            _filterRows.Add(row);
            FilterContainer.Children.Add(row.Panel);

            // Update available prefix lists in all comboboxes
            RefreshAllComboItems();

            // If max reached, disable "+" button
            if (_filterRows.Count >= MAX_FILTERS)
                BtnAddFilter.IsEnabled = false;
        }

        private FilterRow CreateFilterRow()
        {
            var panel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            var combo = new System.Windows.Controls.ComboBox { Width = 150, Margin = new Thickness(0, 0, 5, 0) };
            var removeBtn = new System.Windows.Controls.Button { Content = "-", Width = 25, Margin = new Thickness(5, 0, 0, 0) };

            combo.Items.Add("(All)");
            combo.SelectedIndex = 0;

            var row = new FilterRow { Panel = panel, Combo = combo, RemoveButton = removeBtn };

            combo.SelectionChanged += (s, ev) =>
            {
                RefreshAllComboItems();
                ApplyFilters();
            };

            removeBtn.Click += (s, ev) => RemoveFilterRow(row);

            panel.Children.Add(combo);
            panel.Children.Add(removeBtn);

            return row;
        }

        private void RemoveFilterRow(FilterRow row)
        {
            if (_filterRows.Contains(row))
            {
                _filterRows.Remove(row);
                FilterContainer.Children.Remove(row.Panel);
                RefreshAllComboItems();
                ApplyFilters();

                // If below MAX_FILTERS, enable "+" button
                if (_filterRows.Count < MAX_FILTERS)
                    BtnAddFilter.IsEnabled = true;
            }
        }

        private void ClearAllFilters()
        {
            foreach (var row in _filterRows)
                FilterContainer.Children.Remove(row.Panel);
            _filterRows.Clear();
            RefreshAllComboItems();
            BtnAddFilter.IsEnabled = true;
        }

        private void RefreshAllComboItems()
        {
            // Get all unique prefixes from the log
            var allPrefixes = GetUniquePrefixes();

            // For each combobox, build list of available prefixes
            foreach (var row in _filterRows)
            {
                var currentSelected = row.Combo.SelectedItem?.ToString();
                // Set of selected prefixes in other comboboxes
                var selectedInOthers = _filterRows
                    .Where(r => r != row && r.Combo.SelectedIndex > 0)
                    .Select(r => r.Combo.SelectedItem.ToString())
                    .ToHashSet();

                var availablePrefixes = new List<string> { "(All)" };
                availablePrefixes.AddRange(allPrefixes.Where(p => !selectedInOthers.Contains(p)));

                // Preserve current selection
                row.Combo.ItemsSource = availablePrefixes;
                if (currentSelected != null && availablePrefixes.Contains(currentSelected))
                    row.Combo.SelectedItem = currentSelected;
                else
                    row.Combo.SelectedIndex = 0; // reset to "(All)" if selected disappeared
            }
        }

        private List<string> GetUniquePrefixes()
        {
            var prefixes = new HashSet<string>();
            foreach (var line in _allLines)
            {
                int start = line.IndexOf('[');
                int end = line.IndexOf(']', start + 1);
                if (start >= 0 && end > start)
                {
                    string prefix = line.Substring(start + 1, end - start - 1);
                    prefixes.Add(prefix);
                }
            }
            return prefixes.OrderBy(p => p).ToList();
        }

        private void ApplyFilters()
        {
            LogViewerBox.Document.Blocks.Clear();

            // Active filters: selected prefixes (not "(All)")
            var activeFilters = _filterRows
                .Where(r => r.Combo.SelectedIndex > 0)
                .Select(r => r.Combo.SelectedItem.ToString())
                .ToList();

            foreach (var line in _allLines)
            {
                bool matches = true;
                foreach (var filter in activeFilters)
                {
                    if (!line.Contains($"[{filter}]"))
                    {
                        matches = false;
                        break;
                    }
                }
                if (matches)
                {
                    var paragraph = new Paragraph();
                    paragraph.Inlines.Add(new Run(line) { Foreground = System.Windows.Media.Brushes.White });
                    LogViewerBox.Document.Blocks.Add(paragraph);
                }
            }
        }
    }
}