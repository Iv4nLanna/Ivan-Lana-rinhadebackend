using RinhaBackend.Data;

// Uso: dotnet run --project src/IndexGen -- <dataDir>
// Lê <dataDir>/references.bin (float) e grava <dataDir>/index.bin (gavetas + quantizado).
string dataDir = args.Length > 0 ? args[0] : "/app/data";
string inPath = Path.Combine(dataDir, "references.bin");
string outPath = Path.Combine(dataDir, "index.bin");

using var fs = new FileStream(inPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
using var r = new BinaryReader(fs);

int count = r.ReadInt32();
if (count < 0 || count > 10_000_000)
    throw new InvalidDataException($"references.bin: count={count} inválido");

var floats = new float[count * 14];
for (int i = 0; i < count * 14; i++) floats[i] = r.ReadSingle();
var labels = new byte[count];
for (int i = 0; i < count; i++) labels[i] = r.ReadByte();

Console.WriteLine($"[IndexGen] {count} vetores. Calculando gavetas + quantizando...");
IndexBuilder.WriteToFile(floats, labels, count, outPath);
Console.WriteLine($"[IndexGen] index.bin: {new FileInfo(outPath).Length:N0} bytes");
