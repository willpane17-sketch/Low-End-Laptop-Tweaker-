using System.Windows;
using System.Windows.Input;
using Tickshift.App.Infrastructure;

namespace Tickshift.App.Views;

/// <summary>Themed confirmation dialog. Destructive actions can require an explicit acknowledgement tick.</summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog(ConfirmRequest request)
    {
        InitializeComponent();
        Title = request.Title;
        TitleText.Text = request.Title;
        MessageText.Text = request.Message;
        DetailsList.ItemsSource = request.Details ?? [];
        ConfirmButton.Content = request.ConfirmText;
        CancelButton.Content = request.CancelText;

        if (request.Destructive)
        {
            ConfirmButton.Style = (Style)FindResource("DangerButton");
            IconHost.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "BadSoft");
            HeaderIcon.SetResourceReference(ForegroundProperty, "Bad");
            HeaderIcon.Data = (System.Windows.Media.Geometry)FindResource("Icon.Warning");
        }

        if (request.Acknowledgement is { } ack)
        {
            Acknowledge.Visibility = Visibility.Visible;
            AcknowledgeText.Text = ack;
            ConfirmButton.IsEnabled = false;
        }

        // Safe default: keyboard focus starts on Cancel for destructive actions.
        Loaded += (_, _) => (request.Destructive ? CancelButton : ConfirmButton).Focus();
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };
    }

    private void OnAcknowledgeChanged(object sender, RoutedEventArgs e) => ConfirmButton.IsEnabled = Acknowledge.IsChecked == true;

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
