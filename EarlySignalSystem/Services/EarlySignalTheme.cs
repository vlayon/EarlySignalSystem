using MudBlazor;

namespace EarlySignalSystem.Services;

public static class EarlySignalTheme
{
    public const string ColorBackground = "#0f1117";
    public const string ColorCardBackground = "#1a1d2e";
    public const string ColorPrimary = "#6366f1";
    public const string ColorSuccess = "#22c55e";
    public const string ColorWarning = "#f59e0b";
    public const string ColorDanger = "#ef4444";

    public static MudTheme Theme { get; } = new()
    {
        PaletteDark = new PaletteDark
        {
            Background = ColorBackground,
            Surface = ColorCardBackground,
            AppbarBackground = ColorCardBackground,
            DrawerBackground = ColorCardBackground,
            Primary = ColorPrimary,
            Success = ColorSuccess,
            Warning = ColorWarning,
            Error = ColorDanger,
            TextPrimary = "#e5e7eb",
            TextSecondary = "#9ca3af",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = ["Inter", "Segoe UI", "sans-serif"]
            }
        }
    };
}
