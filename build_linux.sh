#!/usr/bin/env bash
set -euo pipefail

repo_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
cd "$repo_dir"

runtime="${RID:-linux-x64}"
framework="net10.0"
run_tests=false
skip_restore=false
test_project="Jellyfin.slnx"
test_filter=""

usage() {
    cat <<'EOF'
Usage: bash ./build_linux.sh [options]

Build and package the Jellyfin server for Linux. Requires .NET 10 SDK,
Git, GNU tar, gzip, sha256sum and Bash (Git Bash on Windows is supported).
Set DOTNET_BIN to an SDK dotnet executable if it is not on PATH.

  -r, --runtime RID       linux-x64 (default), linux-arm64 or linux-arm
      --tests             Run tests on the build host before packaging
      --test-project PATH Test solution/project (default: Jellyfin.slnx)
      --test-filter EXPR  dotnet test filter (also enables --tests)
      --skip-restore      Reuse existing restored assets for this RID/tests
  -h, --help              Show this help

Outputs:
  _temp/                       SDK binaries/intermediates and build logs
  _output/net10.0/<RID>/publish/ Complete self-contained Linux publish
  _tests/                      Test result files
  _artifacts/<RID>/net10.0/Jellyfin/  Ready server directory
  _artifacts/Jellyfin.<RID>.<version>-alien21.<commit>[-dirty].tar.gz

The archive contains one Jellyfin/ directory. It includes the server,
not jellyfin-web, FFmpeg or user configuration/data. No deployment occurs.
EOF
}

require_value() {
    if [[ $# -lt 2 || -z "$2" || "$2" == --* ]]; then
        printf 'Missing value for %s\n' "$1" >&2
        exit 2
    fi
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        -r|--runtime) require_value "$@"; runtime="$2"; shift 2 ;;
        --tests) run_tests=true; shift ;;
        --test-project) require_value "$@"; test_project="$2"; run_tests=true; shift 2 ;;
        --test-filter) require_value "$@"; test_filter="$2"; run_tests=true; shift 2 ;;
        --skip-restore) skip_restore=true; shift ;;
        -h|--help) usage; exit 0 ;;
        *) printf 'Unknown argument: %s\n' "$1" >&2; usage >&2; exit 2 ;;
    esac
done

case "$runtime" in
    linux-x64|linux-arm64|linux-arm) ;;
    *) printf 'Unsupported Linux runtime: %s\n' "$runtime" >&2; exit 2 ;;
esac
dotnet_bin="${DOTNET_BIN:-dotnet}"
command -v "$dotnet_bin" >/dev/null || { printf 'Required .NET 10 SDK not found; set DOTNET_BIN or PATH\n' >&2; exit 1; }
for command in git tar gzip sha256sum realpath; do
    command -v "$command" >/dev/null || { printf 'Required command not found: %s\n' "$command" >&2; exit 1; }
done
[[ -f "$test_project" ]] || { printf 'Test project/solution not found: %s\n' "$test_project" >&2; exit 2; }

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_CLI_USE_MSBUILD_SERVER=0
export MSBUILDDISABLENODEREUSE=1

temp_dir="$repo_dir/_temp"
publish_dir="$repo_dir/_output/$framework/$runtime/publish"
ready_dir="$repo_dir/_artifacts/$runtime/$framework/Jellyfin"
stage_parent="$temp_dir/package/$runtime-$$"
stage_dir="$stage_parent/Jellyfin"
log_dir="$temp_dir/logs"
test_sdk_dir="$temp_dir/test-sdk"
# Resolve every generated path, including symlinked parents, before writing,
# recursively deleting or moving it. Never allow a generated root itself.
generated_path() {
    local resolved
    resolved="$(realpath -m -- "$1")"
    case "$resolved" in
        "$repo_dir/_temp/"*|"$repo_dir/_output/"*|"$repo_dir/_tests/"*|"$repo_dir/_artifacts/"*) ;;
        *) printf 'Refusing path outside generated subdirectories: %s\n' "$resolved" >&2; return 1 ;;
    esac
    printf '%s\n' "$resolved"
}
remove_generated_dir() {
    local resolved
    resolved="$(generated_path "$1")"
    rm -rf -- "$resolved"
}
cleanup() {
    remove_generated_dir "$stage_parent"
}
trap cleanup EXIT

