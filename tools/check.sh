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
#   - the version loaded at the last start and the game check;
#   - the mod's errors and warnings since that start (counted, the last ones in full);
#   - the last two memory lines and memory watch lines since the start, and the watch file if there is one;
#   - the container: state, start time, memory limit and use;
#   - processes the kernel killed for memory since the host started, and the host's memory.
set -uo pipefail

server="${1:?usage: tools/check.sh /path/to/server}"
log="$server/BepInEx/LogOutput.log"
if [ ! -f "$log" ]; then
  echo "No BepInEx/LogOutput.log in $server: check the server path." >&2
  exit 1
fi
container="$(basename "$server")"

# The log isn't cleared between starts: look only at the part since the last time this mod was loaded.
start=$(grep -an "CarriageTracker [0-9][0-9.]* loaded" "$log" | tail -1 | cut -d: -f1)
since() { if [ -n "$start" ]; then tail -n +"$start" "$log"; else cat "$log"; fi; }

echo "== Version and game check (last start)"
if [ -n "$start" ]; then sed -n "${start}p" "$log"; else echo "No 'CarriageTracker ... loaded' line in the log: the mod didn't load."; fi
since | grep -a "CarriageTracker: game check\|CarriageTracker: this game version lacks" | tail -2

echo
echo "== The mod's errors and warnings since the start"
errors=$(since | grep -ac "^\[Error *:CarriageTracker\]")
warnings=$(since | grep -ac "^\[Warning *:CarriageTracker\]")
echo "errors $errors, warnings $warnings"
since | grep -a "^\[Error *:CarriageTracker\]" | tail -3 | cut -c1-400
since | grep -a "^\[Warning *:CarriageTracker\]" | tail -3 | cut -c1-300

echo
echo "== Memory"
since | grep -a "CarriageTracker memory:" | tail -2 | cut -c1-600
[ "$(since | grep -ac "CarriageTracker memory:")" -eq 0 ] && echo "No memory line yet: the first comes 10 minutes after the start."
watch=$(since | grep -ac "memory watch")
echo "memory watch lines since the start: $watch"
if [ -f "$server/BepInEx/CarriageTracker-memory.txt" ]; then
  echo "watch file (last 6 lines):"
  tail -6 "$server/BepInEx/CarriageTracker-memory.txt" | cut -c1-700
fi

echo
echo "== Container $container"
if command -v docker >/dev/null && docker inspect "$container" >/dev/null 2>&1; then
  docker inspect "$container" --format 'state {{.State.Status}}, started {{.State.StartedAt}}, OOM-killed last time {{.State.OOMKilled}}, exit code {{.State.ExitCode}}, limit {{.HostConfig.Memory}} bytes'
  docker stats --no-stream --format 'memory now {{.MemUsage}}' "$container" 2>/dev/null
else
  echo "no container named $container"
fi

echo
echo "== Killed for memory since the host started"
kills=$(dmesg -T 2>/dev/null | grep "Killed process")
if [ -n "$kills" ]; then echo "$kills" | tail -3; else echo "none (or dmesg not readable)"; fi

echo
echo "== Host"
echo "up since $(uptime -s 2>/dev/null)"
free -m | grep -E "^Mem"
