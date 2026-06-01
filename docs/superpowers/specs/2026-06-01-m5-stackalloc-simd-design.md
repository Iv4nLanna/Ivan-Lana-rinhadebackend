# Design: M5 — stackalloc (query) + SIMD (distância)

**Data:** 2026-06-01
**Marco:** M5 (Rinha de Backend 2026)
**Stack:** C# .NET 9, `System.Runtime.Intrinsics` (`Vector256<float>`), `Span<T>`/`stackalloc`, xUnit
**Metodologia:** Evolutiva — atacar o gargalo medido, medir o antes/depois

---

## Contexto

O **M4** (Top-K em uma passada) derrubou a latência de uma request de ~1s para ~20ms (50x) ao eliminar o `OrderBy()` de 3M e a alocação de 12 MB. Sob carga (k6, 900 req/s) o score continuou −6000 — mas pela primeira vez houve **238 respostas corretas (fp/fn = 0)**, provando que a lógica e o dataset estão certos. O gargalo restante é **CPU pura**: o loop de distância sobre 3M × 14 floats por request.

O M5 ataca esse loop com duas otimizações de hot-path que o design original previa (M4 = `stackalloc`, M5 = SIMD), agrupadas aqui por decisão do usuário porque ambas vivem no caminho de uma request.

---

## Objetivo

1. **stackalloc do vetor de query:** eliminar o `new float[14]` por request, alocando o vetor normalizado na stack.
2. **SIMD no cálculo de distância:** computar a distância² com `Vector256<float>` (8 floats por instrução) em vez do loop escalar de 14, com fallback escalar quando não há aceleração de hardware.

**Não-objetivos:**
- Não mudar o algoritmo Top-K do M4 (a seleção dos 5 menores fica idêntica; só o cálculo da distância individual muda).
- Não eliminar a alocação do `HashSet<string> knownMerchants` por request (fora de escopo; possível marco futuro).
- Não mudar para busca aproximada (HNSW → marco futuro).
- Não mudar o contrato HTTP (`/fraud-score` request/response inalterados).

---

## Parte 1 — stackalloc do vetor de query

### Mudança de assinatura

`Normalizer.Normalize` deixa de **retornar** `float[]` e passa a **escrever** num buffer fornecido pelo chamador:

```csharp
// antes:
public static float[] Normalize(TransactionRequest req, Dictionary<string,float> mccRisk, HashSet<string> knownMerchants)

// depois:
public static void Normalize(TransactionRequest req, Dictionary<string,float> mccRisk, HashSet<string> knownMerchants, Span<float> dest)
```

O corpo é idêntico, só troca `var vec = new float[14]` por escrita em `dest[0..13]` e remove o `return`.

### A restrição do `async` (detalhe crítico)

`stackalloc` produz um `Span<float>`, que é um `ref struct` — e **não pode existir num método `async`**. Motivo: um método `async` pausa no `await` e guarda seu estado num objeto no heap; a stack não sobrevive a essa pausa, então o compilador proíbe `Span`/`stackalloc` ali. O handler `/fraud-score` é `async` (faz `await request.ReadFromJsonAsync`).

**Solução:** separar em dois. O handler `async` faz apenas a parte que espera (ler e validar o JSON, checar `IsReady`); depois delega para um método **síncrono** que faz o trabalho com `stackalloc`:

```csharp
app.MapPost("/fraud-score", async (HttpRequest request, ReferenceDataset ds) =>
{
    TransactionRequest? req;
    try { req = await request.ReadFromJsonAsync<TransactionRequest>(); if (req is null) return Results.BadRequest("body vazio"); }
    catch (Exception) { return Results.BadRequest("JSON inválido"); }
    if (!ds.IsReady) return Results.StatusCode(503);
    return ComputeScore(req, ds);   // síncrono — aqui mora o stackalloc
});

static IResult ComputeScore(TransactionRequest req, ReferenceDataset ds)
{
    var knownMerchants = new HashSet<string>(req.Customer.KnownMerchants, StringComparer.OrdinalIgnoreCase);
    Span<float> vector = stackalloc float[14];
    Normalizer.Normalize(req, ds.MccRisk, knownMerchants, vector);
    float fraudScore = KnnSearch.Search(vector, ds);
    return Results.Ok(new FraudScoreResponse { Approved = fraudScore < 0.6f, FraudScore = fraudScore });
}
```

