#!/usr/bin/env bash
set -Eeuo pipefail

BIN=/opt/Jellyfin
WEB=/opt/UI
CONFIG=/opt/config
DEFAULTS=/etc/default/jellyfin
DATA=/opt/data
CACHE=/opt/cache
LOG=/opt/log
BACKUPS=/var/backups/jellyfin
LOCK=/run/lock/jellyfin-local-install.lock
OWNER_USER=alien21
OWNER_GROUP=plex
ID_PATTERN='[0-9]{8}T[0-9]{6}Z-[0-9]+'
python_bin="${PYTHON_BIN:-python3}"
health_url=http://127.0.0.1:8096/health
health_timeout=600
mode=install
check_only=false
rollback_id=
stopped=false
backup_complete=false
new_may_have_run=false
state_changed=false
was_active=false
update_server=false
update_web=false

usage() {
    cat <<'HELP'
Usage: sudo bash ./install.sh [--check] [--health-url URL] [ARCHIVE.tar.gz ...]
       sudo bash ./install.sh [--check] --rollback [RELEASE_ID]
       sudo bash ./install.sh [--check] --fix-permissions
       bash ./install.sh --help

Updates an existing Ubuntu 24.04 amd64 Jellyfin installation.
Server: /opt/Jellyfin; web: /opt/UI. Copy this script and the selected packages to /opt.
Data/config/cache/log: /opt/data, /opt/config, /opt/cache, /opt/log.
Without archive arguments, packages are selected beside this script: at most one
Jellyfin.linux-x64.*.tar.gz and one jellyfin-web-*.tar.gz, with their .sha256 files.
At least one package is required. One or two explicit paths may also be supplied.
An absent component is left in place and is neither backed up nor installed.

  --check                Validate inputs and show the plan; no persistent writes.
  --rollback [RELEASE_ID] Restore the components changed by that installation.
                         Server rollback also restores configuration and databases.
                         Without an ID, choose the latest successful, unused backup.
  --fix-permissions      Repair ownership/modes without archives or service restart.
  --health-url URL       Loopback health endpoint, e.g. http://127.0.0.1:8096/base/health
  --health-timeout SEC   Readiness timeout (10-3600 seconds; default 600).
  -h, --help             Show help without requiring Linux or root.

Requires Bash, GNU coreutils/tar, Python 3 stdlib, curl, systemctl and flock.
No packages are installed. Backups and failed/new directories are retained.
Server changes also back up configuration, active DATA/data/*.db files and SQLite sidecars.
Images, metadata directories, SQLiteBackups, cache and logs are never backed up.
Created files belong to alien21:plex. App/web: directories/executables 775, files 664.
Configuration, databases and backups: directories/executables 770, other files 660.
HELP
}

die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

require_value() {
    [[ $# -ge 2 && -n "$2" && "$2" != --* ]] || die "Missing value for $1"
}

select_archives() {
    local directory="$1" archive base nullglob_set=false
    shift
    local -a archives=("$@") servers=() webs=()
    server_archive=; web_archive=
    update_server=false; update_web=false
    if (( ${#archives[@]} == 0 )); then
        if shopt -q nullglob; then nullglob_set=true; fi
        shopt -s nullglob
        servers=("$directory"/Jellyfin.linux-x64.*.tar.gz)
        webs=("$directory"/jellyfin-web-*.tar.gz)
        if [[ "$nullglob_set" == false ]]; then shopt -u nullglob; fi
        (( ${#servers[@]} <= 1 && ${#webs[@]} <= 1 )) ||
            die "Place at most one server and one web archive beside install.sh, or pass the selected paths explicitly"
        archives=("${servers[@]}" "${webs[@]}")
    fi
    (( ${#archives[@]} >= 1 && ${#archives[@]} <= 2 )) || die "Provide at least one server or web archive (at most one of each)"
    for archive in "${archives[@]}"; do
        base="${archive##*/}"
        case "$base" in
            Jellyfin.linux-x64.*.tar.gz)
                [[ "$update_server" == false ]] || die "More than one server archive was selected"
                server_archive="$(realpath -e -- "$archive")" || die "Server archive does not exist: $archive"
                update_server=true ;;
            jellyfin-web-*.tar.gz)
                [[ "$update_web" == false ]] || die "More than one web archive was selected"
                web_archive="$(realpath -e -- "$archive")" || die "Web archive does not exist: $archive"
                update_web=true ;;
            *) die "Unrecognized server/web archive name: $base" ;;
        esac
    done
}

