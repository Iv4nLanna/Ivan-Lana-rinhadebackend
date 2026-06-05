# Rinha de Backend 2026 — Detecção de fraude por busca vetorial

API em **.NET / C#** que, para cada transação de cartão, decide se é fraude **comparando-a
com 3 milhões de transações de referência** — e responde em poucos milissegundos, sob
limite de **1 CPU e 350 MB de RAM**.

Desafio oficial: [zanfranceschi/rinha-de-backend-2026](https://github.com/zanfranceschi/rinha-de-backend-2026).

---

## O que ela faz

Recebe um `POST /fraud-score` com os dados da transação (valor, cliente, lojista, terminal…)
e devolve:

```json
{ "approved": false, "fraud_score": 0.8 }
```

A regra do desafio: transformar a transação em um **vetor de 14 números**, achar as **5
transações de referência mais parecidas** (k-vizinhos mais próximos, kNN) e calcular
`fraud_score = nº de fraudes entre as 5 / 5`. Aprova se `fraud_score < 0.6`.

O problema: comparar com os 3 milhões a cada request é **lento demais**. Todo o projeto é
sobre **não precisar comparar com todos**.

---

## A ideia em uma frase

> Em vez de comparar a transação nova com os 3 milhões, separo os 3 milhões em **"gavetas"**
> por características da transação, e só olho a gaveta certa.

E mais: descobri que a **maioria das gavetas é "pura"** (ou quase ninguém é fraude, ou todo
mundo é). Numa gaveta pura, a resposta é sempre a mesma — então respondo **na hora, sem
comparar com ninguém**.

---

## Como funciona (o caminho de um request)

```
transação (JSON)
   │
   ▼
1. Normaliza  →  vetor de 14 números em [0,1]        (Normalizer.cs)
   │   ex.: valor, valor/média do cliente, hora, distância, é online?, cartão presente?…
   ▼
2. Escolhe a gaveta                                  (PartitionKey.cs)
   │   3 perguntas sim/não (online? cartão presente? lojista desconhecido?)
   │   + faixa de valor → uma de 32 gavetas
   ▼
3. A gaveta é "pura"?                                 (taxa de fraude pré-calculada)
   ├── SIM  → responde O(1): 0 (legítima) ou 1 (fraude). Não compara com ninguém.
   └── NÃO  → kNN só dentro da gaveta:
   │            árvore com poda por "caixa" — pula ramos longe demais  (PartitionForest.cs)
   ▼
4. Responde  approved / fraud_score
```

Por que as gavetas são por essas perguntas? São as dimensões **categóricas (0/1)**: quem
respondeu diferente já está "longe" no espaço vetorial, então nunca seria vizinho — dá pra
ignorar as outras gavetas quase de graça.

---

## A evolução (cada passo é uma lição)

| Marco | O que mudou | Lição |
|---|---|---|
| **M1–M5** | Força bruta (comparar com os 3M) + SIMD/stackalloc | O custo é **ler os 168 MB** a cada request (memory-bound). |
| **M6** | KD-Tree global exata | **Falhou**: em 14 dimensões a poda genérica não funciona (maldição da dimensionalidade). |
| **M7** | Índice por partição (gavetas) | 1º score positivo. Mas gavetas desbalanceadas (a maior = 600k) seguram o p99. |
| **M8** | **Atalho de pureza** (gaveta pura → O(1)) | p99 **1185 → 23 ms** sem perder detecção. A vilã (gaveta de 600k, 100% fraude) vira O(1). |
| **M9** | **Árvore com poda** nas gavetas ambíguas | Busca 27× mais rápida e exata — **mas não moveu o score**: a busca deixou de ser o gargalo. |
| **M10** | *(próximo)* request path | Cortar alocações/parse por request — o gargalo real agora. |

> A grande lição do M9: **otimização certa no lugar errado = zero ganho.** Sempre medir
> *onde* está o gargalo antes de otimizar. Detalhes em `docs/superpowers/specs/`.

---

## Estrutura do projeto

```
src/
  Api/                 A API (ASP.NET)
    Program.cs         endpoints /ready e /fraud-score; orquestra o caminho acima
    Models.cs          contrato do request/response
    Detection/
      Normalizer.cs        transação → vetor de 14 números
      PartitionKey.cs      vetor → número da gaveta (0..31)
      Quantizer.cs         float → int8 (1 byte/dim, metade da banda de memória)
      PartitionedIndex.cs  scan plano de uma gaveta (M7)
      PartitionForest.cs   árvore k-d com poda por gaveta (M9)
    Data/
      ReferenceDataset.cs  carrega o índice (mmap), calcula taxas e monta as árvores
      IndexBuilder.cs      monta o índice (gavetas + quartis + quantização)
  IndexGen/            console offline: gera data/index.bin a partir de references.bin
  Experiment/          console offline: mede modelos vs gabarito (sem subir API/k6)
tests/Api.Tests/       testes xUnit
infra/                 Dockerfile, docker-compose.yml (nginx + 2 APIs), nginx.conf
docs/superpowers/      specs e planos de cada marco (a história do projeto)
data/                  dataset + índice (NÃO versionado — arquivos grandes)
```

> `data/*.bin`, `data/*.json.gz` e o harness `/test/` (k6, baixado do repo oficial) ficam
> **fora do git** por serem grandes. `tests/` (xUnit) é nosso e é versionado.

---

## Como rodar

**Pré-requisitos:** .NET SDK 9, Docker, [k6](https://k6.io/). O dataset oficial
(`references.json.gz` → `data/`) e o `mcc_risk.json`.

```bash
# 1. Gerar o índice (lê data/references.bin → grava data/index.bin)
dotnet run -c Release --project src/IndexGen -- data

# 2. Testes
dotnet test

# 3. Subir a stack (nginx + 2 APIs com os limites oficiais)
docker compose -f infra/docker-compose.yml up --build -d
curl http://localhost:9999/ready          # 200 quando pronta

# 4. Benchmark oficial (scoring idêntico ao do juiz)
k6 run test/test.js && cat test/results.json
```

Medir sem subir nada (rápido, pra desenvolver):

```bash
dotnet run -c Release --project src/Experiment -- data test/test-data.json
```

---

## Pontuação

`score_final = score_latência + score_detecção` (cada um de −3000 a +3000).

- **Latência** (`p99`): cada 10× de melhora vale +1000. Satura em +3000 a ≤1 ms; **−3000 se
  passar de 2000 ms**. 1000 ms = 0 pontos.
- **Detecção**: taxa de erro ponderada, onde **erro HTTP > falso negativo > falso positivo**.
  **−3000 se a taxa de falhas passar de 15%**.

**Limites:** CPU total **1.0**, memória total **350 MB** (nginx + 2 réplicas da API).
```
