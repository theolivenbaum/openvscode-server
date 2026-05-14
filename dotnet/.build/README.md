# Azure DevOps build pipelines

This folder holds the Azure DevOps YAML pipelines that build the
`OpenVSCodeServer.Kestrel` library for the platforms we ship.

| Pipeline                                | Hosted image      | Distribution built          | Output artifacts                                                  |
|-----------------------------------------|-------------------|-----------------------------|-------------------------------------------------------------------|
| `azure-pipelines-windows.yml`           | `windows-latest`  | `vscode-reh-web-win32-x64`  | `vscode-reh-web-win32-x64.tar.gz`, `OpenVSCodeServer.Kestrel.nupkg`  |
| `azure-pipelines-macos.yml`             | `macos-latest`    | `vscode-reh-web-darwin-arm64` | `vscode-reh-web-darwin-arm64.tar.gz`, `OpenVSCodeServer.Kestrel.nupkg` |

Each pipeline is self-contained and intentionally **independent** — they do
not share stages, parameters, or templates. That keeps each one easy to read
and easy to enable / disable individually.

## How they work

1. Check out the repository (shallow clone).
2. Install the Node.js version pinned in [`.nvmrc`](../../.nvmrc) via
   `NodeTool@0` (`versionSource: fromFile`).
3. Install the .NET SDK (`10.0.x`) via `UseDotNet@2`.
4. Run the platform-specific build script:
   - Windows: `pwsh scripts/build-vscode-release.ps1 -Platform win32 -Arch x64`
   - macOS:   `bash scripts/build-vscode-release.sh darwin arm64`

   Both scripts perform the same three steps:
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
2. **Existing Azure Pipelines YAML file** and point at one of the two YAML
   files in this folder.
3. Repeat for the second pipeline.

Both pipelines declare `trigger: none` and `pr: none` — they are explicitly
run on demand, scheduled, or invoked via the Azure DevOps REST API. Add a
`trigger:` / `schedules:` block if you want automatic runs.

## Notes / things you might want to change

- `buildConfiguration` is exposed as a runtime parameter (default `Release`).
- The macOS pipeline sets `GYP_DEFINES=kerberos_use_rtld=false`, mirroring
  what upstream uses on hosted macOS agents to avoid runtime kerberos
  loading issues.
- `versioningScheme: off` on `dotnet pack` means the version is read from
  the `.csproj` / `Directory.Build.props`. Switch to `byEnvVar` or
  `byBuildNumber` if you want pipeline-controlled versioning.
- Neither pipeline pushes the resulting `.nupkg` anywhere. Wire up a
  `NuGetCommand@2 push` (or `dotnet nuget push`) step to publish to a feed.
- Artifacts: per the user's note, getting artifacts to the right place is
  out of scope here — both pipelines simply publish them as
  `PublishPipelineArtifact@1` outputs so any downstream stage / pipeline
  can pick them up.
