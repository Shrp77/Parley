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
#   ./install.sh --node-config <path>  the catalogue of agents this machine may start
#   ./install.sh --uninstall     remove both tools and the autostart entry, and stop
#   ./install.sh --force         end whatever is holding the tools, rather than refusing
#
# --force ends a broker somebody started by hand and the parley-mcp adapter of every editor
# session with Parley configured. Those sessions keep running and lose Parley until the editor
# is restarted, which is the cost and is why it is not the default: ending a process a person
# launched themselves is their decision, and this is that decision made deliberately on the
# command line. It weakens no check -- everything is waited for and then looked for again, and
# anything still holding the files is still fatal.

set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

uninstall=0
force=0
store=""
node_config=""

while [ $# -gt 0 ]; do
    case "$1" in
        --uninstall) uninstall=1; shift ;;
        --force) force=1; shift ;;
        # --db as well as --store, because `parley serve --db` is the flag this ends up
        # passing and the two being different words is a trap laid for nobody's benefit.
        --store|--db) store="${2:-}"; shift 2 ;;
        --node-config) node_config="${2:-}"; shift 2 ;;
        --node-config=*) node_config="${1#--node-config=}"; shift ;;
        --store=*) store="${1#--store=}"; shift ;;
        --db=*) store="${1#--db=}"; shift ;;
        -h|--help) sed -n '2,25p' "${BASH_SOURCE[0]}" | sed 's/^# \\?//'; exit 0 ;;
        *)
            echo "Unknown argument: $1" >&2
            echo "Usage: ./install.sh [--store <path>] [--node-config <path>] [--uninstall] [--force]" >&2
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

# The pids of anything running under the given command names.
#
# One place, because three guards ask the same question and a fourth asks it again after a kill.
holding() {
    local out="" found

    for cmd in "$@"; do
        found="$(pgrep -x "$cmd" 2>/dev/null || true)"
        [ -n "$found" ] && out="$out$found
"
    done

    printf '%s' "$out" | sed '/^$/d'
}

