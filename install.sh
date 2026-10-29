#!/usr/bin/env bash
#
# Builds Parley from this checkout and installs its tools.
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
# It then registers the broker to start when you log in and checks that it answers. The broker
# serves every project this machine names; a machine that names none yet is served too, waiting
# for the first, so this never asks where a store goes -- a project says that. Installing
# the tools is not the same as having Parley working: the broker is a separate process that owns
# the store, and until one is running every agent fails to attach with "No broker is listening"
# -- a message that arrives in an editor, days later, rather than here.
#
#   ./install.sh                 build, install, and set the broker up
#   ./install.sh --uninstall     remove every tool and the autostart entry, and stop
#   ./install.sh --surfaces X    which operator surfaces to install: terminal, web-ui,
#                                both or none. Defaults to web-ui here and to both on
#                                Windows -- a headless box has no browser to point at a
#                                web UI, but it can still serve one over a tunnel, and a
#                                terminal surface on a machine nobody has a session on is
#                                a package installed to be never run.
#
#                                A surface not named is not removed. It is left at
#                                whatever version it had, and this says so on the way out:
#                                a skipped surface going quietly is how a machine ends up
#                                running a terminal three versions behind its broker.
#   ./install.sh --only parley-terminal
#                                install exactly these tools and leave every other one alone.
#                                --only parley-terminal replaces the terminal and does not stop
#                                the broker, because the broker's binary is not among the files
#                                being unlinked. SPEC.md section 12 says the node stopping tells
#                                every agent it started to quit, and the process is what held
#                                everything that agent had learned.
#   ./install.sh --force         end whatever is holding the tools, rather than refusing
#   ./install.sh --hidden        register a logon entry that opens no console window, by
#                                passing --hidden to `parley autostart install`. The entry
#                                runs the broker in your own session -- the node starts
#                                agents, and an agent is a CLI you have already signed in
#                                to -- and a console program in a session is given a
#                                console. This is how that is not paid: the broker lets go
#                                of it and everything it would have said is in the log
#                                named below. Opt-in, so a machine installed without it
#                                behaves at logon exactly as it did before.
#   ./install.sh --reset         wipe this machine's Parley first, by running
#                                reset.sh --everything --force, then install. Every project,
#                                store, conversation, port and node.json goes, and nothing
#                                asks: passing --reset is the decision. It runs after the
#                                build and pack succeed, so a failed build leaves the machine
#                                as it was. Refused with --uninstall and with --only.
#   ./install.sh --reset --repositories <dir>
#                                also take the Parley entries out of every checkout's
#                                .mcp.json under <dir>, as reset.sh --repositories does.
#
# --force ends a broker somebody started by hand and the parley-mcp adapter of every editor
# session with Parley configured. Those sessions keep running and lose Parley until the editor
# is restarted, which is the cost and is why it is not the default: ending a process a person
# launched themselves is their decision, and this is that decision made deliberately on the
# command line. It weakens no check -- everything is waited for and then looked for again, and
# anything still holding the files is still fatal.

set -euo pipefail

# Leaves the console in a state a person can type in, before doing anything else. A terminal left
# in mouse-reporting mode prints `[555;47;16M` at the prompt on every mouse movement; the mode
# belongs to the window rather than to any process, so it outlives whatever set it. Disabling a
# mode that is already off does nothing, so this is free on a healthy terminal and repairs a stuck
# one.
#
# The three mouse modes and bracketed paste. The alternate screen is left alone: switching back to
# the main screen would throw away what is on the screen of somebody running this from inside a
# full-screen program, to fix a problem they do not have.
if [ -t 1 ]; then
    printf '[?1003l[?1015l[?1006l[?2004l[?25h'
fi

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

uninstall=0
force=0
hidden=0
surfaces=""
only=""
reset=0
repositories=""

