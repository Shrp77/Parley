#!/usr/bin/env bash
#
# Builds Parley from this checkout and installs its two tools.
#
# Parley is not on nuget.org, so installing it means building it. That is three commands per
# tool and two of them have a trap in, which is why this exists rather than a paragraph in the
# README:
#
#   * `dotnet tool update` does nothing at all when the version has not changed. It reports
#     success, at some length, and replaces nothing -- so a rebuilt tool appears to install and
#     does not, on the machine most likely to be running the old one.
#
#   * `--add-source <folder>` treats that folder as a feed, and bin/Release accumulates every
#     version ever packed there. "I installed from a clean state" is then true only by luck.
#
# This clears the stale packages, packs, installs, and then checks that the version now on PATH
# is the one that was just built. That last step is the point: it is the only way to tell a
# successful install from a silent no-op.
#
# It then registers the broker to start when you log in and checks that it answers. Installing
# the tools is not the same as having Parley working: the broker is a separate process that owns
# the store, and until one is running every agent fails to attach with "No broker is listening"
# -- a message that arrives in an editor, days later, rather than here.
#
#   ./install.sh                 build, install, and set the broker up
#   ./install.sh --store <path>  the store the broker owns; asked for if not given
#   ./install.sh --uninstall     remove both tools and the autostart entry, and stop

set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

uninstall=0
store=""

while [ $# -gt 0 ]; do
    case "$1" in
        --uninstall) uninstall=1; shift ;;
        # --db as well as --store, because `parley serve --db` is the flag this ends up
        # passing and the two being different words is a trap laid for nobody's benefit.
        --store|--db) store="${2:-}"; shift 2 ;;
        --store=*) store="${1#--store=}"; shift ;;
        --db=*) store="${1#--db=}"; shift ;;
        -h|--help) sed -n '2,25p' "${BASH_SOURCE[0]}" | sed 's/^# \\?//'; exit 0 ;;
        *)
            echo "Unknown argument: $1" >&2
            echo "Usage: ./install.sh [--store <path>] [--uninstall]" >&2
            exit 64
            ;;
    esac
done

packages=("Parley.Cli" "Parley.Mcp")
projects=("src/Parley.Cli/Parley.Cli.csproj" "src/Parley.Mcp/Parley.Mcp.csproj")
commands=("parley" "parley-mcp")

if [ -t 1 ]; then
    cyan=$'\033[36m'; grey=$'\033[90m'; green=$'\033[32m'; plain=$'\033[0m'
else
    cyan=""; grey=""; green=""; plain=""
fi

# What `parley autostart install` calls the unit or the launch agent. Duplicated from
# AutostartPlan.Name rather than asked for, because the one command that could report it is the
# one this script may be in the middle of deleting.
task="parley-broker"

# The command the broker runs as, so the pre-flight below can leave it out of the adapters and
# recognise it afterwards.
broker="parley"

step() { printf '%s==> %s%s\n' "$cyan" "$1" "$plain"; }
note() { printf '%s    %s%s\n' "$grey" "$1" "$plain"; }

# Stops the broker the autostart entry started.
#
# Needed by both paths. `autostart remove` deliberately leaves a running broker alone -- somebody
# rearranging their machine has not asked to end every attached agent's session -- but replacing
# or deleting the binary it is running is exactly that request.
#
# The failure it prevents is quiet. `dotnet tool uninstall` returns non-zero both for a tool it
# could not remove and for one that was never there; `dotnet tool install` then returns *zero*,
# prints "is already installed", and installs nothing. The pair reports success while leaving the
# previous build in place, and the version check afterwards cannot tell, because the version it
# finds is the version it expected.
#
# A broker somebody started by hand is left alone: ending a process a person launched themselves
# is their decision, and the checks downstream report honestly when it blocks them.
stop_broker() {
    if command -v systemctl >/dev/null 2>&1; then
        systemctl --user stop "$task.service" >/dev/null 2>&1 || true
    elif command -v launchctl >/dev/null 2>&1; then
        launchctl bootout "gui/$(id -u)/$task" >/dev/null 2>&1 || true
    fi
}

# Which of this repository's tools are installed globally.
#
# Asked of `dotnet tool list` rather than inferred from an exit code. `dotnet tool uninstall`
# returns non-zero both when a tool was absent and when it could not be removed, and treating
# those two as one is what let this script announce a clean uninstall with a tool still on PATH.
installed() {
    local listed
    listed="$(dotnet tool list -g 2>/dev/null || true)"

    for package in "${packages[@]}"; do
        if printf '%s\n' "$listed" | grep -qi "^$(printf '%s' "$package" | tr 'A-Z' 'a-z')[[:space:]]"; then
            printf '%s\n' "$package"
        fi
    done
}

