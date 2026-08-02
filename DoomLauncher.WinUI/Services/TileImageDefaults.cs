using System.Security.Cryptography;

namespace DoomLauncher.WinUI.Services;

internal static class TileImageDefaults
{
    private static readonly string[] Styles = ["colored", "grayscale"];

    // Exact hashes of the obsolete 320x200 defaults shipped through 0.8.7.
    // Only these known files may be upgraded or removed automatically; user
    // replacements with different content are always preserved.
    private static readonly HashSet<string> LegacyDefaultHashes = new(
        [
            "154930037C87808847826FD852960F0B80357A7A7322BB0CEE2052E32FC4DF17",
            "1943C14316B26C3A2BED28275A8F216B9FB1AECC94117F4ED7B620F4BF95812F",
            "280C3328DAE981CE52117275C758A277D39CEA7CD6D3EC66B217FFB0656A85BC",
            "351245422E264BD47107CFBE7A809A15B470B3A06D7755A3029551D40083951A",
            "39F5FB7EB53C462AB67A64D3F965F8EA5E109936CC44AB4D57C2785290B0ACE0",
            "3C2DA1EF96430590814174F19F9A63BD3D50C670456A7F2488D3D413511D7F36",
            "3EB9E070A3A0C9992697F74953CBCD9982889D8E090E657DFCC6EEFC2DA5A50D",
            "791B625046B79C23418EB1775F90D6F7E5B1CDE7119E61FFC0964718A39AD2E9",
            "7F4E6D15B12164AF01B088AA1752827C7111CDF2B1C7A329A6CB4DC40821EF38",
            "807D6586E09E8691681468253DD7B7ACA2AECCDB279602685B2D272EECC986B7",
            "81C85B4BD90A663EAFFDE1F0C1AE9C86458EBB84878273EDDE3E91ECC4118487",
            "946B2AF79095691404C59827D8776F471118B00ADABBF62E22E7AECBADD2FBB5",
            "99576A6662B0F776F34D8901002DBEE1A92C5ACC68B873F28BD9B6B3905C7930",
            "9C5B3471580FE8E4431B957EF7BE413C192111CF775FC7FAFB5BF6DCEE351510",
            "B7F7BE33B286281B5D68549D650FCA88F80CAA9E9A0DF27560D28EB0CF5B9822",
            "B98B6EECEF51E0D1980EFB936BDCDA40A6109C332E5FDEA3A43BD63F1CBF6D65",
            "CB702059AFAC6A722910160EBED39CE16C689CA5A10C22259EAAE5316930FE17",
            "D5545AE32E15E1C9867A2EAFB9EDA6D0A824C085A21297B5BCFE62970F3F33CC",
            "D7A90DF9580A0884C92E9AF3CE464AE2C3447296EB5DF48611D93EAA2FCA8F1D",
            "DE548710DC00F3CF427760EB6FF735789E2D50C3CB168D83B129C08F73DD89BD",
            "E06845A4B1E7A08B2A7EB2F54E43BF152B677DCC88BD85966AF558C6D32E3F72",
            "EA3D378B312641DD3CFF28803A83B69333278DC1C7ABD0E99C20681A31E30BA2",
            "F2B033D8156387BD8F1184B2D0FEBB55CE1AE9521A4C42CE657771CA47DF24BE",
        ],
        StringComparer.OrdinalIgnoreCase);

    public static void EnsurePortableCopies()
    {
        var sourceRoot = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Library");
        var destinationRoot = Path.Combine(
            GetPortableRoot(),
            "Data",
            "TileImages");
        EnsurePortableCopies(sourceRoot, destinationRoot);
    }

    internal static void EnsurePortableCopies(
        string sourceRoot,
        string destinationRoot)
    {
        if (!Directory.Exists(sourceRoot))
            return;

        Directory.CreateDirectory(destinationRoot);
        RemoveObsoleteRootDefaults(destinationRoot);

        foreach (var style in Styles)
        {
            var sourceDirectory = Path.Combine(sourceRoot, style);
            if (!Directory.Exists(sourceDirectory))
                continue;

            var destinationDirectory = Path.Combine(destinationRoot, style);
            Directory.CreateDirectory(destinationDirectory);
            foreach (var sourceFile in Directory.EnumerateFiles(
                         sourceDirectory,
                         "*.png",
                         SearchOption.TopDirectoryOnly))
            {
                var target = Path.Combine(
                    destinationDirectory,
                    Path.GetFileName(sourceFile));
                if (!File.Exists(target) || IsLegacyDefault(target))
                    File.Copy(sourceFile, target, overwrite: true);
            }
        }
    }

    internal static bool IsLegacyDefaultHash(string hash) =>
        LegacyDefaultHashes.Contains(hash);

    private static void RemoveObsoleteRootDefaults(string destinationRoot)
    {
        foreach (var file in Directory.EnumerateFiles(
                     destinationRoot,
                     "*.png",
                     SearchOption.TopDirectoryOnly))
        {
            if (IsLegacyDefault(file))
                File.Delete(file);
        }
    }

    private static bool IsLegacyDefault(string path)
    {
        using var stream = File.OpenRead(path);
        return IsLegacyDefaultHash(Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static string GetPortableRoot()
    {
        var database = Environment.GetEnvironmentVariable(
            DoomLauncherDatabaseLocator.DatabaseEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(database))
        {
            var databasePath = Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(
                    database.Trim().Trim('"')));
            return Path.GetDirectoryName(databasePath)!;
        }

        var applicationDirectory = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        return Path.GetFileName(applicationDirectory).Equals(
            "WinUI",
            StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(applicationDirectory)!
            : applicationDirectory;
    }
}
