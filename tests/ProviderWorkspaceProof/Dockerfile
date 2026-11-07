FROM alpine/git:2.45.2
USER 1000:1000
WORKDIR /workspace
ENTRYPOINT ["/bin/sh", "-ec", "exec busybox httpd -f -p 7681"]