while [ $# -gt 0 ]; do
    case "$1" in
        --uninstall) uninstall=1; shift ;;
        --surfaces) surfaces="${2:-}"; shift 2 ;;
        --only) only="${only:+$only }${2:-}"; shift 2 ;;
        --only=*) only="${only:+$only }${1#--only=}"; shift ;;
        --surfaces=*) surfaces="${1#--surfaces=}"; shift ;;
        --force) force=1; shift ;;
        --hidden) hidden=1; shift ;;
        --reset) reset=1; shift ;;
        --repositories) repositories="${2:-}"; shift 2 ;;
        --repositories=*) repositories="${1#--repositories=}"; shift ;;
        # Every comment line after the shebang, not a line range: it reads to the first
        # non-comment line, so a paragraph added to the header is in the help by construction
        # and there is no line number to keep in step.
        -h|--help) awk 'NR>1 && /^#/ { sub(/^# ?/, ""); print; next } NR>1 { exit }' "${BASH_SOURCE[0]}"; exit 0 ;;
        *)
            echo "Unknown argument: $1" >&2
            echo "Usage: ./install.sh [--surfaces terminal|web-ui|both|none] [--only <command>] [--uninstall] [--force] [--hidden] [--reset [--repositories <dir>]]" >&2
            exit 64
            ;;
    esac
done

# Checked before anything is stopped or built, so a combination that makes no sense costs nothing.
if [ "$reset" = "1" ] && [ "$uninstall" = "1" ]; then
    echo "--reset and --uninstall together make no sense: --uninstall removes what was installed and installs nothing." >&2
    exit 64
fi

if [ "$reset" = "1" ] && [ -n "$only" ]; then
    echo "--reset wipes the whole deployment and stops the broker, and --only exists to leave the broker running. Use one or the other." >&2
    exit 64
fi

if [ -n "$repositories" ] && [ "$reset" = "0" ]; then
    echo "--repositories is only used with --reset." >&2
    exit 64
fi

# A fourth array rather than three. `surface_of` says which of the two operator surfaces a tool
# is, and empty means it is not one: the broker and the agent adapter are what Parley is, and
# every machine that runs Parley at all needs both.
packages=("Parley.Cli" "Parley.Mcp" "Parley.Terminal" "Parley.WebUi")
projects=("src/Parley.Cli/Parley.Cli.csproj" "src/Parley.Mcp/Parley.Mcp.csproj" "src/Parley.Terminal/Parley.Terminal.csproj" "src/Parley.WebUi/Parley.WebUi.csproj")
commands=("parley" "parley-mcp" "parley-terminal" "parley-web-ui")

surface_of=("" "" "terminal" "web-ui")

# The web UI alone by default, for the reason at the top: this script runs where there is usually
# no browser, and a terminal nobody has a session on is a package installed to be never run.
if [ -z "$surfaces" ]; then
    surfaces="web-ui"
fi

case "$surfaces" in
    both)     wanted="terminal web-ui" ;;
    terminal) wanted="terminal" ;;
    web-ui)   wanted="web-ui" ;;
    none)     wanted="" ;;
    *)
        echo "--surfaces takes terminal, web-ui, both or none, and was given '$surfaces'." >&2
        exit 64
        ;;
esac

# Whether this machine asked for a tool. One with no surface is always installed: it is not
# optional, it is Parley. --only is exact and overrides everything: it names the tools, so a
# surface set would be a second answer to a question already answered.
is_wanted() {
    if [ -n "$only" ]; then
        case " $only " in
            *" ${commands[$1]} "*) return 0 ;;
            *) return 1 ;;
        esac
    fi

    [ -z "${surface_of[$1]}" ] && return 0

    case " $wanted " in
        *" ${surface_of[$1]} "*) return 0 ;;
        *) return 1 ;;
    esac
}

# Checked before anything is built. A mistyped --only would otherwise select no tool at all and
# this script would pack nothing, install nothing, and report success -- which is the exact shape
# of failure every other guard in this file exists to prevent.
if [ -n "$only" ]; then
    for name in $only; do
        found=0

        for cmd in "${commands[@]}"; do
            [ "$cmd" = "$name" ] && found=1
        done

        if [ "$found" = "0" ]; then
            echo "--only names no such tool: $name. This repository ships ${commands[*]}." >&2
            exit 64
        fi
    done
