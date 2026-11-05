#!/bin/sh
# Counts regular-file I/O per Docker container with eBPF (fileio.bt) and writes the totals to $OUT once per
# second, one "<container id> <read bytes> <write bytes>" line per container, for management_api to read.
# Totals start at 0 when the tracer (or the container) starts.
#
# Needs: privileged, the host's cgroup namespace (cgroup: host) so /sys/fs/cgroup/docker is visible,
# init: true so docker stop reaches this script, and the host's /lib/modules to load kheaders. This kernel
# has no BTF, so bpftrace reads struct layouts from the kernel's own headers, which kheaders provides.
set -e
OUT=${OUT:-/run/fileio/counters}

modprobe kheaders
rm -rf /kh && mkdir /kh && tar -C /kh -xJf /sys/kernel/kheaders.tar.xz
mountpoint -q /sys/kernel/tracing || mount -t tracefs nodev /sys/kernel/tracing
mkdir -p "$(dirname "$OUT")"

# bpftrace keys its totals by cgroup id, which is the inode number of the cgroup's directory. Each "end"
# line, the ids are mapped to containers (/sys/fs/cgroup/docker/<id>, including nested cgroups) and the
# file is replaced in one rename, so readers never see a half-written one.
BPFTRACE_KERNEL_SOURCE=/kh bpftrace /fileio.bt | awk -v out="$OUT" '
  /^@read\[/  { split($0, a, /[][]/); r[a[2]] = $NF; next }
  /^@write\[/ { split($0, a, /[][]/); w[a[2]] = $NF; next }
  /^end$/ {
    split("", owner); split("", read); split("", write)
    cmd = "find /sys/fs/cgroup/docker -mindepth 1 -type d -exec stat -c \"%i %n\" {} +"
    while ((cmd | getline line) > 0) {
      split(line, f, " "); split(f[2], p, "/")
      owner[f[1]] = p[6]; read[p[6]] += 0; write[p[6]] += 0
    }
    close(cmd)
    for (id in r) if (id in owner) read[owner[id]] += r[id]
    for (id in w) if (id in owner) write[owner[id]] += w[id]
    tmp = out ".tmp"
    printf "" > tmp
    for (c in read) printf "%s %.0f %.0f\n", c, read[c], write[c] > tmp
    close(tmp)
    system("mv " tmp " " out)
  }
'
