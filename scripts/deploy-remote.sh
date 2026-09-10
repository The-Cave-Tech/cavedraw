#!/usr/bin/env bash
# Build the VCCad image on the docker host and (re)deploy the container.
# Always does build + restart so a browser reload picks up the new build.
#
# Usage: ./scripts/deploy-remote.sh [tag]
set -euo pipefail
cd "$(dirname "$0")/.."

TAG="${1:-0.1.0}"
HOST="${VCCAD_DOCKER_HOST:-user@host}"
REMOTE_DIR="~/vccad-build"
ARCHIVE="/tmp/vccad-src.tgz"

echo "==> Archiving HEAD"
git archive --format=tar.gz HEAD -o "$ARCHIVE"

echo "==> Uploading to $HOST"
scp -q "$ARCHIVE" "$HOST:/tmp/vccad-src.tgz"

echo "==> Building image vccad:$TAG on the host"
ssh "$HOST" "rm -rf $REMOTE_DIR && mkdir -p $REMOTE_DIR && \
  tar -xzf /tmp/vccad-src.tgz -C $REMOTE_DIR && \
  cd $REMOTE_DIR && docker build -f docker/Dockerfile -t vccad:$TAG ."

echo "==> Restarting container"
ssh "$HOST" "docker rm -f vccad >/dev/null 2>&1 || true; \
  docker run -d --name vccad -p 8080:8080 vccad:$TAG >/dev/null; \
  sleep 6; docker ps --filter name=vccad --format '{{.Image}} {{.Status}}'; \
  curl -fsS http://127.0.0.1:8080/api/v1/health"

echo
echo "==> Deployed: http://${HOST#*@}:8080"