log_dir="$(generated_path "$log_dir")"
publish_dir="$(generated_path "$publish_dir")"
ready_dir="$(generated_path "$ready_dir")"
stage_parent="$(generated_path "$stage_parent")"
stage_dir="$(generated_path "$stage_dir")"
test_sdk_dir="$(generated_path "$test_sdk_dir")"
test_results_dir="$(generated_path "$repo_dir/_tests/results")"
mkdir -p "$log_dir" "$test_results_dir"

run_step() {
    local name="$1"
    shift
    printf '\n== %s ==\n' "$name"
    if "$@" 2>&1 | tee "$log_dir/$name.log"; then
        return 0
    else
        local result=$?
        printf 'Step failed: %s (see %s/%s.log)\n' "$name" "$log_dir" "$name" >&2
        return "$result"
    fi
}

source_version="$(sed -n 's/.*AssemblyVersion("\([0-9.]*\)").*/\1/p' SharedVersion.cs | head -n 1)"
[[ -n "$source_version" ]] || { printf 'Cannot read SharedVersion.cs version\n' >&2; exit 1; }
commit="$(git rev-parse HEAD)"
short_commit="$(git rev-parse --short=12 HEAD)"
branch="$(git symbolic-ref --quiet --short HEAD || printf 'detached')"
dirty=false
dirty_suffix=""
if [[ -n "$(git status --porcelain --untracked-files=normal)" ]]; then
    dirty=true
    dirty_suffix="-dirty"
fi
sdk_version="$("$dotnet_bin" --version)"
build_utc="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
archive_name="Jellyfin.$runtime.$source_version-alien21.$short_commit$dirty_suffix.tar.gz"
archive_path="$(generated_path "$repo_dir/_artifacts/$archive_name")"
server_project="Jellyfin.Server/Jellyfin.Server.csproj"
common_args=(--disable-build-servers -p:UseSharedCompilation=false)

printf 'Jellyfin %s | %s | SDK %s | %s | dirty=%s\n' "$source_version" "$runtime" "$sdk_version" "$short_commit" "$dirty"
if [[ "$skip_restore" == false ]]; then
    run_step server-restore "$dotnet_bin" restore "$server_project" --runtime "$runtime" -p:SelfContained=true "${common_args[@]}"
fi
remove_generated_dir "$publish_dir"
run_step server-publish "$dotnet_bin" publish "$server_project" --configuration Release --framework "$framework" \
    --runtime "$runtime" --self-contained true --no-restore --output "$publish_dir" \
    -p:PublishReadyToRun=false -p:PublishSingleFile=false "${common_args[@]}"

if [[ "$run_tests" == true ]]; then
    # Host test assets are separate from the Linux publish assets, including on Windows.
    if [[ "$skip_restore" == false ]]; then
        run_step tests-restore "$dotnet_bin" restore "$test_project" --artifacts-path "$test_sdk_dir" "${common_args[@]}"
    fi
    test_args=("$dotnet_bin" test "$test_project" --configuration Release --no-restore --artifacts-path "$test_sdk_dir"
        --results-directory "$test_results_dir" --logger trx "${common_args[@]}")
    if [[ -n "$test_filter" ]]; then
        test_args+=(--filter "$test_filter")
    fi
    run_step tests "${test_args[@]}"
fi

for required in jellyfin jellyfin.dll jellyfin.deps.json jellyfin.runtimeconfig.json \
    libcoreclr.so libSkiaSharp.so libHarfBuzzSharp.so libe_sqlite3.so libpdfium.so \
    ServerSetupApp/index.mstemplate.html wwwroot/api-docs/jellyfin.svg; do
    [[ -s "$publish_dir/$required" ]] || { printf 'Missing publish file: %s\n' "$required" >&2; exit 1; }
