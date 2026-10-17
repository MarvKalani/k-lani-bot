#!/usr/bin/env bash
set -euo pipefail
repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$repo_root"
source scripts/check-clock.sh
# An unhandled exception in a regression process would otherwise leave a core dump of tens of MB per red run;
# the non-zero exit code and the logged message already carry the failure.
ulimit -c 0
mkdir -p .artifacts
test_python=${K_LANI_BOT_TEST_PYTHON:-python3}
# One complete build supplies every regression executable. Invoking the DLLs
# directly avoids another restore/build and cannot silently select another RID.
build_exit_code=0
scripts/build.sh --check > .artifacts/all-project-builds.log 2>&1 || build_exit_code=$?
if [[ "$build_exit_code" -ne 0 ]]; then
    cat .artifacts/all-project-builds.log >&2
    exit "$build_exit_code"
fi
runtime_root="$repo_root/.artifacts/check-build/linux/bin"
node_binary="$runtime_root/k-lani-bot-node/debug/k-lani-bot-node.dll"
printf 'step\telapsed_ms\texit_code\n' > .artifacts/regression-timings.tsv

run_test() {
    local label=$1
    local log=$2
    shift 2
    local started finished exit_code=0
    started=$(check_now_ms)
    "$@" > "$log" 2>&1 || exit_code=$?
    finished=$(check_now_ms)
    printf '%s\t%s\t%s\n' "$label" "$((finished - started))" "$exit_code" >> .artifacts/regression-timings.tsv
    if [[ "$exit_code" -ne 0 ]]; then
        cat "$log" >&2
        return "$exit_code"
    fi
}

# A failing suite is recorded instead of ending the run, so one failure does not hide the state of the others.
# The run still ends with a non-zero exit code and names every failed step.
failed_steps=()
check_step() {
    local label=$1
    run_test "$@" || failed_steps+=("$label")
}