fi

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

    # **The web UI is waited for as well.** The broker ends the child it started, so the child
    # goes -- but a moment later, and the next thing that happens is an uninstall of a binary the
    # child may still be running from. Waited for rather than assumed, which is the rule every
    # other guard in this script is written to.
    # The broker alone here, and the web UI below. Waiting for both at once spends the whole
    # budget on exactly the machine this exists to help: an orphaned web UI is never going to
    # exit on its own, so the wait runs to its deadline and only then reaches the code that ends
    # it.
    waited=0

    while [ "$waited" -lt 50 ]; do
        [ -z "$(holding parley)" ] && break
        sleep 0.2
        waited=$((waited + 1))
    done

    # **A web UI whose broker has gone is an orphan, and it is this script that orphaned it.**
    # Stopping the unit terminates the process rather than asking it to exit, so the broker never
    # reaches the code that ends the child it started. The child keeps running, keeps the port,
    # and keeps the binary busy -- which refuses the very install that stopped the broker, every
    # time. Told apart by its parent rather than by its name, because a web UI somebody started by
    # hand is still theirs: an orphan's parent is init, which is pid 1.
    for pid in $(holding parley-web-ui); do
        parent="$(ps -o ppid= -p "$pid" 2>/dev/null | tr -d ' ')"

        if [ "$parent" = "1" ]; then
            echo "    ending the web UI the stopped broker left behind, pid $pid"
            kill "$pid" 2>/dev/null || true
        fi
    done

    # And then waited for: `kill` asks; it does not wait, and announcing an ended pid and then
    # refusing the install naming that same pid would contradict itself. The same wait covers a web
    # UI exiting with its broker on the ordinary path, where nothing was killed and the child simply
    # goes a moment after its parent.
    waited=0

    while [ "$waited" -lt 50 ]; do
        [ -z "$(holding parley-web-ui)" ] && break
        sleep 0.2
        waited=$((waited + 1))
    done
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
    echo "Every Parley tool is gone, and so is the autostart entry. The store and any .mcp.json are"
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
    if ! is_wanted "$i"; then
        note "skipping ${packages[$i]}: --surfaces did not name ${surface_of[$i]}"
        continue
    fi

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

# ------------------------------------------------------------------- reset ---

# Here rather than first, so a build that fails leaves the machine as it was. reset.sh stops the
# broker and its children and removes the autostart entry itself; the steps below then find
# nothing to stop, which they already handle. Run through bash rather than executed, so a
# checkout that lost the file's executable bit still resets.
if [ "$reset" = "1" ]; then
    step "Resetting this machine (--reset)"

    if [ -n "$repositories" ]; then
        bash "$root/reset.sh" --everything --force --repositories "$repositories"
    else
        bash "$root/reset.sh" --everything --force
    fi
fi

# ----------------------------------------------------------------- install ---

# Before anything is stopped. A running `parley-mcp` holds its own files in the tool store, so
# `dotnet tool uninstall` fails on a denied path. Checked after the broker was stopped, that would
# leave a machine that had been serving a deployment with no broker and one tool replaced and one
# not.
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
#
# **Only the tools about to be replaced.** A surface this run is skipping is not going to be
# unlinked, so a terminal somebody has open is no reason to refuse an install of the broker --
# and refusing anyway would make --surfaces useless on the machine most likely to want it.
#
# **The web UI is excluded with it, for the same reason.** The broker starts parley-web-ui as a
# child and ends it when it ends, so a running web UI on a machine whose broker is about to be
# stopped is not a process anybody has to close. Counting it as a holder would refuse the install
# naming a process with no window and tell the person to use --force to end something that was
# going to stop anyway. One started by hand is caught after the broker stops, where it can be told
# apart, exactly as the broker's own exclusion works.
replacing_broker=0

for i in "${!commands[@]}"; do
    if [ "${commands[$i]}" = "$broker" ] && is_wanted "$i"; then
        replacing_broker=1
    fi
done

adapters=()

for i in "${!commands[@]}"; do
    [ "${commands[$i]}" = "$broker" ] && continue
    is_wanted "$i" || continue

    if [ "${commands[$i]}" = "parley-web-ui" ] && [ "$replacing_broker" -eq 1 ]; then
        continue
    fi

    adapters+=("${commands[$i]}")
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

note "nothing is holding the tool store"

