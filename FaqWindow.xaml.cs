using System;
using System.IO;
using System.Windows;

namespace AskaServerManager
{
    public partial class FaqWindow : Window
    {
        public FaqWindow()
        {
            InitializeComponent();
            NativeMethods.SetDarkTitleBar(this);
            Owner = System.Windows.Application.Current.MainWindow;

            LoadFaqFromResources();
        }

        private void LoadFaqFromResources()
        {
            try
            {
                // Читаем файл прямо из "тела" программы
                var uri = new Uri("pack://application:,,,/faq.txt");
                var resourceStream = System.Windows.Application.GetResourceStream(uri);

                if (resourceStream != null)
                {
                    using (var reader = new StreamReader(resourceStream.Stream))
                    {
                        FaqContent.Text = reader.ReadToEnd();
                    }
                }
            }
            catch (Exception)
            {
                FaqContent.Text = "Internal FAQ resource not found.";
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
