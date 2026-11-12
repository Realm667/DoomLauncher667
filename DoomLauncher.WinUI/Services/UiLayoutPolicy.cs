namespace DoomLauncher.WinUI.Services;

// All values are device-independent pixels. Windows rasterization is applied
// by XAML, never a second time by the responsive layout.
internal sealed record UiLayoutProfile(bool CompactNavigation, bool StackedHeader,
    bool InlineDetails, bool StackedTools, double DetailsWidth, double Padding, double HeroHeight);

internal static class UiLayoutPolicy
{
    internal static UiLayoutProfile ForViewport(double width, double height) => new(
        width < 1280, width < 1360, width >= 1280, width < 1180,
        width >= 1660 ? 370 : 330, width < 1100 ? 12 : 24,
        Math.Clamp(height * .34, 190, 340));
    internal static double DialogWidth(double viewport, double preferred) => Math.Max(240, Math.Min(preferred, viewport - 96));
    internal static double DialogHeight(double viewport) => Math.Max(180, viewport - 150);
}