# Read-only and independently callable after sourcing this file.
# JSON is parsed by Python; it is never evaluated as shell code.
validate_archives() {
    "$python_bin" -B - "$1" "$2" <<'PY'
import contextlib, hashlib, json, pathlib, re, sys, tarfile

def fail(message):
    raise ValueError(message)

def checksum(archive):
    sidecar = pathlib.Path(str(archive) + ".sha256")
    lines = sidecar.read_text(encoding="utf-8").splitlines()
    if len(lines) != 1:
        fail(f"{sidecar}: expected exactly one checksum")
    match = re.fullmatch(r"([0-9a-fA-F]{64}) [ *](.+)", lines[0])
    if not match or match[2] != archive.name:
        fail(f"{sidecar}: checksum must name the actual archive basename")
    digest = hashlib.sha256()
    with archive.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    if digest.hexdigest() != match[1].lower():
        fail(f"{archive}: SHA256 mismatch")

def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            fail(f"Duplicate JSON key: {key}")
        result[key] = value
    return result

def members(archive, prefix):
    found = {}
    for item in archive.getmembers():
        name = item.name.rstrip("/")
        parts = name.split("/")
        if (not name or name.startswith("/") or "\\" in name
                or any(ord(c) < 32 or ord(c) == 127 for c in name)
                or any(p in ("", ".", "..") for p in parts)
                or parts[0] != prefix):
            fail(f"Unsafe archive path: {item.name!r}")
        if name in found:
            fail(f"Duplicate archive entry: {name}")
        if not (item.isdir() or item.isreg()):
            fail(f"Links and special entries are forbidden: {name}")
        found[name] = item
    if prefix not in found or not found[prefix].isdir():
        fail(f"Missing archive root directory: {prefix}/")
    return found

def required(found, name, directory=False):
    item = found.get(name)
    if item is None or (not item.isdir() if directory else not item.isreg() or item.size == 0):
        fail(f"Missing required {'directory' if directory else 'file'}: {name}")

def metadata(archive, found, name):
    required(found, name)
    with archive.extractfile(found[name]) as stream:
        result = json.load(stream, object_pairs_hook=unique_object)
    if not isinstance(result, dict):
        fail(f"{name}: expected a JSON object")
    return result

def elf(archive, found, name):
    required(found, name)
    with archive.extractfile(found[name]) as stream:
        header = stream.read(20)
    if (len(header) != 20 or header[:7] != b"\x7fELF\x02\x01\x01"
            or header[7] not in (0, 3) or header[18:20] != b"\x3e\x00"):
        fail(f"{name}: expected Linux ELF64 little-endian x86-64")

try:
    server_path, web_path = (pathlib.Path(value) if value else None for value in sys.argv[1:])
    paths = [path for path in (server_path, web_path) if path is not None]
    if not paths:
        fail("At least one server or web archive is required")
    # Verify every selected checksum before parsing or extracting any tar.
    for path in paths:
        checksum(path)
    versions = []
    with contextlib.ExitStack() as stack:
        if server_path is not None:
            server = stack.enter_context(tarfile.open(server_path, "r:gz"))
            sm = members(server, "Jellyfin")
            for name in ("jellyfin", "jellyfin.dll", "jellyfin.deps.json",
                         "jellyfin.runtimeconfig.json", "libcoreclr.so",
                         "libhostfxr.so", "libhostpolicy.so", "libSkiaSharp.so",
                         "libHarfBuzzSharp.so", "libe_sqlite3.so", "libpdfium.so",
                         "ServerSetupApp/index.mstemplate.html", "wwwroot/api-docs/jellyfin.svg"):
                required(sm, "Jellyfin/" + name)
            required(sm, "Jellyfin/wwwroot", directory=True)
            elf(server, sm, "Jellyfin/jellyfin")
            if "Jellyfin/createdump" in sm:
                elf(server, sm, "Jellyfin/createdump")
            si = metadata(server, sm, "Jellyfin/build-info.json")
            versions.append(si.get("sourceVersion"))
            if si.get("runtime") != "linux-x64" or si.get("selfContained") is not True:
                fail("Server build-info must describe a self-contained linux-x64 publish")
        if web_path is not None:
            web = stack.enter_context(tarfile.open(web_path, "r:gz"))
            wm = members(web, "jellyfin-web")
            for name in ("index.html", "config.json", "serviceworker.js", "build-info.json"):
                required(wm, "jellyfin-web/" + name)
            wi = metadata(web, wm, "jellyfin-web/build-info.json")
            versions.append(wi.get("packageVersion"))
            for theme in ("appletv", "blueradiance", "dark", "light", "purplehaze", "wmc"):
                required(wm, f"jellyfin-web/themes/{theme}/theme.css")
        if any(not isinstance(version, str) or not re.fullmatch(r"\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.+-]+)?", version)
               for version in versions):
            fail("Invalid server sourceVersion or web packageVersion")
        if len(set(versions)) != 1:
            fail("Server sourceVersion and web packageVersion must match")
        print(json.dumps({"version": versions[0], "runtime": "linux-x64" if server_path else None,
                          "server": str(server_path) if server_path else None,
                          "web": str(web_path) if web_path else None}))
except Exception as error:
    print(f"Archive validation failed: {error}", file=sys.stderr)
    sys.exit(1)
PY
}

validate_state_backup() {
    "$python_bin" -B - "$1" "${CONFIG#/}" "${DEFAULTS#/}" "${DATA#/}/data" <<'PY'
import hashlib, pathlib, re, sys, tarfile
try:
    archive = pathlib.Path(sys.argv[1])
    lines = pathlib.Path(str(archive) + ".sha256").read_text().splitlines()
    if len(lines) != 1 or not re.fullmatch(r"[0-9a-f]{64}  state\.tar", lines[0]):
        raise ValueError("Invalid state.tar checksum sidecar")
    digest = hashlib.sha256()
    with archive.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    if digest.hexdigest() != lines[0][:64]:
        raise ValueError("state.tar SHA256 mismatch")
    roots = tuple(sys.argv[2:4])
    database_prefix = sys.argv[4].rstrip("/") + "/"
    database_name = re.compile(r"[A-Za-z0-9_.-]+\.db(?:-(?:wal|shm|journal))?\Z")
    with tarfile.open(archive, "r:") as source:
        items = source.getmembers()
        names, links = set(), set()
        for item in items:
            name = item.name.rstrip("/")
            if (name.startswith("/") or "\\" in name
                    or any(p in ("", ".", "..") for p in name.split("/"))
                    or name in names):
                raise ValueError(f"Unsafe state entry: {name!r}")
            is_configuration = any(name == root or name.startswith(root + "/") for root in roots)
            is_database = name.startswith(database_prefix) and database_name.fullmatch(name[len(database_prefix):])
            if not is_configuration and not is_database:
                raise ValueError(f"Non-database data is forbidden in the backup: {name!r}")
            if is_database and (not item.isreg() or item.size == 0):
                raise ValueError(f"Database backup entries must be nonempty regular files: {name}")
            names.add(name)
            if item.issym() or item.islnk():
                links.add(name)
            elif not (item.isdir() or item.isreg()):
                raise ValueError(f"Unsupported state entry: {name}")
            if item.islnk() and item.linkname.rstrip("/") not in names:
                raise ValueError(f"Invalid state hardlink: {name}")
        if not set(roots).issubset(names) or database_prefix + "jellyfin.db" not in names:
            raise ValueError("Configuration/database backup is incomplete")
        for name in names:
            if name.startswith(database_prefix) and name.endswith((".db-wal", ".db-shm", ".db-journal")):
                if name.rsplit("-", 1)[0] not in names:
                    raise ValueError(f"SQLite sidecar has no matching database: {name}")
        for name in names:
            parts = name.split("/")
            if any("/".join(parts[:i]) in links for i in range(1, len(parts))):
                raise ValueError(f"State entry traverses a link: {name}")
except Exception as error:
    print(f"State validation failed: {error}", file=sys.stderr)
    sys.exit(1)
PY
}

