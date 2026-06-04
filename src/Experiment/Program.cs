using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using RinhaBackend;
using RinhaBackend.Data;
using RinhaBackend.Detection;

// M8 experimento: mede OFFLINE modelos sobre test-data.json (gabarito = expected_approved).
//   - Taxa: responde com a taxa de fraude da gaveta (O(1), sem kNN).
//   - kNN M7 (baseline): PartitionedIndex.Search na gaveta (reproduz a API atual).
//   - Taxa FINA: mesma ideia O(1), mas gavetas refinadas (5 flags + octis) p/ purificar.
// Uso: dotnet run -- <dataDir> <testJson>

const int Dims = 14;
string dataDir = args.Length > 0 ? args[0] : "data";
string testPath = args.Length > 1 ? args[1] : "test/test-data.json";

// 1. references.bin (count int32, count*14 floats, count bytes labels) — igual IndexGen.
string refPath = Path.Combine(dataDir, "references.bin");
Console.WriteLine($"[exp] lendo {refPath} ...");
var sw = Stopwatch.StartNew();
int count;
float[] floats;
byte[] labels;
using (var fs = new FileStream(refPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
using (var r = new BinaryReader(fs))
{
    count = r.ReadInt32();
    floats = new float[count * Dims];
    for (int i = 0; i < count * Dims; i++) floats[i] = r.ReadSingle();
    labels = new byte[count];
    for (int i = 0; i < count; i++) labels[i] = r.ReadByte();
}
Console.WriteLine($"[exp] {count:N0} referências carregadas em {sw.ElapsedMilliseconds} ms");

// 2. índice em memória (cuts + offsets + vetores quantizados agrupados + labels agrupados).
var (vec, lab, offsets, cuts) = IndexBuilder.BuildInMemory(floats, labels, count);

// 3. taxa de fraude por gaveta (coarse, 32) + distribuição.
int P = PartitionKey.Count;
var rate = new float[P];
var size = new int[P];
long totalFraud = 0;
for (int p = 0; p < P; p++)
{
    int s = offsets[p], e = offsets[p + 1];
    int fr = 0;
    for (int i = s; i < e; i++) if (lab[i] != 0) fr++;
    size[p] = e - s;
    rate[p] = size[p] > 0 ? fr / (float)size[p] : -1f;
    totalFraud += fr;
}
float globalRate = totalFraud / (float)count;
for (int p = 0; p < P; p++) if (rate[p] < 0) rate[p] = globalRate;

Console.WriteLine($"[exp] taxa global de fraude = {globalRate:P2}");

// 4. mcc_risk.json (dim 12 do Normalizer).
var mccRisk = JsonSerializer.Deserialize<Dictionary<string, float>>(
    File.ReadAllText(Path.Combine(dataDir, "mcc_risk.json")))
    ?? throw new InvalidOperationException("mcc_risk.json inválido");

// 5. test-data.json (transações cruas + gabarito).
var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
TestFile tf;
using (var ts = File.OpenRead(testPath))
    tf = JsonSerializer.Deserialize<TestFile>(ts, jsonOpts)
         ?? throw new InvalidOperationException("test-data.json inválido");
var entries = tf.Entries;
int n = entries.Length;
Console.WriteLine($"[exp] {n:N0} transações de teste");

// 6. avalia coarse (taxa + kNN) numa passada; guarda os vetores normalizados p/ reuso.
var testVecs = new float[n * Dims];
var rateScores = new float[n];
var expected = new bool[n];
var kApproved = new bool[n]; // decisão do kNN por query (p/ reuso no híbrido)
var testKey = new int[n];    // gaveta coarse de cada query
int ktp = 0, ktn = 0, kfp = 0, kfn = 0; // kNN @ 0.6 (igual API)

Span<float> v = stackalloc float[Dims];
Span<byte> q = stackalloc byte[Dims];
sw.Restart();
for (int i = 0; i < n; i++)
{
    var req = entries[i].Request;
    var known = new HashSet<string>(req.Customer.KnownMerchants, StringComparer.OrdinalIgnoreCase);
    Normalizer.Normalize(req, mccRisk, known, v);
    v.CopyTo(testVecs.AsSpan(i * Dims, Dims));
    int key = PartitionKey.Compute(v, cuts);
    testKey[i] = key;

    expected[i] = entries[i].ExpectedApproved;
    rateScores[i] = rate[key];

    Quantizer.Quantize(v, q);
    float ks = PartitionedIndex.Search(vec, lab, offsets, key, q);
    kApproved[i] = ks < 0.6f;
    Tally(kApproved[i], expected[i], ref ktp, ref ktn, ref kfp, ref kfn);
}
Console.WriteLine($"[exp] avaliação coarse (kNN + taxa) em {sw.ElapsedMilliseconds} ms");

double kDet = DetScore(kfp, kfn, n);
var (rThr, rtp, rtn, rfp, rfn) = SweepBest(rateScores, expected, n);

// 7. PARTIÇÃO FINA: 5 flags (add dim8=tx_count, dim2=amount/avg) + octis do valor (dim0).
//    Thresholds data-driven (medianas / octis das referências).
float[] octis = Percentiles(floats, 0, count, Dims, [1, 2, 3, 4, 5, 6, 7], 8);
float txCut = Percentiles(floats, 8, count, Dims, [4], 8)[0];     // mediana de dim8
float ratioCut = Percentiles(floats, 2, count, Dims, [4], 8)[0];  // mediana de dim2
Console.WriteLine($"[exp] partição fina: octis(v0)=[{string.Join(", ", Array.ConvertAll(octis, x => x.ToString("F3")))}]"
    + $"  txCut(v8)={txCut:F3}  ratioCut(v2)={ratioCut:F3}");

const int FineP = 8 * 32; // 8 octis × 32 flags
var fineFraud = new int[FineP];
var fineSize = new int[FineP];
for (int i = 0; i < count; i++)
{
    int fk = FineKey(floats.AsSpan(i * Dims, Dims), octis, txCut, ratioCut);
    fineSize[fk]++;
    if (labels[i] != 0) fineFraud[fk]++;
}
var fineRate = new float[FineP];
for (int p = 0; p < FineP; p++)
    fineRate[p] = fineSize[p] > 0 ? fineFraud[p] / (float)fineSize[p] : globalRate;

var fineScores = new float[n];
for (int i = 0; i < n; i++)
{
    int fk = FineKey(testVecs.AsSpan(i * Dims, Dims), octis, txCut, ratioCut);
    fineScores[i] = fineRate[fk];
}
var (fThr, ftp, ftn, ffp, ffn) = SweepBest(fineScores, expected, n);
int nonEmptyFine = 0;
for (int p = 0; p < FineP; p++) if (fineSize[p] > 0) nonEmptyFine++;
Console.WriteLine($"[exp] gavetas finas não-vazias: {nonEmptyFine}/{FineP}");

// 8. veredito.
Console.WriteLine();
Console.WriteLine($"=== RESULTADO (N={n:N0}, offline erro=0) ===");
Report("kNN M7 (32 gav)    @0.6  ", ktp, ktn, kfp, kfn, n);
Report($"Taxa coarse (32)   @{rThr:F3}", rtp, rtn, rfp, rfn, n);
Report($"Taxa FINA  (256)   @{fThr:F3}", ftp, ftn, ffp, ffn, n);
Console.WriteLine();
Console.WriteLine("detScore ignora latência. Lookup O(1) (taxa) → p99 ~sub-ms → p99Score ~no teto.");
Console.WriteLine("kNN paga p99 maior (scan/árvore da gaveta).");
Console.WriteLine($"Veredito: se Taxa FINA ~≥ kNN ({kDet:F0}) → O(1) substitui kNN. Senão → árvore-kNN.");

// 9. HÍBRIDO: gaveta pura (rate<eps ou >1-eps) responde O(1); ambígua usa kNN (já calculado).
//    Em gaveta pura, taxa==kNN (os 5 vizinhos têm o mesmo rótulo) → zero perda de detecção.
const float pureEps = 0.01f;
var isPure = new bool[P];
int pureCount = 0;
for (int p = 0; p < P; p++) { isPure[p] = size[p] > 0 && (rate[p] < pureEps || rate[p] > 1f - pureEps); if (isPure[p]) pureCount++; }

int htp = 0, htn = 0, hfp = 0, hfn = 0, shortcut = 0;
var scanM7 = new int[n];
var scanHyb = new int[n];
for (int i = 0; i < n; i++)
{
    int key = testKey[i];
    bool approved = isPure[key] ? rate[key] < 0.5f : kApproved[i];
    Tally(approved, expected[i], ref htp, ref htn, ref hfp, ref hfn);
    scanM7[i] = size[key];
    scanHyb[i] = isPure[key] ? 0 : size[key];
    if (isPure[key]) shortcut++;
}

Console.WriteLine();
Console.WriteLine($"--- HÍBRIDO (eps={pureEps}: {pureCount} gavetas puras de {P}) ---");
Report("Híbrido O(1)+kNN         ", htp, htn, hfp, hfn, n);
Console.WriteLine($"queries atalhadas O(1): {shortcut:N0}/{n:N0} = {shortcut * 100.0 / n:F1}%");
Console.WriteLine("custo de scan por query (nº de vetores comparados) — proxy direto da latência:");
Console.WriteLine($"  M7 (sempre scan): p50={Pct(scanM7, 50):N0}  p90={Pct(scanM7, 90):N0}  p99={Pct(scanM7, 99):N0}  max={Pct(scanM7, 100):N0}");
Console.WriteLine($"  Híbrido         : p50={Pct(scanHyb, 50):N0}  p90={Pct(scanHyb, 90):N0}  p99={Pct(scanHyb, 99):N0}  max={Pct(scanHyb, 100):N0}");

static int Pct(int[] a, double p)
{
    var c = (int[])a.Clone();
    Array.Sort(c);
    int idx = (int)Math.Ceiling(p / 100.0 * c.Length) - 1;
    return c[Math.Clamp(idx, 0, c.Length - 1)];
}

static (float thr, int tp, int tn, int fp, int fn) SweepBest(float[] scores, bool[] expected, int n)
{
    double best = double.NegativeInfinity;
    float bestThr = 0f;
    int btp = 0, btn = 0, bfp = 0, bfn = 0;
    for (float thr = 0.025f; thr <= 0.9751f; thr += 0.025f)
    {
        int tp = 0, tn = 0, fp = 0, fn = 0;
        for (int i = 0; i < n; i++) Tally(scores[i] < thr, expected[i], ref tp, ref tn, ref fp, ref fn);
        double det = DetScore(fp, fn, n);
        if (det > best) { best = det; bestThr = thr; btp = tp; btn = tn; bfp = fp; bfn = fn; }
    }
    return (bestThr, btp, btn, bfp, bfn);
}

// percentis de uma coluna (dim) — idx-ésimo de cada quantil em `parts` partes.
static float[] Percentiles(float[] data, int dim, int count, int dims, int[] parts, int totalParts)
{
    var col = new float[count];
    for (int i = 0; i < count; i++) col[i] = data[i * dims + dim];
    Array.Sort(col);
    var outp = new float[parts.Length];
    for (int j = 0; j < parts.Length; j++)
    {
        int idx = Math.Min((parts[j] * count) / totalParts, count - 1);
        outp[j] = col[idx];
    }
    return outp;
}

static int FineKey(ReadOnlySpan<float> v, float[] octis, float txCut, float ratioCut)
{
    int flags = (v[9] > 0.5f ? 1 : 0)
              | (v[10] > 0.5f ? 2 : 0)
              | (v[11] > 0.5f ? 4 : 0)
              | (v[8] > txCut ? 8 : 0)
              | (v[2] > ratioCut ? 16 : 0);
    int bucket = 0;
    for (int i = 0; i < octis.Length; i++) { if (v[0] > octis[i]) bucket++; else break; }
    return bucket * 32 + flags;
}

static void Tally(bool approved, bool expectedApproved, ref int tp, ref int tn, ref int fp, ref int fn)
{
    if (approved == expectedApproved)
    {
        if (approved) tn++; // legítima aprovada
        else tp++;          // fraude negada
    }
    else
    {
        if (approved) fn++; // fraude aprovada (perdeu fraude)
        else fp++;          // legítima negada
    }
}

static double DetScore(int fp, int fn, int n)
{
    double failureRate = (fp + fn) / (double)n;
    if (failureRate > 0.15) return -3000;
    double e = fp * 1.0 + fn * 3.0;
    double eps = e / n;
    double rateComp = 1000 * Math.Log10(1.0 / Math.Max(eps, 0.001));
    double absPen = -300 * Math.Log10(1.0 + e);
    return rateComp + absPen;
}

static void Report(string name, int tp, int tn, int fp, int fn, int n)
{
    double failureRate = (fp + fn) / (double)n;
    double det = DetScore(fp, fn, n);
    Console.WriteLine(
        $"{name} | tp={tp,6} tn={tn,6} fp={fp,5} fn={fn,5} | falhas={failureRate,6:P2} "
        + $"| E={fp + 3 * fn,6} | detScore={det,8:F0}");
}

public sealed class TestFile
{
    [JsonPropertyName("entries")] public TestEntry[] Entries { get; set; } = [];
}

public sealed class TestEntry
{
    [JsonPropertyName("request")] public TransactionRequest Request { get; set; } = null!;
    [JsonPropertyName("expected_approved")] public bool ExpectedApproved { get; set; }
}
