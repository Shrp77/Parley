#!/usr/bin/env bash
#
# Clears this machine's Parley so the next run starts from nothing.
#
# A deployment is one root holding projects/<name>/. Each project folder owns its project.json, its
# parley.db and that store's companions (-wal, -shm, .lock, .owner, .files, .files-incoming), and its
# port is recorded in ports.json beside them. Beside the projects the root holds node.json, this
# machine's catalogue of which checkouts may be started.
#
# The counterpart of `reset.ps1`. Behaviour is the same; what differs is how a shell does it.
#
# What it does, in order:
#
#   1. Stops the broker and removes the autostart entry, because `StoreLock` holds each store with
#      no sharing and a running broker cannot have its files deleted underneath it.
#   2. Removes parley-broker.log from the root.
#   3. With --everything, removes the whole root: every project, every store, node.json and
#      ports.json. Everything a project owns is inside its folder, so there is no list of paths to
#      keep in step.
#   4. With --repositories, takes the Parley launch entries out of each checkout's .mcp.json.
#
# What it KEEPS without --everything: node.json, ports.json and projects/. Each is named in the
# output.
#
#   ./reset.sh --dry-run          say what would be removed and remove nothing; run this first
#   ./reset.sh --root <path>      where Parley keeps this machine's files
#   ./reset.sh --force            do not ask before deleting
#   ./reset.sh --files-only       delete files and touch nothing else: no autostart entry, no
#                                 running process. **This is what a test or a dry run against a
#                                 scratch directory must pass.** `parley autostart remove` acts on
#                                 this machine whatever --root says, so without it a run pointed at
#                                 a throwaway directory still takes down a real logon entry.
#   ./reset.sh --everything       leave nothing at all: the whole root goes, including node.json,
#                                 ports.json and every project. A reset that cannot remove what the
#                                 last run created works exactly once.
#   ./reset.sh --repositories <dir>
#                                 a directory of checkouts. Every `parley` and `parley-<project>`
#                                 key is taken out of each repository's .mcp.json, and a file left
#                                 with no servers is deleted. Here because the launch configuration
#                                 is the one piece of a deployment that does not live under the
#                                 root, so a wipe that ignored it would leave every repository
#                                 pointing at a broker that is gone.

set -uo pipefail

root=""
force=0
files_only=0
everything=0
dry_run=0
repositories=""

while [ $# -gt 0 ]; do
    case "$1" in
        --root) root="${2:-}"; shift 2 ;;
        --root=*) root="${1#--root=}"; shift ;;
        --repositories) repositories="${2:-}"; shift 2 ;;
        --repositories=*) repositories="${1#--repositories=}"; shift ;;
        --force) force=1; shift ;;
        --files-only) files_only=1; shift ;;
        --everything) everything=1; shift ;;
        --dry-run|--what-if) dry_run=1; shift ;;
        -h|--help)
            awk 'NR>1 && /^#/ { sub(/^# ?/, ""); print; next } NR>1 { exit }' "${BASH_SOURCE[0]}"
            exit 0
            ;;
        *)
            echo "Unknown argument: $1" >&2
            echo "Usage: ./reset.sh [--root <path>] [--repositories <dir>] [--dry-run] [--force] [--files-only] [--everything]" >&2
            exit 64
            ;;
    esac
done

step() { printf '==> %s\n' "$1"; }
note() { printf '    %s\n' "$1"; }

# **Asked of the tool before it is guessed at.** `parley project list` prints a `root:` line, so on a
# machine with Parley installed this is the root the tools themselves resolve rather than this
# script's idea of the platform's convention. The fallback is what `ProjectManifest.DefaultPathFrom`
# computes on Unix -- XDG_CONFIG_HOME, else $HOME/.config -- and it is a fallback rather than the
# rule because a guess that disagrees with the tools deletes the wrong directory in silence.
if [ -z "$root" ]; then
    if command -v parley >/dev/null 2>&1; then
        root="$(parley project list 2>/dev/null | sed -n 's/^root:[[:space:]]*//p' | tail -n 1)"
    fi
