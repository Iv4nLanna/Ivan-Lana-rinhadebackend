# M2 — Dataset Binário: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Converter o dataset para formato binário, reduzindo o startup de >5min (JSON parsing) para ~5s (leitura direta de bytes).

**Architecture:** Um script Python converte `references.json.gz` → `references.bin` uma única vez. A API tenta carregar `references.bin` primeiro; se não encontrar, cai no JSON como fallback (mantém compatibilidade com testes). O formato binário é: `[int32 count][count×14×float32 vetores][count×1 byte labels]` — sem parsing, sem alocações intermediárias.

**Tech Stack:** Python 3 (stdlib only), C# .NET 9, BinaryReader/BinaryWriter, xUnit

---

## Estrutura de arquivos

```
rinha-2026/
├── scripts/
│   └── generate_binary.py          ← NEW: converte references.json.gz → references.bin
├── data/
│   └── references.bin              ← gerado pelo script, ~163 MB, no .gitignore
├── src/Api/Data/
│   └── ReferenceDataset.cs         ← MODIFY: adiciona LoadFromBinary, refatora LoadAsync
├── tests/Api.Tests/
│   └── DatasetTests.cs             ← MODIFY: adiciona teste de carregamento binário
└── .gitignore                      ← MODIFY: adiciona data/*.bin
```

---

## Task 1: Script de conversão e geração do arquivo binário

**Files:**
- Create: `scripts/generate_binary.py`
- Modify: `.gitignore`

### Formato binário

```
[4 bytes  ] int32 little-endian  → número de entradas (count)
[count × 56 bytes] float32 × 14  → vetores, row-major, little-endian
[count × 1 byte ] uint8           → labels: 1 = fraude, 0 = legítimo
```

Para 3M entradas: 4 + 168_000_000 + 3_000_000 = **~163 MB**

