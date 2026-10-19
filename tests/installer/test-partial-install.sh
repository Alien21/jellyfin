#!/usr/bin/env bash
# Offline integration tests; no service or installation paths are accessed.
set -Eeuo pipefail

repo_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd -P)"
installer="$repo_dir/install.sh"
python_tool="${PYTHON_BIN:-python3}"
fixture_dir="$repo_dir/_temp/install-validation/partial-$(date -u +%Y%m%dT%H%M%SZ)-$$"
report_dir="$repo_dir/_tests/install-validation"
case "$fixture_dir" in "$repo_dir/_temp/install-validation/partial-"*) ;; *) exit 2 ;; esac
mkdir -p -- "$fixture_dir/packages" "$report_dir"
checks_file="$fixture_dir/checks.tsv"
: > "$checks_file"

"$python_tool" -B - "$fixture_dir/packages" <<'PY'
import hashlib, io, json, pathlib, sys, tarfile
root = pathlib.Path(sys.argv[1])
server = {name: b"fixture" for name in (
    "jellyfin.dll", "jellyfin.deps.json", "jellyfin.runtimeconfig.json", "libcoreclr.so",
    "libhostfxr.so", "libhostpolicy.so", "libSkiaSharp.so", "libHarfBuzzSharp.so",
    "libe_sqlite3.so", "libpdfium.so", "ServerSetupApp/index.mstemplate.html", "wwwroot/api-docs/jellyfin.svg")}
server["jellyfin"] = b"\x7fELF\x02\x01\x01\x00" + b"\x00" * 10 + b"\x3e\x00"
server["build-info.json"] = json.dumps(dict(sourceVersion="10.12.0-test", runtime="linux-x64", selfContained=True)).encode()
web = {name: b"fixture" for name in ("index.html", "config.json", "serviceworker.js")}
web["build-info.json"] = json.dumps(dict(packageVersion="10.12.0-test")).encode()
for theme in ("appletv", "blueradiance", "dark", "light", "purplehaze", "wmc"):
    web[f"themes/{theme}/theme.css"] = b"body {}"
def archive(name, prefix, files):
    path = root / name
    with tarfile.open(path, "w:gz") as target:
        for directory in ([prefix, prefix + "/wwwroot"] if prefix == "Jellyfin" else [prefix]):
            item = tarfile.TarInfo(directory); item.type = tarfile.DIRTYPE; target.addfile(item)
        for name, data in files.items():
            item = tarfile.TarInfo(prefix + "/" + name); item.size = len(data); target.addfile(item, io.BytesIO(data))
    pathlib.Path(str(path) + ".sha256").write_text(hashlib.sha256(path.read_bytes()).hexdigest() + "  " + path.name + "\n")
archive("Jellyfin.linux-x64.test.tar.gz", "Jellyfin", server)
archive("jellyfin-web-test.tar.gz", "jellyfin-web", web)
web["build-info.json"] = json.dumps(dict(packageVersion="0.0.0-mismatch")).encode()
archive("jellyfin-web-mismatch.tar.gz", "jellyfin-web", web)
PY
server_fixture="$fixture_dir/packages/Jellyfin.linux-x64.test.tar.gz"
web_fixture="$fixture_dir/packages/jellyfin-web-test.tar.gz"

