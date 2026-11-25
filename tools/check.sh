#!/bin/bash
# One look at the mod on the server after a deploy or after trouble: everything the checks by hand used to need
# six separate commands for. Reads only, changes nothing.
#
# Usage: tools/check.sh /path/to/server
#   /path/to/server is the folder with VRisingServer.exe and BepInEx/ (the same as for tools/deploy.sh). Under
#   Pelican its name is the server's UUID, which is also the Docker container's name; if no container has that
#   name, the container part is skipped.
#
# Shows:
#   - whether the mod loaded at the last start of the server, its version and the game check;
#   - the mod's errors and warnings since that start: counted, the first line of the last few;
#   - the last two memory lines and the memory watch lines since the start, and the end of the watch file;
#   - the container: state, start time, memory limit and use;
#   - processes the kernel killed for memory (any on the host, as long as the kernel log still holds them), and the
#     host's memory.
set -uo pipefail

server="${1:?usage: tools/check.sh /path/to/server}"
log="$server/BepInEx/LogOutput.log"
if [ ! -f "$log" ]; then
  echo "No BepInEx/LogOutput.log in $server: check the server path." >&2
  exit 1
fi
container="$(basename "$server")"

# Only the part of the log since the last start of the server counts. BepInEx overwrites the log at each start by
# default, but keeps adding to it with AppendLog = true, so the start is found by BepInEx's first line; failing
# that, by BepInEx loading this mod (which comes before the mod's own load errors), or by the mod's own line.
start=$(grep -an "^\[Message: *Preloader\] BepInEx" "$log" | tail -1 | cut -d: -f1)
[ -n "$start" ] || start=$(grep -an "Loading \[CarriageTracker [0-9.]*\]" "$log" | tail -1 | cut -d: -f1)
[ -n "$start" ] || start=$(grep -an "CarriageTracker [0-9][0-9.]* loaded" "$log" | tail -1 | cut -d: -f1)
since() { if [ -n "$start" ]; then tail -n +"$start" "$log"; else cat "$log"; fi; }

echo "== Version and game check (last start of the server)"
since | grep -a "Loading \[CarriageTracker" | tail -1
loaded=$(since | grep -a "CarriageTracker [0-9][0-9.]* loaded" | tail -1)
if [ -n "$loaded" ]; then echo "$loaded"; else echo "No 'CarriageTracker ... loaded' line since the start: the mod didn't finish loading (or the server is still starting)."; fi
check=$(since | grep -a "CarriageTracker: game check\|CarriageTracker: this game version lacks" | tail -2)
if [ -n "$check" ]; then echo "$check"; else echo "No game check yet: it runs at the first check after the world has loaded."; fi

echo
echo "== The mod's errors and warnings since the start"
errors=$(since | grep -ac "^\[Error *:CarriageTracker\]")
warnings=$(since | grep -ac "^\[Warning *:CarriageTracker\]")
frames=$(since | grep -ac "at CarriageTracker\.")
echo "errors $errors, warnings $warnings; stack lines in the mod's code (errors of other sources included): $frames"
since | grep -a "^\[Error *:CarriageTracker\]" | tail -3 | cut -c1-400
since | grep -a "^\[Warning *:CarriageTracker\]" | tail -3 | cut -c1-300

echo
echo "== Memory"
memory=$(since | grep -a "CarriageTracker memory:" | tail -2 | cut -c1-600)
if [ -n "$memory" ]; then echo "$memory"; else echo "No memory line yet: the first comes 10 minutes after the start."; fi
echo "memory watch lines since the start: $(since | grep -ac "CarriageTracker memory watch, line")"
if [ -f "$server/BepInEx/CarriageTracker-memory.txt" ]; then
  echo "watch file, last 6 lines (it is kept over restarts, so they may be older than this start):"
  tail -6 "$server/BepInEx/CarriageTracker-memory.txt" | cut -c1-700
fi

echo
echo "== Container $container"
if command -v docker >/dev/null && docker inspect --type container "$container" >/dev/null 2>&1; then
  docker inspect --type container "$container" --format 'state {{.State.Status}}, started {{.State.StartedAt}}, OOM-killed last time {{.State.OOMKilled}}, exit code {{.State.ExitCode}}, limit {{.HostConfig.Memory}} bytes'
  docker stats --no-stream --format 'memory now {{.MemUsage}}' "$container" 2>/dev/null
else
  echo "no container named $container"
fi

echo
echo "== Killed for memory (any process on the host, while the kernel log still holds it)"
kills=$(dmesg -T 2>/dev/null | grep "Killed process")
if [ -n "$kills" ]; then echo "$kills" | tail -3; else echo "none (or dmesg not readable)"; fi

echo
echo "== Host"
echo "up since $(uptime -s 2>/dev/null)"
free -m | grep -E "^Mem"
