#!/bin/sh
set -eu

: "${DB_HOST:?DB_HOST obrigatorio}"
DB_PORT="${DB_PORT:-3306}"
mkdir -p /tmp/openvpn

printf '%s\n' "[DB-BRIDGE] iniciando OpenVPN isolado" >&2
openvpn --config "${OVPN_FILE:-/vpn/profile/client.ovpn}" \
  --auth-user-pass /vpn/auth.txt \
  --auth-nocache \
  --writepid /tmp/openvpn/openvpn.pid \
  > /tmp/openvpn/openvpn.log 2>&1 &
OPENVPN_PID=$!
trap 'kill "$OPENVPN_PID" 2>/dev/null || true' EXIT INT TERM

for i in $(seq 1 45); do
  if ! kill -0 "$OPENVPN_PID" 2>/dev/null; then
    echo "[DB-BRIDGE] OpenVPN encerrou antes de concluir a conexao." >&2
    cat /tmp/openvpn/openvpn.log >&2 || true
    exit 1
  fi
  if grep -q "Initialization Sequence Completed" /tmp/openvpn/openvpn.log 2>/dev/null; then
    break
  fi
  sleep 1
done

if ! grep -q "Initialization Sequence Completed" /tmp/openvpn/openvpn.log 2>/dev/null; then
  cat /tmp/openvpn/openvpn.log >&2 || true
  echo "[DB-BRIDGE] timeout ao conectar VPN" >&2
  exit 1
fi

echo "[DB-BRIDGE] VPN conectada." >&2
echo "[DB-BRIDGE] validando DNS e rota para ${DB_HOST}:${DB_PORT}..." >&2
nslookup "$DB_HOST" >&2 2>/dev/null || true
ip route >&2 2>/dev/null || true

DB_OK=0
for i in $(seq 1 20); do
  if nc -z -w 3 "$DB_HOST" "$DB_PORT" >/dev/null 2>&1; then
    DB_OK=1
    break
  fi
  sleep 1
done

if [ "$DB_OK" -ne 1 ]; then
  echo "[DB-BRIDGE] ERRO: VPN conectou, mas ${DB_HOST}:${DB_PORT} nao esta acessivel de dentro do container." >&2
  echo "[DB-BRIDGE] Ultimas linhas do OpenVPN:" >&2
  tail -n 30 /tmp/openvpn/openvpn.log >&2 2>/dev/null || true
  exit 2
fi

echo "[DB-BRIDGE] banco remoto acessivel; iniciando proxy ${DB_HOST}:${DB_PORT} -> :3306" >&2
exec socat TCP-LISTEN:3306,fork,reuseaddr TCP:"${DB_HOST}":"${DB_PORT}"