# Where the broker's store goes: given on the command line, asked for otherwise, defaulted when
# there is nobody to ask.
#
# The default is under $HOME rather than beside this checkout, and that is why it is a question
# rather than a constant. The store outlives any one clone -- it holds every exchange the
# deployment has ever had -- so putting it inside a repository means `git clean` destroys the
# history of twenty agents, and putting it inside *this* one makes Parley's own working copy
# load-bearing for machines that do not have it.
resolve_store() {
    local default="$HOME/.parley/parley.db"

    if [ -n "$store" ]; then
        printf '%s\n' "$store"
        return
    fi

    # Prompting needs somebody to answer. No terminal on stdin means a script or a CI job is
    # running this, and `read` there returns nothing -- so take the default and say so, because
    # a silent choice of where a database lives is worse than a loud one.
    if [ ! -t 0 ]; then
        note "nothing to prompt (no terminal); using $default" >&2
        printf '%s\n' "$default"
        return
    fi

    {
        echo
        echo '  The broker owns one SQLite file, and every agent on this machine reaches it'
        echo '  through that one process. It belongs outside every repository, because it is'
        echo '  shared by all of them and outlives any one clone.'
        echo
    } >&2

    local answer
    read -r -p "  Store [$default]: " answer </dev/tty >&2 || answer=""

    printf '%s\n' "${answer:-$default}"
}

# ---------------------------------------------------------------- uninstall --

if [ "$uninstall" = "1" ]; then
    # The broker first, before anything deletes a file. `autostart remove` deliberately leaves a
    # running broker alone -- somebody rearranging their machine has not asked to end every
    # attached agent's session -- but this has asked for exactly that, because it is about to
    # delete the binary that broker is running.
    step "Stopping the broker"

    stop_broker

    # Anything left is a broker somebody started by hand, and this does not kill it. Ending a
    # process a person launched themselves is a decision for them; the check at the end reports
    # honestly either way.
    if pgrep -x parley >/dev/null 2>&1; then
        note "a broker is still running that no autostart entry started"
    fi

    # Then the entry, still before the tools, because it names an executable about to be deleted.
    # The other order leaves a unit or a launch agent pointing at a path that is no longer there,
    # which fails at the next logon, silently, somewhere nobody looks.
    step "Removing the autostart entry"

    if command -v parley >/dev/null 2>&1; then
        parley autostart remove >/dev/null 2>&1 || true
    else
        note "parley is not on PATH; nothing to remove it with"
    fi

    before="$(installed)"

    for package in "${packages[@]}"; do
        step "Removing $package"

        # Absent and unremovable are different, and reporting both as "was not installed" is what
        # turned a failed uninstall into a success message. Asked before acting, so the note can
        # say which of the two happened.
        if ! printf '%s\n' "$before" | grep -qx "$package"; then
            note "was not installed"
            continue
        fi

        # Failure is not fatal: stopping at the first would leave the second installed, and
        # somebody running this to clean up wants both attempted.
        dotnet tool uninstall -g "$package" >/dev/null 2>&1 || true
    done

    # The same check the install does, for the same reason. Every step above can report success
    # while leaving a tool exactly where it was, so the only honest closing line is one that
    # asked afterwards.
    remaining="$(installed)"

    if [ -n "$remaining" ]; then
        echo "Still installed: $(printf '%s' "$remaining" | tr '\n' ' ')" >&2
        echo "A running process is holding the files. Stop it and run this again." >&2
        exit 1
    fi

    echo
    echo "Both tools are gone, and so is the autostart entry. The store and any .mcp.json are"
    echo "left alone: those are the deployment rather than the installation."
    exit 0
fi

# ------------------------------------------------------------ prerequisites --

if ! command -v dotnet >/dev/null 2>&1; then
    echo "The .NET SDK is not on PATH. Parley builds from source, so it needs one:" >&2
    echo "  https://dotnet.microsoft.com/download" >&2
    exit 1
fi

step "Building with .NET SDK $(dotnet --version)"

# The floor is whatever global.json pins, and dotnet itself enforces that far better than a
# string comparison here would. Said only so that a failure below reads as a version problem
# rather than a mysterious build error.
note "global.json pins the SDK band; dotnet will say so if this one is outside it."