fi

if [ -z "$root" ]; then
    root="${XDG_CONFIG_HOME:-$HOME/.config}/Parley"
fi

step "Root: $root"

if [ "$dry_run" -eq 1 ]; then
    note 'dry run: nothing will be deleted'
fi

# Removes a path, or says it would. One place, so that --dry-run cannot cover some deletions and
# miss others -- which is the way a dry run comes to be trusted and be wrong.
remove() {
    local path="$1"

    [ -e "$path" ] || return 0

    if [ "$dry_run" -eq 1 ]; then
        note "would remove $path"
        return 0
    fi

    rm -rf -- "$path" && note "removed $path"
}

# **Not an early exit when there are repositories to clear**, which is the second iteration of the
# loop this script exists for: the root was removed last time and the launch entries it wrote are
# still sitting in every checkout. Exiting here because the root is absent would report a clean
# machine while leaving each repository pointing at a broker that no longer exists.
if [ ! -d "$root" ] && [ -z "$repositories" ]; then
    echo "Nothing to reset: $root does not exist."
    exit 0
fi

# ---- 1. stop the broker ---------------------------------------------------------------------

if [ "$files_only" -eq 1 ]; then
    step 'Files only: leaving the autostart entry and any running broker alone'
else
    step 'Stopping the broker and removing the autostart entry'

    if command -v parley >/dev/null 2>&1; then
        if [ "$dry_run" -eq 1 ]; then
            note 'would run `parley autostart remove`'
        else
            # Best effort: an entry that is not installed is not a failure. What matters is that
            # nothing is holding a store afterwards.
            parley autostart remove 2>&1 | while IFS= read -r line; do note "$line"; done
        fi
    else
        note '`parley` is not on PATH; skipping the autostart entry.'
    fi

    # **Every tool, not just the broker.** `parley-web-ui` is started as a child the broker does not
    # wait on, so ending the broker alone leaves it holding its port -- and the next `autostart
    # install` then reports a web UI that could not bind, on a machine where nothing is using it.
    # `parley-terminal` is included because a terminal left open holds a view of a deployment that
    # is about to stop existing.
    for name in parley parley-web-ui parley-terminal; do
        for pid in $(pgrep -x "$name" 2>/dev/null || true); do
            if [ "$dry_run" -eq 1 ]; then
                note "would stop $name (pid $pid)"
            else
                note "stopping $name (pid $pid)"
                kill "$pid" 2>/dev/null || true
            fi
        done
    done
fi

# **JSON is read with a real parser or not at all.** An .mcp.json is somebody's file and its shape is
# not this script's to guess. Rewriting one needs python3; where it is missing, launch entries are
# left alone and said so.
reader=""

if command -v python3 >/dev/null 2>&1; then
    reader="python3"
fi

# ---- 2. confirm ------------------------------------------------------------------------------

projects=()

