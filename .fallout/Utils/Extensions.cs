using System;
using System.Linq;
using NuGet.Versioning;

namespace Utils;

public static class Extensions
{
    public static bool IsNightly(this NuGetVersion version) {
        return version.ReleaseLabels.FirstOrDefault()
            ?.Equals("nightly", StringComparison.OrdinalIgnoreCase) == true;
    }

    public static string ReplaceMsBuildCharacters(this string s) =>
        s.Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A")
            .Replace("$", "%24").Replace("@", "%40").Replace("'", "%27")
            .Replace("(", "%28").Replace(")", "%29").Replace(";", "%3b")
            .Replace(",", "%2c").Replace("\"", "%22");
}
