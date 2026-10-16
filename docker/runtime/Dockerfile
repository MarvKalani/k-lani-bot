FROM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:35d40304542c8689331f8cab17c65926cdf48fe711e289321d71924b230a7d29 AS build
WORKDIR /source
COPY Directory.Build.props global.json /source/
COPY tools/CodeStandardAnalyzer /source/tools/CodeStandardAnalyzer
COPY contracts /source/contracts
COPY assets/branding /source/assets/branding
COPY src/k-lani-bot-node /source/src/k-lani-bot-node
RUN dotnet publish src/k-lani-bot-node/k-lani-bot-node.csproj -c Release -f net10.0 -r linux-x64 \
    -p:TargetFrameworks=net10.0 --self-contained true -o /node --nologo -warnaserror

FROM ubuntu:24.04@sha256:008173c23f95b170204355c12626cb5a965d779a7e1283b09e9cffbb1bf33ca3 AS runtime
ENV DEBIAN_FRONTEND=noninteractive
COPY --from=build /etc/ssl/certs/ca-certificates.crt /etc/ssl/certs/ca-certificates.crt
RUN rm -f /etc/apt/sources.list.d/ubuntu.sources \
    && printf 'deb https://snapshot.ubuntu.com/ubuntu/20261007T000000Z noble main universe\ndeb https://snapshot.ubuntu.com/ubuntu/20261007T000000Z noble-updates main universe\ndeb https://snapshot.ubuntu.com/ubuntu/20261007T000000Z noble-security main universe\n' > /etc/apt/sources.list \
    && apt-get -o APT::Update::Error-Mode=any update && apt-get install -y --no-install-recommends \
    ca-certificates libglib2.0-bin xvfb fluxbox dbus-x11 xdotool xclip scrot wmctrl \
    at-spi2-core libatk-bridge2.0-0 libgtk-3-0t64 mousepad xfonts-base fonts-dejavu-core \
    libicu74 libssl3t64 libgssapi-krb5-2 \
    && dpkg-query -W -f='${Package}\t${Version}\n' > /usr/share/k-lani-bot-runtime-packages.tsv \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --uid 10001 --create-home k-lani-bot
COPY --from=build /node/k-lani-bot-node /app/k-lani-bot-node
COPY docker/runtime/entrypoint.sh docker/performance/session-exec.sh /app/
RUN chmod 755 /app/entrypoint.sh /app/session-exec.sh /app/k-lani-bot-node \
    && ! command -v dotnet && ! command -v node && ! command -v nodejs && ! command -v python && ! command -v python3
ENV DISPLAY=:99 LANG=C.UTF-8 LC_ALL=C.UTF-8 TZ=UTC NO_AT_BRIDGE=0 GTK_MODULES=atk-bridge \
    GDK_BACKEND=x11 GIO_USE_VFS=local GSETTINGS_BACKEND=memory \
    HOME=/tmp/home XDG_RUNTIME_DIR=/tmp/runtime XDG_CONFIG_HOME=/tmp/config \
    XDG_CACHE_HOME=/tmp/cache XDG_DATA_HOME=/tmp/data DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp/dotnet
WORKDIR /workspace
USER 10001:10001
ENTRYPOINT ["/app/entrypoint.sh"]
CMD ["/app/k-lani-bot-node", "--help"]