# Only when the file being replaced is the broker's own. A live broker holds `parley` open, which
# makes the uninstall fail and the install after it a no-op that reports success -- so it has to
# stop. But it only has to stop for its own binary.
#
# **This is the whole point of the split, and it is one `if`.** A run that replaces only the
# terminal unlinks a file the broker never opened, so there is nothing to stop: SPEC.md section 12
# has the node telling every agent it started to quit, and each of those processes is what held
# everything that agent had learned. Restarting a broker to change a terminal spends all of it.
replacing_broker=0

for i in "${!commands[@]}"; do
    if [ "${commands[$i]}" = "$broker" ] && is_wanted "$i"; then
        replacing_broker=1
    fi
done

if [ "$replacing_broker" = "1" ]; then
    # **What stopping it will interrupt, said before it is stopped.**
    #
    # SPEC.md section 12: the node stopping tells every agent it started to quit, and the process
    # is what held everything each of them had learned. A Request already drained into an agent's
    # inbox is marked delivered, and killing the agent that holds it leaves the exchange open and
    # owed with nothing running to answer it; nothing times out, so it waits and is flagged.
    #
    # **Asked of the broker, never of the store.** Only the broker opens the database (section 4),
    # and reading it here for a progress message would break the one boundary this architecture is
    # built on. `parley open` is the same conversations() reply both surfaces read.
    #
    # A warning and not a refusal: the person running this has decided to upgrade.
    # **Asked of each project, because `parley open` refuses to choose between several.** On a machine naming more than
    # one, a bare `parley open` could never answer, and those are the machines with the most in
    # flight. Names come from `parley project list`, whose first column is the name and whose
    # second is the endpoint; a machine naming none is asked once, of the broker it would default to.
    in_flight=""
    unasked=""
    asked=0

    if command -v parley >/dev/null 2>&1; then
        names="$( (parley project list 2>/dev/null || true) | sed -n -E 's/^([a-z][a-z0-9-]*)[[:space:]]+[^[:space:]]+:\/\/.*/\1/p')"

        if [ -z "$names" ]; then
            if in_flight="$(parley open 2>/dev/null)"; then
                asked=1
            fi
        else
            asked=1

            for name in $names; do
                if said="$(parley --project "$name" open 2>/dev/null)"; then
                    if [ -n "$said" ]; then
                        in_flight="${in_flight}$(printf '%s\n' "$said" | sed "s/^/$name: /")"$'\n'
                    fi
                else
                    unasked="$unasked $name"
                fi
            done
        fi
    fi

    if [ "$asked" = "1" ]; then
        if [ -n "$unasked" ]; then
            echo "warning: could not ask${unasked} what is open, so this cannot say what stopping the" >&2
            echo "         broker will interrupt there. That is not the same as nothing being in flight." >&2
        fi

        if [ -n "$in_flight" ]; then
            count="$(printf '%s\n' "$in_flight" | grep -c . || true)"

            echo "warning: $count conversation(s) have an exchange still open. Stopping the broker" >&2
            echo "         ends every agent it started. When an agent's turn ends its open exchange" >&2
            echo "         stays open and is flagged to the person:" >&2

            printf '%s\n' "$in_flight" | while IFS= read -r line; do
                [ -n "$line" ] && echo "      $line" >&2
            done
        fi
    else
        # **Said as could-not-ask, not as nothing-in-flight.** A guard that fails open has to say
        # so: the two readings look identical from here and lead somewhere completely different.
        echo "warning: could not ask the broker what is open, so this cannot say what stopping it" >&2
        echo "         will interrupt. That is not the same as nothing being in flight." >&2
    fi

    step "Stopping any running broker"

    stop_broker
else
    step "Leaving the broker alone"
    note "nothing being replaced is the broker's, so no agent is ended and no exchange interrupted"
fi

# After the stop, because until now a running broker was expected. What is left is one somebody
# started by hand, which stop_broker deliberately leaves alone -- and which holds the binary just
# as firmly. Caught here rather than by the uninstall, which would fail and leave the tools half
# replaced.
# Only what is being replaced, for the reason the adapter check gives.
replacing=()

for i in "${!commands[@]}"; do
    is_wanted "$i" && replacing+=("${commands[$i]}")
done

strays="$(holding "${replacing[@]}")"

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

# What happened to each, said one line per tool at the end. Three outcomes, and only one of them
# is "nothing happened": a surface this machine did not ask for may still be installed from
# before, at whatever version it was, and that is the case worth naming.
outcomes=()
skew=""
listed_before="$(dotnet tool list -g 2>/dev/null || true)"

