using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using Fallout.Common;
using Fallout.Common.CI.GitHubActions;
using Fallout.Common.Git;
using Fallout.Common.IO;
using Fallout.Common.Tooling;
using Fallout.Common.Tools.DotNet;
using Fallout.Common.Tools.Git;
using Fallout.Common.Tools.GitHub;
using Fallout.Common.Tools.MinVer;
using Fallout.Common.Utilities;
using Fallout.Common.Utilities.Collections;
using Numerge;
using Octokit;
using Octokit.Internal;
using Serilog;
using Utils;
using Repository = NuGet.Protocol.Core.Types.Repository;
// ReSharper disable AllUnderscoreLocalParameterName

class Build : FalloutBuild
{
    [Fallout.Common.Parameter(Name = "dry-run")] public bool IsDryRun { get; set; }

    [Fallout.Common.Parameter(Name = "nuget-feed-url")]
    public string NuGetFeedUrl { get; set; }
        = "https://api.nuget.org/v3/index.json";

    [Secret] [Fallout.Common.Parameter(Name = "nuget-api-key")] public string? NugetApiKey { get; set; }

    [Fallout.Common.Parameter(Name = "tag")] public string? Tag { get; set; }

    [Fallout.Common.Parameter(Name = "build-command")] public string? BuildCommand { get; set; }

    public static int Main() => Execute<Build>(x => x.Info);

    Target Info => _ => _
        .Executes(() =>
        {
            Log.Information("This is cli tool for assisting in pipelines");
        });

    PackVersion? PackVersion;

    Target ResolveVersion => _ => _
        .Executes(async () =>
        {
            Log.Information("Resolving is current commit has tag");

            var commitHash = GitTasks.GitCurrentCommit();
            using var gitFindIsCurrentCommitHasTag = ProcessTasks.StartProcess(GitTasks.GitPath,
                $"describe --exact-match --tags {commitHash}",
                workingDirectory: RootDirectory,
                logger: (_, _) =>
                {
                });
            gitFindIsCurrentCommitHasTag.AssertWaitForExit();
            var tagFound = gitFindIsCurrentCommitHasTag.ExitCode == 0;

            if (tagFound)
            {
                Log.Information("Current commit has tag. Resolving version via git tag");
                var tag = GitTasks.Git($"describe --tags {commitHash}")
                    .First().Text;
                var version = tag.TrimStart('v');

                var gitRepository = GitRepository.FromLocalDirectory(RootDirectory);

                var (owner, name) = (gitRepository.GetGitHubOwner(), gitRepository.GetGitHubName());
                var credentials = new Credentials(GitHubActions.Instance.Token);
                GitHubTasks.GitHubClient = new GitHubClient(
                    new ProductHeaderValue(nameof(FalloutBuild)),
                    new InMemoryCredentialStore(credentials));

                var generatedReleaseNotes = await GitHubTasks.GitHubClient.Repository.Release
                    .GenerateReleaseNotes(owner, name, new GenerateReleaseNotesRequest(tag));

                PackVersion = new PackVersion(version, generatedReleaseNotes.Body);
            }
            else
            {
                Log.Information("Current commit doesn't have a tag. Resolving version via minver");
                var minver = MinVerTasks.MinVer(s => s
                    .SetTagPrefix("v")
                    .SetDefaultPreReleaseIdentifiers("nightly")
                    .SetVerbosity(MinVerVerbosity.Error));
                var version = minver.Result.Version;
                var lastCommitMessage = GitTasks.Git("log -1 --pretty=%B")
                    .Select(output => output.Text)
                    .JoinNewLine();

                var commitUrl = $"{GitHubActions.Instance.ServerUrl}/" +
                                $"{GitHubActions.Instance.Repository}/" +
                                $"commit/" +
                                $"{commitHash}";

                var releaseNotes = $"This version based on commit {commitUrl}\n\n{lastCommitMessage}";
                PackVersion = new PackVersion(version, releaseNotes);
            }

            Log.Information("Resolved version information is {Info}", PackVersion);
        });

