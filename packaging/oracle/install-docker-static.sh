#!/usr/bin/env bash
set -euo pipefail

docker_version="29.9.3"
compose_version="v5.7.1"
tmp_dir="$(mktemp -d)"
trap 'rm -rf "$tmp_dir"' EXIT

curl -fsSL "https://download.docker.com/linux/static/stable/x86_64/docker-${docker_version}.tgz" -o "$tmp_dir/docker.tgz"
tar -xzf "$tmp_dir/docker.tgz" -C "$tmp_dir"
sudo install -m 0755 "$tmp_dir"/docker/* /usr/local/bin/

sudo mkdir -p /usr/local/lib/docker/cli-plugins
sudo curl -fsSL "https://github.com/docker/compose/releases/download/${compose_version}/docker-compose-linux-x86_64" \
  -o /usr/local/lib/docker/cli-plugins/docker-compose
sudo chmod 0755 /usr/local/lib/docker/cli-plugins/docker-compose

sudo groupadd -f docker
sudo usermod -aG docker opc

sudo tee /etc/systemd/system/docker.service >/dev/null <<'EOF'
[Unit]
Description=Docker Application Container Engine
Documentation=https://docs.docker.com
After=network-online.target firewalld.service
Wants=network-online.target

[Service]
Type=notify
ExecStart=/usr/local/bin/dockerd --host=unix:///var/run/docker.sock
ExecReload=/bin/kill -s HUP $MAINPID
TimeoutStartSec=0
Restart=always
RestartSec=2
LimitNOFILE=infinity
LimitNPROC=infinity
LimitCORE=infinity
TasksMax=infinity
Delegate=yes
KillMode=process
OOMScoreAdjust=-500

[Install]
WantedBy=multi-user.target
EOF

sudo systemctl daemon-reload
sudo systemctl enable --now docker
sudo docker version
sudo docker compose version