finish() {
    local result=$?
    trap - EXIT
    "$python_tool" -B - "$checks_file" "$report_dir/partial-results.json" "$result" <<'PY'
import csv, json, pathlib, sys
rows = list(csv.reader(pathlib.Path(sys.argv[1]).open(), delimiter="\t"))
checks = [dict(name=r[0], passed=r[1] == "0", exitCode=int(r[1]), log=r[2]) for r in rows]
pathlib.Path(sys.argv[2]).write_text(json.dumps(dict(passed=int(sys.argv[3]) == 0 and all(c["passed"] for c in checks), checks=checks), indent=2) + "\n")
print(f"Partial installer checks: {len(checks)}; report: {sys.argv[2]}")
PY
    exit "$result"
}
trap finish EXIT
run_check() {
    local name="$1" code=0 child log="$fixture_dir/$1.log"
    shift
    # Background execution keeps errexit active inside functions under test.
    "$@" > "$log" 2>&1 &
    child=$!
    wait "$child" || code=$?
    printf '%s\t%s\t%s\n' "$name" "$code" "$log" >> "$checks_file"
    printf '%s: %s\n' "$name" "$code"
    if (( code != 0 )); then cat -- "$log" >&2; return 1; fi
}
require() { if ! "$@"; then printf 'Assertion failed: %s\n' "$*" >&2; return 1; fi; }
selection_check() (
    local component="$1" explicit="$2" directory="$fixture_dir/select-$1-$2"
    source "$installer"
    mkdir -p -- "$directory"
    local -a paths=()
    if [[ "$component" != web ]]; then
        cp -- "$server_fixture" "$server_fixture.sha256" "$directory/"
        paths+=("$directory/${server_fixture##*/}")
    fi
    if [[ "$component" != server ]]; then
        cp -- "$web_fixture" "$web_fixture.sha256" "$directory/"
        paths+=("$directory/${web_fixture##*/}")
    fi
    if [[ "$component" == both && "$explicit" == true ]]; then paths=("${paths[1]}" "${paths[0]}"); fi
    if [[ "$explicit" == true ]]; then select_archives "$directory" "${paths[@]}"; else select_archives "$directory"; fi
    require test "$update_server" = "$([[ "$component" == web ]] && printf false || printf true)"
    require test "$update_web" = "$([[ "$component" == server ]] && printf false || printf true)"
    validate_archives "$server_archive" "$web_archive"
)
invalid_check() (
    source "$installer"
    local kind="$1" directory="$fixture_dir/reject-$1"
    mkdir -p -- "$directory"
    case "$kind" in
        missing) if (select_archives "$directory"); then exit 1; fi ;;
        duplicate)
            cp -- "$server_fixture" "$directory/Jellyfin.linux-x64.one.tar.gz"
            cp -- "$server_fixture" "$directory/Jellyfin.linux-x64.two.tar.gz"
            if (select_archives "$directory"); then exit 1; fi ;;
        missing-explicit) if (select_archives "$directory" "$directory/Jellyfin.linux-x64.missing.tar.gz"); then exit 1; fi ;;
        unknown-explicit)
            cp -- "$server_fixture" "$directory/unknown.tar.gz"
            if (select_archives "$directory" "$directory/unknown.tar.gz"); then exit 1; fi ;;
        mismatch)
            validate_archives '' "$fixture_dir/packages/jellyfin-web-mismatch.tar.gz"
            if (validate_archives "$server_fixture" "$fixture_dir/packages/jellyfin-web-mismatch.tar.gz"); then exit 1; fi ;;
        bad-manifest)
            assert_managed_path() { [[ "$1" == "$directory/components" && ! -L "$1" ]]; }
            printf 'unexpected\n' > "$directory/components"
            if (read_backup_components "$directory"); then exit 1; fi ;;
    esac
)