for i in "${!packages[@]}"; do
    if ! is_wanted "$i"; then
        lowered="$(printf '%s' "${packages[$i]}" | tr '[:upper:]' '[:lower:]')"

        if printf '%s' "$listed_before" | grep -qi "^$lowered[[:space:]]"; then
            outcomes+=("${commands[$i]}: left as it was, not selected by --surfaces")
            skew="$skew ${commands[$i]}"
        else
            outcomes+=("${commands[$i]}: not installed, not selected by --surfaces")
        fi

        continue
    fi

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

    outcomes+=("${commands[$i]}: installed $built")
done

for outcome in "${outcomes[@]}"; do
    note "$outcome"
done

# Said loudly rather than in the list above, because it is the one outcome that leaves the
# machine inconsistent. The surfaces speak SPEC.md section 10.2, which is a versioned contract,
# so a surface left behind is a client of a broker it may no longer agree with.
if [ -n "$skew" ]; then
    echo "==>${skew} is installed and was not updated to $built. Re-run with --surfaces both to" >&2
    echo "    bring it up, or with --uninstall to remove everything." >&2
fi

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
#
# **Asked of each tool that was installed, rather than of `parley`.** A run can replace only a
# surface, and asking the broker would then compare the version just built against a broker
# deliberately left alone, and fail a run that did exactly what it was asked.
echo

for i in "${!commands[@]}"; do
    is_wanted "$i" || continue

    if [ -x "$shims/${commands[$i]}" ]; then
        binary="$shims/${commands[$i]}"
    else
        binary="${commands[$i]}"
    fi

    # A tool that exits non-zero, or says nothing, is a failure named below rather than a silent exit: under set -e a
    # bare substitution here ended the script with no message, which is how parley-mcp answering --version only with a
    # launch configuration went unnoticed.
    says="$("$binary" --version 2>&1)" || says=""

    case "$says" in
        *"$built"*) ;;
        *)
            echo "Installed $built but ${commands[$i]} reports '$says'. Something older is still" >&2
            echo "there; run this with --uninstall and try again." >&2
            exit 1
            ;;
    esac

    printf '%s%s%s\n' "$green" "$says" "$plain"
done

# ---------------------------------------------------------------- broker ----

# **Skipped entirely when the broker is not being replaced, and this is the half that is easy to
# miss.** Not stopping the broker earlier is worthless if the script re-registers the
# autostart entry down here: `autostart install` replaces the unit or launch agent, which
# restarts the process, which ends every resident agent on the machine along with everything each
# had learned. The stop would have been avoided and the cost paid anyway, hundreds of lines later.
#
# Nothing here is needed for a surface: a terminal is not registered with a supervisor, does not
# listen on a port, and has no store. There is nothing to configure and nothing to prove.
if [ "$replacing_broker" != "1" ]; then
    echo ""
    echo "${green}The broker was not touched, and is still serving whatever it was serving.${plain}"
    echo "Your agents kept running, so nothing they were working on was lost."

    exit 0
fi


# **Every machine's entry serves the manifest**: every project this machine names, each on its own store and port, and
# a project named later started within seconds. A machine that names none yet gets the same entry, waiting for the
# first -- so this never asks where a store goes. That is a project's to say, and a project is named in `parley terminal`
# (the terminal), where everything else it needs is said at the same time.
manifest=""
named=0

if listed="$("$parley_command" project list 2>/dev/null)"; then
    # The `root:` line of `project list`: the directory of projects the broker is about to serve.
    manifest="$(printf '%s\n' "$listed" | sed -n 's/^root:[[:space:]]*//p' | tail -n 1)"

    if ! printf '%s' "$listed" | grep -q 'No projects configured'; then
        named=1
    fi
fi

step "Registering the broker to start at logon"

if [ -n "$manifest" ]; then
    echo "  serving every project in $manifest"
fi

