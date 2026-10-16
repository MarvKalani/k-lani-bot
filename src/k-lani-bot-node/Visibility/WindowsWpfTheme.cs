#if WINDOWS
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Markup;

namespace KLaniBot.Visibility;

// All node windows share a readable WPF palette. Translucent chrome belongs to passive overlays; dialogs retain
// solid readable content and the native keyboard/accessibility behavior of WPF controls.
internal static class WindowsWpfTheme
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    public static void Apply(Window window)
    {
        ApplyResources(window);
        window.FontFamily = new FontFamily("Segoe UI");
        window.FontSize = 14;
        window.Foreground = Brush(window, "TextBrush");
        window.Background = Brush(window, "SurfaceBrush");
        window.SourceInitialized += (_, _) => ApplyNativeFrame(window);
    }

    public static void ApplyResources(FrameworkElement element)
    {
        using Stream? resource = typeof(WindowsWpfTheme).Assembly.GetManifestResourceStream("KLaniBot.windows-theme.xaml");
        element.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(resource!));
    }

    private static void ApplyNativeFrame(Window window)
    {
        const int ImmersiveDarkMode = 20;
        const int CornerPreference = 33;
        const int DarkMode = 1;
        const int RoundCorners = 2;
        int darkMode = DarkMode;
        int rounded = RoundCorners;
        IntPtr handle = new WindowInteropHelper(window).Handle;
        DwmSetWindowAttribute(handle, ImmersiveDarkMode, ref darkMode, sizeof(int));
        DwmSetWindowAttribute(handle, CornerPreference, ref rounded, sizeof(int));
    }

    public static Brush Brush(FrameworkElement element, string key)
    {
        return (Brush)element.FindResource(key);
    }

    public static Border Surface(FrameworkElement element, UIElement content, int padding = 18)
    {
        return new Border
        {
            Child = content,
            Background = Brush(element, "SurfaceBrush"),
            BorderBrush = Brush(element, "LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(padding)
        };
    }

    public static TextBlock Text(string text, int size = 14)
    {
        return new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 9) };
    }

    public static Image Logo()
    {
        using var stream = new MemoryStream(ProductBranding.Read(ProductImage.WindowsDialog));
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();

        return new Image
        {
            Source = image, Width = 180, Height = 60, Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 16)
        };
    }
}
#endif