    Target Compile => _ => _
        .DependsOn(ResolveVersion)
        .Executes(() =>
        {
            Debug.Assert(PackVersion is not null);
            Log.Information("Start building project");
            var buildCommand = BuildCommand;

            if (buildCommand is null || buildCommand.IsEmpty())
            {
                buildCommand = "dotnet pack";
            }

            buildCommand = buildCommand.Trim();

            var hasSubstitutions = buildCommand.Contains("{VERSION}")
                                   || buildCommand.Contains("{RELEASENOTES}");

            if (hasSubstitutions)
            {
                Log.Information("Replacing VERSION and RELEASENOTES in build command: {Command}", buildCommand);
                buildCommand = buildCommand
                    .Replace("{VERSION}", PackVersion.Version.ReplaceMsBuildCharacters().DoubleQuoteIfNeeded())
                    .Replace("{RELEASENOTES}", PackVersion.ReleaseNotes.ReplaceMsBuildCharacters().DoubleQuoteIfNeeded());
            }
            else
            {
                if (buildCommand.StartsWith("dotnet"))
                {
                    Log.Information("Appending dotnet properties for version and release notes");
                    buildCommand +=
                        $" /p:Version={PackVersion.Version.ReplaceMsBuildCharacters().DoubleQuoteIfNeeded()}" +
                        $" /p:PackageReleaseNotes={PackVersion.ReleaseNotes.ReplaceMsBuildCharacters().DoubleQuoteIfNeeded()}";
                }
                else
                {
                    Log.Warning(
                        "Build command doesn't start with dotnet, but also doesn't contains any variables to replace");
                }
            }

            var firstSpaceIndex = buildCommand.IndexOf(' ');

            string executable;
            if (firstSpaceIndex == -1)
            {
                executable = buildCommand;
                buildCommand = null;
            }
            else
            {
                executable = buildCommand[..firstSpaceIndex];
                buildCommand = buildCommand[firstSpaceIndex..].Trim();
            }

            Log.Information("Executing {Command} with {Parameters}", executable, buildCommand);
            var buildProcess = ProcessTasks.StartProcess(executable, buildCommand, workingDirectory: RootDirectory,
                logger: (_, message) => Log.Information(message));
            buildProcess.AssertZeroExitCode();
        });

    Target Numerge => _ => _
        .DependsOn(Compile)
        .OnlyWhenDynamic(() => (RootDirectory / "numerge.config.json").FileExists())
        .Executes(() =>
        {
            Debug.Assert(PackVersion is not null);
            Log.Information("Starting Numerge'ing packages");
            var numergeConfigFile = RootDirectory / "numerge.config.json";
            var config = MergeConfiguration.LoadFile(numergeConfigFile);

            var tempPath = Path.GetTempPath() + Guid.NewGuid();
            Log.Information("Creating temporary directory: {TempPath}", tempPath);
            Directory.CreateDirectory(tempPath);

            try
            {
                Log.Information("Moving .nupkg files to temporary directory");
                MovePackagesToTempDirectory(RootDirectory, "nupkg", "Release", config, tempPath, PackVersion.Version,
                    true);

                Log.Information("Moving .snupkg files to temporary directory");
                MovePackagesToTempDirectory(RootDirectory, "snupkg", "Release", config, tempPath,
                    PackVersion.Version, true);

                var outputDirectory = RootDirectory / ".artifacts";
                Log.Information("Output directory: {OutputDirectory}", outputDirectory);

                Log.Information("Starting NuGet package merge process");
                var mergeResult = NugetPackageMerger.Merge(tempPath, outputDirectory, config, new NumergeLogger());

                Assert.True(mergeResult, "Nuget package merge process failed");
            }
            finally
            {
                Log.Information("Cleaning up temporary directory: {TempPath}", tempPath);
                Directory.Delete(tempPath, true);
            }
        });

    Target Pack => _ => _
        .DependsOn(ResolveVersion, Compile, Numerge);

    Target HideOutdatedNightlyPackages => _ => _
        .Executes(async () =>
        {
            ArgumentNullException.ThrowIfNull(NugetApiKey);

            Log.Information("Fetching all tags reachable from current commit");
            var readOnlyCollection = GitTasks.Git("tag --merged HEAD", workingDirectory: RootDirectory);
            var oldVersionsStrings = readOnlyCollection
                .Select(output => output.Text.TrimStart('v'))
                .Skip(1);
            var versionsCollection = new VersionsCollection(oldVersionsStrings);
            Log.Information("Fetched {Count} old tags", versionsCollection.Count);

            Log.Information("Searching all nuget package files");
            var nupkgs = RootDirectory.GlobFiles("**/*.nupkg");
            Log.Information("Found {Count} files: \n{Files}", nupkgs.Count, string.Join("\n", nupkgs));

            var packageNames = nupkgs.Select(GetPackageNameFromNupkg)
                .Where(s => s is not null)
                .Distinct()
                .ToImmutableArray();
            Log.Information("Found {Count} packages: \n{Files}", packageNames.Length, string.Join("\n", packageNames));

            var nuget = Repository.Factory.GetCoreV3(NuGetFeedUrl);
            foreach (var packageName in packageNames)
            {
                await HideOutdatedPackages(nuget, versionsCollection, packageName!);
            }
        });

