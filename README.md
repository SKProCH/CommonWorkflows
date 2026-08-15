# CommonWorkflows

Some github actions I use for my .NET libraries/projects.

**Warning:** This is very opinionated and probably only works for my specific project structure (uses Fallout, MinVer, etc). Don't expect it to be a general-purpose tool.

## What it does

It basically automates the whole release flow for a NuGet package:
1. Calculates version using `minver` (based on git tags).
2. Restores dependencies, builds, runs tests against the build, then packs.
3. Publishes to NuGet using Trusted Publishing (OIDC) - so no more leaking API keys.
4. If it's a tag, it creates a GitHub release/forms changelog from PRs.
5. If it's a nightly build, it can push to NuGet too and even hide older nightly versions so your package page doesn't look like a mess.

Solved my headache of doing all this manually every time I push a tag. Removed the necessity of manually changing/tracking releases in some project files.

## Usage

Example of `build-publish` action in your `.github/workflows`:

```yaml
name: Build and publish

on:
  push:
    branches:
      - '**'
    paths-ignore:
      - Material.Avalonia.Demo*/** # Ignore demo projects, do not trigger builds for them
    tags:
      - v**
  pull_request:

permissions:
  id-token: write
  contents: write

jobs:
  build:

    runs-on: ubuntu-latest

    steps:
    - uses: actions/checkout@v4
    - name: Setup .NET
      uses: actions/setup-dotnet@v4
      with:
        dotnet-version: 10
    - name: Build and Publish
      uses: SKProCH/CommonWorkflows/actions/build-publish@v1
      with:
          publish-nightly: true
          nuget-user: ${{ secrets.NUGET_USER }}
```

### Trusted Publishing Setup (OIDC)

You need to set this up once on NuGet.org so you don't have to deal with secrets:
1. Go to your package on NuGet -> Settings -> Trusted Publishers.
2. Add a new. More info: https://aka.ms/nuget/trusted-publishing
3. Add repository secret: `NUGET_USER` matching your NuGet username.
4. **CRITICAL:** Make sure your workflow has `permissions: id-token: write` (see example above).

### Inputs

- `nuget-user`: Your NuGet username for Trusted Publishing. Required only when this run is allowed to publish.
- `publish-nightly`: Set `true` to allow nightly publishing from the default branch of the original repository. Feature branches, pull requests and forks always run build/test/pack only.
- `only-build`: Set `true` to skip pushing anything. Useful for PR checks.
- `build-command`: Optional override for the build/pack command. Without it, the C# pipeline runs restore, build, test and pack with `Release` configuration. You can use `{VERSION}` and `{RELEASENOTES}` as placeholders.
- `test-command`: Optional override for the test command. After the standard Release build it defaults to `dotnet test --no-build --no-restore --configuration Release`. With `build-command`, tests may build their projects before the custom pack command runs.
- `github-token`: Token for GitHub API. Defaults to `${{ github.token }}`.

### Workflow triggers

Use `push` with `branches: ['**']` and `pull_request` to validate every branch and pull request, including contributions in fork repositories. The action applies the publication policy itself, so the workflow does not need a branch allow-list for publishing.

You can still narrow `branches`, add `paths-ignore`, or omit `pull_request` when a repository does not need validation for those events. These filters control whether CI starts; they do not grant publishing permission.

### Publishing policy

Publishing is decided automatically by the action:

- Push to the default branch of the original repository: nightly publishing is allowed when `publish-nightly: true`.
- Push of a tag in the original repository: release publishing is allowed.
- Feature branches, `release/**` branches, pull requests and forks: build/test/pack only.
- `only-build: true`: always disables publishing.

The workflow therefore does not need to contain the repository name or an explicit branch allow-list. Forks can use the same workflow for validation without receiving publish credentials.

### Numerge support

If you have a `numerge.config.json` in your root, the action will automatically try to merge your packages using Numerge. Use it if you have multiple projects but want a single NuGet package.
