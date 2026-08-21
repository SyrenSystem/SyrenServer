FROM rust:1.85-bookworm AS librespot-builder

RUN cargo install librespot \
    --version 0.8.0 \
    --locked \
    --no-default-features \
    --features rustls-tls-webpki-roots,with-libmdns

FROM debian:13-slim

RUN apt-get update \
    && apt-get install --yes --no-install-recommends ca-certificates iproute2 snapserver=0.31.0-1 \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

COPY --from=librespot-builder /usr/local/cargo/bin/librespot /usr/local/bin/librespot
COPY snapserver.conf /etc/snapserver.conf
COPY --chmod=755 start-snapserver.sh /usr/local/bin/start-snapserver

USER _snapserver

ENTRYPOINT ["/usr/local/bin/start-snapserver"]
