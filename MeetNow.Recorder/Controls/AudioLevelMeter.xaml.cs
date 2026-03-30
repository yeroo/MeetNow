using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MeetNow.Recorder.Controls;

public partial class AudioLevelMeter : UserControl
{
    public static readonly DependencyProperty LevelProperty =
        DependencyProperty.Register(
            nameof(Level),
            typeof(float),
            typeof(AudioLevelMeter),
            new PropertyMetadata(0f, OnLevelChanged));

    public float Level
    {
        get => (float)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public AudioLevelMeter()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateBar(Level);
    }

    private static void OnLevelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AudioLevelMeter meter)
            meter.UpdateBar((float)e.NewValue);
    }

    private void UpdateBar(float level)
    {
        double displayLevel = Math.Min(1.0, level * 5.0);
        LevelBar.Width = ActualWidth * displayLevel;

        if (level > 0.15f)
            LevelBar.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F44336"));
        else if (level > 0.08f)
            LevelBar.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFEB3B"));
        else
            LevelBar.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4CAF50"));
    }
}