    Target CreateRelease => _ => _
        .Executes(async () =>
        {
            ArgumentNullException.ThrowIfNull(Tag);

            var gitRepository = GitRepository.FromLocalDirectory(RootDirectory);

            var (owner, name) = (gitRepository.GetGitHubOwner(), gitRepository.GetGitHubName());
            var credentials = new Credentials(GitHubActions.Instance.Token);
            GitHubTasks.GitHubClient = new GitHubClient(
                new ProductHeaderValue(nameof(FalloutBuild)),
                new InMemoryCredentialStore(credentials));

            var releaseNotes = await GitHubTasks.GitHubClient.Repository.Release
                .GenerateReleaseNotes(owner, name, new GenerateReleaseNotesRequest(Tag));

            Release? oldRelease = null;
            try
            {
                oldRelease = await GitHubTasks.GitHubClient.Repository.Release.Get(owner, name, Tag);
            }
            catch (Exception)
            {
                // ignored
            }

            var nuGetVersion = NuGetVersion.Parse(Tag.Trim('v'));
            if (oldRelease is not null)
            {
                Log.Information("Editing release {TagName}", Tag);
                var releaseUpdate = new ReleaseUpdate
                    { Body = releaseNotes.Body, Name = Tag, Prerelease = nuGetVersion.IsPrerelease };
                await GitHubTasks.GitHubClient.Repository.Release.Edit(owner, name, oldRelease.Id, releaseUpdate);
            }
            else
            {
                Log.Information("Creating release {TagName}", Tag);
                var newRelease = new NewRelease(Tag)
                {
                    Name = Tag,
                    GenerateReleaseNotes = true,
                    Prerelease = nuGetVersion.IsPrerelease
                };
                await GitHubTasks.GitHubClient.Repository.Release.Create(owner, name, newRelease);
            }
        });

    private static string? GetPackageNameFromNupkg(AbsolutePath path)
    {
        using var zipArchive = ZipFile.OpenRead(path);
        return zipArchive.Entries
            .FirstOrDefault(entry => !entry.FullName.Contains('/') &&
                                     entry.Name.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
            ?.Name[..^".nuspec".Length];
    }

    private async Task HideOutdatedPackages(SourceRepository sourceRepository, VersionsCollection oldVersions,
        string packageName)
    {
        ArgumentNullException.ThrowIfNull(NugetApiKey);

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
            .Where(metadata => metadata.IsListed)
            .Where(metadata => metadata.Identity.HasVersion)
            .Where(metadata => metadata.Identity.Version.IsNightly())
            .Where(metadata => oldVersions.IsNightlyVersionSuperseded(metadata.Identity.Version));
        foreach (var outdatedVersion in outdatedVersions)
        {
            Log.Information("Hiding previous nightly version {Version}", outdatedVersion.Identity.Version.ToString());
            if (IsDryRun)
                continue;

            var packageUpdateResource = await sourceRepository.GetResourceAsync<PackageUpdateResource>();
            await packageUpdateResource.Delete(packageName, outdatedVersion.Identity.Version.ToString(),
                _ => NugetApiKey, _ => true, false, NugetLogger.Instance);
        }

        Log.Information("All previous nightly version for {PackageName} was hidden", packageName);
    }

    private void MovePackagesToTempDirectory(string solutionDirectory, string extension, string configuration,
        MergeConfiguration config, string destination, string version, bool move)
    {
        var targetFileNames = config.Packages.SelectMany(x => x.Merge)
            .Select(mergeConfiguration => mergeConfiguration.Id)
            .Concat(config.Packages.Select(x => x.Id))
            .Select(id => $"{id}.{version}.{extension}")
            .ToImmutableArray();

        var files = Directory.GetFiles(solutionDirectory, "*." + extension, SearchOption.AllDirectories)
            .Where(s => s.Contains(configuration))
            .Where(s => targetFileNames.Contains(Path.GetFileName(s)));

        foreach (var file in files)
        {
            if (move)
            {
                File.Move(file, Path.Combine(destination, Path.GetFileName(file)));
            }
            else
            {
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
            }
        }
    }
}
