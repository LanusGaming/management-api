FROM alpine:3.22

# kmod for modprobe of the compressed kheaders module; bpftrace brings LLVM/Clang to compile fileio.bt
RUN apk add --no-cache bpftrace kmod

COPY fileio.bt run.sh /

ENTRYPOINT ["/bin/sh", "/run.sh"]
