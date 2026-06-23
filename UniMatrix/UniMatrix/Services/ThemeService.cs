using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;

namespace UniMatrix.Services
{
    /// <summary>
    /// Controls the app accent color. By default the accent follows the system
    /// accent color; the user can switch it to the UniMatrix signature green.
    /// The relevant brush resources are mutated in place so every control that
    /// references them updates immediately.
    /// </summary>
    internal static class ThemeService
    {
        /// <summary>The UniMatrix signature green used when the system accent is disabled.</summary>
        public static readonly Color SignatureGreen = Color.FromArgb(0xFF, 0x0D, 0xBD, 0x8B);

        private static Color GetSystemAccent()
        {
            try
            {
                var ui = new Windows.UI.ViewManagement.UISettings();
                return ui.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
            }
            catch
            {
                return SignatureGreen;
            }
        }

        /// <summary>Applies the accent preference to the shared brush resources.</summary>
        /// <param name="useSystemAccent">True to follow the system accent; false for signature green.</param>
        public static void Apply(bool useSystemAccent)
        {
            Color accent = useSystemAccent ? GetSystemAccent() : SignatureGreen;
            SetBrush("AppAccentBrush", accent);
            SetBrush("AppOwnBubbleBrush", accent);
        }

        private static void SetBrush(string key, Color color)
        {
            object res;
            if (Application.Current.Resources.TryGetValue(key, out res))
            {
                var brush = res as SolidColorBrush;
                if (brush != null) brush.Color = color;
            }
        }
    }
}
