#!/bin/sh

set -eu

script_dir="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)"
repo_dir="$(CDPATH= cd -- "$script_dir/../.." && pwd -P)"
dotnet_bin="${DOTNET_BIN:-dotnet}"
fuzz_case="${1:-}"
case "$fuzz_case" in
    ''|.|..|*[!A-Za-z0-9_.-]*) printf 'Usage: %s <fuzz-case>\n' "$0" >&2; exit 2 ;;
esac
corpus_dir="$script_dir/Testcases/$fuzz_case"
[ -d "$corpus_dir" ] || { printf 'Missing fuzz corpus: %s\n' "$corpus_dir" >&2; exit 2; }

instrumented_dir="$(realpath -m -- "$repo_dir/_temp/fuzz/Emby.Server.Implementations.Fuzz/instrumented")"
output_dir="$(realpath -m -- "$repo_dir/_output/fuzz/Emby.Server.Implementations.Fuzz")"
findings_dir="$(realpath -m -- "$repo_dir/_tests/fuzz/Emby.Server.Implementations.Fuzz/$fuzz_case")"
case "$instrumented_dir" in "$repo_dir/_temp/"*) ;; *) printf 'Invalid instrumentation path\n' >&2; exit 1 ;; esac
case "$output_dir" in "$repo_dir/_output/"*) ;; *) printf 'Invalid output path\n' >&2; exit 1 ;; esac
case "$findings_dir" in "$repo_dir/_tests/"*) ;; *) printf 'Invalid findings path\n' >&2; exit 1 ;; esac

cd "$repo_dir"
mkdir -p "$instrumented_dir" "$output_dir" "$findings_dir"
# A previous instrumented output must never be reused by an incremental build.
rm -f -- "$instrumented_dir/Emby.Server.Implementations.dll"
"$dotnet_bin" build -c Release "$repo_dir/Emby.Server.Implementations/Emby.Server.Implementations.csproj" --output "$instrumented_dir"
sharpfuzz "$instrumented_dir/Emby.Server.Implementations.dll"

"$dotnet_bin" build "$script_dir/Emby.Server.Implementations.Fuzz.csproj" --configuration Debug --output "$output_dir"
cmp -- "$instrumented_dir/Emby.Server.Implementations.dll" "$output_dir/Emby.Server.Implementations.dll"
AFL_SKIP_BIN_CHECK=1 afl-fuzz -i "$corpus_dir" -o "$findings_dir" -t 5000 "$output_dir/Emby.Server.Implementations.Fuzz" "$fuzz_case"
