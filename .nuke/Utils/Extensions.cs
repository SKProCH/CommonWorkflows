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

    public static string ReplaceCommas(this string s) => s.Replace(",", "%2c");
}