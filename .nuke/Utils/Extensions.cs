using System.Collections.Immutable;
using System.Linq;
using NuGet.Versioning;

namespace Utils;

public static class Extensions
{
    public static bool IsNightly(this NuGetVersion version) {
        if (version.ToString().Contains("nightly")) {
            return true;
        }

        // 3.2.5-nightly.0.1
        // If x.y on the end - this is nightly
        var lastLabels = version.ReleaseLabels.TakeLast(2).ToImmutableArray();
        return lastLabels.Length == 2 && lastLabels.All(s => int.TryParse(s, out _));
    }

    public static string ReplaceMsBuildCharacters(this string s) =>
        s.Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A")
            .Replace("$", "%24").Replace("@", "%40").Replace("'", "%27")
            .Replace("(", "%28").Replace(")", "%29").Replace(";", "%3b")
            .Replace(",", "%2c").Replace("\"", "%22");
}
