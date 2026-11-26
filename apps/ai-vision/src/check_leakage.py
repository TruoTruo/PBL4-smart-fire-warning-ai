"""Kiểm tra rò rỉ dữ liệu giữa các tập train / val / test.

Bước 1 – theo tên gốc: tách tên gốc của ảnh bằng biểu thức chính quy, gom vào bảng băm
         tên gốc -> các tập chứa nó. Tên gốc xuất hiện ở hơn một tập là trùng. O(N).
Bước 2 – theo nội dung: dHash 64 bit cho ảnh gốc và ảnh lật ngang, Hamming <= ngưỡng
         (mặc định 5 bit) là gần trùng. O(V · Na · Nb) phép XOR.

Ví dụ:
    python src/check_leakage.py --data config/data.yaml
    python src/check_leakage.py --data config/data.yaml --write-clean
"""
import argparse
import csv
import json
import re
import sys
import time
from collections import defaultdict
from concurrent.futures import ProcessPoolExecutor
from pathlib import Path

import numpy as np

from common import list_images, load_data_yaml, split_image_dir
from dhash import dhash_file, near_duplicates

# Bỏ hậu tố do Roboflow thêm (abc_jpg.rf.<mã>) và đuôi bản sao "(1)", "_copy"
DEFAULT_ROOT_RE = r"(_(jpe?g|png)\.rf\.[0-9a-f]+)$|(\s*\(\d+\)|_copy\d*)$"


def source_root(stem, pattern):
    return re.sub(pattern, "", stem, flags=re.IGNORECASE)


def check_by_name(files_by_split, pattern):
    table = defaultdict(set)  # bảng băm: tên gốc -> các tập
    for split, files in files_by_split.items():
        for p in files:
            table[source_root(p.stem, pattern)].add(split)
    return {root: sorted(s) for root, s in table.items() if len(s) > 1}


def hash_split(files, cache_file, workers):
    """dHash cả một tập, có lưu đệm .npz để lần sau chạy nhanh."""
    names = [str(p) for p in files]
    if cache_file.is_file():
        data = np.load(cache_file, allow_pickle=False)
        if list(data["names"]) == names:
            return data["h"], data["hf"]
    t0 = time.time()
    with ProcessPoolExecutor(max_workers=workers) as ex:
        pairs = list(ex.map(dhash_file, files, chunksize=64))
    h = np.array([a for a, _ in pairs], dtype=np.uint64)
    hf = np.array([b for _, b in pairs], dtype=np.uint64)
    np.savez(cache_file, names=np.array(names), h=h, hf=hf)
    print(f"  dHash {len(files)} ảnh trong {time.time() - t0:.1f} s")
    return h, hf


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--data", default="config/data.yaml")
    ap.add_argument("--splits", nargs="+", default=["train", "val", "test"])
    ap.add_argument("--threshold", type=int, default=5, help="ngưỡng Hamming (bit)")
    ap.add_argument("--root-regex", default=DEFAULT_ROOT_RE, help="biểu thức tách tên gốc")
    ap.add_argument("--out", default="runs/leakage")
    ap.add_argument("--workers", type=int, default=4)
    ap.add_argument("--write-clean", action="store_true",
                    help="ghi danh sách ảnh test sạch + data_clean.yaml")
    args = ap.parse_args()

    cfg = load_data_yaml(args.data)
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    files = {s: list_images(split_image_dir(cfg, s)) for s in args.splits}
    for s, f in files.items():
        print(f"{s}: {len(f)} ảnh")

    # Bước 1
    dup_names = check_by_name(files, args.root_regex)
    print(f"\nBước 1 – tên gốc xuất hiện ở nhiều tập: {len(dup_names)}")
    with open(out / "trung_ten.csv", "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["ten_goc", "cac_tap"])
        for root, splits in sorted(dup_names.items()):
            w.writerow([root, "|".join(splits)])

    # Bước 2
    print("\nBước 2 – dHash 64 bit")
    hashes = {s: hash_split(files[s], out / f"dhash_{s}.npz", args.workers) for s in args.splits}
    rows, dirty = [], defaultdict(set)
    for i, a in enumerate(args.splits):
        for b in args.splits[i + 1:]:
            hits = near_duplicates(hashes[b][0], hashes[b][1], hashes[a][0], args.threshold)
            for qi, rj, dist, flipped in hits:
                rows.append([b, files[b][qi].name, a, files[a][rj].name, dist, int(flipped)])
                dirty[b].add(qi)
            print(f"  {b} so với {a}: {len(hits)} cặp gần trùng, {len({h[0] for h in hits})} ảnh {b} bị ảnh hưởng")
    with open(out / "trung_noi_dung.csv", "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["tap", "anh", "trung_voi_tap", "anh_trung", "hamming", "lat_ngang"])
        w.writerows(rows)

    summary = {
        "so_anh": {s: len(f) for s, f in files.items()},
        "nguong_hamming": args.threshold,
        "trung_ten_goc": len(dup_names),
        "cap_gan_trung": len(rows),
        "anh_bi_anh_huong": {s: len(v) for s, v in dirty.items()},
    }

    if args.write_clean and "test" in files:
        keep = [str(p) for i, p in enumerate(files["test"]) if i not in dirty["test"]]
        (out / "test_clean.txt").write_text("\n".join(keep) + "\n", encoding="utf-8")
        import yaml
        clean = {"path": cfg["path"], "train": cfg["train"], "val": cfg["val"],
                 "test": str((out / "test_clean.txt").resolve()),
                 "names": {i: n for i, n in enumerate(cfg["names"])}}
        with open(out / "data_clean.yaml", "w", encoding="utf-8") as f:
            yaml.safe_dump(clean, f, allow_unicode=True, sort_keys=False)
        summary["test_sach"] = len(keep)
        print(f"\nTập test sạch: {len(keep)} / {len(files['test'])} ảnh -> {out / 'test_clean.txt'}")

    (out / "tong_ket.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"\nKết quả: {out.resolve()}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
