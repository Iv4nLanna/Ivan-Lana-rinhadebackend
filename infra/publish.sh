#!/usr/bin/env bash
# Publica a imagem AUTO-CONTIDA (com o índice embutido) no GHCR, para linux/amd64.
# Pré-requisitos (uma vez):
#   1. Docker Desktop aberto.
#   2. docker login ghcr.io -u Iv4nLanna   (senha = um Personal Access Token com escopo write:packages)
# Depois é só rodar este script. Por padrão usa a tag :latest.
set -euo pipefail

IMAGE="${IMAGE:-ghcr.io/iv4nlanna/rinha-fraud:latest}"
cd "$(dirname "$0")/.."   # raiz do repo (contexto de build)

if [[ ! -f data/index.bin ]]; then
  echo "ERRO: data/index.bin não existe. Gere com: dotnet run -c Release --project src/IndexGen -- data" >&2
  exit 1
fi

echo "Construindo e publicando $IMAGE (linux/amd64)..."
docker buildx build --platform linux/amd64 -f infra/Dockerfile -t "$IMAGE" --push .
echo
echo "OK: $IMAGE publicada."
echo "IMPORTANTE: torne o pacote PÚBLICO em https://github.com/users/Iv4nLanna/packages"
echo "(GHCR → o pacote rinha-fraud → Package settings → Change visibility → Public)."
