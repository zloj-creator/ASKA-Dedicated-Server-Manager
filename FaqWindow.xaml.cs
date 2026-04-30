using System.IO;
using System.Windows;

namespace AskaServerManager
{
    public partial class FaqWindow : Window
    {
        public FaqWindow()
        {
            InitializeComponent();
            Owner = System.Windows.Application.Current.MainWindow;
            string faqPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FAQ.txt");
            if (File.Exists(faqPath))
                FaqContent.Text = File.ReadAllText(faqPath);
            else
                FaqContent.Text = "FAQ.txt file not found in program folder.";
        }
        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}