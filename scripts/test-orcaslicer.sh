#!/usr/bin/env bash
set -euo pipefail
repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$repo_root"
mode=${1:-fixture}
[[ "$mode" == fixture || "$mode" == real || "$mode" == real-replay ]] || { printf 'Usage: scripts/test-orcaslicer.sh [fixture|real|real-replay]\n' >&2; exit 2; }
appimage=.artifacts/orcaslicer/OrcaSlicer-2.4.2.AppImage
[[ -f "$appimage" ]] || { printf 'Supply the official OrcaSlicer 2.4.2 Ubuntu 24.04 AppImage at %s. Download provenance and SHA-256 are documented in the cartridge.\n' "$appimage" >&2; exit 2; }
printf '%s  %s\n' d12fb8c8eac1aecd2dfb6377acd48f994f8fa439ed5292fa532dd82880f029fd "$appimage" | sha256sum --check --status
if [[ ! -d .artifacts/orcaslicer/squashfs-root ]]; then
    chmod 755 "$appimage"
    (cd .artifacts/orcaslicer && ./OrcaSlicer-2.4.2.AppImage --appimage-extract > extract.log)
fi
dotnet publish tools/k-lani-bot-cartridge-lab/k-lani-bot-cartridge-lab.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o .artifacts/orcaslicer/fixture --nologo > .artifacts/orcaslicer/fixture-build.log 2>&1
dotnet build src/k-lani-bot-node/k-lani-bot-node.csproj -f net10.0 -p:TargetFrameworks=net10.0 -r linux-x64 --nologo > .artifacts/orcaslicer/node-build.log 2>&1
docker build -f docker/Dockerfile.node -t k-lani-bot-actuator:local . > .artifacts/orcaslicer/node-image-build.log 2>&1
docker build -f "$repo_root/tests/OrcaSlicer/Dockerfile" -t k-lani-bot-orcaslicer-test:2.4.2 .artifacts/orcaslicer > .artifacts/orcaslicer/docker-build.log 2>&1
dotnet run --project tests/OrcaSlicer/OrcaSlicer.csproj --verbosity quiet -- "$repo_root/src/k-lani-bot-node/bin/Debug/net10.0/linux-x64/k-lani-bot-node.dll" "$repo_root" "$mode"