validate_service_paths() {
    if (( $# < 4 || $# > 6 )); then
        printf 'Usage: validate_service_paths BIN WEB CONFIG DATA [CACHE [LOG]]\n' >&2
        return 2
    fi
    local service_exec service_files
    service_exec="$(systemctl show jellyfin -p ExecStart --value)" || return 1
    service_files="$(systemctl show jellyfin -p EnvironmentFiles --value)" || return 1
    "$python_bin" -B - "$service_exec" "$service_files" "$@" <<'PY'
import json, pathlib, re, shlex, sys

OPT_NAMES = {"JELLYFIN_WEB_OPT", "JELLYFIN_FFMPEG_OPT", "JELLYFIN_SERVICE_OPT",
             "JELLYFIN_NOWEBAPP_OPT", "JELLYFIN_ADDITIONAL_OPTS"}
DIR_NAMES = {name: f"JELLYFIN_{name.upper()}_DIR" for name in ("web", "config", "data", "cache", "log")}
FLAGS = {"--webdir": "web", "-w": "web", "--configdir": "config", "-c": "config",
         "--datadir": "data", "-d": "data", "--cachedir": "cache", "-C": "cache",
         "--logdir": "log", "-l": "log"}

def fail(message):
    raise ValueError(message)

def words(value):
    if any(c in value for c in ("\\", chr(96), "%", "\n", "\r")):
        fail("Unsupported escaping, expansion or multiline value")
    return shlex.split(value, comments=False, posix=True)

def property_fields(value):
    value = value.strip()
    if not value.startswith("{") or not value.endswith("}") or "{" in value[1:] or "}" in value[:-1]:
        fail("ExecStart must describe exactly one direct command")
    fields = {}
    allowed = {"path", "argv[]", "ignore_errors", "start_time", "stop_time", "pid", "code", "status"}
    for field in value[1:-1].split(";"):
        if not field.strip():
            continue
        key, sep, content = field.strip().partition("=")
        if not sep or key not in allowed or key in fields:
            fail("Unrecognized ExecStart property format")
        fields[key] = content.strip()
    if not fields.get("path") or not fields.get("argv[]"):
        fail("ExecStart property is missing path or argv[]")
    return fields

def literal_defaults(text):
    relevant = OPT_NAMES | set(DIR_NAMES.values())
    values = {}
    for line in text.splitlines():
        line = line.strip()
        if not line or line.startswith("#") or line.startswith(";"):
            continue
        match = re.match(r"([A-Za-z_][A-Za-z0-9_]*)\s*=(.*)\Z", line)
        if not match:
            if re.search(r"\bJELLYFIN_[A-Za-z0-9_]+\b", line):
                fail("Unsupported relevant assignment in /etc/default/jellyfin")
            continue
        name, value = match.groups()
        if name not in relevant:
            continue
        if name in values:
            fail(f"Duplicate relevant defaults assignment: {name}")
        value = value.strip()
        if value[:1] in ("'", '"'):
            quote = value[0]
            if len(value) < 2 or value[-1] != quote or quote in value[1:-1]:
                fail(f"Only simple literal quoting is supported for {name}")
            value = value[1:-1]
        elif "'" in value or '"' in value:
            fail(f"Only whole-value quoting is supported for {name}")
        if any(c in value for c in ("$", "\\", chr(96), "%", "\n", "\r")):
            fail(f"Expansions or escaping are unsupported for {name}")
        values[name] = value
    return values

def validate_service_contract(exec_property, files_property, expected, *, read_defaults=None, canonical=None):
    # Injectable read-only dependencies let offline tests exercise this exact implementation.
    if read_defaults is None:
        read_defaults = lambda: pathlib.Path("/etc/default/jellyfin").read_text(encoding="utf-8")
    if canonical is None:
        canonical = lambda value: str(pathlib.Path(value).resolve(strict=True))
    def resolved(value):
        if not value or not value.startswith("/") or any(c in value for c in ("$", "\\", chr(96), "%", "\n", "\r")):
            fail("All effective service paths must be absolute literal paths")
        return canonical(value)
    required = {name: value for name, value in expected.items() if value}
    if not all(required.get(name) for name in ("bin", "web", "config", "data")):
        fail("Expected BIN, WEB, CONFIG and DATA are required")
    fields = property_fields(exec_property)
    command = words(fields["argv[]"])
    executable = resolved(expected["bin"].rstrip("/") + "/jellyfin")
    if not command or resolved(fields["path"]) != executable or resolved(command[0]) != executable:
        fail("ExecStart executable must resolve to the expected BIN/jellyfin")
    files_property = files_property.strip()
    uses_defaults = bool(re.fullmatch(r"/etc/default/jellyfin \(ignore_errors=(?:yes|no)\)", files_property))
    if files_property and not uses_defaults:
        fail("Only the single EnvironmentFiles entry /etc/default/jellyfin is supported")
    env = literal_defaults(read_defaults()) if uses_defaults else {}
    arguments = []
    for token in command[1:]:
        if token.startswith("$"):
            name = token[1:]
            if not uses_defaults or name not in OPT_NAMES:
                fail("Only known whole-argument $JELLYFIN_*_OPT expansions with /etc/default/jellyfin are supported")
            arguments.extend(words(env.get(name, "")))
        elif "$" in token:
            fail("Embedded or unknown ExecStart variable expansion is unsupported")
        else:
            arguments.append(token)
    paths, index = {}, 0
    while index < len(arguments):
        token = arguments[index]
        if "$" in token or token == "--":
            fail("Unknown expansion or option terminator in effective service arguments")
        flag, equals, value = token.partition("=")
        name = FLAGS.get(flag)
        if name:
            if name in paths:
                fail(f"Duplicate service path option: {name}")
            if not equals:
                index += 1
                if index >= len(arguments) or arguments[index].startswith("-"):
                    fail(f"Missing value for service path option: {name}")
                value = arguments[index]
            if not value:
                fail(f"Empty service path option: {name}")
            paths[name] = value
        elif any(token.startswith(short) for short in ("-w", "-c", "-d", "-C", "-l")) and not token.startswith("--"):
            fail("Combined short service path options are unsupported")
        index += 1
    result = {"executable": executable}
    for name in ("web", "config", "data", "cache", "log"):
        if name not in required:
            continue
        actual = paths[name] if name in paths else env.get(DIR_NAMES[name])
        if actual is None:
            fail(f"Missing explicit {name} path; set its CLI option or {DIR_NAMES[name]}")
        if resolved(actual) != resolved(required[name]):
            fail(f"Effective {name} path does not resolve to expected {required[name]}")
        result[name] = resolved(actual)
    return result

if __name__ == "__main__":
    try:
        expected = dict(zip(("bin", "web", "config", "data", "cache", "log"), sys.argv[3:]))
        result = validate_service_contract(sys.argv[1], sys.argv[2], expected)
        print(json.dumps(result, sort_keys=True))
    except Exception as error:
        print(f"Service path preflight failed: {error}", file=sys.stderr)
        sys.exit(1)
PY
}

# Only fixed installation paths and operation-specific siblings can be moved.
assert_managed_path() {
    local target="$1" parent canonical
    case "$target" in
        "$BIN"|"$WEB"|"$CONFIG"|"$DEFAULTS"|"$DATA"|"$DATA/data"|"$CACHE"|"$LOG"|"$BACKUPS"|"$LOCK") ;;
        *)
            if ! [[ "$target" =~ ^(/opt/Jellyfin|/opt/UI)\.(new|previous|failed)-$ID_PATTERN$ ||
                    "$target" =~ ^(/opt/config|/etc/default/jellyfin)\.(failed|restore-failed)-$ID_PATTERN$ ||
                    "$target" =~ ^/opt/data/data/[A-Za-z0-9_.-]+\.db(-(wal|shm|journal))?(\.(failed|restore-failed)-$ID_PATTERN)?$ ||
                    "$target" =~ ^/var/backups/jellyfin/$ID_PATTERN(/(state.tar|state.tar.partial|state.tar.sha256|database-files.list|database-files-restore.list|install-success|install-success.partial|rolled-back|rolled-back.partial|operation|components))?$ ]]; then
                printf 'Unexpected managed path: %s\n' "$target" >&2; return 1
            fi
            ;;
    esac
    if [[ "$target" != /* || "$target" == / || "$(realpath -ms -- "$target")" != "$target" ]]; then
        printf 'Non-canonical target: %s\n' "$target" >&2; return 1
    fi
    parent="$(dirname -- "$target")" || return 1
    if [[ "$(realpath -e -- "$parent")" != "$parent" || -L "$target" ]]; then
        printf 'Symlink/junction in target path: %s\n' "$target" >&2; return 1
    fi
    if [[ -e "$target" ]]; then
        canonical="$(realpath -e -- "$target")" || return 1
        if [[ "$canonical" != "$target" ]]; then
            printf 'Resolved path escaped: %s\n' "$target" >&2; return 1
        fi
    fi
}

safe_move() {
    local source="$1" destination="$2" source_device destination_device
    assert_managed_path "$source" || return 1
    assert_managed_path "$destination" || return 1
    if [[ ! -e "$source" || -e "$destination" || -L "$destination" ]]; then
        printf 'Move needs an existing source and unused destination: %s -> %s\n' "$source" "$destination" >&2; return 1
    fi
    source_device="$(stat -c %d -- "$source")" || return 1
    destination_device="$(stat -c %d -- "$(dirname -- "$destination")")" || return 1
    if [[ "$source_device" != "$destination_device" ]]; then
        printf 'Atomic rename would cross filesystems: %s -> %s\n' "$source" "$destination" >&2; return 1
    fi
    mv -T -- "$source" "$destination"
}

inactive() {
    [[ "$(systemctl show jellyfin -p ActiveState --value)" == inactive ]]
}

set_file_permissions() {
    local target="$1" mode="$2"
    assert_managed_path "$target" || return 1
    [[ -f "$target" && ! -L "$target" ]] || return 1
    chown -h "$OWNER_USER:$OWNER_GROUP" -- "$target" || return 1
    chmod "$mode" -- "$target" || return 1
}

acquire_install_lock() {
    assert_managed_path "$LOCK"
    if [[ ! -e "$LOCK" ]]; then
        # Create only a missing lock, without replacing a racing installer's file.
        if ! (set -o noclobber; : >"$LOCK") 2>/dev/null; then
            [[ -e "$LOCK" ]] || die "Cannot create installer lock: $LOCK"
        fi
    fi
    assert_managed_path "$LOCK"
    [[ -f "$LOCK" && ! -L "$LOCK" ]] || die "Expected a regular installer lock file: $LOCK"
    case "$(stat -c %u -- "$LOCK")" in
        0|"$owner_uid") ;;
        *) die "Unexpected lock file owner" ;;
    esac
    # Retained locks belong to the installation owner. O_CREAT can be denied even
    # to root in sticky /run/lock; flock supports an exclusive read-only descriptor.
    # Keep the same file/inode so running installers continue to share one lock.
    if ! { exec 9<"$LOCK"; }; then die "Cannot read installer lock: $LOCK"; fi
    flock -n 9 || die "Another Jellyfin installation/rollback is running"
    set_file_permissions "$LOCK" 660
}

set_tree_permissions() {
    local target="$1" directory_mode="$2" file_mode="$3" preserve_exec="${4:-false}"
    assert_managed_path "$target" || return 1
    [[ -d "$target" && ! -L "$target" ]] || return 1
    chown -hR "$OWNER_USER:$OWNER_GROUP" -- "$target" || return 1
    find "$target" -type d -exec chmod "$directory_mode" {} + || return 1
    if [[ "$preserve_exec" == true ]]; then
        # Executables keep x throughout repair, including while Jellyfin is running.
        find "$target" -type f -perm /111 -exec chmod "$directory_mode" {} + || return 1
        find "$target" -type f ! -perm /111 -exec chmod "$file_mode" {} + || return 1
    else
        find "$target" -type f -exec chmod "$file_mode" {} + || return 1
    fi
}

set_app_permissions() {
    local target="$1" executable
    set_tree_permissions "$target" 775 664 true || return 1
    for executable in jellyfin createdump; do
        if [[ -f "$target/$executable" && ! -L "$target/$executable" ]]; then
            chmod 775 -- "$target/$executable" || return 1
        fi
    done
}

set_state_permissions() {
    local item
    set_tree_permissions "$CONFIG" 770 660 true || return 1
    set_file_permissions "$DEFAULTS" 660 || return 1
    for item in "$DATA" "$DATA/data"; do
        assert_managed_path "$item" || return 1
        [[ -d "$item" ]] || return 1
        chown -h "$OWNER_USER:$OWNER_GROUP" -- "$item" || return 1
        chmod 770 -- "$item" || return 1
    done
    # Only flat database files are touched; media, images and metadata are excluded.
    collect_database_files false | while IFS= read -r -d '' item; do
        set_file_permissions "/$item" 660 || return 1
    done || return 1
}

fix_permissions() {
    local item
    for item in "$BIN" "$WEB" "$BIN".new-* "$BIN".previous-* "$BIN".failed-* \
                "$WEB".new-* "$WEB".previous-* "$WEB".failed-*; do
        [[ -e "$item" || -L "$item" ]] || continue
        set_app_permissions "$item" || return 1
    done
    set_state_permissions || return 1
    if [[ -e "$BACKUPS" || -L "$BACKUPS" ]]; then
        set_tree_permissions "$BACKUPS" 770 660 || return 1
    fi
}

set_retained_permissions() {
    local item
    for item in "${source_bin:-}" "${source_web:-}" "${old_bin:-}" "${old_web:-}" "${recover_bin:-}" "${recover_web:-}"; do
        if [[ -n "$item" && -d "$item" && ! -L "$item" ]]; then
            set_app_permissions "$item" || return 1
        fi
    done
    if [[ -n "${backup_dir:-}" && -d "$backup_dir" && ! -L "$backup_dir" ]]; then
        set_tree_permissions "$backup_dir" 770 660 || return 1
    fi
}

collect_database_files() {
    local require_main="$1" directory="$DATA/data" file base nullglob_set=false
    local -a candidates=()
    assert_managed_path "$directory" || return 1
    [[ -d "$directory" ]] || { printf 'Missing database directory: %s\n' "$directory" >&2; return 1; }
    if [[ "$require_main" == true && ( ! -s "$directory/jellyfin.db" || -L "$directory/jellyfin.db" ) ]]; then
        printf 'Missing regular Jellyfin database: %s/jellyfin.db\n' "$directory" >&2; return 1
    fi
    if shopt -q nullglob; then nullglob_set=true; fi
    shopt -s nullglob
    candidates=("$directory"/*.db "$directory"/*.db-wal "$directory"/*.db-shm "$directory"/*.db-journal)
    if [[ "$nullglob_set" == false ]]; then shopt -u nullglob; fi
    for file in "${candidates[@]}"; do
        base="$file"
        case "$file" in
            *.db-wal) base="${file%-wal}" ;;
            *.db-shm) base="${file%-shm}" ;;
            *.db-journal) base="${file%-journal}" ;;
        esac
        if [[ "$require_main" == true && ! -f "$base" ]]; then continue; fi
        assert_managed_path "$file" || return 1
        [[ -f "$file" && ! -L "$file" ]] || { printf 'Refusing non-regular database: %s\n' "$file" >&2; return 1; }
        # Empty sidecars have no state to restore; omit them from snapshots.
        if [[ "$require_main" == true && ! -s "$file" ]]; then continue; fi
        printf '%s\0' "${file#/}"
    done
}

backup_state() {
    local database_list="$backup_dir/database-files.list"
    local -a database_files=()
    assert_managed_path "$backup_dir/state.tar.partial"
    inactive || die "Refusing to back up a running service"
    assert_managed_path "$database_list"
    collect_database_files true > "$database_list"
    mapfile -d '' -t database_files < "$database_list"
    tar --acls --xattrs --numeric-owner -C / -cpf "$backup_dir/state.tar.partial" \
        "${CONFIG#/}" "${DEFAULTS#/}" "${database_files[@]}"
    tar -tf "$backup_dir/state.tar.partial" >/dev/null
    safe_move "$backup_dir/state.tar.partial" "$backup_dir/state.tar"
    local digest
    digest="$(sha256sum -- "$backup_dir/state.tar")"
    printf '%s  state.tar\n' "${digest%% *}" > "$backup_dir/state.tar.sha256"
    validate_state_backup "$backup_dir/state.tar"
    set_tree_permissions "$backup_dir" 770 660
    backup_complete=true
}

restore_state() {
    local archive="$1" suffix="$2" item database_list="$backup_dir/database-files-restore.list"
    local -a database_files=()
    validate_state_backup "$archive" || return 1
    for item in "$CONFIG" "$DEFAULTS"; do
        assert_managed_path "$item" || return 1
        assert_managed_path "$item.$suffix" || return 1
        if [[ -e "$item" ]]; then
            safe_move "$item" "$item.$suffix" || return 1
            if [[ -d "$item.$suffix" ]]; then
                set_tree_permissions "$item.$suffix" 770 660 true || return 1
            else
                set_file_permissions "$item.$suffix" 660 || return 1
            fi
        fi
    done
    # Move only current databases/sidecars aside. Images and other DATA contents stay in place.
    assert_managed_path "$database_list" || return 1
    collect_database_files false > "$database_list" || return 1
    set_file_permissions "$database_list" 660 || return 1
    mapfile -d '' -t database_files < "$database_list" || return 1
    for item in "${database_files[@]}"; do
        safe_move "/$item" "/$item.$suffix" || return 1
        set_file_permissions "/$item.$suffix" 660 || return 1
    done
    tar --acls --xattrs --numeric-owner -C / -xpf "$archive" || return 1
    set_state_permissions || return 1
}

wait_ready() {
    local deadline=$((SECONDS + health_timeout)) body status
    while (( SECONDS < deadline )); do
        status="$(systemctl show jellyfin -p ActiveState --value)" || return 1
        [[ "$status" != failed ]] || return 1
        if [[ "$status" == active ]] &&
           body="$(curl --fail --silent --show-error --connect-timeout 2 --max-time 5 "$health_url" 2>/dev/null)" &&
           [[ "$body" == Healthy ]]; then
            return 0
        fi
        sleep 3
    done
    return 1
}

recover() {
    printf 'Deployment failed; restoring the state from before this operation.\n' >&2
    systemctl stop jellyfin || return 1
    inactive || return 1
    # Existence checks also cover a signal between an atomic rename and its next shell line.
    if [[ "$update_server" == true && -d "$old_bin" ]]; then
        if [[ -e "$BIN" ]]; then safe_move "$BIN" "$recover_bin" || return 1; fi
        safe_move "$old_bin" "$BIN" || return 1
    fi
    if [[ "$update_web" == true && -d "$old_web" ]]; then
        if [[ -e "$WEB" ]]; then safe_move "$WEB" "$recover_web" || return 1; fi
        safe_move "$old_web" "$WEB" || return 1
    fi
    if [[ "$update_server" == true && ( "$new_may_have_run" == true || "$state_changed" == true ) ]]; then
        [[ "$backup_complete" == true ]] || return 1
        restore_state "$backup_dir/state.tar" "$recover_state_suffix" || return 1
    fi
    if [[ "$update_server" == true ]]; then set_app_permissions "$BIN" || return 1; fi
    if [[ "$update_web" == true ]]; then set_app_permissions "$WEB" || return 1; fi
    if [[ "$was_active" == true ]]; then
        systemctl start jellyfin || return 1
        systemctl is-active --quiet jellyfin || return 1
    fi
    printf 'Previous selected components and required data state restored. Backups retained: %s\n' "$backup_dir" >&2
}

on_exit() {
    local result=$? marker=
    trap - EXIT INT TERM
    if (( result != 0 )) && [[ "$stopped" == true ]]; then
        set +e
        if [[ "$mode" == install ]]; then marker="$backup_dir/install-success";
        else marker="$rollback_backup/rolled-back"; fi
        if [[ -e "$marker" ]]; then
            if assert_managed_path "$marker"; then
                rm -f -- "$marker" || printf 'Could not remove failed-operation marker: %s\n' "$marker" >&2
            fi
        fi
        if ! recover; then
            systemctl stop jellyfin >/dev/null 2>&1 || true
            printf 'AUTOMATIC RECOVERY INCOMPLETE. Keep Jellyfin stopped and inspect the retained components.\n' >&2
            if [[ "$update_server" == true && "$backup_complete" == true ]]; then
                printf 'Configuration/database snapshot: %s/state.tar\n' "$backup_dir" >&2
            fi
            printf 'Retained binary/web paths: %s ; %s\n' "$old_bin" "$old_web" >&2
        fi
    fi
    if (( result != 0 )); then
        # Mandatory normalization runs before success; partial failures get best-effort cleanup.
        set_retained_permissions || printf 'Could not set permissions on all retained output.\n' >&2
    fi
    exit "$result"
}

write_backup_components() {
    local manifest="$backup_dir/components"
    assert_managed_path "$manifest"
    {
        if [[ "$update_server" == true ]]; then printf 'server\n'; fi
        if [[ "$update_web" == true ]]; then printf 'web\n'; fi
    } > "$manifest"
    set_file_permissions "$manifest" 660
}

read_backup_components() {
    local manifest="$1/components" component
    update_server=false; update_web=false
    assert_managed_path "$manifest" || return 1
    if [[ ! -e "$manifest" ]]; then
        # Installations made before component manifests always replaced both trees.
        update_server=true; update_web=true
        return
    fi
    [[ -f "$manifest" && ! -L "$manifest" ]] || return 1
    while IFS= read -r component || [[ -n "$component" ]]; do
        case "$component" in
            server) [[ "$update_server" == false ]] || return 1; update_server=true ;;
            web) [[ "$update_web" == false ]] || return 1; update_web=true ;;
            *) printf 'Invalid backup component: %s\n' "$component" >&2; return 1 ;;
        esac
    done < "$manifest"
    [[ "$update_server" == true || "$update_web" == true ]]
}

rollback_available() {
    local directory="$1" release_id="${1##*/}"
    read_backup_components "$directory" || return 1
    if [[ "$update_server" == true ]]; then
        [[ -s "$directory/state.tar" && -d "$BIN.previous-$release_id" &&
           -s "$BIN.previous-$release_id/jellyfin" && -s "$BIN.previous-$release_id/jellyfin.dll" ]] || return 1
    fi
    if [[ "$update_web" == true ]]; then
        [[ -d "$WEB.previous-$release_id" && -s "$WEB.previous-$release_id/index.html" ]] || return 1
    fi
}

select_rollback() {
    local listing line candidate
    if [[ -n "$rollback_id" ]]; then return; fi
    [[ -d "$BACKUPS" && ! -L "$BACKUPS" ]] || die "No installation backups found"
    listing="$(find "$BACKUPS" -mindepth 2 -maxdepth 2 -type f -name install-success -printf '%T@ %h\n' | sort -nr)"
    while IFS= read -r line; do
        candidate="${line#* }"
        if [[ -n "$line" && ! -e "$candidate/rolled-back" ]] && rollback_available "$candidate" 2>/dev/null; then
            rollback_id="${candidate##*/}"
            return
        fi
    done <<< "$listing"
    die "No successful installation backup is available for rollback"
}

main() {
    local script_dir validation item command
    local -a archives=()
    while [[ $# -gt 0 ]]; do
        case "$1" in
            -h|--help) usage; return ;;
            --check) check_only=true; shift ;;
            --rollback)
                [[ "$mode" == install ]] || die "Choose only one operation"
                mode=rollback; shift
                if [[ $# -gt 0 && "$1" != --* ]]; then rollback_id="$1"; shift; fi
                ;;
            --fix-permissions)
                [[ "$mode" == install ]] || die "Choose only one operation"
                mode=permissions; shift ;;
            --health-url) require_value "$@"; health_url="$2"; shift 2 ;;
            --health-timeout) require_value "$@"; health_timeout="$2"; shift 2 ;;
            --*) die "Unknown option: $1" ;;
            *) archives+=("$1"); shift ;;
        esac
    done
    export PATH=/usr/sbin:/usr/bin:/sbin:/bin
    [[ "$(uname -s)" == Linux && "$(uname -m)" == x86_64 ]] ||
        die "This installer requires Ubuntu 24.04 amd64; --help also works elsewhere"
    # /etc/os-release is the trusted operating-system identity, not archive metadata.
    . /etc/os-release
    [[ "$ID" == ubuntu && "$VERSION_ID" == 24.04 ]] || die "Only the verified Ubuntu 24.04 installation is supported"
    for command in "$python_bin" tar sha256sum realpath stat find sort od tr cp mv rm mkdir chmod chown id getent curl systemctl flock date sleep; do
        command -v "$command" >/dev/null || die "Missing prerequisite: $command (nothing is installed automatically)"
    done
    [[ "$(tar --version)" == *"GNU tar"* ]] || die "GNU tar is required"
    owner_uid="$(id -u "$OWNER_USER")" || die "Missing owner account: $OWNER_USER"
    getent group "$OWNER_GROUP" >/dev/null || die "Missing owner group: $OWNER_GROUP"
    [[ "$health_timeout" =~ ^[0-9]{1,4}$ ]] || die "Invalid health timeout"
    health_timeout=$((10#$health_timeout))
    (( health_timeout >= 10 && health_timeout <= 3600 )) || die "Health timeout must be 10-3600 seconds"
    [[ "$health_url" =~ ^https?://(127\.0\.0\.1|localhost|\[::1\])(:[0-9]+)?(/[^[:space:]]*)?$ ]] ||
        die "Health URL must use HTTP(S) on loopback, without whitespace"
    if [[ "$check_only" != true ]]; then (( EUID == 0 )) || die "Run install/rollback/permission repair with sudo"; fi
    for item in "$BIN" "$WEB" "$CONFIG" "$DEFAULTS" "$DATA" "$CACHE" "$LOG"; do
        assert_managed_path "$item"
        [[ -e "$item" ]] || die "Existing installation path is missing: $item"
    done
    for item in "$BIN" "$WEB" "$CONFIG" "$DATA" "$CACHE" "$LOG"; do [[ -d "$item" ]] || die "Expected directory: $item"; done
    [[ -f "$DEFAULTS" ]] || die "Expected defaults file: $DEFAULTS"
    [[ "$(systemctl show jellyfin -p LoadState --value)" == loaded ]] || die "jellyfin.service is not loaded"
    validate_service_paths "$BIN" "$WEB" "$CONFIG" "$DATA" "$CACHE" "$LOG" ||
        die "Service paths do not resolve to the expected /opt installation; no changes made"
    if [[ "$mode" == permissions ]]; then
        (( ${#archives[@]} == 0 )) || die "Permission repair does not accept archive paths"
    elif [[ "$mode" == install ]]; then
        script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
        select_archives "$script_dir" "${archives[@]}"
        validation="$(validate_archives "$server_archive" "$web_archive")"
        printf 'Validated archives: %s\n' "$validation"
    else
        (( ${#archives[@]} == 0 )) || die "Rollback does not accept archive paths"
        select_rollback
        [[ "$rollback_id" =~ ^$ID_PATTERN$ ]] || die "Invalid rollback release ID"
        rollback_backup="$BACKUPS/$rollback_id"
        assert_managed_path "$rollback_backup"
        [[ -f "$rollback_backup/install-success" && ! -e "$rollback_backup/rolled-back" ]] ||
            die "The selected release is not an unused successful installation backup"
        rollback_available "$rollback_backup" || die "Missing or invalid rollback components"
        source_bin=; source_web=
        if [[ "$update_server" == true ]]; then source_bin="$BIN.previous-$rollback_id"; fi
        if [[ "$update_web" == true ]]; then source_web="$WEB.previous-$rollback_id"; fi
        for item in "$source_bin" "$source_web"; do
            [[ -n "$item" ]] || continue
            assert_managed_path "$item"
            [[ -d "$item" ]] || die "Missing rollback directory: $item"
        done
        if [[ "$update_server" == true ]]; then validate_state_backup "$rollback_backup/state.tar"; fi
    fi
    printf 'Plan: %s bin=%s web=%s; preserve service settings, FFmpeg and libjemalloc; health=%s (timeout %ss).\n' \
        "$mode" "$BIN" "$WEB" "$health_url" "$health_timeout"
    printf 'Ownership: alien21:plex; app/web directories and executables 775, files 664; private state/backups 770/660.\n'
    if [[ "$mode" == permissions ]]; then
        printf 'Repair current/retained app and web, configuration, flat databases and installer backups; no restart or archive extraction.\n'
    else
        printf 'Selected components: server=%s; web=%s. Unselected components stay in place.\n' "$update_server" "$update_web"
        if [[ "$update_server" == true ]]; then printf 'Snapshot configuration and active SQLite databases/sidecars for the server change.\n'; fi
        printf 'Stop jellyfin, replace only selected components, then start and verify health.\n'
    fi
    printf 'Images, metadata, SQLiteBackups, cache and logs are excluded; DATA is never moved as a directory.\n'
    if [[ "$check_only" == true ]]; then printf 'Check complete; no persistent changes made.\n'; return; fi

    umask 007
    acquire_install_lock
    if [[ "$mode" == permissions ]]; then
        fix_permissions
        printf 'Ownership and permissions repaired: alien21:plex. Jellyfin was not restarted.\n'
        return
    fi
    operation_id="$(date -u +%Y%m%dT%H%M%SZ)-$$"
    backup_dir="$BACKUPS/$operation_id"
    assert_managed_path "$BACKUPS"
    mkdir -p -m 770 -- "$BACKUPS"
    set_tree_permissions "$BACKUPS" 770 660
    assert_managed_path "$backup_dir"
    mkdir -m 770 -- "$backup_dir"
    set_tree_permissions "$backup_dir" 770 660
    old_bin=; old_web=; recover_bin=; recover_web=
    if [[ "$update_server" == true ]]; then old_bin="$BIN.previous-$operation_id"; recover_bin="$BIN.failed-$operation_id"; fi
    if [[ "$update_web" == true ]]; then old_web="$WEB.previous-$operation_id"; recover_web="$WEB.failed-$operation_id"; fi
    recover_state_suffix="failed-$operation_id"
    if [[ "$mode" == rollback ]]; then
        if [[ "$update_server" == true ]]; then old_bin="$BIN.failed-$operation_id"; fi
        if [[ "$update_web" == true ]]; then old_web="$WEB.failed-$operation_id"; fi
        recover_bin="$source_bin"; recover_web="$source_web"
        recover_state_suffix="restore-failed-$operation_id"
    else
        source_bin=; source_web=
        if [[ "$update_server" == true ]]; then source_bin="$BIN.new-$operation_id"; fi
        if [[ "$update_web" == true ]]; then source_web="$WEB.new-$operation_id"; fi
    fi
    for item in "$source_bin" "$source_web" "$old_bin" "$old_web" "$recover_bin" "$recover_web"; do
        [[ -n "$item" ]] || continue
        assert_managed_path "$item"
    done
    write_backup_components
    trap on_exit EXIT
    trap 'exit 130' INT
    trap 'exit 143' TERM
    if [[ "$mode" == install ]]; then
        if [[ "$update_server" == true ]]; then
            mkdir -m 770 -- "$source_bin"
            chown -h "$OWNER_USER:$OWNER_GROUP" -- "$source_bin"
            tar --no-same-owner --no-same-permissions --strip-components=1 -xzf "$server_archive" -C "$source_bin"
        fi
        if [[ "$update_web" == true ]]; then
            mkdir -m 770 -- "$source_web"
            chown -h "$OWNER_USER:$OWNER_GROUP" -- "$source_web"
            tar --no-same-owner --no-same-permissions --strip-components=1 -xzf "$web_archive" -C "$source_web"
            if [[ -f "$WEB/config.json" ]]; then cp -L --preserve=timestamps -- "$WEB/config.json" "$source_web/config.json"; fi
        fi
        for item in "$source_bin" "$source_web"; do
            [[ -n "$item" ]] || continue
            [[ -z "$(find "$item" -type l -print -quit)" ]] || die "Unexpected staged symlink"
            assert_managed_path "$item"
        done
    fi
    if [[ "$update_server" == true ]]; then set_app_permissions "$source_bin"; fi
    if [[ "$update_web" == true ]]; then set_app_permissions "$source_web"; fi
    if systemctl is-active --quiet jellyfin; then was_active=true; fi
    stopped=true
    systemctl stop jellyfin
    inactive || die "Service did not become inactive"
    if [[ "$update_server" == true ]]; then
        backup_state
        safe_move "$BIN" "$old_bin"
        safe_move "$source_bin" "$BIN"
    fi
    if [[ "$update_web" == true ]]; then
        safe_move "$WEB" "$old_web"
        safe_move "$source_web" "$WEB"
    fi
    if [[ "$mode" == rollback && "$update_server" == true ]]; then
        state_changed=true
        restore_state "$rollback_backup/state.tar" "failed-$operation_id"
    fi
    if [[ "$update_server" == true ]]; then
        [[ "$backup_complete" == true && -d "$old_bin" && -d "$BIN" ]] || die "Refusing start without a server backup and swap"
    fi
    if [[ "$update_web" == true ]]; then
        [[ -d "$old_web" && -d "$WEB" ]] || die "Refusing start without a web backup and swap"
    fi
    new_may_have_run=true
    systemctl start jellyfin
    wait_ready || die "Service/HTTP readiness failed within $health_timeout seconds"
    set_retained_permissions
    printf '%s\n' "$mode" > "$backup_dir/operation"
    set_file_permissions "$backup_dir/operation" 660
    if [[ "$mode" == install ]]; then
        assert_managed_path "$backup_dir/install-success.partial"
        printf '%s\n' "$operation_id" > "$backup_dir/install-success.partial"
        set_file_permissions "$backup_dir/install-success.partial" 660
        safe_move "$backup_dir/install-success.partial" "$backup_dir/install-success"
    else
        assert_managed_path "$rollback_backup/rolled-back.partial"
        printf '%s\n' "$operation_id" > "$rollback_backup/rolled-back.partial"
        set_file_permissions "$rollback_backup/rolled-back.partial" 660
        safe_move "$rollback_backup/rolled-back.partial" "$rollback_backup/rolled-back"
    fi
    printf 'Completed %s. Release ID: %s\nBackup record: %s\n' "$mode" "$operation_id" "$backup_dir" || true
    if [[ "$update_server" == true ]]; then printf 'Server backup: %s\nState snapshot: %s/state.tar\n' "$old_bin" "$backup_dir" || true; fi
    if [[ "$update_web" == true ]]; then printf 'Web backup: %s\n' "$old_web" || true; fi
}

# Sourcing exposes only validation/functions for offline archive QA; no deployment runs.
if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then main "$@"; fi