# ------------------------------------------------------------------- clear ---

step "Clearing previously packed versions"

for project in "${projects[@]}"; do
    output="$root/$(dirname "$project")/bin/Release"

    if [ -d "$output" ]; then
        stale=$(find "$output" -maxdepth 1 -name '*.nupkg' | wc -l | tr -d ' ')

        if [ "$stale" != "0" ]; then
            note "$stale old package(s) in $output"
            find "$output" -maxdepth 1 -name '*.nupkg' -delete
        fi
    fi
done

# -------------------------------------------------------------------- pack ---

for i in "${!packages[@]}"; do
    step "Packing ${packages[$i]}"

    if ! dotnet pack "$root/${projects[$i]}" -c Release --nologo -v quiet; then
        echo "Could not pack ${packages[$i]}." >&2
        exit 1
    fi
done

# The version every package just took, read from the packages rather than from the project file.
# What matters is what was produced, and a build that ignored the property would be exactly the
# defect this check should catch.
built=$(find "$root/src/Parley.Cli/bin/Release" -maxdepth 1 -name 'Parley.Cli.*.nupkg' \
        | head -1 | sed -E 's/.*Parley\.Cli\.(.*)\.nupkg/\1/')

if [ -z "$built" ]; then
    echo "Nothing was packed. The build produced no package." >&2
    exit 1
fi

# ----------------------------------------------------------------- install ---

# Before anything replaces a file. A live broker holds its binary, which makes the uninstall
# below fail and the install after it a no-op that reports success.
step "Stopping any running broker"

# Before anything is stopped, because the failure this catches used to arrive afterwards.
#
# A running `parley-mcp` holds its own files in the tool store, so `dotnet tool uninstall` fails
# on a denied path. That on its own would be a clear error and no great harm. The harm was the
# order: the broker was stopped first, so a machine that had been serving a deployment was left
# with no broker, one tool replaced and one not, by a script that had reported nothing but
# success up to that point.
#
# The broker itself is not checked here. It is stopped a few lines below, deliberately and by
# name, which is a different thing from a session somebody has open.
step "Checking nothing holds the tools"

# Derived from the command list above rather than named again here. A third tool, or a rename,
# would otherwise leave this checking something that no longer exists -- quietly, because a guard
# that matches nothing looks exactly like a machine with nothing running.
#
# The broker's own command is excluded: it is stopped a few lines below, deliberately and by name.
# One started by hand is a different matter and is caught after that, where it can be told apart.
holders=""

for cmd in "${commands[@]}"; do
    [ "$cmd" = "$broker" ] && continue

    found="$(pgrep -x "$cmd" 2>/dev/null || true)"
    [ -n "$found" ] && holders="$holders$found
"
done

holders="$(printf '%s' "$holders" | sed '/^$/d')"

if [ -n "$holders" ]; then
    count="$(printf '%s\n' "$holders" | wc -l | tr -d ' ')"

    echo "==> $count Parley adapter(s) are running and hold the files this would replace:" >&2
    printf '      pid %s\n' $holders >&2
    echo "    Each one belongs to an editor session with Parley configured. Close those" >&2
    echo "    sessions and run this again. Nothing has been changed and the broker has not" >&2
    echo "    been stopped." >&2

    exit 1
fi

note "no adapter is holding the tool store"

stop_broker

# After the stop, because until now a running broker was expected. What is left is one somebody
# started by hand, which stop_broker deliberately leaves alone -- and which holds the binary just
# as firmly. Caught here rather than by the uninstall, which would fail and leave the tools half
# replaced.
strays=""

for cmd in "${commands[@]}"; do
    found="$(pgrep -x "$cmd" 2>/dev/null || true)"
    [ -n "$found" ] && strays="$strays$cmd $found
"
done

if [ -n "$(printf '%s' "$strays" | sed '/^$/d')" ]; then
    echo "==> Something is still holding the tools after the broker was stopped:" >&2
    printf '      %s
' $strays >&2
    echo "    A broker started by hand is not stopped by this script, on purpose. Stop it and" >&2
    echo "    run this again. Nothing has been replaced." >&2

    exit 1
fi

step "Installing version $built"

