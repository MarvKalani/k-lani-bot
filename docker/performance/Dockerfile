FROM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:35d40304542c8689331f8cab17c65926cdf48fe711e289321d71924b230a7d29 AS build
WORKDIR /source
COPY Directory.Build.props global.json /source/
COPY tools/CodeStandardAnalyzer /source/tools/CodeStandardAnalyzer
COPY tools/k-lani-bot-cartridge-lab /source/tools/k-lani-bot-cartridge-lab
COPY contracts /source/contracts
COPY assets/branding /source/assets/branding
COPY src/k-lani-bot-node /source/src/k-lani-bot-node
RUN dotnet publish src/k-lani-bot-node/k-lani-bot-node.csproj -c Release -f net10.0 -r linux-x64 \
    -p:TargetFrameworks=net10.0 -o /node --nologo -warnaserror \
    && dotnet publish tools/k-lani-bot-cartridge-lab/k-lani-bot-cartridge-lab.csproj -c Release -r linux-x64 \
    --self-contained true -p:PublishSingleFile=true -o /lab --nologo -warnaserror

FROM ubuntu:24.04@sha256:008173c23f95b170204355c12626cb5a965d779a7e1283b09e9cffbb1bf33ca3 AS desktop
ENV DEBIAN_FRONTEND=noninteractive
COPY --from=build /etc/ssl/certs/ca-certificates.crt /etc/ssl/certs/ca-certificates.crt
# A dated, signed archive freezes dependency resolution, including transitive packages.
RUN rm -f /etc/apt/sources.list.d/ubuntu.sources \
    && printf 'deb https://snapshot.ubuntu.com/ubuntu/20261007T000000Z noble main universe\ndeb https://snapshot.ubuntu.com/ubuntu/20261007T000000Z noble-updates main universe\ndeb https://snapshot.ubuntu.com/ubuntu/20261007T000000Z noble-security main universe\n' > /etc/apt/sources.list \
    && apt-get -o APT::Update::Error-Mode=any update && apt-get install -y --no-install-recommends \
    ca-certificates libglib2.0-bin xvfb openbox dbus-x11 xdotool xclip scrot wmctrl at-spi2-core libatk-bridge2.0-0 libgtk-3-0t64 \
    python3 python3-gi python3-pyatspi python3-pil python3-reportlab python3-uno \
    converseen xarchiver mousepad libreoffice-calc poppler-utils webp zip unzip \
    xfonts-base fonts-dejavu-core libicu74 libssl3t64 libgssapi-krb5-2 \
    && dpkg-query -W -f='${Package}\t${Version}\n' > /usr/share/k-lani-bot-performance-packages.tsv \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --uid 10001 --create-home k-lani-bot
COPY --from=build /node/k-lani-bot-node /app/k-lani-bot-node
COPY --from=build /lab/k-lani-bot-cartridge-lab /app/k-lani-bot-cartridge-lab
COPY docker/performance/entrypoint.sh docker/performance/capabilities.py docker/performance/session-exec.sh /app/
RUN chmod 755 /app/entrypoint.sh /app/session-exec.sh /app/k-lani-bot-node /app/k-lani-bot-cartridge-lab
ENV DISPLAY=:99 LANG=C.UTF-8 LC_ALL=C.UTF-8 TZ=UTC NO_AT_BRIDGE=0 GTK_MODULES=atk-bridge \
    QT_LINUX_ACCESSIBILITY_ALWAYS_ON=1 GDK_BACKEND=x11 GIO_USE_VFS=local GSETTINGS_BACKEND=memory \
    HOME=/tmp/home XDG_RUNTIME_DIR=/tmp/runtime XDG_CONFIG_HOME=/tmp/config \
    XDG_CACHE_HOME=/tmp/cache XDG_DATA_HOME=/tmp/data DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp/dotnet
WORKDIR /workspace
USER 10001:10001
ENTRYPOINT ["/app/entrypoint.sh"]
CMD ["/app/k-lani-bot-node", "--help"]

FROM desktop AS mail
USER root
RUN apt-get -o APT::Update::Error-Mode=any update && apt-get install -y --no-install-recommends thunderbird evince tesseract-ocr \
    && dpkg-query -W -f='${Package}\t${Version}\n' > /usr/share/k-lani-bot-performance-packages.tsv \
    && rm -rf /var/lib/apt/lists/*
USER 10001:10001
