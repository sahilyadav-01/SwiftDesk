#!/usr/bin/env bash
set -euo pipefail

cd /home/opc/SwiftDesk
umask 077

bootstrap="$(openssl rand -hex 32)"
operator="$(openssl rand -hex 32)"
signing="$(openssl rand -hex 32)"
dbpass="$(openssl rand -hex 32)"

{
  printf 'SWIFTDESK_DOMAIN=%s\n' 'swiftdesk.130-210-1-191.sslip.io'
  printf 'REMOTEDESK_BOOTSTRAP_TOKEN=%s\n' "$bootstrap"
  printf 'REMOTEDESK_OPERATOR_KEY=%s\n' "$operator"
  printf 'REMOTEDESK_SIGNING_KEY=%s\n' "$signing"
  printf 'SWIFTDESK_DB_PASSWORD=%s\n' "$dbpass"
} > .env.oracle

chmod 600 .env.oracle
sudo docker compose --env-file .env.oracle -f compose.oracle.yaml config --quiet