`ComputeScore` é um método estático local/de topo no `Program.cs` (o arquivo já tem `public partial class Program {}`; o método pode ser uma função estática de topo, que o C# permite em arquivos de nível superior).

---

## Parte 2 — SIMD no cálculo de distância

### Assinatura de Search

`KnnSearch.Search` passa a receber `ReadOnlySpan<float>` (um `float[]` converte implicitamente, então os testes que passam `new float[14]` não mudam):

```csharp
public static float Search(ReadOnlySpan<float> query, ReferenceDataset ds)
```

A estrutura Top-K do M4 (buffer `stackalloc` de 5, `FindWorst` com desempate por maior índice, divisão por K) **fica intacta**. Só o cálculo da distância individual sai do loop inline e vira um helper.

### O helper SquaredDistance

```csharp
private static float SquaredDistance(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
{
    if (Vector256.IsHardwareAccelerated)
    {
        // dims 0-7 num único vetor de 8 floats (Create lê os 8 primeiros do span)
        var va = Vector256.Create(a);
        var vb = Vector256.Create(b);
        var diff = va - vb;
        float sum = Vector256.Sum(diff * diff);
        // sobra: dims 8-13 escalar
        for (int d = 8; d < 14; d++)
        {
            float df = a[d] - b[d];
            sum += df * df;
        }
        return sum;
    }
    // fallback escalar (CPU sem AVX)
    float s = 0f;
    for (int d = 0; d < 14; d++)
    {
        float df = a[d] - b[d];
        s += df * df;
    }
    return s;
}
```

Requer apenas `using System.Runtime.Intrinsics;`. `Vector256.Create(ReadOnlySpan<float>)` lê os 8 primeiros elementos do span (exige `Length ≥ 8`); tanto `a` (query, 14) quanto `b` (vetor de referência, 14) têm 14 ≥ 8, então é seguro. Sem `unsafe`, sem `MemoryMarshal`.

**Por que `Vector256.*` (genérico) e não `Avx.*`:** as APIs genéricas `Vector256.IsHardwareAccelerated` / `LoadUnsafe` / `Sum` são portáveis (usam AVX no x64, têm caminho equivalente em ARM) e mais limpas que intrínsecos específicos de ISA. O `AllowUnsafeBlocks` já está ligado (M3); este código **não** precisa de `unsafe` (as APIs `LoadUnsafe`/`GetReference` são seguras em uso normal).

### Consequência: não é mais bit-a-bit igual ao M4

A soma de floats não é associativa. O SIMD soma as dims 0-7 numa ordem diferente do loop escalar sequencial do M4, depois adiciona a sobra. O resultado pode diferir do M4 em **1 ULP** (a menor unidade representável do float). Isso é:
- **Matematicamente a mesma distância** — só muda o arredondamento.
- **Irrelevante para o ranking** dos 5 vizinhos exceto em empates de precisão infinitesimal, que não ocorrem nos testes (distâncias bem distintas) e são desprezíveis em dados reais.

Os testes existentes do KNN continuam válidos: `Search_TiedDistances` usa 6 vetores **idênticos** (distância idêntica em SIMD ou escalar → empate preservado); os demais usam valores bem separados.

---

## Estrutura de arquivos

| Arquivo | Ação |
|---------|------|
| `src/Api/Detection/Normalizer.cs` | `Normalize` → escreve em `Span<float> dest`, sem return |
| `src/Api/Detection/KnnSearch.cs` | `Search(ReadOnlySpan<float> query, ...)` + helper `SquaredDistance` com SIMD/fallback; Top-K intacto |
| `src/Api/Program.cs` | extrai `ComputeScore` síncrono com `stackalloc float[14]` |
| `tests/Api.Tests/NormalizerTests.cs` | só o corpo do helper privado `Normalize` (aloca buffer, chama nova assinatura, retorna) |

Sem mudança em `KnnSearchTests.cs` (conversão implícita `float[]`→`ReadOnlySpan<float>`), `EndpointTests.cs` (contrato HTTP igual), `DatasetTests.cs`, csproj ou infra.

---

## Testes

- **NormalizerTests:** os ~30 casos ficam idênticos; só o helper privado muda para:
  ```csharp
  private static float[] Normalize(TransactionRequest req, Dictionary<string,float>? mcc = null, HashSet<string>? known = null)
  {
      var vec = new float[14];
      Normalizer.Normalize(req, mcc ?? [], known ?? new HashSet<string>(req.Customer.KnownMerchants), vec);
      return vec;
  }
  ```
- **KnnSearchTests:** sem mudança — os 6 testes (incluindo empate e count<5) continuam verdes, validando que o Top-K e o desempate sobrevivem à troca do cálculo de distância.
- **EndpointTests:** sem mudança — validam o contrato HTTP de ponta a ponta (o `ComputeScore` é exercitado por eles).

---

## Verificação e medição

1. `dotnet test` — todos verdes.
2. `docker compose up` → `/ready`.
3. Latência de uma request (curl com `time_total`) — comparar com os ~20ms do M4.
4. k6 oficial (120s → 900 req/s) → `test/results.json`.
5. Registrar no Obsidian (nota de conceito **SIMD** se ainda não existir / nota de marco **M5**, atualizar índice e callout de baseline): p99, score, failure_rate, breakdown (tp/tn/fp/fn/http_errors), comparados contra **M4 (p99 2002ms, score −6000, 238 respostas corretas)**.

**Expectativa honesta:** SIMD acelera o loop de distância ~2-4x (não 8x limpo, por causa da soma horizontal + sobra de 6 dims). Em ~1 CPU, o sistema ainda deve ficar abaixo de 900 req/s → provavelmente **continua −6000**, mas com mais respostas completas e p99 melhor. O número real dirá quão perto o brute-force exato chega antes de o HNSW (parar de varrer os 3M) virar necessário.

---

## Conceito para o Obsidian

Atualizar/criar a nota **SIMD** em `TEORIA/Performance e Sistemas/`: o que é "uma instrução, vários dados", `Vector256<float>` = 8 floats, a soma horizontal, por que para vetores curtos (14) o ganho é parcial, e a não-associatividade da soma float (por que o resultado muda no último ULP).
