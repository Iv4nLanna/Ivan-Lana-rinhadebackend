# Como submeter à Rinha de Backend 2026

Tudo que precisa já está preparado. Faltam **3 ações suas** (login + 2 cliques). O resto
está pronto neste repo.

## Pré-condições (já feitas neste repo)
- ✅ Imagem **auto-contida**: `infra/Dockerfile` embute `data/index.bin` + `mcc_risk.json`.
- ✅ `LICENSE` MIT (exigência das regras).
- ✅ `info.json` na raiz.
- ✅ Stack de submissão em `infra/submission/` (compose + nginx) somando **1.0 CPU / 350 MB**.
- ✅ Branch `submission` criada (compose + nginx + info.json na raiz, sem código-fonte).
- ✅ Repo público com `main` (código) + `submission` (entrega).

## Passo 1 — Publicar a imagem no GHCR  *(você: login; o resto é automático)*
```bash
# Docker Desktop aberto. Login uma vez (senha = Personal Access Token com 'write:packages'):
docker login ghcr.io -u Iv4nLanna
# Publica a imagem amd64 com o índice embutido:
bash infra/publish.sh
```
Depois, deixe o pacote **público**: GitHub → seu perfil → Packages → `rinha-fraud` →
*Package settings* → *Change visibility* → **Public**.

> O `docker-compose.yml` da submissão aponta pra `ghcr.io/iv4nlanna/rinha-fraud:latest`.

## Passo 2 — Inscrever no repo oficial  *(você: 1 PR)*
Faça um fork de `zanfranceschi/rinha-de-backend-2026`, adicione o arquivo
**`participants/Iv4nLanna.json`** com o conteúdo de `docs/submission/participants-Iv4nLanna.json`:
```json
[
    { "id": "ivan-lana-dotnet", "repo": "https://github.com/Iv4nLanna/Ivan-Lana-rinhadebackend" }
]
```
e abra um Pull Request.

## Passo 3 — Rodar o teste de prévia  *(você: 1 issue)*
No repo oficial, abra uma **issue** com `rinha/test` na descrição. A engine roda o teste,
**comenta sua pontuação** e fecha a issue. Pode repetir à vontade (até o prazo).

---

### Checklist de regras (tudo OK aqui)
- [x] Load balancer (nginx) + ≥2 instâncias de API, round-robin, sem lógica de negócio no LB.
- [x] Porta **9999**, rede **bridge** (default), sem `host`/`privileged`.
- [x] Soma dos limites ≤ **1 CPU / 350 MB**.
- [x] Imagens públicas, `linux/amd64`.
- [x] Licença **MIT**; repo público; branch `submission` sem código-fonte.

### Prazo
**2026-06-05 23:59:59 (-03:00).** Resultado final na semana de 8 de junho.
Ambiente do juiz: Mac Mini 2014 (2.6 GHz, 2 cores, 8 GB) — fraquinho, então o ganho de
latência do M8/M9 conta.
