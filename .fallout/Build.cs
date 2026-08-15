using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using Fallout.Common;
using Fallout.Common.CI.GitHubActions;
using Fallout.Common.Git;
using Fallout.Common.IO;
using Fallout.Common.Tooling;
using Fallout.Common.Tools.Git;
using Fallout.Common.Tools.GitHub;
using Fallout.Common.Tools.MinVer;
using Fallout.Common.Utilities;
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

    [Fallout.Common.Parameter(Name = "test-command")] public string? TestCommand { get; set; }

    [Fallout.Common.Parameter(Name = "only-build")] public bool OnlyBuild { get; set; }

    [Fallout.Common.Parameter(Name = "publish-nightly")] public bool PublishNightly { get; set; }

    public static int Main() => Execute<Build>(x => x.Info);

    void LoadEnvironmentInputs()
    {
        BuildCommand ??= Environment.GetEnvironmentVariable("BUILD_COMMAND");
        TestCommand ??= Environment.GetEnvironmentVariable("TEST_COMMAND");
        NugetApiKey ??= Environment.GetEnvironmentVariable("NUGET_API_KEY");
        OnlyBuild |= string.Equals(Environment.GetEnvironmentVariable("ONLY_BUILD"), "true",
            StringComparison.OrdinalIgnoreCase);
        PublishNightly |= string.Equals(Environment.GetEnvironmentVariable("PUBLISH_NIGHTLY"), "true",
            StringComparison.OrdinalIgnoreCase);
    }

    Target Info => _ => _
        .Executes(() =>
        {
            Log.Information("This is cli tool for assisting in pipelines");
        });

    PackVersion? PackVersion;

    enum PublicationMode
    {
        BuildOnly,
        PublishNightly,
        PublishRelease
    }

    sealed record PublicationPolicy(PublicationMode Mode, string Reason);

    PublicationPolicy ResolvePublicationPolicy()
    {
        var onlyBuild = OnlyBuild ||
                        string.Equals(Environment.GetEnvironmentVariable("ONLY_BUILD"), "true",
                            StringComparison.OrdinalIgnoreCase);
        var publishNightly = PublishNightly ||
                             string.Equals(Environment.GetEnvironmentVariable("PUBLISH_NIGHTLY"), "true",
                                 StringComparison.OrdinalIgnoreCase);

        if (onlyBuild)
            return new(PublicationMode.BuildOnly, "only-build was enabled");

        var eventName = Environment.GetEnvironmentVariable("GITHUB_EVENT_NAME");
        var refType = Environment.GetEnvironmentVariable("GITHUB_REF_TYPE");
        var refName = Environment.GetEnvironmentVariable("GITHUB_REF_NAME");
        var eventPath = Environment.GetEnvironmentVariable("GITHUB_EVENT_PATH");
        var isFork = false;
        var defaultBranch = string.Empty;
        if (!string.IsNullOrWhiteSpace(eventPath) && File.Exists(eventPath))
        {
            using var eventDocument = JsonDocument.Parse(File.ReadAllText(eventPath));
            if (eventDocument.RootElement.TryGetProperty("repository", out var repository))
            {
                isFork = repository.TryGetProperty("fork", out var fork) && fork.GetBoolean();
                defaultBranch = repository.TryGetProperty("default_branch", out var branch)
                    ? branch.GetString() ?? string.Empty
                    : string.Empty;
            }
        }

        if (isFork)
            return new(PublicationMode.BuildOnly, "the repository is a fork");
        if (!string.Equals(eventName, "push", StringComparison.OrdinalIgnoreCase))
            return new(PublicationMode.BuildOnly, "publishing is allowed only for push events");
        if (string.Equals(refType, "tag", StringComparison.OrdinalIgnoreCase))
            return new(PublicationMode.PublishRelease, "the push is a tag");
        if (publishNightly && string.Equals(refType, "branch", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(refName, defaultBranch, StringComparison.OrdinalIgnoreCase))
            return new(PublicationMode.PublishNightly, "the push is to the default branch");

        return new(PublicationMode.BuildOnly, "the push is not to the default branch");
    }

    void RunCommand(string executable, string arguments)
    {
        using var process = ProcessTasks.StartProcess(executable, arguments, RootDirectory,
            logger: (_, message) => Log.Information(message));
        process.AssertZeroExitCode();
    }

    void RunCommandLine(string command)
    {
        var separator = command.IndexOf(' ');
        var executable = separator < 0 ? command : command[..separator];
        var arguments = separator < 0 ? string.Empty : command[(separator + 1)..].Trim();
        RunCommand(executable, arguments);
    }

    ImmutableArray<string> FindPackages(string version, string extension) =>
        RootDirectory.GlobFiles($"**/*.{version}.{extension}")
            .Select(path => path.ToString())
            .ToImmutableArray();

    void PublishPackages(IEnumerable<string> packages)
    {
        ArgumentNullException.ThrowIfNull(NugetApiKey);
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GITHUB_ACTIONS")))
            Console.WriteLine($"::add-mask::{NugetApiKey}");
        foreach (var package in packages)
        {
            Log.Information("Publishing {Package}", package);
            RunCommand("dotnet",
                $"nuget push \"{package}\" --api-key \"{NugetApiKey}\" --source \"{NuGetFeedUrl}\" --skip-duplicate");
        }
    }

    void WriteSummary(PublicationPolicy policy, IEnumerable<string> packages, IEnumerable<string> symbols)
    {
        var summary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (string.IsNullOrWhiteSpace(summary))
            return;
        File.AppendAllText(summary,
            $"## CommonWorkflows\n\n- Mode: `{policy.Mode}`\n- Reason: {policy.Reason}\n- Version: `{PackVersion?.Version ?? "unknown"}`\n- Packages: {string.Join(", ", packages.Select(Path.GetFileName))}\n- Symbols: {string.Join(", ", symbols.Select(Path.GetFileName))}\n");
    }

    Target Pipeline => _ => _
        .DependsOn(FetchHistory, PublishPackagesTarget, HideOutdatedNightlyPackages, CreateRelease)
        .Executes(() =>
        {
            LoadEnvironmentInputs();
            var policy = ResolvePublicationPolicy();
            Log.Information("Pipeline mode: {Mode}. Reason: {Reason}", policy.Mode, policy.Reason);
            WriteSummary(policy, FindPackages(PackVersion!.Version, "nupkg"),
                FindPackages(PackVersion.Version, "snupkg"));
        });

    Target PublishPackagesTarget => _ => _
        .DependsOn(Pack)
        .OnlyWhenDynamic(() => ResolvePublicationPolicy().Mode != PublicationMode.BuildOnly)
        .Executes(() =>
        {
            NugetApiKey ??= Environment.GetEnvironmentVariable("NUGET_API_KEY");
            if (string.IsNullOrWhiteSpace(NugetApiKey))
                throw new InvalidOperationException(
                    "NuGet publishing is enabled, but no OIDC API key is available. Check nuget-user and id-token: write permission.");
            var packages = FindPackages(PackVersion!.Version, "nupkg");
            var symbols = FindPackages(PackVersion.Version, "snupkg");
            if (packages.Length == 0)
                throw new InvalidOperationException(
                    $"No NuGet packages for version {PackVersion.Version} were produced");

            PublishPackages(packages);
            if (symbols.Length > 0)
                PublishPackages(symbols);
            else
                Log.Information("No symbol packages for version {Version}; skipping symbols publish",
                    PackVersion.Version);
        });

    Target FetchHistory => _ => _
        .Executes(() =>
        {
            RunCommand("git", "config remote.origin.fetch \"+refs/heads/*:refs/remotes/origin/*\"");
            using var shallowCheck = ProcessTasks.StartProcess(GitTasks.GitPath,
                "rev-parse --is-shallow-repository", RootDirectory,
                logger: (_, _) =>
                {
                });
            shallowCheck.AssertWaitForExit();
            var unshallow = shallowCheck.Output.FirstOrDefault().Text.Trim()
                .Equals("true", StringComparison.OrdinalIgnoreCase)
                ? "--unshallow "
                : string.Empty;
            RunCommand("git", $"fetch --no-recurse-submodules {unshallow}--tags --prune --filter=tree:0 origin");
        });

    Target Restore => _ => _
        .Executes(() =>
        {
            RunCommand("dotnet", "workload restore");
            RunCommand("dotnet", "restore");
        });

    Target DotNetBuildTarget => _ => _
        .DependsOn(Restore, ResolveVersion)
        .Executes(() =>
        {
            LoadEnvironmentInputs();
            BuildCommand ??= Environment.GetEnvironmentVariable("BUILD_COMMAND");
            if (!string.IsNullOrWhiteSpace(BuildCommand))
                return;

            RunCommand("dotnet", $"build --no-restore --configuration Release /p:Version={PackVersion!.Version}");
        });

    Target Test => _ => _
        .DependsOn(DotNetBuildTarget)
        .Executes(() =>
        {
            LoadEnvironmentInputs();
            TestCommand ??= Environment.GetEnvironmentVariable("TEST_COMMAND");
            var command = string.IsNullOrWhiteSpace(TestCommand)
                ? string.IsNullOrWhiteSpace(BuildCommand)
                    ? "dotnet test --no-build --no-restore --configuration Release"
                    : "dotnet test --no-restore --configuration Release"
                : TestCommand!;
            RunCommandLine(command);
        });

    Target ResolveVersion => _ => _
        .DependsOn(FetchHistory)
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
            var githubRefType = Environment.GetEnvironmentVariable("GITHUB_REF_TYPE");
            var tagFound = string.IsNullOrWhiteSpace(githubRefType)
                ? gitFindIsCurrentCommitHasTag.ExitCode == 0
                : string.Equals(githubRefType, "tag", StringComparison.OrdinalIgnoreCase);

            if (tagFound)
            {
                Log.Information("Current commit has tag. Resolving version via git tag");
                var tag = Environment.GetEnvironmentVariable("GITHUB_REF_NAME") ??
                          GitTasks.Git($"describe --tags {commitHash}").First().Text;
                Tag = tag.Trim();
                var version = Tag.TrimStart('v');

                if (ResolvePublicationPolicy().Mode != PublicationMode.PublishRelease)
                {
                    PackVersion = new PackVersion(version, CreateCommitReleaseNotes(commitHash));
                    Log.Information("Release notes API call skipped because this is not a publishable release run");
                    return;
                }

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
                PackVersion = new PackVersion(version, CreateCommitReleaseNotes(commitHash));
            }

            Log.Information("Resolved version information is {Info}", PackVersion);
        });

    string CreateCommitReleaseNotes(string commitHash)
    {
        var serverUrl = Environment.GetEnvironmentVariable("GITHUB_SERVER_URL");
        var repository = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY");
        var commitUrl = string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(repository)
            ? commitHash
            : $"{serverUrl}/{repository}/commit/{commitHash}";
        var lastCommitMessage = GitTasks.Git("log -1 --pretty=%B")
            .Select(output => output.Text)
            .JoinNewLine();
        return $"This version based on commit {commitUrl}\n\n{lastCommitMessage}";
    }

    Target Compile => _ => _
        .DependsOn(Test)
        .Executes(() =>
        {
            if (string.IsNullOrWhiteSpace(BuildCommand))
            {
                RunCommand("dotnet",
                    $"pack --no-build --configuration Release /p:Version={PackVersion!.Version} /p:PackageReleaseNotes=\"{PackVersion.ReleaseNotes.ReplaceMsBuildCharacters()}\"");
                return;
            }

            Debug.Assert(PackVersion is not null);
            var buildCommand = BuildCommand!.Trim();

            buildCommand = buildCommand.Trim();

            var hasSubstitutions = buildCommand.Contains("{VERSION}")
                                   || buildCommand.Contains("{RELEASENOTES}");

            if (hasSubstitutions)
            {
                Log.Information("Replacing VERSION and RELEASENOTES in build command: {Command}", buildCommand);
                buildCommand = buildCommand
                    .Replace("{VERSION}", PackVersion.Version.ReplaceMsBuildCharacters().DoubleQuoteIfNeeded())
                    .Replace("{RELEASENOTES}",
                        PackVersion.ReleaseNotes.ReplaceMsBuildCharacters().DoubleQuoteIfNeeded());
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

            RunCommandLine(buildCommand);
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
        .DependsOn(PublishPackagesTarget)
        .OnlyWhenDynamic(() => ResolvePublicationPolicy().Mode == PublicationMode.PublishRelease)
        .Executes(async () =>
        {
            LoadEnvironmentInputs();
            ArgumentNullException.ThrowIfNull(NugetApiKey);

            Log.Information("Fetching all tags reachable from current commit");
            var readOnlyCollection = GitTasks.Git("tag --merged HEAD", workingDirectory: RootDirectory);
            var currentVersion = PackVersion?.Version ?? Tag?.TrimStart('v');
            var oldVersionsStrings = readOnlyCollection
                .Select(output => output.Text.TrimStart('v'))
                .Where(version => !string.Equals(version, currentVersion, StringComparison.OrdinalIgnoreCase));
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
        .DependsOn(HideOutdatedNightlyPackages)
        .OnlyWhenDynamic(() => ResolvePublicationPolicy().Mode == PublicationMode.PublishRelease)
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
            catch (NotFoundException)
            {
                // The release does not exist yet.
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
