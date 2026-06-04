# M8 — Experimento: taxa de fraude pré-computada por gaveta

**Data:** 2026-06-04
**Estado:** experimento (decide o rumo do M8)
**Parte de:** Rinha de Backend 2026 — detecção de fraude por KNN

## Contexto

O M7 (índice por partição, ver `RINHA DE BACKEND/M7 - Indice por Particao.md`) tirou o
projeto do −6000 e cravou +606. A busca atual escaneia **toda** a gaveta do query (scan
plano), top-5. O gargalo passou a ser **latência**: gavetas desbalanceadas (a maior tem
600k de 3M) arrastam o p99 pra 1880ms.

A solução de referência (top-12, `fksegundo/rinha-dotnet`) resolve isso com uma **árvore
com poda por bounding box dentro de cada gaveta** (limita o pior caso) + AVX2 + tuning de
sistema. Esse é o plano "seguro" do M8.

Antes de investir na árvore, este experimento testa uma **hipótese mais ousada**.

## Fórmula de score (descoberta no harness `test/k6-summary.js`)

`score_final = score_p99 + score_detecção`

- **p99:** `1000·log10(1000/p99)`. 1000ms = 0 pontos; <1000 positivo; >2000ms = −3000 (corte).
- **detecção:** se `failures/N > 0.15` → −3000. Senão `1000·log10(1/ε) − 300·log10(1+E)`,
  com **`E = fp·1 + fn·3 + erro·5`** e `ε = E/N`. Falso negativo (fraude aprovada) pesa 3×.

Gabarito: `test/test-data.json` traz 54.100 transações cruas + `expected_approved`.

## Hipótese

O score final é só `(nº de fraudes nos top-5)/5`. Talvez cada gaveta seja "pura" o
suficiente pra responder com a **taxa de fraude da própria gaveta** (pré-computada),
respondendo em **O(1)** (uma consulta de tabela) — **sem kNN**. Se a detecção passar
folgada no corte de 15%, isso é dramaticamente mais simples e rápido que a árvore.

## O experimento (medição offline, sem API/k6)

Console C# (`src/Experiment`) que referencia `Api` e reusa `Normalizer`, `PartitionKey`,
`IndexBuilder`, `PartitionedIndex`, `Quantizer`. Passos:

1. Carrega `data/references.bin` (3M vetores float + labels).
2. `IndexBuilder.BuildInMemory` → cuts (quartis dim0), offsets, vetores quantizados
   agrupados por gaveta, labels agrupados.
3. **Taxa por gaveta:** `rate[p] = fraudes / total` por partição (de offsets + labels).
4. Carrega `data/mcc_risk.json`.
5. Para cada transação de `test/test-data.json`: normaliza (mesmo `Normalizer` da API),
   calcula a gaveta, e prediz por **dois** modelos:
   - **Taxa:** `approved = rate[key] < threshold`.
   - **kNN M7 (baseline):** `PartitionedIndex.Search` → `approved = score < 0.6`
     (reproduz exatamente a API atual, offline).
6. Tabula tp/tn/fp/fn dos dois. Faz **varredura de threshold** no modelo de taxa e escolhe
   o de melhor `detScore`.
7. Calcula `detScore` pela fórmula real (offline `erro=0`).

## Regra de decisão (autônoma, sem aprovação)

- O lookup O(1) tem p99 ~sub-ms → `p99Score` ~no teto. Logo a decisão é pela **detecção**.
- Comparar `detScore` do modelo de taxa (melhor threshold) vs. `detScore` do kNN M7, ambos
  no mesmo conjunto de 54.100, ambos com `erro=0`:
  - **Taxa vence** (detScore ≥ kNN, ou muito próximo com folga grande sob 15%): a ideia
    continua — vira o caminho do M8 (responder por taxa, sem scan).
  - **Taxa perde feio** (detScore bem abaixo do kNN, ou failureRate alto): **reverter** a
    ideia e seguir o plano da árvore com poda + int8 (M8 "seguro").
- Em qualquer caso: registrar os números medidos numa nota e no commit.

## Fora de escopo (viram M9+)

Árvore com poda, AVX2 int8, mais flags+octis, tuning de sistema (mlock/AOT/LB Rust),
busca cross-partition exata.

