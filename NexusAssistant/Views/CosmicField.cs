using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace NexusAssistant.Views;

public sealed class CosmicField : FrameworkElement
{
    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(Activity), typeof(CosmicField), new PropertyMetadata(Activity.Idle, ActivityChanged));
    public static readonly DependencyProperty AudioProperty = DependencyProperty.Register(
        nameof(Audio), typeof(double), typeof(CosmicField), new PropertyMetadata(0d, ActivityChanged));
    public static readonly DependencyProperty RevealProperty = DependencyProperty.Register(
        nameof(Reveal), typeof(double), typeof(CosmicField), new PropertyMetadata(1d, RevealChanged));

    public Activity Mode { get => (Activity)GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
    public double Audio { get => (double)GetValue(AudioProperty); set => SetValue(AudioProperty, value); }
    public double Reveal { get => (double)GetValue(RevealProperty); set => SetValue(RevealProperty, value); }

    private static readonly Rect ImageBounds = new(0, 0, AccretionScene.Width, AccretionScene.Height);
    private const int FramesPerSecond = 24;
    private static readonly Lazy<Task<BitmapSource[]>> Frames = new(() => Task.Run(BakeFrames));
    public static Task PrepareAsync() => Frames.Value;

    private readonly DispatcherTimer _playback = new(DispatcherPriority.Background);
    private readonly DrawingGroup _scene = new();
    private readonly DrawingGroup _signal = new() { Opacity = 0 };
    private readonly DrawingGroup _error = new() { Opacity = 0 };
    private readonly ImageDrawing _current = new() { Rect = ImageBounds };
    private Window? _window;
    private double _position;
    private long _lastFrameTime;
    private bool _ready;

    public CosmicField()
    {
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
        _scene.Children.Add(_current);
        _signal.Children.Add(CreateGlow(Color.FromRgb(255, 177, 95)));
        _error.Children.Add(CreateGlow(Color.FromRgb(239, 81, 76)));
        _scene.Children.Add(_signal);
        _scene.Children.Add(_error);
        _playback.Tick += Advance;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => UpdatePlayback();
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        _window = Window.GetWindow(this);
        if (_window is not null)
        {
            _window.Activated += WindowChanged;
            _window.Deactivated += WindowChanged;
            _window.StateChanged += WindowChanged;
        }
        await PrepareAsync();
        if (!IsLoaded) return;
        EnsureImage();
        InvalidateVisual();
        UpdatePlayback();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _playback.Stop();
        if (_window is null) return;
        _window.Activated -= WindowChanged;
        _window.Deactivated -= WindowChanged;
        _window.StateChanged -= WindowChanged;
        _window = null;
    }

    private void WindowChanged(object? sender, EventArgs args) => UpdatePlayback();

    private void UpdatePlayback()
    {
        if (!_ready || !IsLoaded || !IsVisible || _window is null ||
            _window.WindowState == WindowState.Minimized || !SystemParameters.ClientAreaAnimation)
        {
            _playback.Stop();
            return;
        }
        // Background priority lets input run first without waiting for a completely idle dispatcher.
        var interval = TimeSpan.FromMilliseconds(_window.IsActive ? 36 : 83);
        if (_playback.Interval != interval) _playback.Interval = interval;
        if (_playback.IsEnabled) return;
        _lastFrameTime = Stopwatch.GetTimestamp();
        _playback.Start();
    }

    private void Advance(object? sender, EventArgs args)
    {
        // Skip late frames instead of queuing work behind keyboard input.
        var frames = Frames.Value.Result;
        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(_lastFrameTime, now).TotalSeconds;
        var speed = Mode is Activity.Recognizing or Activity.Processing ? 1.45 : 1;
        _position = (_position + elapsed * FramesPerSecond * speed) % frames.Length;
        _lastFrameTime = now;
        var frame = frames[(int)_position];
        if (!ReferenceEquals(_current.ImageSource, frame)) _current.ImageSource = frame;
    }

    private static void ActivityChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var field = (CosmicField)sender;
        if (!field._ready) return;
        field.UpdateSignal();
        if (args.Property == ModeProperty) field.UpdatePlayback();
    }

    private static void RevealChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((CosmicField)sender)._scene.Opacity = Math.Clamp((double)args.NewValue, 0, 1);

    private void UpdateSignal()
    {
        var intensity = Mode switch
        {
            Activity.Listening => .08 + Audio * .42,
            Activity.Speaking => .08 + Audio * .37,
            Activity.Recognizing or Activity.Processing => .19,
            _ => 0
        };
        FadeTo(_signal, intensity);
        FadeTo(_error, Mode == Activity.Error ? .58 : 0);
    }

    private static void FadeTo(DrawingGroup layer, double opacity)
    {
        if ((double)layer.GetAnimationBaseValue(DrawingGroup.OpacityProperty) == opacity) return;
        var previous = layer.Opacity;
        layer.BeginAnimation(DrawingGroup.OpacityProperty, null);
        layer.Opacity = opacity;
        if (SystemParameters.ClientAreaAnimation && Math.Abs(previous - opacity) > .01)
        {
            var fade = new DoubleAnimation(previous, opacity, TimeSpan.FromMilliseconds(140))
            {
                FillBehavior = FillBehavior.Stop
            };
            Timeline.SetDesiredFrameRate(fade, 30);
            layer.BeginAnimation(DrawingGroup.OpacityProperty, fade);
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || !Frames.Value.IsCompletedSuccessfully) return;
        EnsureImage();
        var dpi = VisualTreeHelper.GetDpi(this);
        var scale = Math.Min(.88 * Math.Min(ActualWidth / AccretionScene.Width, ActualHeight / AccretionScene.Height),
            1280 / (AccretionScene.Width * dpi.DpiScaleX));
        var width = AccretionScene.Width * scale;
        var height = AccretionScene.Height * scale;
        dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, (ActualWidth - width) / 2, (ActualHeight - height) / 2));
        dc.DrawDrawing(_scene);
        dc.Pop();
    }

    private void EnsureImage()
    {
        if (_ready) return;
        _current.ImageSource = Frames.Value.Result[0];
        _scene.Opacity = Math.Clamp(Reveal, 0, 1);
        _ready = true;
        UpdateSignal();
    }

    private static GeometryDrawing CreateGlow(Color tint)
    {
        var brush = new RadialGradientBrush
        {
            Center = new Point(.508, .49), GradientOrigin = new Point(.508, .49), RadiusX = .47, RadiusY = .46,
            GradientStops =
            [
                new(Color.FromArgb(0, tint.R, tint.G, tint.B), .28),
                new(Color.FromArgb(70, tint.R, tint.G, tint.B), .56),
                new(Color.FromArgb(0, tint.R, tint.G, tint.B), 1)
            ]
        };
        var drawing = new GeometryDrawing(brush, null, new RectangleGeometry(ImageBounds));
        drawing.Freeze();
        return drawing;
    }

    private static BitmapSource[] BakeFrames()
    {
        const int count = 36;
        const float duration = (float)count / FramesPerSecond;
        const float overlap = .35f;
        var renderer = new AccretionScene();
        var pixels = renderer.NewFrame();
        var seam = renderer.NewFrame();
        var frames = new BitmapSource[count];
        for (var phase = 0; phase < count; phase++)
        {
            var time = (float)phase / FramesPerSecond;
            renderer.Render(pixels, time);
            if (time > duration - overlap)
            {
                renderer.Render(seam, time - duration);
                var blend = (int)(256 * (time - duration + overlap) / overlap);
                for (var index = 0; index < pixels.Length; index++)
                    pixels[index] = BlendPixel(pixels[index], seam[index], blend);
            }
            var image = BitmapSource.Create(AccretionScene.Width, AccretionScene.Height, 96, 96,
                PixelFormats.Bgr32, null, pixels, AccretionScene.Width * sizeof(int));
            image.Freeze();
            frames[phase] = image;
        }
        return frames;
    }

    private static int BlendPixel(int first, int second, int weight)
    {
        var inverse = 256 - weight;
        var redBlue = ((first & 0xff00ff) * inverse + (second & 0xff00ff) * weight) >>> 8 & 0xff00ff;
        var green = ((first & 0x00ff00) * inverse + (second & 0x00ff00) * weight) >>> 8 & 0x00ff00;
        return redBlue | green;
    }
}
