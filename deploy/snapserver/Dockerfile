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

COPY librespot-device-id.patch /tmp/librespot-device-id.patch
RUN git apply --check /tmp/librespot-device-id.patch \
    && git apply /tmp/librespot-device-id.patch

COPY librespot-session-events.patch /tmp/librespot-session-events.patch
RUN git apply --check /tmp/librespot-session-events.patch \
    && git apply /tmp/librespot-session-events.patch

COPY librespot-external-volume.patch /tmp/librespot-external-volume.patch
RUN git apply --check /tmp/librespot-external-volume.patch \
    && git apply /tmp/librespot-external-volume.patch

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

COPY spotify_destination_probe.py spotify_event.py spotify_sessions.py spotify_supervisor.py casting_sessions.py casting_supervisor.py casting_dbus_relay.py start-profile-audio.py \
    test_spotify_destination_probe.py test_spotify_sessions.py test_spotify_control.py test_spotify_event.py \
    test_casting_sessions.py test_casting_supervisor.py test_casting_dbus_relay.py test_start_profile_audio.py /tmp/spotify-probe/
COPY host/ /tmp/spotify-probe/host/
RUN python3 -m unittest discover -s /tmp/spotify-probe -p 'test_*.py' -v

FROM docker.io/library/debian:13-slim AS airplay-builder
RUN apt-get update \
    && apt-get install --yes --no-install-recommends ca-certificates git build-essential autoconf automake libtool pkg-config \
        libpopt-dev libconfig-dev libavahi-client-dev libssl-dev libplist-dev libplist-utils libsodium-dev uuid-dev \
        libgcrypt20-dev xxd libavutil-dev libavcodec-dev libavformat-dev libswresample-dev python3 dbus-daemon util-linux \
    && rm -rf /var/lib/apt/lists/*
ARG NQPTP_REVISION=0a88cd7ca9325f023ca5d891714695167672bdb9
WORKDIR /source/nqptp
RUN git init && git remote add origin https://github.com/mikebrady/nqptp.git \
    && git fetch --depth 1 origin "$NQPTP_REVISION" && git checkout --detach FETCH_HEAD
COPY nqptp-syren.patch /tmp/nqptp-syren.patch
RUN git apply --check /tmp/nqptp-syren.patch && git apply /tmp/nqptp-syren.patch \
    && autoreconf -fi && ./configure && make -j2 \
    && ./nqptp -V | grep -q -- '0a88cd7-dirty'
ARG SHAIRPORT_SYNC_REVISION=b45f15b32882409a7d39b01293d43da3d5519416
WORKDIR /source/shairport-sync
RUN git init && git remote add origin https://github.com/mikebrady/shairport-sync.git \
    && git fetch --depth 1 origin "$SHAIRPORT_SYNC_REVISION" && git checkout --detach FETCH_HEAD
COPY shairport-sync-syren.patch /tmp/shairport-sync-syren.patch
RUN git apply --check /tmp/shairport-sync-syren.patch && git apply /tmp/shairport-sync-syren.patch
# Both sides must speak the same shared memory interface version.
RUN test "$(grep -h 'define NQPTP_SHM_STRUCTURES_VERSION' nqptp-shm-structures.h)" = \
         "$(grep -h 'define NQPTP_SHM_STRUCTURES_VERSION' /source/nqptp/nqptp-shm-structures.h)"
RUN autoreconf -fi \
    && ./configure --sysconfdir=/etc --with-airplay-2 --with-ssl=openssl --with-avahi \
        --with-pipe --with-metadata --with-metadata-pipe \
    && make -j2 \
    && ./shairport-sync -V | tee /tmp/shairport-version \
    && grep -q -- '-dirty-AirPlay2-smi11' /tmp/shairport-version && grep -q -- '-Avahi' /tmp/shairport-version \
    && grep -q -- '-pipe' /tmp/shairport-version && grep -q -- '-metadata' /tmp/shairport-version
# The smoke runs the binary from where the runtime image keeps it, because Landlock only allows running files under /usr.
RUN install -m 0755 /source/shairport-sync/shairport-sync /usr/local/bin/shairport-sync
COPY spotify_sessions.py spotify_supervisor.py casting_sessions.py casting_dbus_relay.py smoke_casting.py /tmp/casting/
RUN python3 -I /tmp/casting/smoke_casting.py --shairport-sync /usr/local/bin/shairport-sync --nqptp /source/nqptp/nqptp

FROM docker.io/library/debian:13-slim

ARG SNAPSERVER_URL=https://github.com/snapcast/snapcast/releases/download/v0.35.0/snapserver_0.35.0-1_amd64_trixie.deb
ARG SNAPSERVER_SHA256=6a82325cf9eae68b75fb062491639cb31ec58288fe8930f53e86689e22a095da

RUN apt-get update \
    && apt-get install --yes --no-install-recommends ca-certificates curl iproute2 python3 python3-paho-mqtt \
        libpopt0 libconfig11 libplist-2.0-4 libsodium23 libavutil59 libavcodec61 libavformat61 libswresample5 libssl3t64 \
        libavahi-client3 libgcrypt20 libuuid1 \
    && curl --fail --location --retry 3 "$SNAPSERVER_URL" --output /tmp/snapserver.deb \
    && echo "$SNAPSERVER_SHA256  /tmp/snapserver.deb" | sha256sum --check \
    && apt-get install --yes --no-install-recommends /tmp/snapserver.deb \
    && rm /tmp/snapserver.deb \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

RUN install -d -o snapserver -g snapserver -m 750 /var/lib/snapserver

COPY --from=librespot-builder /usr/local/cargo/bin/librespot /usr/local/bin/librespot
COPY --from=snapserver-builder /source/snapcast/bin/snapserver /usr/bin/snapserver
COPY --from=airplay-builder /source/shairport-sync/shairport-sync /usr/local/bin/shairport-sync
COPY --from=airplay-builder /source/nqptp/nqptp /usr/share/syrensystem/host/nqptp
COPY casting_dbus_relay.py /usr/share/syrensystem/host/syren-avahi-relay.py
COPY host/ /usr/share/syrensystem/host/
RUN cd /usr/share/syrensystem/host \
    && sha256sum nqptp syren-avahi-relay.py syren-nqptp.service syren-avahi-relay.service \
        syrensystem-casting.sysusers.conf syrensystem-casting.tmpfiles.conf > SHA256SUMS \
    && /usr/local/bin/shairport-sync -V
COPY versions.json /usr/share/syrensystem/audio-versions.json
COPY snapserver.conf /etc/snapserver.conf
COPY --chmod=755 start-snapserver.sh /usr/local/bin/start-snapserver

COPY --chmod=755 spotify_event.py spotify_sessions.py spotify_supervisor.py start-profile-audio.py casting_sessions.py casting_supervisor.py casting_dbus_relay.py /usr/local/bin/

USER snapserver

ENTRYPOINT ["/usr/local/bin/start-snapserver"]
