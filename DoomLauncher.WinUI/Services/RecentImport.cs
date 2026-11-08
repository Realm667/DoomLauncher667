namespace DoomLauncher.WinUI.Services;

internal static class RecentImport
{
    public static bool IsNew(DateTime? importedAt, DateTime now) =>
        importedAt is { } timestamp && timestamp <= now && now - timestamp < TimeSpan.FromDays(7);
}
