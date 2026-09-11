FROM docker.io/library/rust:1.98.1-slim-trixie AS librespot-builder

RUN apt-get update \
    && apt-get install --yes --no-install-recommends ca-certificates git pkg-config \
    && rm -rf /var/lib/apt/lists/*

ARG LIBRESPOT_REVISION=a1b66d3c8a14e55a9572a9e17467150dca618c9a
WORKDIR /source/librespot
RUN git init \
    && git remote add origin https://github.com/librespot-org/librespot.git \
    && git fetch --depth 1 origin "$LIBRESPOT_REVISION" \
    && git checkout --detach FETCH_HEAD

COPY librespot-recovery.patch /tmp/librespot-recovery.patch
RUN git apply --check /tmp/librespot-recovery.patch \
    && git apply /tmp/librespot-recovery.patch

RUN cargo test --locked --workspace \
    --no-default-features \
    --features rustls-tls-webpki-roots,with-libmdns
RUN cargo install --path . --locked \
    --no-default-features \
    --features rustls-tls-webpki-roots,with-libmdns

FROM docker.io/library/debian:13-slim AS snapserver-builder

RUN apt-get update \
    && apt-get install --yes --no-install-recommends ca-certificates git cmake ninja-build g++ pkg-config \
        libasound2-dev libavahi-client-dev libboost-dev libexpat1-dev libflac-dev libopus-dev libsoxr-dev libssl-dev libvorbis-dev python3 \
    && rm -rf /var/lib/apt/lists/*
ARG SNAPSERVER_REVISION=f12373479243e93a97237592d6a3703539ec41d5
WORKDIR /source/snapcast
RUN git init \
    && git remote add origin https://github.com/snapcast/snapcast.git \
    && git fetch --depth 1 origin "$SNAPSERVER_REVISION" \
    && git checkout --detach FETCH_HEAD
COPY snapserver-lifecycle.patch /tmp/snapserver-lifecycle.patch
RUN git apply --check /tmp/snapserver-lifecycle.patch \
    && git apply /tmp/snapserver-lifecycle.patch
RUN cmake -S . -B build -G Ninja -DCMAKE_BUILD_TYPE=Release -DBUILD_CLIENT=OFF -DREVISION=syren-lifecycle \
    && cmake --build build --parallel 2
COPY smoke_snapserver.py /tmp/smoke_snapserver.py
RUN python3 /tmp/smoke_snapserver.py --executable /source/snapcast/bin/snapserver

FROM docker.io/library/debian:13-slim

ARG SNAPSERVER_URL=https://github.com/snapcast/snapcast/releases/download/v0.35.0/snapserver_0.35.0-1_amd64_trixie.deb
ARG SNAPSERVER_SHA256=6a82325cf9eae68b75fb062491639cb31ec58288fe8930f53e86689e22a095da

RUN apt-get update \
    && apt-get install --yes --no-install-recommends ca-certificates curl iproute2 \
    && curl --fail --location --retry 3 "$SNAPSERVER_URL" --output /tmp/snapserver.deb \
    && echo "$SNAPSERVER_SHA256  /tmp/snapserver.deb" | sha256sum --check \
    && apt-get install --yes --no-install-recommends /tmp/snapserver.deb \
    && rm /tmp/snapserver.deb \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

RUN install -d -o snapserver -g snapserver -m 750 /var/lib/snapserver

COPY --from=librespot-builder /usr/local/cargo/bin/librespot /usr/local/bin/librespot
COPY --from=snapserver-builder /source/snapcast/bin/snapserver /usr/bin/snapserver
COPY versions.json /usr/share/syrensystem/audio-versions.json
COPY snapserver.conf /etc/snapserver.conf
COPY --chmod=755 start-snapserver.sh /usr/local/bin/start-snapserver

USER snapserver

ENTRYPOINT ["/usr/local/bin/start-snapserver"]
