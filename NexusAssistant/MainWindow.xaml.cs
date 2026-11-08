using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NexusAssistant.Views;
using NexusAssistant.Services;

namespace NexusAssistant;

public partial class MainWindow : Window
{
    private readonly AssistantViewModel _model;
    private DispatcherOperation? _chromeUpdate;

    public MainWindow(AssistantViewModel model, TelegramControl? telegram = null)
    {
        _model = model;
        InitializeComponent();
        DataContext = model;
        RemotePanel.DataContext = telegram;
        _model.MessageAdded += ScrollToLatest;
        Loaded += OnLoaded;
        StateChanged += (_, _) => UpdateChrome();
        SizeChanged += (_, _) => ScheduleInsets();
        LocationChanged += (_, _) => ScheduleInsets();
        DpiChanged += (_, _) => ScheduleInsets();
        Closed += (_, _) => _model.MessageAdded -= ScrollToLatest;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        ScrollToLatest();
        if (_model.Intro && SystemParameters.ClientAreaAnimation)
        {
            Cosmos.BeginAnimation(Views.CosmicField.RevealProperty, Fade(0, 1, 950));
            var brand = Fade(0, 1, 510);
            brand.BeginTime = TimeSpan.FromMilliseconds(230);
            HeroTitle.BeginAnimation(OpacityProperty, brand);
        }
        if (_model.ShowComposer) InputBox.Focus();
    }

    internal Views.PcToolsPanel PcTools => PcPanel;

    private void QuickInput_Click(object sender, RoutedEventArgs e) => (Application.Current as App)?.ShowQuickInput();

    private void Navigate(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || sender is not RadioButton { Tag: string tag } || !Enum.TryParse<Page>(tag, out var page)) return;
        ClearConfirmation.Visibility = Visibility.Collapsed;
        _model.CurrentPage = page;
        if (SystemParameters.ClientAreaAnimation)
            ContentArea.BeginAnimation(OpacityProperty, Fade(.2, 1, 220));
        if (_model.ShowConversation) ScrollToLatest();
    }

    private static DoubleAnimation Fade(double from, double to, int duration) =>
        new(from, to, TimeSpan.FromMilliseconds(duration))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };

    private void ScrollToLatest() => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
        () => ChatScroll.ScrollToEnd());

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateChrome()
    {
        var maximized = WindowState == WindowState.Maximized;
        Frame.CornerRadius = new CornerRadius(maximized ? 0 : 13);
        MaximizeIcon.Text = maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = maximized ? "Восстановить" : "Развернуть";
        if (!maximized) Frame.Margin = default;
        ScheduleInsets();
    }

    private void ScheduleInsets()
    {
        if (WindowState != WindowState.Maximized || _chromeUpdate?.Status == DispatcherOperationStatus.Pending) return;
        _chromeUpdate = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (IsLoaded && WindowState == WindowState.Maximized)
                Frame.Margin = MaximizedWindowInsets.Get(this);
        });
    }

    private async void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await _model.SendAsync();
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        InputBox.Focus();
        await _model.SendAsync();
    }
    private async void Mic_Click(object sender, RoutedEventArgs e) => await _model.ListenAsync();
    private async void Save_Click(object sender, RoutedEventArgs e) => await _model.SaveSettingsAsync();
    private void Switch_Click(object sender, RoutedEventArgs e)
    {
        if (_model.ClickSounds) SoundSwitch_Click(sender, e);
    }
    private void SoundSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton toggle) SwitchSound.Play(toggle.IsChecked == true);
    }

    private void Example_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string command }) return;
        _model.Input = command;
        ChatNav.IsChecked = true;
        InputBox.Focus();
        InputBox.CaretIndex = InputBox.Text.Length;
    }

    private void ConfirmClear_Click(object sender, RoutedEventArgs e) => ClearConfirmation.Visibility = Visibility.Visible;
    private void CancelClear_Click(object sender, RoutedEventArgs e) => ClearConfirmation.Visibility = Visibility.Collapsed;
    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        ClearConfirmation.Visibility = Visibility.Collapsed;
        await _model.ClearHistoryAsync();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string text } button) return;
        try
        {
            Clipboard.SetText(text);
            button.ToolTip = "Сообщение скопировано";
        }
        catch (ExternalException) { button.ToolTip = "Буфер обмена занят. Попробуйте ещё раз."; }
    }

}
