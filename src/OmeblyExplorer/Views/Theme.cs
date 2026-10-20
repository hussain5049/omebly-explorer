// Which look is on (Omebly Glass = dark, Windows Light = light), and its colours for things made in code.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace OmeblyExplorer.Views;

public static class Theme
{
    public static ElementTheme Current { get; set; } = ElementTheme.Dark;
    private static ResourceDictionary? _themes;
    public static Brush Get(string key)
    {
        _themes ??= Application.Current.Resources.MergedDictionaries.First((d) => d.ThemeDictionaries.ContainsKey("Dark"));
        var dict = (ResourceDictionary)_themes.ThemeDictionaries[Current == ElementTheme.Light ? "Light" : "Dark"];
        return (Brush)dict[key];
    }
}