prepare_sandbox() {
    sandbox="$fixture_dir/$1"
    mkdir -p -- "$sandbox/app" "$sandbox/web" "$sandbox/config" "$sandbox/data/data" "$sandbox/cache" "$sandbox/log"
    printf old-server > "$sandbox/app/jellyfin"; printf old-dll > "$sandbox/app/jellyfin.dll"
    printf old-web > "$sandbox/web/index.html"; printf local-config > "$sandbox/web/config.json"
    printf old-config > "$sandbox/config/system.xml"; printf old-database > "$sandbox/data/data/jellyfin.db"
    printf old-defaults > "$sandbox/defaults"; : > "$sandbox/events"
    # Only default paths and host guards change in the test copy. All archive,
    # staging, transaction, manifest and recovery logic remains the actual code.
    "$python_tool" -B - "$installer" "$sandbox/install.sh" <<'PY'
import pathlib, re, sys
original, target = map(pathlib.Path, sys.argv[1:])
source = original.read_text(encoding="utf-8")
for variable, suffix in dict(BIN="app", WEB="web", CONFIG="config", DEFAULTS="defaults", DATA="data", CACHE="cache", LOG="log", BACKUPS="backups", LOCK="installer.lock").items():
    source, count = re.subn(r"^" + variable + r"=.*$", variable + '="$sandbox/' + suffix + '"', source, count=1, flags=re.M)
    assert count == 1, variable
source, count = re.subn(r'    \[\[ "\$\(uname -s\)" == Linux && "\$\(uname -m\)" == x86_64 \]\] \|\|\n        die [^\n]+', '    : # Offline QA platform adapter', source, count=1)
assert count == 1, "platform adapter"
source = source.replace('    . /etc/os-release', '    ID=ubuntu; VERSION_ID=24.04 # Offline QA OS adapter', 1)
source, count = re.subn(r'    if \[\[ "\$check_only" != true \]\]; then \(\( EUID == 0 \)\) \|\| die [^\n]+', '    : # Offline QA root adapter', source, count=1)
assert count == 1, "root adapter"
target.write_text(source, encoding="utf-8", newline="\n")
PY
}
load_sandbox() {
    source "$sandbox/install.sh"
    assert_managed_path() {
        local resolved
        resolved="$(realpath -m -- "$1")" || return 1
        case "$resolved" in "$sandbox"/*) [[ ! -L "$1" ]] ;; *) printf 'Unsafe QA path: %s\n' "$1" >&2; return 98 ;; esac
    }
    safe_move() {
        assert_managed_path "$1" && assert_managed_path "$2" && [[ -e "$1" && ! -e "$2" ]] || return 1
        printf 'move\t%s\t%s\n' "$1" "$2" >> "$sandbox/events"
        command mv -T -- "$1" "$2"
    }
    chown() { :; }; id() { printf '0\n'; }; getent() { :; }; flock() { :; }
    curl() { printf 'Unexpected network access in offline test\n' >&2; return 97; }
    # NTFS does not implement Linux directory modes; permission commands have separate QA.
    mkdir() {
        local -a arguments=()
        while (( $# )); do
            if [[ "$1" == -m ]]; then shift 2; else arguments+=("$1"); shift; fi
        done
        command mkdir "${arguments[@]}"
    }
    stat() { if [[ "$*" == '-c %u -- '* ]]; then printf '0\n'; else command stat "$@"; fi; }
    set_file_permissions() { assert_managed_path "$1"; }
    set_tree_permissions() { assert_managed_path "$1"; }
    set_app_permissions() { assert_managed_path "$1" && printf 'permissions\t%s\n' "$1" >> "$sandbox/events"; }
    validate_service_paths() { :; }
    systemctl() {
        printf 'service\t%s\n' "$*" >> "$sandbox/events"
        case "$*" in
            'show jellyfin -p LoadState --value') printf 'loaded\n' ;;
            'show jellyfin -p ActiveState --value') printf '%s\n' "$service_state" ;;
            'is-active --quiet jellyfin') [[ "$service_state" == active ]] ;;
            'stop jellyfin') service_state=inactive ;;
            'start jellyfin') service_state=active ;;
            *) printf 'Unexpected service call: %s\n' "$*" >&2; return 97 ;;
        esac
    }
    inactive() { [[ "$service_state" == inactive ]]; }
    wait_ready() {
        if [[ "$fail_health" == true ]]; then
            if [[ "$update_server" == true ]]; then
                printf failed-config > "$CONFIG/system.xml"
                printf failed-database > "$DATA/data/jellyfin.db"
            fi
            return 1
        fi
    }
    # Existing check-install.sh covers tar/state internals. These adapters let
    # transaction checks verify when state is backed up/restored without / access.
    backup_state() {
        inactive || return 1
        printf 'backup-state\n' >> "$sandbox/events"
        mkdir -p -- "$backup_dir/state-fixture"
        cp -- "$CONFIG/system.xml" "$backup_dir/state-fixture/config"
        cp -- "$DATA/data/jellyfin.db" "$backup_dir/state-fixture/database"
        printf offline-state > "$backup_dir/state.tar"; backup_complete=true
    }
    validate_state_backup() { [[ -s "$1" ]]; }
    restore_state() {
        printf 'restore-state\n' >> "$sandbox/events"
        cp -- "${1%/*}/state-fixture/config" "$CONFIG/system.xml"
        cp -- "${1%/*}/state-fixture/database" "$DATA/data/jellyfin.db"
    }
    service_state=active; fail_health=false
}
lock_check() (
    local kind="$1" marker=retained-lock-marker lock_inode= lock_digest= test_owner=0
    prepare_sandbox "lock-$kind"
    load_sandbox
    owner_uid=1000
    stat() { if [[ "$*" == '-c %u -- '* ]]; then printf '%s\n' "$test_owner"; else command stat "$@"; fi; }
    flock() {
        printf 'flock\t%s\n' "$*" >> "$sandbox/events"
        [[ "$kind" != busy ]]
    }
    set_file_permissions() {
        assert_managed_path "$1" || return 1
        require test "$1" = "$LOCK"
        printf 'lock-permissions\t%s\n' "$2" >> "$sandbox/events"
    }

    case "$kind" in
        fresh) require test ! -e "$LOCK" ;;
        directory) mkdir -- "$LOCK" ;;
        retained-root|retained-owner|unexpected-owner|busy)
            printf '%s\n' "$marker" > "$LOCK"
            lock_inode="$(command stat -c %i -- "$LOCK")"
            lock_digest="$(sha256sum -- "$LOCK")"
            if [[ "$kind" == retained-owner ]]; then test_owner=1000; fi
            if [[ "$kind" == unexpected-owner ]]; then test_owner=1001; fi ;;
        *) printf 'Unknown lock test: %s\n' "$kind" >&2; return 2 ;;
    esac

    if [[ "$kind" == directory || "$kind" == unexpected-owner || "$kind" == busy ]]; then
        local result=0 child
        (
            if [[ "$kind" == busy ]]; then main "$web_fixture"; else acquire_install_lock; fi
            printf 'after-lock\n' >> "$sandbox/events"
        ) > "$sandbox/lock-output" 2>&1 &
        child=$!
        wait "$child" || result=$?
        require test "$result" != 0
        if grep -Eq '^(lock-permissions|after-lock|permissions|move|backup-state)' "$sandbox/events" ||
                grep -Eq '^service[[:space:]](start|stop) jellyfin$' "$sandbox/events"; then
            printf 'Rejected lock changed permissions, installation, or service state\n' >&2
            return 1
        fi
        if [[ "$kind" == busy ]]; then
            require grep -Fxq -- "flock"$'\t'"-n 9" "$sandbox/events"
            require test ! -e "$sandbox/backups"
            assert_untouched app old-server
            assert_untouched web old-web
        else
            require test ! -s "$sandbox/events"
        fi
        if [[ "$kind" == directory ]]; then
            require test -d "$LOCK"
        else
            require test "$(command stat -c %i -- "$LOCK")" = "$lock_inode"
            require test "$(sha256sum -- "$LOCK")" = "$lock_digest"
        fi
        return
    fi

    acquire_install_lock
    require test -f "$LOCK"
    require test ! -L "$LOCK"
    if (printf unexpected-write >&9) 2>/dev/null; then
        printf 'Installer lock descriptor is writable\n' >&2
        return 1
    fi
    if [[ "$kind" == fresh ]]; then
        require test ! -s "$LOCK"
    else
        require test "$(command stat -c %i -- "$LOCK")" = "$lock_inode"
        require test "$(sha256sum -- "$LOCK")" = "$lock_digest"
        local actual_marker
        IFS= read -r actual_marker <&9
        require test "$actual_marker" = "$marker"
    fi
    require test "$(cat -- "$sandbox/events")" = $'flock\t-n 9\nlock-permissions\t660'
    exec 9<&-
)

assert_untouched() {
    local component="$1" content="$2" file=index.html
    if [[ "$component" == app ]]; then file=jellyfin; fi
    require test "$(cat -- "$sandbox/$component/$file")" = "$content"
    if grep -Fq -- "$sandbox/$component." "$sandbox/events" || grep -Fxq -- "permissions"$'\t'"$sandbox/$component" "$sandbox/events"; then
        printf 'Unselected component was touched: %s\n' "$component" >&2; return 1
    fi
}
transaction_check() (
    local component="$1" failure="$2" action="$3"
    prepare_sandbox "$1-$2-$3"
    load_sandbox
    local -a archives=() markers=()
    if [[ "$component" != web ]]; then archives+=("$server_fixture"); fi
    if [[ "$component" != server ]]; then archives+=("$web_fixture"); fi
    fail_health="$failure"
    local result=0 child
    (if [[ "$action" == check ]]; then main --check "${archives[@]}"; else main "${archives[@]}"; fi) > "$sandbox/install-output" 2>&1 &
    child=$!; wait "$child" || result=$?
    if [[ "$failure" == true ]]; then
        require test "$result" != 0
        require test "$(cat -- "$sandbox/app/jellyfin")" = old-server
        require test "$(cat -- "$sandbox/web/index.html")" = old-web
        if [[ "$component" == server ]]; then assert_untouched web old-web; fi
        if [[ "$component" == web ]]; then
            assert_untouched app old-server
            require test "$(grep -c '^restore-state$' "$sandbox/events" || true)" = 0
            require test "$(grep -c '^backup-state$' "$sandbox/events" || true)" = 0
        else
            require test "$(grep -c '^restore-state$' "$sandbox/events" || true)" = 1
            require test "$(cat -- "$sandbox/config/system.xml")" = old-config
            require test "$(cat -- "$sandbox/data/data/jellyfin.db")" = old-database
        fi
        return
    fi
    if (( result != 0 )); then cat -- "$sandbox/install-output" >&2; return "$result"; fi
    if [[ "$action" == check ]]; then
        assert_untouched app old-server
        assert_untouched web old-web
        require test ! -e "$sandbox/backups"
        require test ! -e "$sandbox/installer.lock"
        return
    fi
    markers=("$sandbox"/backups/*/install-success)
    require test "${#markers[@]}" = 1
    local backup="${markers[0]%/*}" release
    release="${backup##*/}"
    require test -s "$backup/components"
    if [[ "$component" == web ]]; then
        assert_untouched app old-server
        require test ! -e "$backup/state.tar"
        require test ! -e "$sandbox/app.previous-$release"
    else require test -s "$backup/state.tar"; require test -d "$sandbox/app.previous-$release"; fi
    if [[ "$component" == server ]]; then
        assert_untouched web old-web
        require test ! -e "$sandbox/web.previous-$release"
    else
        require test -d "$sandbox/web.previous-$release"
        require test "$(cat -- "$sandbox/web/config.json")" = local-config
    fi
    if [[ "$action" == install ]]; then return; fi
    if [[ "$action" == legacy ]]; then command mv -- "$backup/components" "$backup/components.legacy-fixture"; fi
    printf post-config > "$sandbox/config/system.xml"; printf post-database > "$sandbox/data/data/jellyfin.db"
    date() { if [[ "$*" == '-u +%Y%m%dT%H%M%SZ' ]]; then printf '20300101T000001Z\n'; else command date "$@"; fi; }
    (mode=install; if [[ "$action" == legacy ]]; then main --rollback "$release"; else main --rollback; fi) > "$sandbox/rollback-output" 2>&1 &
    child=$!; result=0; wait "$child" || result=$?
    if (( result != 0 )); then cat -- "$sandbox/rollback-output" >&2; return "$result"; fi
    require test -f "$backup/rolled-back"
    require test "$(cat -- "$sandbox/app/jellyfin")" = old-server
    require test "$(cat -- "$sandbox/web/index.html")" = old-web
    if [[ "$component" == web ]]; then
        assert_untouched app old-server
        require test "$(cat -- "$sandbox/config/system.xml")" = post-config
        require test "$(cat -- "$sandbox/data/data/jellyfin.db")" = post-database
    else
        require test "$(cat -- "$sandbox/config/system.xml")" = old-config
        require test "$(cat -- "$sandbox/data/data/jellyfin.db")" = old-database
    fi
    if [[ "$component" == server ]]; then assert_untouched web old-web; fi
)

run_check syntax bash -n "$installer"
for lock_case in fresh retained-root retained-owner directory unexpected-owner busy; do
    run_check "lock-$lock_case" lock_check "$lock_case"
done
for component in server web both; do
    run_check "discover-$component" selection_check "$component" false
    run_check "explicit-$component" selection_check "$component" true
done
for invalid in missing duplicate missing-explicit unknown-explicit mismatch bad-manifest; do run_check "reject-$invalid" invalid_check "$invalid"; done
for component in server web both; do
    run_check "check-$component" transaction_check "$component" false check
    run_check "install-$component" transaction_check "$component" false install
    run_check "recover-$component" transaction_check "$component" true install
    run_check "rollback-$component" transaction_check "$component" false rollback
done
run_check rollback-legacy-both transaction_check both false legacy