for folder in "$root"/projects/*/; do
    [ -d "$folder" ] && projects+=("$folder")
done

if [ "$everything" -eq 1 ] && [ "${#projects[@]}" -gt 0 ]; then
    step "${#projects[@]} project(s) under $root/projects"

    for folder in "${projects[@]}"; do
        note "$(basename "$folder")"
    done

    if [ "$force" -eq 0 ] && [ "$dry_run" -eq 0 ]; then
        echo
        echo 'These folders hold every conversation in those projects. Nothing here is recoverable.'
        printf 'Type the word delete to remove them: '
        read -r answer

        if [ "$answer" != "delete" ]; then
            echo 'Nothing was deleted.'
            exit 0
        fi
    fi
fi

# ---- 3. the broker log -----------------------------------------------------------------------

remove "$root/parley-broker.log"

# ---- 4. the root -----------------------------------------------------------------------------
#
# Only under --everything, because clearing the projects destroys a working deployment. Everything a
# project owns lives inside its own folder under the root -- its parley.db, that store's companions,
# and the file location beside it -- so the root going is the whole of it, and there is no second
# list of paths to keep in step.

if [ "$everything" -eq 1 ]; then
    step 'Everything: removing the whole root'

    for folder in "${projects[@]+"${projects[@]}"}"; do
        note "removing project $(basename "$folder")"
    done

    remove "$root"
else
    for kept in node.json ports.json projects; do
        [ -e "$root/$kept" ] && note "keeping $root/$kept"
    done
fi

# ---- 5. the launch configurations, which are the one thing outside the root -------------------

if [ -n "$repositories" ]; then
    step "Clearing Parley launch entries under $repositories"

    if [ ! -d "$repositories" ]; then
        note "$repositories is not a directory; nothing to clear."
    elif [ "$reader" != "python3" ]; then
        # **Rewritten with a real parser or not at all.** An .mcp.json routinely holds servers that
        # have nothing to do with Parley, and editing somebody else's file with sed is how a reset
        # eats an editor's settings. jq could do it; python3 is required here because the rewrite
        # has to preserve everything it does not own, and one implementation is easier to hold to
        # that than two.
        note 'python3 is not here, so launch configurations are left alone.'
        note 'Remove the `parley` and `parley-<project>` keys from each .mcp.json by hand.'
    else
        cleared=0

        for repository in "$repositories"/*/; do
            launch="$repository.mcp.json"

            [ -f "$launch" ] || continue

            if [ "$dry_run" -eq 1 ]; then
                if python3 -c '
import json, sys
try:
    d = json.load(open(sys.argv[1], encoding="utf-8-sig"))
except Exception:
    sys.exit(1)
servers = d.get("mcpServers") or {}
sys.exit(0 if [k for k in servers if k == "parley" or k.startswith("parley-")] else 1)
' "$launch" 2>/dev/null; then
                    note "would clear $(basename "$repository"): $launch"
                    cleared=$((cleared + 1))
                fi

                continue
            fi

            said="$(python3 - "$launch" <<'CLEAR'
import json, os, sys

path = sys.argv[1]

try:
    with open(path, encoding="utf-8-sig") as handle:
        document = json.load(handle)
except Exception:
    # Left alone rather than rewritten. A file this cannot parse is one somebody else owns the
    # shape of, and guessing at it is how a reset eats an editor's settings.
    #
    # **Said, and not counted.** The first version printed this and the caller counted every
    # message, so a run that deliberately touched nothing reported "3 repositories cleared" with
    # one of the three left exactly as it was. Found by running it.
    print("left\tnot JSON; left alone")
    sys.exit(0)

servers = document.get("mcpServers")

if not isinstance(servers, dict):
    sys.exit(0)

# `parley` and `parley-<project>` are the keys this tooling owns, per LaunchKey. Nothing else is
# touched.
ours = [key for key in servers if key == "parley" or key.startswith("parley-")]

if not ours:
    sys.exit(0)

for key in ours:
    del servers[key]

if not servers and len(document) == 1:
    os.remove(path)
    print("cleared	removed .mcp.json (nothing else in it)")
else:
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(document, handle, indent=2)
        handle.write("\n")

    print("cleared	removed %s" % ", ".join(ours))
CLEAR
)"

            # Split on the marker: only a rewrite counts. A file left alone is still reported,
            # because silence about a repository this walked past is worse than a line saying why.
            kind="${said%%	*}"
            message="${said#*	}"

            if [ -n "$said" ]; then
                note "$(basename "$repository"): $message"
                [ "$kind" = "cleared" ] && cleared=$((cleared + 1))
            fi
        done

        if [ "$cleared" -eq 1 ]; then
            note "1 repository cleared"
        else
            note "$cleared repositories cleared"
        fi
    fi
fi

echo
echo 'Done.'
echo
echo '  parley                           create one; it gets its own store and port'
echo '  parley autostart install         serve every project at logon'
