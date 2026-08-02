using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DoomLauncher.Bootstrap
{
    internal static class Program
    {
        private const string ProductName = "Doom Launcher 667";

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                string root = Path.GetFullPath(
                    AppDomain.CurrentDomain.BaseDirectory);
                string pathRoot = Path.GetPathRoot(root);
                if (root.Length > pathRoot.Length)
                {
                    root = root.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar);
                }
                string winUiDirectory = Path.Combine(root, "WinUI");
                string launcherPath = Path.Combine(
                    winUiDirectory,
                    "DoomLauncher.WinUI.exe");
                if (!File.Exists(launcherPath))
                {
                    return Fail(
                        "The launcher component was not found:\n" + launcherPath +
                        "\n\nExtract the complete release package before " +
                        "starting the application.");
                }

                string userStateDirectory = Path.Combine(
                    root,
                    "Data",
                    "UserState");
                Directory.CreateDirectory(userStateDirectory);

                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = launcherPath;
                startInfo.WorkingDirectory = winUiDirectory;
                startInfo.UseShellExecute = false;
                startInfo.Arguments = JoinArguments(args);
                startInfo.EnvironmentVariables["DOOMLAUNCHER_DATABASE"] =
                    Path.Combine(root, "DoomLauncher.sqlite");
                startInfo.EnvironmentVariables["DOOMLAUNCHER_USER_STATE"] =
                    Path.Combine(
                        userStateDirectory,
                        "DoomLauncher.WinUI.state.json");
                startInfo.EnvironmentVariables["DOOMLAUNCHER_DIAGNOSTIC_LOG"] =
                    Path.Combine(
                        userStateDirectory,
                        "DoomLauncher.WinUI.crash.log");

                Process.Start(startInfo);
                return 0;
            }
            catch (Exception exception)
            {
                return Fail(
                    "Doom Launcher 667 could not be started.\n\n" +
                    exception.Message);
            }
        }

        private static string JoinArguments(string[] arguments)
        {
            StringBuilder result = new StringBuilder();
            foreach (string argument in arguments)
            {
                if (result.Length > 0)
                {
                    result.Append(' ');
                }
                result.Append(QuoteArgument(argument));
            }
            return result.ToString();
        }

        private static string QuoteArgument(string argument)
        {
            StringBuilder result = new StringBuilder();
            result.Append('"');
            int backslashes = 0;
            foreach (char character in argument)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (character == '"')
                {
                    result.Append('\\', backslashes * 2 + 1);
                    result.Append('"');
                    backslashes = 0;
                    continue;
                }
                result.Append('\\', backslashes);
                backslashes = 0;
                result.Append(character);
            }
            result.Append('\\', backslashes * 2);
            result.Append('"');
            return result.ToString();
        }

        private static int Fail(string message)
        {
            MessageBoxW(IntPtr.Zero, message, ProductName, 0x00000010);
            return 1;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(
            IntPtr window,
            string text,
            string caption,
            uint type);
    }
}
