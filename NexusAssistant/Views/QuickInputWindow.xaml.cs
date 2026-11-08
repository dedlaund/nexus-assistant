using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace NexusAssistant.Views;

public partial class QuickInputWindow : Window
{
    private readonly AssistantViewModel _model;
    private bool _sending;
    private bool _closing;

    internal QuickInputWindow(AssistantViewModel model)
    {
        _model = model;
        InitializeComponent();
        using var resource = Application.GetResourceStream(
            new Uri("/NexusAssistant;component/Assets/Nexus.ico", UriKind.Relative)).Stream;
        var icon = BitmapDecoder.Create(resource, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Brand.Source = icon.Frames.MaxBy(frame => frame.PixelWidth);
        Closing += (_, _) => _closing = true;
        Deactivated += (_, _) => { if (IsVisible && !_closing) Hide(); };
    }

    internal void Open()
    {
        if (!IsVisible)
        {
            if (!_sending)
            {
                ResultArea.Visibility = Visibility.Collapsed;
                Query.Clear();
            }
            // Show first so PointFromScreen uses the monitor DPI of this window.
            Opacity = 0;
            var area = Forms.Screen.FromPoint(Forms.Cursor.Position).WorkingArea;
            var anchor = new System.Windows.Point(area.Left + area.Width / 2, area.Top + area.Height / 4);
            Show();
            Position(anchor);
            Position(anchor); // Correct any offset after a per-monitor DPI change.
            Opacity = 1;
        }
        Activate();
        Query.Focus();
        Keyboard.Focus(Query);
    }

    private void Position(System.Windows.Point anchor)
    {
        var offset = PointFromScreen(anchor);
        Left = (double.IsNaN(Left) ? 0 : Left) + offset.X - Width / 2;
        Top = (double.IsNaN(Top) ? 0 : Top) + offset.Y;
    }

    private async void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Hide(); e.Handled = true; }
        if (e.Key == Key.Enter) { e.Handled = true; await SubmitAsync(); }
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await SubmitAsync();
    private void Dismiss_Click(object sender, RoutedEventArgs e) => Hide();

    private async Task SubmitAsync()
    {
        if (_sending || string.IsNullOrWhiteSpace(Query.Text)) return;
        if (!_model.CanEdit)
        {
            Result.Text = "NEXUS занят. Повторите запрос через несколько секунд.";
            ResultArea.Visibility = Visibility.Visible;
            return;
        }
        _sending = true;
        var text = Query.Text;
        Query.Clear();
        Result.Text = "Обрабатываю запрос…";
        ResultArea.Visibility = Visibility.Visible;
        try { Result.Text = await _model.SendQuickAsync(text); }
        finally { _sending = false; }
    }
}
