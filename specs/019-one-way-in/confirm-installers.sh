#!/usr/bin/env bash
# T107: confirms the commands install.sh drives still answer, on Linux, after 019's removals.
#
# Covers what the task names: `project list` including the readiness poll's own pattern, `open`,
# `autostart install`, `autostart remove`, and `<tool> --version`.
set -uo pipefail

fail=0
ok()   { echo "  ok   $1"; }
bad()  { echo "  FAIL $1"; fail=1; }
check(){ if [ "$2" = "$3" ]; then ok "$1"; else bad "$1: expected '$3', got '$2'"; fi; }

echo "=== copying the checkout ==="
mkdir -p /work
cd /src || exit 1
tar -cf - --exclude=./.git --exclude=./artifacts --exclude='./*/*/bin' --exclude='./*/*/obj' . \
  | (cd /work && tar -xf -)
cd /work || exit 1

export PATH="$PATH:/root/.dotnet/tools"

echo
echo "=== install.sh end to end ==="
./install.sh --hidden > /tmp/install.log 2>&1
code=$?
tail -4 /tmp/install.log | sed 's/^/    /'

# Non-zero is expected here and only here: a container has no systemd, so registering the unit
# cannot succeed. Everything before it must have.
grep -q 'parley: installed' /tmp/install.log && ok "the broker installed" || bad "the broker did not install"
grep -q 'parley-mcp: installed' /tmp/install.log && ok "the adapter installed" || bad "the adapter did not install"
grep -q 'could not enable it' /tmp/install.log && ok "the unit step degraded honestly (no systemd here)" \
  || echo "    note: the unit step did not report a problem (exit $code)"

echo
echo "=== <tool> --version, which the installer checks against what it built ==="
for tool in parley parley-mcp parley-web-ui; do
    said="$(command -v "$tool" >/dev/null 2>&1 && "$tool" --version 2>&1 | head -1)"
    case "$said" in
        *"speaking Parley protocol"*) ok "$tool answers --version" ;;
        *) bad "$tool --version said: $said" ;;
    esac
done

echo
echo "=== project list on a machine with none ==="
said="$(parley project list 2>&1)"
echo "$said" | sed 's/^/    /'
echo "$said" | grep -q 'No projects configured' && ok "says so in the words the scripts read" \
  || bad "the phrase both installers grep for is gone"
echo "$said" | grep -q 'parley terminal' && ok "sends a person to the terminal" || bad "no way on named"

echo
echo "=== project list with a project, written by hand ==="
# By hand because `project add` was removed on 2026-09-25 and no terminal is installed here --
# which is the documented consequence, and the layout is documented precisely so a file may be
# written directly. `ProjectFolder` reads it under the same rules either way.
root="$(printf '%s' "$said" | sed -n 's/^No projects configured. One would live under //p' | sed 's/\.$//')"
root="${root%/projects}"
[ -n "$root" ] || root="$HOME/.config/Parley"

mkdir -p "$root/projects/orders"
printf '%s' '{ "endpoint": "http://127.0.0.1:19100" }' > "$root/projects/orders/project.json"

listed="$(parley project list 2>&1)"
echo "$listed" | sed 's/^/    /'

# The three facts T071 separated, and the line both installers now scrape.
echo "$listed" | grep -q '^root:' && ok "names the root on a labelled line" || bad "no root: line to scrape"
echo "$listed" | grep -qE ' (not )?answering$' && ok "says whether the broker answered" || bad "no answering verdict"
echo "$listed" | grep -q 'only this machine can address it' && ok "says who can address it" || bad "address scope missing"
echo "$listed" | grep -q 'reachable' && bad "the word FR-023 forbids came back" || ok "never says reachable"

# The readiness poll's own patterns, run against real output rather than assumed.
if printf '%s\n' "$listed" | grep -q ' answering$' && ! printf '%s\n' "$listed" | grep -q 'not answering'; then
    bad "the poll would report a broker answering when none is running"
else
    ok "the poll's pattern does not match with no broker running"
fi

echo
echo "=== parley open, which both installers ask before stopping a broker ==="
parley open >/tmp/open.log 2>&1
code=$?
head -2 /tmp/open.log | sed 's/^/    /'
# No broker is running, so this must fail rather than claim nothing is in flight -- which is what
# lets the scripts say "could not ask" instead of implying a quiet deployment.
[ "$code" -ne 0 ] && ok "fails when it cannot ask, rather than answering emptily" \
  || bad "open returned 0 with no broker running"

echo
echo "=== autostart status and remove ==="
parley autostart status >/tmp/status.log 2>&1
head -2 /tmp/status.log | sed 's/^/    /'
# Any of the four states status can now report. It gained two when T107 found it calling a
# written-but-not-enabled unit "starts at logon"; the pattern here named only the old pair.
grep -qE 'does not start on its own|starts at logon|will not start at logon|could not be checked' /tmp/status.log   && ok "status answers with one of its four states" || bad "status said nothing useful"
grep -q 'NOT enabled' /tmp/status.log   && ok "and it is the honest one here: the unit was written and never enabled"   || bad "status did not notice the unit is unenabled"


parley autostart remove >/tmp/remove.log 2>&1
code=$?
head -2 /tmp/remove.log | sed 's/^/    /'
[ "$code" -eq 0 ] && ok "remove exits zero" || bad "remove exited $code after deleting the unit file"
grep -q 'Removed' /tmp/remove.log && ok "and says it removed the unit file it deleted" || bad "remove denied removing anything"

echo
echo "=== reset.sh against what the install left ==="
./reset.sh --files-only --force --everything > /tmp/reset.log 2>&1
tail -3 /tmp/reset.log | sed 's/^/    /'
[ -d "$root" ] && bad "the root survived --everything" || ok "the root is gone"

echo
if [ "$fail" -eq 0 ]; then echo "ALL CHECKS PASSED"; else echo "SOME CHECKS FAILED"; fi
exit "$fail"
