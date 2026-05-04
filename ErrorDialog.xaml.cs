using System.Windows;
using System.Windows.Input;

namespace AskaServerManager
{
    public partial class ErrorDialog : Window
    {

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                this.DragMove(); // Позволяет тянуть окно за любое место фона
        }

        public ErrorDialog(string message, string title = "Error")
        {
            InitializeComponent();
            MessageText.Text = message;
            Title = title;
            Owner = System.Windows.Application.Current.MainWindow;
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        // Static method for quick call
        public static void Show(string message, string title = "Error")
        {
            var dialog = new ErrorDialog(message, title);
            dialog.ShowDialog();
        }
    }
}