# Ends whatever is running under the given command names. Prints the survivors and returns 1
# when anything is left; prints nothing and returns 0 when the files are free.
#
# Only ever called under --force, and it returns rather than exits: each caller knows what it
# was guarding and can say so better than this can.
#
# TERM before KILL, which is where this differs from install.ps1 on purpose. Windows has no
# reliable polite stop for a console process -- Stop-Process without -Force asks for a window
# close, which a broker need not honour -- so that script kills outright. Here a broker asked to
# stop closes its listener and its streams first, and a store left mid-write is worse than
# waiting two seconds. KILL is the backstop for one that will not go.
#
# It waits, and then it looks again. A signal is delivered, not obeyed, and the failure this
# whole guard exists to prevent needs the process actually gone: `dotnet tool uninstall` fails on
# a file still held, `dotnet tool install` then exits *zero* saying "is already installed", and
# the version check afterwards cannot tell -- because the version it finds is the version it
# expected.
#
# Looked for by name rather than by pid, because a session that started while this ran holds the
# same files, and the question is whether anything holds them now.
remove_holders() {
    local pids waited

    pids="$(holding "$@")"
    [ -z "$pids" ] && return 0

    for pid in $pids; do
        note "ending pid $pid"
        kill -TERM "$pid" 2>/dev/null || true
    done

    waited=0
    while [ "$waited" -lt 50 ]; do
        [ -z "$(holding "$@")" ] && return 0
        sleep 0.2
        waited=$((waited + 1))
    done

    for pid in $(holding "$@"); do
        note "pid $pid did not stop when asked; ending it outright"
        kill -KILL "$pid" 2>/dev/null || true
    done

    waited=0
    while [ "$waited" -lt 25 ]; do
        [ -z "$(holding "$@")" ] && return 0
        sleep 0.2
        waited=$((waited + 1))
    done

    holding "$@"
    return 1
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

    # Anything left is a broker somebody started by hand. Ending a process a person launched
    # themselves is a decision for them, so without --force this does not kill it and the check
    # at the end reports honestly either way. With --force that decision has been made, on the
    # command line, by the person it belongs to.
    if pgrep -x parley >/dev/null 2>&1; then
        if [ "$force" = "1" ]; then
            if ! survivors="$(remove_holders parley)"; then
                echo "A broker is still running after --force tried to end it:" >&2
                printf '      pid %s\n' $survivors >&2
                echo "    It is probably another user's, which this cannot end. Nothing has" >&2
                echo "    been removed." >&2
                exit 1
            fi
        else
            note "a broker is still running that no autostart entry started (--force ends it)"
        fi
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
adapters=()

for cmd in "${commands[@]}"; do
    [ "$cmd" = "$broker" ] && continue
    adapters+=("$cmd")
done

holders="$(holding "${adapters[@]}")"

if [ -n "$holders" ]; then
    count="$(printf '%s\n' "$holders" | wc -l | tr -d ' ')"

    if [ "$force" = "1" ]; then
        note "$count adapter(s) hold the tool store; --force is ending them"

        if ! survivors="$(remove_holders "${adapters[@]}")"; then
            echo "==> Adapter(s) still running after --force tried to end them:" >&2
            printf '      pid %s\n' $survivors >&2
            echo "    Nothing has been changed and the broker has not been stopped. A process" >&2
            echo "    this user cannot end is the usual reason." >&2

            exit 1
        fi

        # Said plainly because it is the cost of the flag, and it is paid in somebody else's
        # window. Those sessions keep running; what they lose is Parley, until the editor restarts.
        note "those editor sessions have lost their Parley tools until each editor restarts"
    else
        echo "==> $count Parley adapter(s) are running and hold the files this would replace:" >&2
        printf '      pid %s\n' $holders >&2
        echo "    Each one belongs to an editor session with Parley configured. Close those" >&2
        echo "    sessions and run this again, or re-run with --force to end them. Nothing has" >&2
        echo "    been changed and the broker has not been stopped." >&2

        exit 1
    fi
fi

note "no adapter is holding the tool store"

stop_broker

# After the stop, because until now a running broker was expected. What is left is one somebody
# started by hand, which stop_broker deliberately leaves alone -- and which holds the binary just
# as firmly. Caught here rather than by the uninstall, which would fail and leave the tools half
# replaced.
strays="$(holding "${commands[@]}")"

if [ -n "$strays" ]; then
    if [ "$force" = "1" ]; then
        note "something still holds the tools; --force is ending it"

        if ! survivors="$(remove_holders "${commands[@]}")"; then
            echo "==> Something is still holding the tools after --force tried to end it:" >&2
            printf '      pid %s\n' $survivors >&2
            echo "    Nothing has been replaced." >&2

            exit 1
        fi
    else
        echo "==> Something is still holding the tools after the broker was stopped:" >&2
        printf '      pid %s\n' $strays >&2
        echo "    A broker started by hand is not stopped by this script, on purpose. Stop it" >&2
        echo "    and run this again, or re-run with --force to end it. Nothing has been" >&2
        echo "    replaced." >&2

        exit 1
    fi
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

# A machine that names projects gets an entry that serves them all, and is not asked for a store
# at all: every project's store and port are in the manifest, and SPEC.md 4.1 says one process
# may host every one of them. Asked of the tool rather than by looking for the file, because
# where the manifest lives is the tool's to know.
manifest=""

if [ -z "$store" ]; then
    if listed="$("$parley_command" project list 2>/dev/null)" \
        && ! printf '%s' "$listed" | grep -q 'No projects configured'; then
        manifest="$(printf '%s\n' "$listed" | sed -n 's/^manifest:[[:space:]]*//p' | tail -n 1)"
    fi
fi

if [ -z "$manifest" ]; then
    store="$(resolve_store)"
fi

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
autostart_arguments=()

if [ -n "$manifest" ]; then
    echo "  serving every project in $manifest"

    if [ -n "$node_config" ]; then
        note "--node-config is not used by an entry that serves the manifest: each project names its own catalogue"
    fi
else
    # `autostart install` registers a broker-alone entry when it is given no catalogue, so a
    # machine whose agents were being started for it loses that half -- and the loss arrives days
    # later, on a machine nobody is signed into, as a question that waits out its window and is
    # ghosted.
    #
    # Said here rather than carried over. install.ps1 reads the catalogue back out of the
    # scheduled task it is replacing; there is no portable equivalent here, because this
    # registers a systemd user unit on Linux and a launch agent on macOS. Announcing the gap is
    # better than a read-back that works on one platform and silently does nothing on the other.
    if [ -z "$node_config" ]; then
        echo "  no agent catalogue given, so this entry starts the broker alone. Pass"
        echo "  --node-config <path> to have it start this machine's agents too."
    fi

    autostart_arguments+=(--db "$store")

    if [ -n "$node_config" ]; then
        autostart_arguments+=(--node-config "$node_config")
    fi
fi

if ! "$parley_command" autostart install "${autostart_arguments[@]}"; then
    echo "Could not set the broker to start automatically. The tools are installed, so running" >&2

    if [ -n "$manifest" ]; then
        echo "  parley serve" >&2
    else
        echo "  parley serve --db $store" >&2
    fi

    echo "by hand will say why." >&2
    exit 1
fi

# ----------------------------------------------------------------- prove ----

step "Checking that it answers"

# Asked of the broker, not of the supervisor. "The unit is enabled" and "an agent can attach"
# are different claims, and the second is the one this script is making -- so the check is a
# client. With one store, `parley registry` connects over the transport and exits 69 when
# nothing is listening; with a manifest, `parley project list` asks every project's broker and
# says which are reachable, and the claim is that all of them are.
if [ -n "$manifest" ]; then
    log="$(dirname "$manifest")/parley-broker.log"
else
    log="$(dirname "$store")/parley-broker.log"
fi

answering=0

for _ in $(seq 1 80); do
    if [ -n "$manifest" ]; then
        if listed="$("$parley_command" project list 2>/dev/null)" \
            && printf '%s\n' "$listed" | grep -q ' reachable$' \
            && ! printf '%s\n' "$listed" | grep -q 'not reachable'; then
            answering=1
            break
        fi
    elif "$parley_command" registry >/dev/null 2>&1; then
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

if [ -n "$manifest" ]; then
    note "serves: every project in $manifest"
else
    note "store: $store"
fi

note "log:   $log"

if [ "$foreign" = "1" ] && [ -z "$manifest" ]; then
    echo "warning: a broker that this did not start was already listening, and it is the one" >&2
    echo "answering. It is serving its own store, not $store. Stop it and log out and in, or" >&2
    echo "run 'parley serve --db $store' yourself." >&2
fi

echo
printf '%sThe broker is running, and starts again when you log in.%s\n' "$green" "$plain"
echo
echo "Next, in each repository you want an agent in:   /parley:setup"
echo "Restart the editor there, then check it arrived:  parley registry"
