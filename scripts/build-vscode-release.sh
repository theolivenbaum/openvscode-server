#!/usr/bin/env bash
# Build the openvscode-server distribution and stage it as an embedded resource for the
# .NET library. This is intentionally idempotent so it can be re-run after a code change.
#
# Works on Linux and macOS. For Windows use scripts/build-vscode-release.ps1.
set -euo pipefail

usage() {
    cat <<EOF
Usage: $0 [platform] [arch]
  platform   linux | darwin | win32 | alpine   (default: detected from host)
  arch       x64 | arm64 | ia32                 (default: detected from host)

Examples:
  $0                       # auto-detect host (e.g. darwin arm64 on Apple Silicon)
  $0 linux x64
  $0 darwin arm64          # macOS Apple Silicon

Output:
  dotnet/OpenVSCodeServer.Kestrel/EmbeddedAssets/vscode-reh-web-<platform>-<arch>.tar.gz
EOF
}

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
    usage
    exit 0
fi

# Resolve the repository root from the location of this script so the build doesn't depend on
# the caller's working directory.
SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
ROOT_DIR=$(cd "$SCRIPT_DIR/.." && pwd)

# --- platform / arch detection ---
detect_platform() {
    case "$(uname -s)" in
        Linux*)  echo linux ;;
        Darwin*) echo darwin ;;
        MINGW*|MSYS*|CYGWIN*) echo win32 ;;
        *) echo linux ;;
    esac
}

detect_arch() {
    case "$(uname -m)" in
        x86_64|amd64) echo x64 ;;
        aarch64|arm64) echo arm64 ;;
        i?86) echo ia32 ;;
        *) echo x64 ;;
    esac
}

PLATFORM="${1:-$(detect_platform)}"
ARCH="${2:-$(detect_arch)}"
DISTRO_NAME="vscode-reh-web-${PLATFORM}-${ARCH}"

echo "==> Building ${DISTRO_NAME}"
echo "    repo: ${ROOT_DIR}"

cd "$ROOT_DIR"

# --- optional: trim extensions for a pure C# / BYO-LSP build -------------------------------
# Skips the language stacks, JS/TS tooling, GitHub/MS auth flows and Node debugger VSIXs that
# a customer running their own language server doesn't need. Cuts ~15-30 min of build time.
if [[ "${VSCODE_MINIMAL_BUILD:-0}" == "1" ]]; then
    echo "==> VSCODE_MINIMAL_BUILD=1 -> trimming extensions"
    node dotnet/scripts/prepare-minimal-build.mjs
fi

# --- npm install -----------------------------------------------------------------------------
if [[ ! -d node_modules || "${SKIP_NPM_INSTALL:-0}" != "1" ]]; then
    echo "==> npm install (set SKIP_NPM_INSTALL=1 to skip)"
    npm install
else
    echo "==> Skipping npm install (SKIP_NPM_INSTALL=1)"
fi

# --- gulp build -----------------------------------------------------------------------------
# Invoke gulp directly so we can cap V8's heap below the package.json default of 8192 MiB.
# Microsoft-hosted CI agents (ubuntu-latest, windows-latest, macos-latest) only have ~7 GiB
# of RAM, so an 8 GiB heap forces the OS to swap during the minification stage and triggers
# "Free memory is lower than 5%" agent warnings. 6144 MiB leaves headroom for the rest of
# the toolchain (esbuild, terser, native module compilation) without OOM-ing on small hosts.
# Override with VSCODE_NODE_MAX_OLD_SPACE_MB on beefier machines.
NODE_MAX_OLD_SPACE_MB="${VSCODE_NODE_MAX_OLD_SPACE_MB:-6144}"
GULP_TASK="${DISTRO_NAME}-min"
echo "==> gulp ${GULP_TASK} (--max-old-space-size=${NODE_MAX_OLD_SPACE_MB})"
node "--max-old-space-size=${NODE_MAX_OLD_SPACE_MB}" ./node_modules/gulp/bin/gulp.js "${GULP_TASK}"

# The gulp output lives one level above the repo (BUILD_ROOT = path.dirname(REPO_ROOT)).
BUILD_ROOT=$(cd "$ROOT_DIR/.." && pwd)
BUILD_OUTPUT="${BUILD_ROOT}/${DISTRO_NAME}"
if [[ ! -d "${BUILD_OUTPUT}" ]]; then
    echo "ERROR: expected gulp output directory not found: ${BUILD_OUTPUT}" >&2
    exit 1
fi

# --- package as tar.gz ----------------------------------------------------------------------
EMBED_DIR="${ROOT_DIR}/dotnet/OpenVSCodeServer.Kestrel/EmbeddedAssets"
mkdir -p "${EMBED_DIR}"

# Remove other architectures' archives so we always embed exactly one matching distribution.
find "${EMBED_DIR}" -maxdepth 1 -name 'vscode-reh-web-*.tar.gz' -print -delete || true

ARCHIVE="${EMBED_DIR}/${DISTRO_NAME}.tar.gz"
echo "==> Packaging ${BUILD_OUTPUT} -> ${ARCHIVE}"
tar -C "${BUILD_ROOT}" -czf "${ARCHIVE}" "${DISTRO_NAME}"

echo "==> Done. Archive size: $(du -h "${ARCHIVE}" | cut -f1)"
echo "    Run 'dotnet build dotnet/OpenVSCodeServer.sln' to embed it."
