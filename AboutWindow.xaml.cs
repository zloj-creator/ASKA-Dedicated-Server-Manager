using System.Windows;

namespace AskaServerManager
{
    public partial class AboutWindow : Window
    {
        public AboutWindow()
        {
            InitializeComponent();
            NativeMethods.SetDarkTitleBar(this);
            Owner = System.Windows.Application.Current.MainWindow;
            VersionText.Text = $"Version: {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}";
            DotNetVersionText.Text = $".NET Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}";
        }
        private void OkButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}