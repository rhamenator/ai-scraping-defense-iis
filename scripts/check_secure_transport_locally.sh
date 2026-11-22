#!/usr/bin/env bash
# Exercises Management:RequireSecureTransport (#186) against the real edge app on this machine, with a throwaway
# self-signed certificate and a small proxy that stands in for a TLS-terminating ingress. Nothing leaves localhost.
#
#   dotnet build RedisBlocklistMiddlewareApp/RedisBlocklistMiddlewareApp.csproj
#   scripts/check_secure_transport_locally.sh
#
# Needs: dotnet, openssl, curl, python3, and docker (for a throwaway Redis; set REDIS=host:port to use your own).
set -u
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APP_DIR="${APP_DIR:-$ROOT/RedisBlocklistMiddlewareApp/bin/Debug/net10.0}"
WORK="$(mktemp -d)"; KEY="local-test-key-only"; FAILS=0; PIDS=(); OWN_REDIS=0
cleanup() { for p in "${PIDS[@]}"; do kill "$p" 2>/dev/null; done; [ "$OWN_REDIS" = 1 ] && docker rm -f asd-securetransport-redis >/dev/null 2>&1; rm -rf "$WORK"; }
trap cleanup EXIT

if [ -z "${REDIS:-}" ]; then
  docker run -d --rm --name asd-securetransport-redis -p 127.0.0.1:16379:6379 redis:7-alpine >/dev/null || { echo "need docker or REDIS=host:port"; exit 2; }
  REDIS=127.0.0.1:16379; OWN_REDIS=1
fi
PFX_PASSWORD="$(openssl rand -hex 12)"
openssl req -x509 -newkey rsa:2048 -nodes -keyout "$WORK/key.pem" -out "$WORK/crt.pem" -subj "/CN=localhost" -days 1 \
  -addext "subjectAltName=DNS:localhost,IP:127.0.0.1" 2>/dev/null
openssl pkcs12 -export -out "$WORK/cert.pfx" -inkey "$WORK/key.pem" -in "$WORK/crt.pem" -passout "pass:$PFX_PASSWORD" 2>/dev/null

start_app() {  # port, then extra env assignments
  local port=$1; shift
  # exec, so the recorded PID is the app itself and cleanup really stops it
  (cd "$APP_DIR" && exec env ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS="${URLS:-http://127.0.0.1:$port}" \
     DefenseEngine__Redis__ConnectionString="$REDIS" DefenseEngine__Redis__AllowLoopbackConnectionStringInProduction=true \
     DefenseEngine__Management__ApiKey="$KEY" DefenseEngine__Audit__DatabasePath="$WORK/audit-$port.db" "$@" \
     dotnet AiScrapingDefense.EdgeGateway.dll >"$WORK/app-$port.log" 2>&1) &
  PIDS+=($!)
  timeout 40 bash -c "until curl -sk -m 2 -o /dev/null http://127.0.0.1:$port/ || curl -sk -m 2 -o /dev/null https://127.0.0.1:$port/; do sleep 1; done"
}
code() { curl -sk -m 6 -o /dev/null -w '%{http_code}' "$@"; }
expect() {  # description expected actual
  if [ "$2" = "$3" ]; then printf '  ok    %-70s %s\n' "$1" "$3"; else printf '  FAIL  %-70s expected %s got %s\n' "$1" "$2" "$3"; FAILS=$((FAILS+1)); fi
}
proxy() { python3 - "$@" <<'PY' &
import http.client, sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
listen, upstream, proto = int(sys.argv[1]), int(sys.argv[2]), sys.argv[3]
class H(BaseHTTPRequestHandler):
    def log_message(self, *a): pass
    def do_GET(self):
        h = {k: v for k, v in self.headers.items() if k.lower() not in ("host", "x-forwarded-proto", "x-forwarded-for")}
        h["X-Forwarded-Proto"] = proto; h["X-Forwarded-For"] = "203.0.113.9"
        c = http.client.HTTPConnection("127.0.0.1", upstream, timeout=10); c.request("GET", self.path, headers=h)
        r = c.getresponse(); d = r.read(); self.send_response(r.status); self.end_headers(); self.wfile.write(d)
ThreadingHTTPServer(("127.0.0.1", listen), H).serve_forever()
PY
  PIDS+=($!); }
