# M9 — Árvore com poda por caixa (resultado)

**Data:** 2026-06-04 · **Parte de:** Rinha 2026 · **Segue:** [M8 atalho de pureza](./2026-06-04-m8-experimento-taxa-por-gaveta-design.md)

## O que é

Dentro de cada gaveta ambígua, uma árvore k-d: cada nó guarda a caixa (min/max por dim)
dos vetores ali. Na busca, poda a subárvore cujo lower-bound de distância à caixa já não
bate o 5º melhor. Construída em memória no startup (~1,1s), sem mudar o `index.bin`.
Split pela dim mais larga na mediana (counting sort, chaves byte). Gaveta pura segue O(1).

`src/Api/Detection/PartitionForest.cs` + wiring no `ReferenceDataset`/`Program.cs`.

## Verificação offline (N=54.100)

- **Paridade: 0 divergências** vs scan plano → kNN exato, detecção idêntica ao M8 (detScore 1335).
- **Busca nas ambíguas: 27,5× mais rápida** (21.713ms → 790ms).

## Benchmark real (Docker, mesmo ambiente do M8)

| Load | M8 | M9 |
|---|---|---|
| @300 RPS (sustentável) | p99 23ms · **+3142** | p99 13–45ms · **+2849…+3392** |
| @600 RPS | satura (−3770, 2264 t.o.) | satura (−3849, 2535 t.o.) |
| @900 RPS (oficial) | satura (−3900, ~3400 t.o.) | satura (−4126, 4633 t.o.) |

Detecção idêntica nos dois (fp/fn ~38/31 limpo, ~60 sob saturação).

## Leitura (importante)

**M9 está correto e é 27× mais rápido na busca, mas não move o score neste setup.** Motivo:
depois do M8, **a busca deixou de ser o gargalo**. O que limita aqui é o **caminho do
request** — parse de JSON (`ReadFromJsonAsync`), `new HashSet` por request, alocações,
Kestrel. Por isso:
- @300 RPS: p99 é ruído de cauda (GC/Kestrel ~13–45ms), não a busca → M8≈M9.
- @600/900 RPS: ambos batem no mesmo precipício, fixado pelo request path, não pela busca.

A máquina local não sustenta os 900 RPS oficiais (satura ≥600) — nem com M8 nem M9. O
+606 do M7 veio de ambiente mais forte / preview oficial.

## Decisão

**Manter o M9** (verificado, exato, não regride, e mais escalável — num ambiente forte que
sustente 900 RPS a busca volta a contar no p99, e aí a árvore ajuda). Mas o **próximo lever
real é o request path** (M10): tirar o `new HashSet` por request (busca linear na lista
pequena de `known_merchants`), parse de JSON sem alocação, etc. — é o que sobe o teto de
throughput e deixa a máquina sustentar 900 RPS.
