# CommonWorkflows

Some github actions I use for my .NET libraries/projects.

**Warning:** This is very opinionated and probably only works for my specific project structure (uses NUKE, minver, etc). Don't expect it to be a general-purpose tool.

## What it does

It basically automates the whole release flow for a NuGet package:
1. Calculates version using `minver` (based on git tags).
2. Builds and packs.
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
      - master
      - main
      - release/**
    paths-ignore:
      - Material.Avalonia.Demo*/** # Ignore demo projects, do not trigger builds for them
    tags:
      - v**

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
      uses: SKProCH/CommonWorkflows/actions/build-publish@main
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

- `nuget-user` (**Required**): Your NuGet username for Trusted Publishing.
- `publish-nightly` (**Required**): Set `true` to push non-tagged builds to NuGet (uses `-nightly` suffix).
- `only-build`: Set `true` to skip pushing anything. Useful for PR checks.
- `build-command`: Override the default `dotnet pack`. You can use `{VERSION}` and `{RELEASENOTES}` as placeholders.
- `github-token`: Token for GitHub API. Defaults to `${{ github.token }}`.

### Numerge support

If you have a `numerge.config.json` in your root, the action will automatically try to merge your packages using Numerge. Use it if you have multiple projects but want a single NuGet package.
