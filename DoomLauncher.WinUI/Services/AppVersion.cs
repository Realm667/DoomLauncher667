using System.Reflection;

namespace DoomLauncher.WinUI.Services;

internal static class AppVersion
{
    public static string Current { get; } = Resolve(
        typeof(AppVersion).Assembly);

    internal static string Resolve(Assembly assembly)
    {
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion.Split('+', 2)[0];
        }

        var version = assembly.GetName().Version;
        return version is null
            ? "0.0.0"
            : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
