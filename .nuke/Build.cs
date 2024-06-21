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
using NuGet.Versioning;
using Nuke.Common;
using Nuke.Common.CI;
using Nuke.Common.CI.GitHubActions;
using Nuke.Common.Execution;
using Nuke.Common.Git;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.Git;
using Nuke.Common.Tools.GitHub;
using Nuke.Common.Tools.MinVer;
using Nuke.Common.Utilities;
using Nuke.Common.Utilities.Collections;
using Octokit;
using Octokit.Internal;
using Serilog;
using Utils;
using static Nuke.Common.EnvironmentInfo;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.IO.PathConstruction;
using Repository = NuGet.Protocol.Core.Types.Repository;

class Build : NukeBuild
{
    [Nuke.Common.Parameter(Name = "dry-run")] public bool IsDryRun { get; set; }

    [Nuke.Common.Parameter(Name = "nuget-feed-url")]
    public string NuGetFeedUrl { get; set; }
        = "https://api.nuget.org/v3/index.json";

    [Secret] [Nuke.Common.Parameter(Name = "nuget-api-key")] public string? NugetApiKey { get; set; }

    [Nuke.Common.Parameter(Name = "tag")] public string? Tag { get; set; }

    [Nuke.Common.Parameter(Name = "build-command")] public string? BuildCommand { get; set; }

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

    Target ResolveVersionAndBuild => _ => _
        .Executes(async () =>
        {
            Log.Information("Resolving is current commit has tag");
            var tagFound = false;

            var commitHash = GitTasks.GitCurrentCommit();
            GitTasks.Git($"describe --exact-match --tags {commitHash}",
                exitHandler: process => tagFound = process.ExitCode == 0);

            string version;
            string releaseNotes;
            if (tagFound)
            {
                var tag = GitTasks.Git($"describe --tags {commitHash}")
                    .First().Text;
                version = tag.TrimStart('v');

                var gitRepository = GitRepository.FromLocalDirectory(RootDirectory);

                var (owner, name) = (gitRepository.GetGitHubOwner(), gitRepository.GetGitHubName());
                var credentials = new Credentials(GitHubActions.Instance.Token);
                GitHubTasks.GitHubClient = new GitHubClient(
                    new ProductHeaderValue(nameof(NukeBuild)),
                    new InMemoryCredentialStore(credentials));

                var generatedReleaseNotes = await GitHubTasks.GitHubClient.Repository.Release
                    .GenerateReleaseNotes(owner, name, new GenerateReleaseNotesRequest(tag));

                releaseNotes = generatedReleaseNotes.Body;
            }
            else
            {
                var (minver, _) = MinVerTasks.MinVer(s => s
                    .SetTagPrefix("v")
                    .SetDefaultPreReleasePhase("nightly")
                    .DisableProcessLogOutput());
                version = minver.Version;
                var lastCommitMessage = GitTasks.Git("log -1 --pretty=%B")
                    .Select(output => output.Text)
                    .JoinNewLine();

                var commitUrl = $"{GitHubActions.Instance.ServerUrl}/" +
                                $"{GitHubActions.Instance.Repository}/" +
                                $"commit/" +
                                $"{commitHash}";

                releaseNotes = $"This version based on commit {commitUrl}\n\n{lastCommitMessage}";
            }

            if (BuildCommand.IsNullOrWhiteSpace())
            {
                BuildCommand = "pack";
            }

            var buildProcess = ProcessTasks.StartProcess("dotnet",
                BuildCommand +
                $" /p:Version={version.DoubleQuoteIfNeeded().ReplaceCommas()}" +
                $" /p:PackageReleaseNotes={releaseNotes.DoubleQuoteIfNeeded().ReplaceCommas()}", 
                logger: DotNetTasks.DotNetLogger);
            buildProcess.AssertZeroExitCode();
        });

    Target HideOutdatedNightlyPackages => _ => _
        .Executes(async () =>
        {
            ArgumentNullException.ThrowIfNull(NugetApiKey);

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

    Target CreateRelease => _ => _
        .Executes(async () =>
        {
            ArgumentNullException.ThrowIfNull(Tag);

            var gitRepository = GitRepository.FromLocalDirectory(RootDirectory);

            var (owner, name) = (gitRepository.GetGitHubOwner(), gitRepository.GetGitHubName());
            var credentials = new Credentials(GitHubActions.Instance.Token);
            GitHubTasks.GitHubClient = new GitHubClient(
                new ProductHeaderValue(nameof(NukeBuild)),
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
            .FirstOrDefault(entry => !entry.FullName.Contains('/') && entry.FullName.EndsWith(".nuspec"))
            ?.Name.TrimEnd(".nuspec");
    }

    public async Task HideOutdatedPackages(SourceRepository sourceRepository, IReadOnlyCollection<string> oldVersions,
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
            .Where(metadata => metadata.Identity.HasVersion)
            .Where(metadata => oldVersions.Any(oldVersion => metadata.Identity.Version.ToString().Contains(oldVersion)))
            .Where(metadata => metadata.Identity.Version.IsNightly());
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
}