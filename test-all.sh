#!/usr/bin/env bash
# Run the control-plane test suite the way CI runs it: three separate `dotnet test` processes.
#
# WHY THIS EXISTS (HOUSE-01, 2026-09-07): a bare `dotnet test` reports 6 FAILURES here, and none of
# them is a regression. WebApplicationFactory and FastEndpoints.Factory.Create both mutate the same
# global `FastEndpoints.Config.ServiceResolver`; sharing one parallel pool lets a Factory.Create test
# read a provider a full-host test already disposed (ObjectDisposedException). CI (.github/workflows/ci.yml,
# as titan's CI did since 2026-08-28) splits these into separate steps — this script is the same split, locally, so
# "green" means the same thing on both sides of the fence.
#
# FullHost and Integration tests need a real Postgres (Titan.Tests/Infrastructure/Integration/TestPostgres.cs), first that works:
#   1. TITAN_IT_POSTGRES already set ⇒ that server (loopback only).
#   2. The dev cluster on this machine is up ⇒ port-forward its database to 127.0.0.1:15432, role titan_it.
#   3. Docker ⇒ a throwaway postgres:16 container, as CI does.
# None of them ⇒ RESULT: INCOMPLETE and exit 2: they are the only coverage of real migrations and of the
# money-path concurrency guards, so a run without them is not a pass.
#
# Usage:  ./test-all.sh [--no-build]
# Exit:   0 every step ran and passed · 1 a step failed · 2 full-host and integration did not run, or the
#         port-forward to the dev database died during them
set -uo pipefail

cd "$(dirname "$0")"

BUILD_ARG="${1:-}"
if [ "$BUILD_ARG" != "--no-build" ]; then
    echo "==> build (--no-incremental, so compiler warnings are actually printed)"
    dotnet build --no-incremental || exit 1
fi

failed=0

run_step() {
    local label="$1" filter="$2"
    echo
    echo "==> $label"
    if ! dotnet test --no-build --filter "$filter"; then
        failed=1
    fi
}

run_step "unit"      'Category!=Integration&Category!=FullHost'

DEV_KUBECONFIG="$HOME/.lima/titan-k3s/copied-from-guest/kubeconfig.yaml"
# Same value as https://github.com/OpenViCloud/titan-local-cluster/blob/main/lima/scripts/bootstrap-secrets.sh: a test password, the cluster only listens on 127.0.0.1.
# SSL off: .NET on macOS fails the TLS handshake with CNPG ("bad protocol version"); the hop is loopback into
# kubectl's port-forward tunnel, not the network.
DEV_TEST_SERVER="Host=127.0.0.1;Port=15432;Username=titan_it;Password=titan-it-local-only;Database=postgres;SSL Mode=Disable"
port_forward_pid=""

stop_port_forward() {
    if [ -n "$port_forward_pid" ]; then
        kill "$port_forward_pid" 2>/dev/null
    fi
}
trap stop_port_forward EXIT

# Returns 0 once 127.0.0.1:15432 accepts a connection, 1 after 30 seconds.
wait_for_port() {
    python3 -I -c '
import socket, sys, time
for _ in range(60):
    try:
        socket.create_connection(("127.0.0.1", 15432), timeout=1).close()
        sys.exit(0)
    except OSError:
        time.sleep(0.5)
sys.exit(1)'
}

integration_source=""
if [ -n "${TITAN_IT_POSTGRES:-}" ]; then
    integration_source="TITAN_IT_POSTGRES (already set)"
elif [ -f "$DEV_KUBECONFIG" ] && kubectl --kubeconfig "$DEV_KUBECONFIG" --request-timeout=10s -n titan-db-staging get svc titan-db-rw >/dev/null 2>&1; then
    server=$(kubectl --kubeconfig "$DEV_KUBECONFIG" config view --minify -o 'jsonpath={.clusters[0].cluster.server}')
    kubectl --kubeconfig "$DEV_KUBECONFIG" -n titan-db-staging port-forward svc/titan-db-rw 15432:5432 >/dev/null 2>&1 &
    port_forward_pid=$!
    if wait_for_port; then
        export TITAN_IT_POSTGRES="$DEV_TEST_SERVER"
        integration_source="dev cluster $server, port-forward to 127.0.0.1:15432"
    else
        echo
        echo "==> port-forward to the dev cluster database did not open 127.0.0.1:15432 within 30 s"
    fi
fi
if [ -z "$integration_source" ] && docker info >/dev/null 2>&1; then
    integration_source="Docker (Testcontainers postgres:16)"
fi

# The port-forward can die mid-run: every test after that fails on a refused connection, hundreds of red lines that
# say nothing about the code. Checked before and after each database step; a dead tunnel makes the run INCOMPLETE.
tunnel_died_in=""

tunnel_alive() {
    if [ -z "$port_forward_pid" ]; then
        return 0
    fi
    kill -0 "$port_forward_pid" 2>/dev/null && python3 -I -c '
import socket, sys
try:
    socket.create_connection(("127.0.0.1", 15432), timeout=2).close()
except OSError:
    sys.exit(1)'
}

run_db_step() {
    local label="$1" filter="$2"
    if [ -n "$tunnel_died_in" ]; then
        echo
        echo "==> $label: skipped, the port-forward died during $tunnel_died_in"
        return
    fi
    if ! tunnel_alive; then
        tunnel_died_in="the gap before $label"
        echo
        echo "==> $label: skipped, the port-forward to the dev database is gone"
        return
    fi
    local failed_before="$failed"
    run_step "$label" "$filter"
    if ! tunnel_alive; then
        tunnel_died_in="$label"
        failed="$failed_before"
        echo "==> $label: the port-forward to the dev database died during this step; its failures are not counted"
    fi
}

# FullHost runs the real host on its own Postgres database (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/testing.md#doubles-only-in-unit-tests),
# so it needs the same server as Integration.
if [ -n "$integration_source" ]; then
    echo
    echo "==> Postgres for full-host and integration: $integration_source"
    run_db_step "full-host" 'Category=FullHost'
    run_db_step "integration" 'Category=Integration'
fi

echo
# A failure measured before the tunnel died is real, so FAILED wins over INCOMPLETE.
if [ "$failed" -ne 0 ]; then
    echo "RESULT: FAILED"
    exit 1
fi
if [ -n "$tunnel_died_in" ]; then
    echo "RESULT: INCOMPLETE — the port-forward to the dev cluster database died during $tunnel_died_in."
    echo "        Full-host and integration after that point did not measure the code; run again."
    exit 2
fi
if [ -z "$integration_source" ]; then
    echo "RESULT: INCOMPLETE — full-host and integration did not run: no TITAN_IT_POSTGRES, dev cluster unreachable, no Docker."
    echo "        It is the only coverage of migrations and money-path concurrency; CI steps"
    echo "        'Test (full-host — WebApplicationFactory)' and 'Test (integration — Testcontainers)' are the gate for this change."
    exit 2
fi
echo "RESULT: all steps passed"