done
elf_magic="$(head -c 4 "$publish_dir/jellyfin" | od -An -tx1 | tr -d ' \r\n')"
[[ "$elf_magic" == 7f454c46 ]] || { printf 'The apphost is not a Linux ELF executable\n' >&2; exit 1; }

mkdir -p "$stage_dir"
cp -a "$publish_dir/." "$stage_dir/"
cp LICENSE "$stage_dir/LICENSE"
chmod +x "$stage_dir/jellyfin"
if [[ -f "$stage_dir/createdump" ]]; then chmod +x "$stage_dir/createdump"; fi

json_string() {
    local value="$1"
    value="${value//\\/\\\\}"
    value="${value//\"/\\\"}"
    value="${value//$'\n'/\\n}"
    value="${value//$'\r'/\\r}"
    value="${value//$'\t'/\\t}"
    printf '"%s"' "$value"
}
{
    printf '{\n  "product": "Jellyfin",\n  "sourceVersion": "%s",\n' "$source_version"
    printf '  "baseCommit": "%s",\n  "branch": ' "$commit"
    json_string "$branch"
    printf ',\n  "workingTreeDirty": %s,\n  "buildUtc": "%s",\n' "$dirty" "$build_utc"
    printf '  "sdkVersion": "%s",\n  "framework": "%s",\n  "runtime": "%s",\n' "$sdk_version" "$framework" "$runtime"
    printf '  "configuration": "Release",\n  "selfContained": true,\n  "testsRun": %s\n}\n' "$run_tests"
} > "$stage_dir/build-info.json"

# NTFS may ignore chmod. Add executable entries separately with an explicit
# tar mode instead of inheriting their Windows filesystem permissions.
mkdir -p "$(dirname -- "$archive_path")"
archive_tar="$stage_parent/server.tar"
run_step server-package tar --create --file "$archive_tar" --mode='u=rwX,go=rX' \
    --owner=0 --group=0 --numeric-owner --exclude=Jellyfin/jellyfin --exclude=Jellyfin/createdump \
    --directory "$stage_parent" Jellyfin
executable_entries=(Jellyfin/jellyfin)
if [[ -f "$stage_dir/createdump" ]]; then executable_entries+=(Jellyfin/createdump); fi
run_step server-executables tar --append --file "$archive_tar" --mode=0755 \
    --owner=0 --group=0 --numeric-owner --directory "$stage_parent" "${executable_entries[@]}"
compress_archive() { gzip -n -c -- "$archive_tar" > "$archive_path"; }
run_step server-compress compress_archive
apphost_mode="$(tar --list --verbose --gzip --file "$archive_path" Jellyfin/jellyfin | cut -c 1-10)"
[[ "$apphost_mode" == -rwxr-xr-x ]] || { printf 'Invalid apphost mode in archive: %s\n' "$apphost_mode" >&2; exit 1; }
if [[ -f "$stage_dir/createdump" ]]; then
    dump_mode="$(tar --list --verbose --gzip --file "$archive_path" Jellyfin/createdump | cut -c 1-10)"
    [[ "$dump_mode" == -rwxr-xr-x ]] || { printf 'Invalid createdump mode in archive: %s\n' "$dump_mode" >&2; exit 1; }
fi
(cd "$repo_dir/_artifacts" && sha256sum "$archive_name" > "$archive_name.sha256")
remove_generated_dir "$ready_dir"
mkdir -p "$(dirname -- "$ready_dir")"
mv -- "$(generated_path "$stage_dir")" "$(generated_path "$ready_dir")"
if [[ -f "$repo_dir/install.sh" ]]; then
    cp -- "$repo_dir/install.sh" "$(generated_path "$repo_dir/_artifacts/install.sh")"
fi
printf '\nReady server: %s\nArchive: %s\nChecksum: %s.sha256\n' "$ready_dir" "$archive_path" "$archive_path"
