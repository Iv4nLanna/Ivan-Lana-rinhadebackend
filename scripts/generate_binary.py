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
        f.write(struct.pack("<i", count))
        vectors.tofile(f)
        labels.tofile(f)

    elapsed = time.perf_counter() - t2
    size_mb = os.path.getsize(output_path) / (1024 * 1024)
    print(f"  {output_path} gravado ({size_mb:.1f} MB) em {elapsed:.1f}s")
    print(f"Pronto! Total: {time.perf_counter() - t0:.1f}s")


if __name__ == "__main__":
    main()