## Notas de design

- Experimento é **medição** — código isolado em `src/Experiment`, não toca a API.
- Se a taxa vencer, o M8 "de produção" troca `PartitionedIndex.Search` por um lookup de
  `rate[key]` no runtime (mudança pequena no `Program.cs`).

## Resultado medido (2026-06-04, N=54.100, offline erro=0)

| Modelo | fp | fn | falhas | detScore |
|---|---|---|---|---|
| kNN M7 (32 gavetas) | 117 | 98 | 0,40% | **1335** |
| Taxa coarse (32) | 669 | 308 | 1,81% | 570 |
| Taxa FINA (256, 5 flags+octis) | 1607 | 0 | 2,97% | 565 |
| **Híbrido (puro O(1) + kNN)** | 117 | 98 | 0,40% | **1335** |

**Veredito:**
1. A taxa pura **não substitui o kNN** — empaca em ~570 com 32 *ou* 256 gavetas. A
   ambiguidade dentro das gavetas mistas só se resolve olhando o vizinho (kNN). Logo
   "kNN é desnecessário" = **falso**.
2. Mas as gavetas são quase todas **puras** (rate ~0 ou ~1), e a gaveta que mata o p99
   (#29, 600k, 20% dos dados) é **100% fraude**. Em gaveta pura, taxa == kNN (zero perda).
3. **Híbrido** = atalho O(1) em gaveta pura (eps=0,01 → 9/32 puras) + kNN nas ambíguas:
   - detScore **1335, idêntico ao kNN** (mesmos fp/fn).
   - **74,2%** das queries respondem O(1) (p50 de scan = **0**).
   - p99 de scan (proxy de latência): **600k → 303k**; a #29 some, sobra a #18 (303k, 3,9%).

**Decisão:** M8 = **atalho de pureza** (mudança mínima, mantém detecção, derruba ~metade do
p99). A árvore com poda nas ambíguas grandes (#18=303k, #21=183k) vira **M9**. Busca
cross-partition exata (detecção 1335 → ~3000) fica pra depois.

## Regras / limites oficiais (confirmados em `zanfranceschi/rinha-de-backend-2026`)

- `k6-summary.js` do harness local é **idêntico** ao oficial — fórmula de score validada.
- Limites: **CPU total 1.0, memória total 350MB**. `fraud_score = nº fraudes/5`,
  `approved = score < 0.6`. Latência: +1000 por 10× (satura +3000 a ≤1ms, −3000 acima de
  2000ms). Detecção: pesos **erro HTTP > fn > fp**, corte em 15%.
- **Prazo de submissão: 2026-06-05 23:59** → favorece mudança simples e de alto impacto.

## Benchmark real (Docker, `infra/docker-compose.yml`, 2026-06-04)

A/B **controlado, mesmo ambiente**, load reduzido a 300 RPS (a máquina local satura ≥600
RPS sob o limite de 0.95 CPU — não sustenta os 900 RPS oficiais; o ambiente onde o M7 deu
+606 é mais forte / é o preview oficial):

| | p99 | det | **final** | timeouts |
|---|---|---|---|---|
| M7 (scan sempre) | 1185 ms | +1499 | +1425 | 0 |
| **M8 (atalho)** | **23 ms** | +1503 | **+3142** | 0 |

- Detecção **idêntica** (fp/fn 38/31 nos dois) → atalho não custa detecção, como previsto.
- p99 **1185 → 23 ms (51×)**; score **+1717** só de latência. M8 @300 usou ~100 VUs vs M7
  ~250 VUs (saturou o pool) → requests do M7 bem mais lentas.
- @900 RPS ambos saturam a máquina local (p99 bate o teto 2001ms → −3000). Não é bug:
  detecção segue boa (fp/fn ~60-77). É falta de CPU local pra 900 RPS sustentados.

**Ação:** rodar/submeter M8 no ambiente forte (ou no preview oficial, 10/dia) pra o número
real a 900 RPS. M9 (árvore nas ambíguas grandes #18/#21) reduz CPU/request → sobe o
throughput sustentável → ajuda a limpar os 900 RPS mesmo em máquina fraca.
</content>