# No root here, and not by luck: this writes a systemd *user* unit or a per-user launch agent.
# The broker binds a loopback port and writes under the invoking user's own home, so it needs no
# privilege they do not already have, and asking for one would be asking for the tool's benefit.
#
# **An array rather than a second call in a branch.** Writing the command twice is two spellings
# of one thing, and this one carries the arguments every reinstall depends on -- the way for them
# to drift is for somebody to edit whichever branch they happened to be running.
#
# The words `autostart install` stay adjacent and unquoted on purpose: `InstallScriptTests` reads
# this file for exactly that string, to check the script sets the broker up rather than printing
# the command for a person to copy. Splitting them into two array elements would keep the script
# working and quietly stop the guard seeing it.
#
# An array holding the whole command rather than only the extra, which is what install.ps1 does,
# because expanding a possibly-empty array under `set -u` is an unbound variable in bash 3.2 --
# which is what macOS still ships, and macOS is a platform this script runs on.
install_arguments=(autostart install)

if [ "$hidden" -eq 1 ]; then
    install_arguments+=(--hidden)
fi

# What the command says is shown as it is and read for two things: where the broker logs, and where the web UI it
# starts listens. Read rather than assumed, because both are the command's to decide.
if ! registered="$("$parley_command" "${install_arguments[@]}")"; then
    printf '%s\n' "$registered"
    echo "Could not set the broker to start automatically. The tools are installed, so running" >&2
    echo "  parley serve" >&2
    echo "by hand will say why." >&2
    exit 1
fi

printf '%s\n' "$registered"

log="$(printf '%s\n' "$registered" | sed -n 's/^[[:space:]]*logs:[[:space:]]*//p' | head -n 1)"
web_ui="$(printf '%s\n' "$registered" | sed -n 's/.*--web-ui[[:space:]]\{1,\}"\{0,1\}\([^[:space:]"]*\).*/\1/p' | head -n 1)"

# ----------------------------------------------------------------- prove ----

step "Checking that it answers"

# Asked of the broker, not of the supervisor. "The unit is enabled" and "an agent can attach" are different claims.
# With projects named, `parley project list` asks every project's broker and says which are reachable, and the claim
# is that all of them are. With none named there is no broker to ask yet -- the entry is waiting -- so what is checked
# is that the process it started is running, and the person is told how to name the first project.
answering=0

for _ in $(seq 1 80); do
    if listed="$("$parley_command" project list 2>/dev/null)"; then
        if printf '%s\n' "$listed" | grep -q 'No projects configured'; then
            if pgrep -x parley >/dev/null 2>&1; then
                answering=1
                break
            fi
        # `answering`, not `reachable`: "this process connected to it" must not read as "reachable
        # from the network", which is what somebody planning a firewall rule would act on. This poll
        # wants the first fact -- did the broker answer.
        elif printf '%s\n' "$listed" | grep -q ' answering$' \
            && ! printf '%s\n' "$listed" | grep -q 'not answering'; then
            answering=1
            named=1
            break
        fi
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
fi

note "log:   $log"

echo
printf '%sThe broker is running, and starts again when you log in.%s\n' "$green" "$plain"
echo

# What to do next: make a project, then enrol agents into it. The terminal does both, and so does
# the web console (its project list creates one, a project's Agents tab registers agents), so a
# machine with only the web UI is sent there rather than told to install a second tool. Only a
# machine with neither is told it has nowhere to do it.
if command -v parley-terminal >/dev/null 2>&1 || [ -x "$shims/parley-terminal" ]; then
    echo "Next, run:  parley"

    if [ "$named" = "1" ]; then
        echo "  1. Open a project, or create a new one with Alt+c."
    else
        echo "  1. Create a project with Alt+c. There is none yet, so nothing is being served."
    fi

    echo "  2. In the project, choose Agent > Enroll agents from folders (Alt+f)."
    echo "  3. Pick the folder that holds your repositories, tick the ones you want, press Enter."
elif [ -n "$web_ui" ]; then
    echo "Next, open the web UI at $web_ui"
    echo "  1. Create or open a project."
    echo "  2. On the project's Agents tab, choose Register an agent, and give it a repository or a folder of them."
else
    echo "Projects and agents are set up in the terminal or the web UI, and this machine has installed neither." >&2
    echo "  ./install.sh --only parley-terminal               installs the terminal without stopping the broker"
fi

if [ -n "$web_ui" ]; then
    echo
    echo "The web UI: $web_ui"
fi