- [ ] **Step 1: Adicionar data/*.bin ao .gitignore**

Substituir o conteúdo de `.gitignore`:

```
bin/
obj/
.vs/
*.user
data/*.json.gz
data/*.json
data/*.bin
!data/.gitkeep
```

- [ ] **Step 2: Criar `scripts/generate_binary.py`**

```python
#!/usr/bin/env python3
"""Converte references.json.gz para o formato binário references.bin.

Formato de saída:
  [4 bytes int32]         count de entradas
  [count × 56 bytes]      count × 14 floats (float32 little-endian, row-major)
  [count × 1 byte]        labels: 1=fraude, 0=legítimo

Uso: python3 scripts/generate_binary.py [data_dir]
     Padrão: data/ relativo ao diretório deste script
"""
import array
import gzip
import json
import os
import struct
import sys
import time


def main() -> None:
    script_dir = os.path.dirname(os.path.abspath(__file__))
    data_dir = sys.argv[1] if len(sys.argv) > 1 else os.path.join(script_dir, "..", "data")
    data_dir = os.path.abspath(data_dir)

    input_path = os.path.join(data_dir, "references.json.gz")
    output_path = os.path.join(data_dir, "references.bin")

    if not os.path.exists(input_path):
        print(f"ERRO: {input_path} não encontrado", file=sys.stderr)
        sys.exit(1)

    print(f"Lendo {input_path}...")
    t0 = time.perf_counter()

    with gzip.open(input_path, "rt", encoding="utf-8") as f:
        entries = json.load(f)

    count = len(entries)
    elapsed = time.perf_counter() - t0
    print(f"  {count:,} entradas lidas em {elapsed:.1f}s")

    print("Construindo buffers binários...")
    t1 = time.perf_counter()

    vectors = array.array("f")  # float32
    labels = array.array("B")   # uint8

    for entry in entries:
        vectors.extend(entry["vector"])
        labels.append(1 if entry["label"] == "fraud" else 0)

    elapsed = time.perf_counter() - t1
    print(f"  buffers prontos em {elapsed:.1f}s")

    print(f"Gravando {output_path}...")
    t2 = time.perf_counter()

    with open(output_path, "wb") as f:
        f.write(struct.pack("<i", count))   # header: count como int32 little-endian
        vectors.tofile(f)                   # todos os vetores de uma vez
        labels.tofile(f)                    # todos os labels de uma vez

    elapsed = time.perf_counter() - t2
    size_mb = os.path.getsize(output_path) / (1024 * 1024)
    print(f"  {output_path} gravado ({size_mb:.1f} MB) em {elapsed:.1f}s")
    print(f"Pronto! Total: {time.perf_counter() - t0:.1f}s")


if __name__ == "__main__":
    main()
```

- [ ] **Step 3: Rodar o script**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
python3 scripts/generate_binary.py
```

Resultado esperado:
```
Lendo .../data/references.json.gz...
  3000000 entradas lidas em Xs
Construindo buffers binários...
  buffers prontos em Xs
Gravando .../data/references.bin...
  .../data/references.bin gravado (163.1 MB) em Xs
Pronto! Total: Xs
```

- [ ] **Step 4: Verificar o arquivo gerado**

```bash
ls -lh /mnt/c/Users/Ivan/rinha-2026/data/references.bin
# Esperado: ~163 MB

python3 -c "
import struct
with open('/mnt/c/Users/Ivan/rinha-2026/data/references.bin', 'rb') as f:
    count = struct.unpack('<i', f.read(4))[0]
    print(f'count = {count:,}')  # deve ser 3000000
"
```

- [ ] **Step 5: Commit**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
git add .gitignore scripts/generate_binary.py
git commit -m "feat: script de conversão JSON.gz → binário (M2)"
```

---

## Task 2: Binary loading em ReferenceDataset (TDD)

**Files:**
- Modify: `src/Api/Data/ReferenceDataset.cs`
- Modify: `tests/Api.Tests/DatasetTests.cs`

- [ ] **Step 1: Escrever o teste que vai falhar**

Adicionar ao final de `tests/Api.Tests/DatasetTests.cs`, dentro da classe `DatasetTests`:

```csharp
[Fact]
public async Task Load_BinaryFile_LoadsCorrectly()
{
    var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tempDir);
    try
    {
        // mcc_risk.json é carregado sempre pelo LoadAsync
        await File.WriteAllTextAsync(
            Path.Combine(tempDir, "mcc_risk.json"),
            "{\"5411\":0.15}"
        );

        // Cria references.bin com 2 entradas
        using (var fs = File.Create(Path.Combine(tempDir, "references.bin")))
        using (var w = new BinaryWriter(fs))
        {
            w.Write(2); // count = 2

            // Entrada 0: todas as dims = 0.5f
            for (int d = 0; d < 14; d++) w.Write(0.5f);

            // Entrada 1: dims 5 e 6 = -1f (sentinel last_transaction nula), resto = 0.1f
            for (int d = 0; d < 14; d++) w.Write(d == 5 || d == 6 ? -1f : 0.1f);

            w.Write((byte)1); // label 0: fraude
            w.Write((byte)0); // label 1: legítimo
        }

        var dataset = new ReferenceDataset();
        await dataset.LoadAsync(tempDir);

        Assert.True(dataset.IsReady);
        Assert.Equal(2, dataset.Count);
        Assert.Equal(14, dataset.Vectors.GetLength(1));
        Assert.Equal(2, dataset.Labels.Length);

        // Verifica vetores
        Assert.Equal(0.5f, dataset.Vectors[0, 0]);
        Assert.Equal(0.5f, dataset.Vectors[0, 13]);
        Assert.Equal(0.1f, dataset.Vectors[1, 0]);
        Assert.Equal(-1f,  dataset.Vectors[1, 5]);
        Assert.Equal(-1f,  dataset.Vectors[1, 6]);

        // Verifica labels
        Assert.True(dataset.Labels[0]);   // fraude
        Assert.False(dataset.Labels[1]);  // legítimo
    }
    finally
    {
        Directory.Delete(tempDir, recursive: true);
    }
}
```

- [ ] **Step 2: Rodar para confirmar que FALHA**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
"/mnt/c/Program Files/dotnet/dotnet.exe" test tests/Api.Tests/Api.Tests.csproj --filter "Load_BinaryFile" -v minimal 2>&1 | tail -5
```

Resultado esperado: `FAILED` — o método `LoadAsync` ainda não procura `references.bin`.

- [ ] **Step 3: Refatorar e implementar LoadFromBinary em ReferenceDataset.cs**

Substituir o conteúdo completo de `src/Api/Data/ReferenceDataset.cs`:

```csharp
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RinhaBackend.Data;

public class ReferenceDataset
{
    public float[,] Vectors { get; private set; } = new float[0, 14];
    public bool[] Labels { get; private set; } = [];
    public int Count { get; private set; }
    public Dictionary<string, float> MccRisk { get; private set; } = [];

    private volatile bool _isReady;
    public bool IsReady => _isReady;

    public async Task LoadAsync(string dataDir)
    {
        var mccPath = Path.Combine(dataDir, "mcc_risk.json");
        var mccJson = await File.ReadAllTextAsync(mccPath);
        MccRisk = JsonSerializer.Deserialize<Dictionary<string, float>>(mccJson)
                  ?? throw new InvalidOperationException("mcc_risk.json inválido");

        var binPath = Path.Combine(dataDir, "references.bin");
        if (File.Exists(binPath))
        {
            LoadFromBinary(binPath);
        }
        else
        {
            await LoadFromJsonAsync(dataDir);
        }

        _isReady = true;
    }

    // M2: leitura binária — sem JSON parsing, sem alocações intermediárias
    // Formato: [int32 count][count×14×float32 vetores][count×byte labels]
    private void LoadFromBinary(string binPath)
    {
        using var fs = new FileStream(binPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 1 << 20); // 1 MB de buffer de leitura
        using var reader = new BinaryReader(fs);

        int count = reader.ReadInt32();

        var vectors = new float[count, 14];
        for (int i = 0; i < count; i++)
            for (int d = 0; d < 14; d++)
                vectors[i, d] = reader.ReadSingle();

        var labels = new bool[count];
        for (int i = 0; i < count; i++)
            labels[i] = reader.ReadByte() != 0;

        Count = count;
        Vectors = vectors;
        Labels = labels;
    }

    // Fallback para testes (example-references.json) e compatibilidade
    private async Task LoadFromJsonAsync(string dataDir)
    {
        var fullPath = Path.Combine(dataDir, "references.json.gz");
        var examplePath = Path.Combine(dataDir, "example-references.json");

        ReferenceEntry[] entries;

        if (File.Exists(fullPath))
        {
            using var fileStream = File.OpenRead(fullPath);
            using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
            entries = await JsonSerializer.DeserializeAsync<ReferenceEntry[]>(gzipStream)
                      ?? throw new InvalidOperationException("references.json.gz inválido");
        }
        else if (File.Exists(examplePath))
        {
            var json = await File.ReadAllTextAsync(examplePath);
            entries = JsonSerializer.Deserialize<ReferenceEntry[]>(json)
                      ?? throw new InvalidOperationException("example-references.json inválido");
        }
        else
        {
            throw new FileNotFoundException(
                $"Nenhum arquivo de dataset encontrado em {dataDir}. " +
                "Esperado: references.bin, references.json.gz ou example-references.json");
        }

        Count = entries.Length;
        Vectors = new float[Count, 14];
        Labels = new bool[Count];

        for (int i = 0; i < Count; i++)
        {
            for (int d = 0; d < 14; d++)
                Vectors[i, d] = entries[i].Vector[d];
            Labels[i] = entries[i].Label == "fraud";
        }
    }

    private sealed class ReferenceEntry
    {
        [JsonPropertyName("vector")]
        public float[] Vector { get; set; } = [];

        [JsonPropertyName("label")]
        public string Label { get; set; } = "";
    }
}
```

- [ ] **Step 4: Rodar todos os testes**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
"/mnt/c/Program Files/dotnet/dotnet.exe" test tests/Api.Tests/Api.Tests.csproj -v minimal 2>&1 | tail -5
```

Resultado esperado: `Passed! - Failed: 0, Passed: 35`

> Nota: os testes existentes (`Load_ExampleFile_*`) continuam usando `example-references.json` porque o tempDir deles não tem `references.bin` — o fallback JSON funciona automaticamente.

- [ ] **Step 5: Commit**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
git add src/Api/Data/ReferenceDataset.cs tests/Api.Tests/DatasetTests.cs
git commit -m "feat: M2 — carregamento binário do dataset, startup ~5s vs >5min"
```

---

## Task 3: Verificar melhoria de startup com Docker

**Files:** nenhum — apenas medição

- [ ] **Step 1: Subir os containers e medir tempo de startup**

```bash
cd /mnt/c/Users/Ivan/rinha-2026/infra

# Iniciar containers
docker compose up -d

# Medir tempo até /ready responder
START=$(date +%s)
until curl -sf http://localhost:9999/ready 2>/dev/null; do sleep 1; done
END=$(date +%s)
echo "Startup: $((END - START)) segundos"
```

Resultado esperado: **< 30 segundos** (vs >5 minutos em M1).

- [ ] **Step 2: Teste manual do /fraud-score**

```bash
curl -s -X POST http://localhost:9999/fraud-score \
  -H "Content-Type: application/json" \
  -d '{
    "id": "m2-test",
    "transaction": {"amount": 150.0, "installments": 1, "requested_at": "2026-01-15T14:30:00Z"},
    "customer": {"avg_amount": 120.0, "tx_count_24h": 3, "known_merchants": ["MERC-1"]},
    "merchant": {"id": "MERC-99", "mcc": "5411", "avg_amount": 200.0},
    "terminal": {"is_online": false, "card_present": true, "km_from_home": 2.5},
    "last_transaction": {"timestamp": "2026-01-15T12:00:00Z", "km_from_current": 1.2}
  }' | python3 -m json.tool
```

Resultado esperado: JSON com `approved` e `fraud_score`.

- [ ] **Step 3: Ver RAM usada**

```bash
docker stats --no-stream --format "table {{.Name}}\t{{.MemUsage}}"
```

Anotar os valores — vamos usá-los para decidir se M3 (MemoryMappedFile) é necessário.

- [ ] **Step 4: Parar containers**

```bash
docker compose down
```

- [ ] **Step 5: Commit**

```bash
cd /mnt/c/Users/Ivan/rinha-2026
git add infra/docker-compose.yml
git commit -m "chore: registra resultado de startup M2"
```

---

## Self-Review

**Spec coverage:**
- ✅ Script de conversão JSON.gz → binário
- ✅ Formato binário definido e documentado
- ✅ LoadFromBinary implementado com TDD
- ✅ Fallback JSON mantido (testes continuam funcionando)
- ✅ Verificação de startup com Docker
- ✅ .gitignore atualizado para `data/*.bin`

**Placeholders:** nenhum — todo o código está completo.

**Type consistency:**
- `LoadAsync(string dataDir)` — interface pública inalterada
- `LoadFromBinary(string)` — private, referenciado apenas em `LoadAsync`
- `LoadFromJsonAsync(string)` — private, referenciado apenas em `LoadAsync`
- `BinaryWriter` usado no teste para escrever o mesmo formato que `BinaryReader` lê — consistente
