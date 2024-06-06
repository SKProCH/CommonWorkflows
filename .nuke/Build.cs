#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using Nuke.Common;
using Nuke.Common.CI;
using Nuke.Common.Execution;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.Git;
using Nuke.Common.Utilities;
using Nuke.Common.Utilities.Collections;
using Serilog;
using Utils;
using static Nuke.Common.EnvironmentInfo;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.IO.PathConstruction;

class Build : NukeBuild
{
    [Parameter(Name = "dry-run")]
    public bool IsDryRun { get; set; }
    
    [Parameter(Name = "nuget-feed-url")]
    public string NuGetFeedUrl { get; set; } = "https://api.nuget.org/v3/index.json";
    
    [Secret]
    [Parameter(Name = "nuget-api-key")]
    public string? NuGetApiKey { get; set; }
    
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode
    public static int Main() => Execute<Build>(x => x.Info);

    Target Info => _ => _
        .Executes(() =>
        {
            Log.Information("This is cli tool for assisting in pipelines");
        });

    Target HideOutdatedNightlyPackages => _ => _
        .Executes(async () =>
        {
            Log.Information("Fetching all tags reachable from current commit");
            var readOnlyCollection = GitTasks.Git("tag --merged HEAD");
            var oldVersions = readOnlyCollection
                .Select(output => output.Text)
                .Skip(1)
                .ToImmutableArray();
            Log.Information("Fetched {Count} old tags", oldVersions.Length);

            Log.Information("Searching all nuget package files");
            var nupkgs = RootDirectory.GlobFiles("**/*.nupkg");
            Log.Information("Found {Count} files: \n{Files}", nupkgs.Count, string.Join("\n", nupkgs));

            var packageNames = nupkgs.Select(GetPackageNameFromNupkg)
                .Where(s => s is not null)
                .ToImmutableArray();
            Log.Information("Found {Count} packages: \n{Files}", packageNames.Length, string.Join("\n", packageNames));
            
            var nuget = Repository.Factory.GetCoreV3(NuGetFeedUrl);
            foreach (var packageName in packageNames)
            {
                await HideOutdatedPackages(nuget, oldVersions, packageName!);
            }
        });

    private static string? GetPackageNameFromNupkg(AbsolutePath path)
    {
        using var zipArchive = ZipFile.OpenRead(path);
        return zipArchive.Entries
            .FirstOrDefault(entry => !entry.FullName.Contains('/') && entry.FullName.EndsWith(".nuspec"))
            ?.Name.TrimEnd(".nuspec");
    }
    
    public async Task HideOutdatedPackages(SourceRepository sourceRepository, IReadOnlyCollection<string> oldVersions, string packageName) {
        Log.Information("Retrieving nightly packages version for {PackageName} to hide", packageName);
        var resource = await sourceRepository.GetResourceAsync<PackageMetadataResource>();
        var parametersNugetPackages = await resource.GetMetadataAsync(
            packageName,
            true,
            false,
            new SourceCacheContext(),
            NugetLogger.Instance,
            CancellationToken.None);

        var outdatedVersions = parametersNugetPackages
            .Where(metadata => metadata.Identity.HasVersion)
            .Where(metadata => oldVersions.Any(oldVersion => metadata.Identity.Version.ToString().Contains(oldVersion)))
            .Where(metadata => metadata.Identity.Version.IsNightly());
        foreach (var outdatedVersion in outdatedVersions) {
            Log.Information("Hiding previous nightly version {Version}", outdatedVersion.Identity.Version.ToString());
            if (IsDryRun)
                continue;
            
            var packageUpdateResource = await sourceRepository.GetResourceAsync<PackageUpdateResource>();
            await packageUpdateResource.Delete(packageName, outdatedVersion.Identity.Version.ToString(),
                _ => NuGetApiKey, _ => true, false, NugetLogger.Instance);
        }
                
        Log.Information("All previous nightly version for {PackageName} was hidden", packageName);
    }
}