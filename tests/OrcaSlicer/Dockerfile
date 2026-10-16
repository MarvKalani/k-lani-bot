# Development-only real-application acceptance image. OrcaSlicer is not bundled
# with the product. Build with the official extracted AppImage as context.
FROM k-lani-bot-actuator:local
USER root
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates locales librsvg2-common shared-mime-info libegl1 libglu1-mesa libopengl0 libwebkit2gtk-4.1-0 libmspack0 libsecret-1-0 libgl1-mesa-dri \
    && localedef -i en_US -f UTF-8 en_US.UTF-8 && localedef -i de_DE -f UTF-8 de_DE.UTF-8 \
    && dpkg-query -W > /usr/share/k-lani-bot-orcaslicer-test-packages.txt \
    && rm -rf /var/lib/apt/lists/*
COPY squashfs-root /opt/k-lani-bot-tests/orcaslicer-2.4.2
RUN chmod 755 /opt/k-lani-bot-tests/orcaslicer-2.4.2/AppRun
USER 10001:10001
ENV LIBGL_ALWAYS_SOFTWARE=1 SSL_CERT_FILE=/etc/ssl/certs/ca-certificates.crt
