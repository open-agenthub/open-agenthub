FROM node:26-bookworm-slim

ENV DEBIAN_FRONTEND=noninteractive
# The two test -s lines: WebGL in a pod without a GPU is rendered by the software rasteriser
# that the distribution's chromium-common happens to ship. Nothing declares that as a
# dependency, so a package update that drops it would build fine and only show up as pages
# without a WebGL context. Fail the build instead.
RUN set -eux; \
    apt-get update; \
    chromium_version="$(apt-cache policy chromium | awk '/Candidate:/ {print $2}')"; \
    xvfb_version="$(apt-cache policy xvfb | awk '/Candidate:/ {print $2}')"; \
    x11vnc_version="$(apt-cache policy x11vnc | awk '/Candidate:/ {print $2}')"; \
    xrandr_version="$(apt-cache policy x11-xserver-utils | awk '/Candidate:/ {print $2}')"; \
    websockify_version="$(apt-cache policy websockify | awk '/Candidate:/ {print $2}')"; \
    socat_version="$(apt-cache policy socat | awk '/Candidate:/ {print $2}')"; \
    curl_version="$(apt-cache policy curl | awk '/Candidate:/ {print $2}')"; \
    ca_version="$(apt-cache policy ca-certificates | awk '/Candidate:/ {print $2}')"; \
    apt-get install -y --no-install-recommends \
      "chromium=${chromium_version}" "xvfb=${xvfb_version}" \
      "x11vnc=${x11vnc_version}" "x11-xserver-utils=${xrandr_version}" "websockify=${websockify_version}" \
      "socat=${socat_version}" "curl=${curl_version}" "ca-certificates=${ca_version}"; \
    test -s /usr/lib/chromium/libvk_swiftshader.so; \
    test -s /usr/lib/chromium/vk_swiftshader_icd.json; \
    rm -rf /var/lib/apt/lists/*

WORKDIR /opt/browser
COPY package.json package-lock.json ./
RUN npm ci --omit=dev && npm cache clean --force
COPY supervisor.mjs entrypoint.sh ./
RUN chmod 0555 /opt/browser/entrypoint.sh \
    && mkdir -p /data/chromium /data/home \
    && chown -R 1000:1000 /data /opt/browser

ENV HOME=/data/home
EXPOSE 6080 6081 6082 9222
USER 1000:1000
ENTRYPOINT ["/opt/browser/entrypoint.sh"]
