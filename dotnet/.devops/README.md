# Azure DevOps build pipelines

This folder holds the Azure DevOps YAML pipelines that build the
`OpenVSCodeServer.Kestrel` library for the platforms we ship.

| Pipeline                          | Hosted image      | Distribution built              | Output artifacts                                                       |
|-----------------------------------|-------------------|---------------------------------|------------------------------------------------------------------------|
| `azure-pipelines-windows.yml`     | `windows-latest`  | `vscode-reh-web-win32-x64`      | `vscode-reh-web-win32-x64.tar.gz`, `OpenVSCodeServer.Kestrel.nupkg`    |
| `azure-pipelines-macos.yml`       | `macos-latest`    | `vscode-reh-web-darwin-arm64`   | `vscode-reh-web-darwin-arm64.tar.gz`, `OpenVSCodeServer.Kestrel.nupkg` |
| `azure-pipelines-linux.yml`       | `ubuntu-latest`   | `vscode-reh-web-linux-<arch>`   | `vscode-reh-web-linux-<arch>.tar.gz`, `OpenVSCodeServer.Kestrel.nupkg` |
| `build-nuget.yml`                 | `ubuntu-latest`   | All Linux archs (downloaded)    | Multi-arch `OpenVSCodeServer.Kestrel.nupkg`, pushed to nuget.org       |

The three per-platform pipelines (`azure-pipelines-*.yml`) each build the
openvscode-server distribution from source on a matching hosted agent and
produce a single-platform NuGet package as an artifact. They are intentionally
**independent** — they do not share stages, parameters, or templates. That
keeps each one easy to read and easy to enable / disable individually.

`build-nuget.yml` is a separate release-style pipeline that instead downloads
the official upstream Linux archives (x64 / arm64 / armhf) via
`scripts/download-vscode-release.sh`, packs them all into one NuGet, and
pushes the resulting `.nupkg` to nuget.org.

## How the per-platform pipelines work

1. Check out the repository (shallow clone).
2. Install the Node.js version pinned in [`.nvmrc`](../../.nvmrc) via
   `NodeTool@0` (`versionSource: fromFile`).
3. Install the .NET SDK (`10.0.x`) via `UseDotNet@2`.
4. Run the platform-specific build script:
   - Windows: `pwsh scripts/build-vscode-release.ps1 -Platform win32 -Arch x64`
   - macOS:   `bash scripts/build-vscode-release.sh darwin arm64`
   - Linux:   `bash scripts/build-vscode-release.sh linux <arch>`

   The scripts perform the same three steps:
   - `npm install` (with `ELECTRON_SKIP_BINARY_DOWNLOAD=1` and
     `PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1` to skip downloads not needed for a
     `reh-web` build),
   - `npm run gulp -- vscode-reh-web-<platform>-<arch>-min`,
   - `tar -czf dotnet/OpenVSCodeServer.Kestrel/EmbeddedAssets/vscode-reh-web-<platform>-<arch>.tar.gz <distroDir>`.
5. `dotnet restore` / `dotnet build` / `dotnet pack` against
   `dotnet/OpenVSCodeServer.slnx` so the resulting `.nupkg` embeds the
   freshly-built `.tar.gz`.
6. Publish both the archive and the NuGet package as pipeline artifacts.

## How to wire them up

In Azure DevOps:

1. **Pipelines → New pipeline → Azure Repos Git** (or GitHub, depending on
   where this repo lives).
2. **Existing Azure Pipelines YAML file** and point at one of the YAML
   files in this folder.
3. Repeat for the other pipelines you want enabled.

The per-platform pipelines (`azure-pipelines-windows.yml`,
`azure-pipelines-macos.yml`, `azure-pipelines-linux.yml`) declare
`trigger: none` and `pr: none` — they are explicitly run on demand, scheduled,
or invoked via the Azure DevOps REST API. Add a `trigger:` / `schedules:`
block if you want automatic runs. `build-nuget.yml` is wired to run on push
to `main` / `master` and on `v*` tags, and pushes to nuget.org via the
`nuget-curiosity-org` service connection.

## Notes / things you might want to change

- `buildConfiguration` is exposed as a runtime parameter (default `Release`)
  on the per-platform pipelines.
- `azure-pipelines-linux.yml` accepts a `vscodeArch` parameter (`x64` or
  `arm64`). The hosted `ubuntu-latest` image is x64, so building `arm64`
  there relies on cross-compilation via the upstream gulp tasks.
- The Linux pipeline `apt-get install`s the native build dependencies
  (`build-essential`, `libx11-dev`, `libxkbfile-dev`, `libsecret-1-dev`,
  `libkrb5-dev`, `python3-setuptools`, etc.) that node-gyp needs for the
  native modules the reh-web build compiles.
- The macOS pipeline sets `GYP_DEFINES=kerberos_use_rtld=false`, mirroring
  what upstream uses on hosted macOS agents to avoid runtime kerberos
  loading issues.
- `versioningScheme: off` on `dotnet pack` (per-platform pipelines) means
  the version is read from the `.csproj` / `Directory.Build.props`. Switch
  to `byEnvVar` or `byBuildNumber` if you want pipeline-controlled
  versioning (as `build-nuget.yml` does).
- The per-platform pipelines do not push the resulting `.nupkg` anywhere.
  Wire up a `NuGetCommand@2 push` (or `dotnet nuget push`) step to publish
  to a feed, or use `build-nuget.yml` for the release flow.
- Artifacts: per-platform pipelines publish via `PublishPipelineArtifact@1`
  so any downstream stage / pipeline can pick them up.
