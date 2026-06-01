# Design: Rinha de Backend 2026 — Fraud Detection API

**Data:** 2026-05-31  
**Stack:** C# .NET 9 + Native AOT  
**Metodologia:** Evolutiva (naïve → meça → otimize)

---

## Contexto

Competição brasileira de backend. Construir uma API de detecção de fraude com KNN sobre 3M vetores, sob restrição de 1 CPU + 350 MB RAM totais.

**Deadline:** 5/jun/2026  
**Repo oficial:** https://github.com/zanfranceschi/rinha-de-backend-2026

---

## Endpoints obrigatórios (porta 9999)

```
GET  /ready
     → 2xx quando a API estiver pronta para receber requisições

POST /fraud-score
     → body: JSON com campos id, transaction, customer, merchant, terminal, last_transaction
     → response: { "approved": boolean, "fraud_score": number }
```

---

## Algoritmo de detecção

1. Normalizar a transação em vetor de 14 floats (ver fórmulas abaixo)
2. Buscar os 5 vizinhos mais próximos no dataset de referência (distância euclidiana)
3. `fraud_score = count_fraudulentos_entre_5 / 5`
4. `approved = fraud_score < 0.6`

### Fórmulas do vetor (14 dimensões)

| # | Fórmula |
|---|---------|
| 0 | `clamp(transaction.amount / 10000)` |
| 1 | `clamp(transaction.installments / 12)` |
| 2 | `clamp((transaction.amount / customer.avg_amount) / 10)` |
| 3 | `hour(requested_at) / 23` |
| 4 | `weekday(requested_at) / 6` (Mon=0) |
| 5 | `clamp(minutes_since_last / 1440)` ou `-1` se null |
| 6 | `clamp(last_tx.km / 1000)` ou `-1` se null |
| 7 | `clamp(terminal.km_from_home / 1000)` |
| 8 | `clamp(customer.tx_count_24h / 20)` |
| 9 | `is_online ? 1 : 0` |
| 10 | `card_present ? 1 : 0` |
| 11 | `known_merchants.Contains(merchant.id) ? 0 : 1` |
| 12 | `mcc_risk.GetValueOrDefault(merchant.mcc, 0.5f)` |
| 13 | `clamp(merchant.avg_amount / 10000)` |

`clamp(x)` = max(0, min(1, x))

---

## Arquitetura

```
Cliente (avaliador)
      │ HTTP :9999
   nginx (LB, round-robin)
      ├── api-1 (C# .NET 9, Kestrel)
      └── api-2 (C# .NET 9, Kestrel)
           │
      reference dataset (3M vetores, read-only)
```

### Restrições de recursos

| Serviço | CPU | RAM |
|---------|-----|-----|
| nginx | 0.05 | 10 MB |
| api-1 | 0.475 | ~170 MB |
| api-2 | 0.475 | ~170 MB |
| **Total** | **1.0** | **~350 MB** |

> Em M1 a RAM vai estourar (2 × 168 MB só de dataset). Isso é intencional — é o primeiro problema a resolver.

---

## Componentes

### Load Balancer (nginx)
- Round-robin entre api-1 e api-2
- Único serviço exposto na porta 9999
- Sem lógica de negócio

### API (C# .NET 9)
- **Servidor HTTP:** Kestrel (embutido no .NET)
- **Deserialização:** `System.Text.Json` (M1) → `Utf8JsonReader` zero-alloc (marco posterior)
- **Normalização:** 14 fórmulas, lookups em `Dictionary<string, float>` (mcc_risk) e `HashSet<string>` (known_merchants)
- **KNN:** brute force linear (M1) → evoluir conforme medição
- **Compilação:** JIT (M1) → AOT quando memória apertar

### Dataset de referência
- Fonte: `references.json.gz` (3M vetores rotulados)
- Layout em memória M1: `float[3_000_000, 14]` + `bool[3_000_000]` no heap
- Layout alvo: arquivo binário flat + `MemoryMappedFile` compartilhado entre instâncias

---

## Tratamento de erros

| Situação | Resposta |
|----------|----------|
| JSON malformado | 400 Bad Request |
| `last_transaction` null | Dimensões 5 e 6 = -1 (correto por spec) |
| MCC não encontrado | mcc_risk default = 0.5 |
| Merchant desconhecido | Dimensão 11 = 1 |
| Qualquer panic/exception | 500 (máximo esforço para evitar) |

**Regra de ouro:** o scoring penaliza: erros HTTP > falsos negativos > falsos positivos. Nunca deixar virar 500.

---

## Estratégia de testes

### Correctness
- xUnit para as 14 fórmulas de normalização (casos: null, clamp, values extremos)
- Testes de integração com payloads de preview do repo oficial
- Verificar `/ready` não responde antes do dataset estar carregado

### Performance (loop evolutivo)
```
1. docker compose up (com --cpus e --memory reais)
2. k6 run --vus 10 --duration 60s
3. Coletar: p99, RAM (docker stats), GC (dotnet-counters)
4. Identificar gargalo dominante
5. Aplicar UMA otimização
6. Voltar ao passo 1
```

---

## Marcos de evolução

| Marco | Técnica | Gargalo endereçado |
|-------|---------|-------------------|
| M1 | Brute force ingênuo, JIT, heap normal | Baseline — ver tudo quebrar |
| M2 | Top-K heap (sem array de 3M distâncias) | Alocação de 12 MB/req |
| M3 | `MemoryMappedFile` + dataset binário | RAM: 2 × 168 MB → 168 MB |
| M4 | `stackalloc float[14]` + `Span<T>` | GC pressure por request |
| M5 | SIMD `Vector256<float>` | KNN CPU bound |
| M6 | AOT + `Utf8JsonReader` | RAM baseline + JSON allocs |
| M7+ | HNSW ou IVF-PQ (se necessário) | KNN latência absoluta |

> A ordem M2+ é definida pelo profiler após medir M1. Esta é a ordem mais provável, não obrigatória.

---

## Documentação do aprendizado

Durante o desenvolvimento, cada técnica nova gera:
- **Nota de conceito** em `20_Teoria_CORE/Performance e Sistemas/` (explicação do conceito)
- **Marco** em `RINHA DE BACKEND/` (em primeira pessoa: o que implementei, o que mediu, o que aprendi)

Vault Obsidian: `/mnt/c/Users/Ivan/Documents/Estudo/Estudo/`

---

## Estrutura do projeto (a criar)

```
rinha-2026/
├── src/
│   └── RinhaBackend/
│       ├── Program.cs
│       ├── FraudDetection/
│       │   ├── VectorNormalizer.cs
│       │   └── KnnSearch.cs
│       └── RinhaBackend.csproj
├── infra/
│   ├── docker-compose.yml
│   └── nginx.conf
├── tests/
│   └── RinhaBackend.Tests/
├── scripts/
│   └── benchmark.js
└── docs/
    └── superpowers/specs/
```
