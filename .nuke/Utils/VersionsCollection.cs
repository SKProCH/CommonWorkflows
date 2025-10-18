using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using NuGet.Versioning;

namespace Utils;

public class VersionsCollection : IReadOnlyList<VersionsCollection.VersionInfo>
{
    IReadOnlyList<VersionInfo> ReadOnlyListImplementation;

    public VersionsCollection(IEnumerable<string> versions)
    {
        ReadOnlyListImplementation =
        [
            ..versions.Select(s =>
                NuGetVersion.TryParse(s, out var version)
                    ? new VersionInfo(s, version)
                    : new VersionInfo(s, null))
        ];
    }

    public bool IsNightlyVersionSuperseded(NuGetVersion target)
    {
        var targetVersionString = target.ToString();
        if (this.Any(info => targetVersionString.Contains(info.VersionString)))
        {
            return true;
        }

        // Since we always bump a patch version for a nightlies
        // We need to check if it is a "detached" version
        // Like we have 1.1.0, 1.1.1-nightly-blabla, and (1.2.0 or 2.0.0)
        // In this case 1.1.1-nightly-blabla should be deleted
        var hasPreviousVersion = ReadOnlyListImplementation
            .Where(info => info.Version is not null)
            .Any(info => info.Version!.Major == target.Major 
                         && info.Version.Minor == target.Minor 
                         && info.Version.Patch == target.Patch - 1);
        
        var hasNextVersion = ReadOnlyListImplementation
            .Where(info => info.Version is not null)
            .Any(info => info.Version!.Major == target.Major + 1 ||
                         (info.Version.Major == target.Major && info.Version.Minor == target.Minor + 1));

        return hasPreviousVersion && hasNextVersion;
    }

    public IEnumerator<VersionInfo> GetEnumerator() => ReadOnlyListImplementation.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)ReadOnlyListImplementation).GetEnumerator();

    public int Count => ReadOnlyListImplementation.Count;

    public VersionInfo this[int index] => ReadOnlyListImplementation[index];

    public record VersionInfo(string VersionString, NuGetVersion? Version);
}