for i in "${!packages[@]}"; do
    source_dir="$root/$(dirname "${projects[$i]}")/bin/Release"

    # Uninstall first rather than `update`, which is a no-op when the version has not moved --
    # and the version has not moved on every rebuild that fixed something without shipping new
    # behaviour. Removing first makes the install unconditional.
    dotnet tool uninstall -g "${packages[$i]}" >/dev/null 2>&1 || true

    if ! said="$(dotnet tool install -g --add-source "$source_dir" "${packages[$i]}" \
            --version "$built" 2>&1)"; then
        echo "Could not install ${packages[$i]}: $said" >&2
        exit 1
    fi

    # Zero does not mean installed. `dotnet tool install` exits 0 for a package that is already
    # there, says so, and replaces nothing -- so this message means the uninstall above failed
    # and PATH still holds the previous build. The version check below cannot catch it: the
    # version it finds is the version it expected.
    case "$said" in
        *"already installed"*)
            echo "${packages[$i]} was already installed and could not be replaced, so the build" >&2
            echo "just packed is not the one on PATH. Something is holding its files -- a broker," >&2
            echo "most likely -- and it has to stop before this can work." >&2
            exit 1
            ;;
    esac

    note "${commands[$i]} installed"
done

# ------------------------------------------------------------------ verify ---

step "Checking what is actually on PATH"

shims="$HOME/.dotnet/tools"

# Found on PATH first, and only then at the known location. That order matters on a shell where
# the shim has an extension this script cannot guess -- Git Bash on Windows installs parley.exe
# -- and it is also the honest check: what a person will run is whatever PATH resolves.
if command -v parley >/dev/null 2>&1; then
    parley_command="parley"
elif [ -x "$shims/parley" ]; then
    parley_command="$shims/parley"
    echo "warning: $shims is not on PATH. Add it, or the commands below will not be found." >&2
else
    echo "Installed, but 'parley' is not on PATH and is not at $shims." >&2
    echo "Add that directory to PATH and open a new terminal." >&2
    exit 1
fi

# The check the whole script exists for. An install that silently did nothing leaves the old
# version here, and every other step reports success.
reported="$("$parley_command" --version)"

case "$reported" in
    *"$built"*) ;;
    *)
        echo "Installed $built but PATH reports '$reported'. Something older is still there;" >&2
        echo "run this with --uninstall and try again." >&2
        exit 1
        ;;
esac

echo
printf '%s%s%s\n' "$green" "$reported" "$plain"

# ---------------------------------------------------------------- broker ----

store="$(resolve_store)"

# Asked before registering anything, and only because the check at the end cannot tell one
# broker from another. stop_broker has already stopped the one an autostart entry started, so
# anything answering now was started by hand -- and it holds the port, which means the entry
# about to be registered will not bind until that process goes. The final check would pass
# against it and report a store that nothing is actually serving.
foreign=0

if "$parley_command" registry >/dev/null 2>&1; then
    foreign=1
fi

step "Registering the broker to start at logon"

# No root here, and not by luck: this writes a systemd *user* unit or a per-user launch agent.
# The broker binds a loopback port and writes under the invoking user's own home, so it needs no
# privilege they do not already have, and asking for one would be asking for the tool's benefit.
if ! "$parley_command" autostart install --db "$store"; then
    echo "Could not set the broker to start automatically. The tools are installed, so running" >&2
    echo "  parley serve --db $store" >&2
    echo "by hand will say why." >&2
    exit 1
fi

# ----------------------------------------------------------------- prove ----

step "Checking that it answers"

# Asked of the broker, not of the supervisor. "The unit is enabled" and "an agent can attach"
# are different claims, and the second is the one this script is making -- so the check is a
# client: `parley registry` connects over the transport and exits 69 when nothing is listening.
log="$(dirname "$store")/parley-broker.log"
answering=0

for _ in $(seq 1 80); do
    if "$parley_command" registry >/dev/null 2>&1; then
        answering=1
        break
    fi

    sleep 0.25
done

if [ "$answering" != "1" ]; then
    echo "Registered, but the broker is not answering. It writes its own log, and" >&2
    echo "$log is where it says why." >&2
    exit 1
fi

note "store: $store"
note "log:   $log"

if [ "$foreign" = "1" ]; then
    echo "warning: a broker that this did not start was already listening, and it is the one" >&2
    echo "answering. It is serving its own store, not $store. Stop it and log out and in, or" >&2
    echo "run 'parley serve --db $store' yourself." >&2
fi

echo
printf '%sThe broker is running, and starts again when you log in.%s\n' "$green" "$plain"
echo
echo "Next, in each repository you want an agent in:   /parley:setup"
echo "Restart the editor there, then check it arrived:  parley registry"