check_step node .artifacts/node-tests.log dotnet "$runtime_root/NodeRegression/debug/NodeRegression.dll"
check_step compact-ui .artifacts/compact-ui-tests.log dotnet "$runtime_root/CompactUiRegression/debug/CompactUiRegression.dll"
check_step schema-export .artifacts/schema-export.log scripts/verify-schema-export.sh "$node_binary"
check_step mail-review .artifacts/mail-review-tests.log node --test modules/mail-review/*.test.mjs
check_step agent-authoring-example .artifacts/agent-authoring-example-tests.log "$test_python" -m unittest discover \
    -s examples/agent-authoring/mail-to-tryton -p 'test_*.py'
check_step efficiency-benchmark .artifacts/efficiency-benchmark-tests.log node --test \
    modules/efficiency-benchmark/*.test.mjs modules/efficiency-benchmark/browser-workbench/*.test.mjs modules/desktop-ui-mcp/*.test.mjs
check_step performance-contract .artifacts/performance-contract-tests.log "$test_python" -m unittest discover \
    -s tests/PerformanceRegression -p 'test_*.py'
check_step performance-state .artifacts/performance-state-tests.log dotnet "$runtime_root/PerformanceRegression/debug/PerformanceRegression.dll"
check_step app-scanner .artifacts/app-scanner-tests.log dotnet "$runtime_root/AppScannerRegression/debug/AppScannerRegression.dll"
check_step cartridge .artifacts/cartridge-tests.log dotnet "$runtime_root/CartridgeRegression/debug/CartridgeRegression.dll" "$repo_root/cartridges/apple/macos-27-de/cartridge.json"
check_step publication-guard .artifacts/repository-guard-tests.log dotnet "$runtime_root/RepositoryGuardRegression/debug/RepositoryGuardRegression.dll"
check_step visibility .artifacts/visibility-tests.log dotnet "$runtime_root/VisibilityRegression/debug/VisibilityRegression.dll"
check_step cartridge-intake .artifacts/cartridge-intake-tests.log dotnet "$runtime_root/CartridgeReviewRegression/debug/CartridgeReviewRegression.dll" "$repo_root/examples/cartridges/k-lani-bot-fixture/1.0.0" "$repo_root/contracts/cartridges/proposed-v1/manifest.schema.json"
check_step cartridge-safety .artifacts/cartridge-safety-tests.log dotnet "$runtime_root/CartridgeSafetyRegression/debug/CartridgeSafetyRegression.dll"
check_step release-approval .artifacts/release-review-tests.log dotnet "$runtime_root/ReleaseReviewRegression/debug/ReleaseReviewRegression.dll"
check_step service-assessment .artifacts/service-assessment-tests.log dotnet "$runtime_root/ServiceAssessmentRegression/debug/ServiceAssessmentRegression.dll"
mkdir -p .artifacts/activity-banner-linux
# Every step that can create a window goes through the private session script. It removes the
# operator's display variables (including GDK_BACKEND) and gives the run its own X server, bus
# and runtime directory, so a Wayland desktop cannot be reached by accident.
check_step activity-banner .artifacts/activity-banner-linux.log scripts/private-gui-session.sh \
    dotnet "$runtime_root/VisibilityNativeProbe/debug/k-lani-bot-visibility-probe.dll" "$repo_root/.artifacts/activity-banner-linux"

# A host desktop service started on the private bus has no display and aborts with a core dump per request.
check_step private-bus-services .artifacts/private-bus-services.log scripts/private-gui-session.sh \
    scripts/verify-private-bus-services.sh

if command -v gnome-keyring-daemon >/dev/null && command -v gdbus >/dev/null; then
    check_step credential-provider .artifacts/credential-provider-tests.log scripts/private-gui-session.sh \
        bash scripts/verify-private-credentials.sh "$runtime_root/NodeRegression/debug/NodeRegression.dll"
else
    printf 'SKIP native credential provider: private Secret Service prerequisites unavailable.\n' \
        > .artifacts/credential-provider-tests.log
fi

# The real accessibility backend against a real GTK window with a modal dialog.
check_step modal-dialog .artifacts/modal-dialog.log scripts/private-gui-session.sh \
    dotnet "$runtime_root/ModalDialogRegression/debug/ModalDialogRegression.dll" \
    "$runtime_root/k-lani-bot-cartridge-lab/debug/k-lani-bot-cartridge-lab.dll"

check_step dynamic-form .artifacts/dynamic-form-tests.log scripts/private-gui-session.sh \
    dotnet "$runtime_root/PerformanceRegression/debug/PerformanceRegression.dll" --native \
    "$runtime_root/k-lani-bot-cartridge-lab/debug/k-lani-bot-cartridge-lab.dll"

# A function learned for one synthetic build label is probed against real windows labelled with another build.
# Without wmctrl the Linux window list is unavailable and the step reports a skip, as the transport Info check does.
check_step build-drift .artifacts/build-drift-native.log scripts/private-gui-session.sh scripts/private-window-manager.sh \
    dotnet "$runtime_root/PerformanceRegression/debug/PerformanceRegression.dll" --native-drift \
    "$runtime_root/k-lani-bot-cartridge-lab/debug/k-lani-bot-cartridge-lab.dll"

# These fixtures have independent state, displays, buses and dynamically chosen
# ports. Running them together never touches the operator's physical desktop.
run_test transport .artifacts/transport-e2e.log scripts/private-gui-session.sh \
    scripts/private-window-manager.sh \
    dotnet "$runtime_root/TransportRegression/debug/TransportRegression.dll" "$node_binary" &
transport_test=$!
run_test pairing .artifacts/pairing-e2e.log scripts/private-gui-session.sh \
    dotnet "$runtime_root/PairingRegression/debug/PairingRegression.dll" "$node_binary" &
pairing_test=$!
wait "$transport_test" || failed_steps+=("transport")
wait "$pairing_test" || failed_steps+=("pairing")
if [[ ${#failed_steps[@]} -gt 0 ]]; then
    printf 'Failed steps: %s\n' "${failed_steps[*]}" >&2
    exit 1
fi

for log in node-tests app-scanner-tests cartridge-tests repository-guard-tests visibility-tests cartridge-intake-tests cartridge-safety-tests release-review-tests service-assessment-tests; do
    tail -1 ".artifacts/$log.log"
done
grep -H 'regression checks passed\.' .artifacts/transport-e2e.log .artifacts/pairing-e2e.log
grep -H 'Linux native assignment checks passed' .artifacts/activity-banner-linux.log
grep -H 'modal dialog regression checks passed' .artifacts/modal-dialog.log
grep -H -E 'native build-drift checks passed|SKIP native build drift' .artifacts/build-drift-native.log
printf 'All projects and platform builds passed; real-device coverage is separate.\n'
printf 'Per-step timings: .artifacts/check-build/timings.tsv and .artifacts/regression-timings.tsv\n'
if [ "${K_LANI_BOT_CHECK_GUI:-0}" = 1 ]; then
    docker build -f docker/Dockerfile.node -t k-lani-bot-actuator:local . > .artifacts/sandbox-build.log 2>&1
    run_test sandbox .artifacts/sandbox-tests.log dotnet "$runtime_root/SandboxRegression/debug/SandboxRegression.dll" "$node_binary"
    tail -1 .artifacts/sandbox-tests.log
fi
