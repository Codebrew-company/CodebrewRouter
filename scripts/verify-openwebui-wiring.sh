#!/bin/sh
# Verify the OpenWebUI ← host-gateway wiring end-to-end.
# 1. Build AppHost. 2. Run gateway standalone bound 0.0.0.0:5022 (same as AppHost now enforces).
# 3. Prove a container reaches it via host.docker.internal. 4. Prove OpenWebUI lists its models.
set -e
cd "$(dirname "$0")/.."

echo "=== [1/4] build AppHost (-warnaserror) ==="
dotnet build Blaze.LlmGateway.AppHost --no-incremental -warnaserror 2>&1 | tail -3

echo "=== [2/4] start gateway host process bound 0.0.0.0:5022 ==="
ASPNETCORE_URLS=http://0.0.0.0:5022 \
ASPNETCORE_ENVIRONMENT=Development \
LlmGateway__Auth__SeedDevKeys=true \
LlmGateway__LocalInference__Enabled=false \
LlmGateway__LocalInference__WarmupEnabled=false \
LlmGateway__LocalInference__BlockStartupUntilWarm=false \
dotnet run --project Blaze.LlmGateway.Api --no-build >/tmp/gw.log 2>&1 &
GW_PID=$!
sleep 20
grep -iE "Seeded dev API key|Now listening" /tmp/gw.log | tail -5 || true

echo "=== [3/4] container -> host.docker.internal:5022 ==="
docker run --rm curlimages/curl:latest -s -m 10 \
  -H "Authorization: Bearer sk-blaze-openwebui" \
  -o /dev/null -w "container->gateway HTTP %{http_code}\n" \
  http://host.docker.internal:5022/v1/models

echo "=== [4/4] OpenWebUI container lists gateway models ==="
docker rm -f verify-webui >/dev/null 2>&1 || true
docker run -d --name verify-webui -p 8081:8080 \
  -e WEBUI_AUTH=False -e ENABLE_OLLAMA_API=False -e ENABLE_PERSISTENT_CONFIG=False \
  -e OPENAI_API_BASE_URL=http://host.docker.internal:5022/v1 \
  -e OPENAI_API_KEY=sk-blaze-openwebui \
  ghcr.io/open-webui/open-webui:v0.10.2 >/dev/null
sleep 35
TOKEN=$(curl -s -X POST http://localhost:8081/api/v1/auths/signin \
  -H "Content-Type: application/json" \
  -d '{"email":"a@localhost","password":"a"}' | sed -n 's/.*"token":"\([^"]*\)".*/\1/p')
curl -s http://localhost:8081/api/models -H "Authorization: Bearer $TOKEN" \
  | python -c "import sys,json; d=json.load(sys.stdin); data=d.get('data',[]); print('OPENWEBUI MODELS:', [m.get('id') for m in data])"

echo "=== cleanup ==="
docker rm -f verify-webui >/dev/null 2>&1 || true
kill $GW_PID 2>/dev/null || true
echo "VERIFY DONE"
