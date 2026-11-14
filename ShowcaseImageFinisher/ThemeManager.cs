using System.Windows;

namespace ShowcaseImageFinisher;

/// <summary>
/// Switches the UI theme by swapping the merged resource
/// dictionaries: a palette dictionary (Themes/Dark.xaml /
/// Light.xaml / Standard.xaml) plus, except for Standard, the
/// shared control styles (Themes/Styles.xaml). All theme colors are
/// referenced as DynamicResource, so Dark &lt;-&gt; Light recolors the
/// running window immediately. Standard drops the Styles dictionary
/// entirely — the original, plain default WPF look — which only
/// takes full effect when applied before the window is created
/// (App.OnStartup does that from the saved setting).
/// </summary>
public static class ThemeManager
{
    public const string Dark = "Dark";
    public const string Light = "Light";
    public const string Standard = "Standard";

    public static string Current { get; private set; } = Dark;

    public static string Normalize(string? theme) => theme switch
    {
        Light => Light,
        Standard => Standard,
        _ => Dark,
    };

    public static void Apply(string? theme)
    {
        theme = Normalize(theme);
        Current = theme;
        var merged = Application.Current.Resources.MergedDictionaries;
        merged.Clear();
        merged.Add(new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Themes/{theme}.xaml", UriKind.Absolute),
        });
        if (theme != Standard)
            merged.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Themes/Styles.xaml", UriKind.Absolute),
            });
    }
}