H="X-API-Key: $KEY"

echo "A. HTTP and HTTPS listeners, setting at its default (on)"
URLS="http://127.0.0.1:18080;https://127.0.0.1:18443" start_app 18080 \
  ASPNETCORE_Kestrel__Certificates__Default__Path="$WORK/cert.pfx" ASPNETCORE_Kestrel__Certificates__Default__Password="$PFX_PASSWORD"
expect "plain HTTP management request is refused"            403 "$(code -H "$H" http://127.0.0.1:18080/defense/metrics)"
expect "HTTPS management request with the key succeeds"      200 "$(code -H "$H" https://127.0.0.1:18443/defense/metrics)"
expect "HTTPS without the key is still unauthorized"         401 "$(code https://127.0.0.1:18443/defense/metrics)"
expect "dashboard login over plain HTTP is refused"          403 "$(code -X POST -H 'Content-Type: application/json' -d "{\"apiKey\":\"$KEY\"}" http://127.0.0.1:18080/defense/dashboard/session)"
expect "dot-segment variant over plain HTTP is refused"      403 "$(code --path-as-is -H "$H" http://127.0.0.1:18080/defense/./metrics)"
expect "percent-encoded variant over plain HTTP is refused"  403 "$(code --path-as-is -H "$H" http://127.0.0.1:18080/%64efense/metrics)"
expect "non-management path over plain HTTP is unaffected"   200 "$(code http://127.0.0.1:18080/)"

echo "B. Behind a proxy, TrustedProxy mode"
start_app 18082 DefenseEngine__Networking__ClientIpResolutionMode=TrustedProxy DefenseEngine__Networking__TrustedProxies__0=127.0.0.1
start_app 18083 DefenseEngine__Networking__ClientIpResolutionMode=TrustedProxy DefenseEngine__Networking__TrustedProxies__0=10.9.9.9
proxy 18090 18082 https; proxy 18091 18082 http; proxy 18092 18083 https
timeout 20 bash -c 'until curl -s -m 2 -o /dev/null http://127.0.0.1:18092/; do sleep 1; done'
expect "trusted proxy that terminated TLS (https)"           200 "$(code -H "$H" http://127.0.0.1:18090/defense/metrics)"
expect "trusted proxy forwarding plain http"                 403 "$(code -H "$H" http://127.0.0.1:18091/defense/metrics)"
expect "untrusted proxy claiming https is ignored"           403 "$(code -H "$H" http://127.0.0.1:18092/defense/metrics)"
expect "untrusted client spoofing X-Forwarded-Proto"         403 "$(code -H "$H" -H 'X-Forwarded-Proto: https' http://127.0.0.1:18083/defense/metrics)"

echo "C. Startup validation in Production"
startup() { (cd "$APP_DIR" && env ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS="http://127.0.0.1:$1" \
   DefenseEngine__Redis__ConnectionString="$REDIS" DefenseEngine__Redis__AllowLoopbackConnectionStringInProduction=true \
   DefenseEngine__Management__ApiKey="$KEY" DefenseEngine__Audit__DatabasePath="$WORK/audit-s$1.db" "${@:2}" \
   timeout 20 dotnet AiScrapingDefense.EdgeGateway.dll 2>&1 | grep -c "RequireSecureTransport\|TrustedProxies"); }
expect "default setting, HTTP only: refuses to start (names the setting)"  1 "$([ "$(startup 18095)" -ge 1 ] && echo 1 || echo 0)"
expect "TrustedProxy with no proxy listed: refuses to start (names the setting)" 1 "$([ "$(startup 18096 DefenseEngine__Networking__ClientIpResolutionMode=TrustedProxy)" -ge 1 ] && echo 1 || echo 0)"
expect "explicitly off: starts, with a warning"                            1 "$([ "$(startup 18097 DefenseEngine__Management__RequireSecureTransport=false)" -ge 1 ] && echo 1 || echo 0)"

echo; [ "$FAILS" -eq 0 ] && echo "All checks passed." || { echo "$FAILS check(s) failed."; exit 1; }
