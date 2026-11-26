"""Đọc cấu hình dataset (data.yaml) và nạp nhãn YOLO dùng chung cho các script."""
from pathlib import Path

import numpy as np
import yaml

IMG_EXTS = {".jpg", ".jpeg", ".png", ".bmp", ".webp"}


def load_data_yaml(yaml_path):
    """Đọc data.yaml. `path` tương đối được tính theo thư mục chứa file yaml."""
    yaml_path = Path(yaml_path).resolve()
    with open(yaml_path, encoding="utf-8") as f:
        cfg = yaml.safe_load(f)
    root = Path(cfg.get("path") or ".")
    if not root.is_absolute():
        root = (yaml_path.parent / root).resolve()
    cfg["path"] = str(root)
    names = cfg["names"]
    if isinstance(names, dict):
        names = [names[k] for k in sorted(names)]
    cfg["names"] = list(names)
    return cfg


def write_resolved_yaml(cfg, out_dir):
    """Ghi bản data.yaml với đường dẫn tuyệt đối để đưa thẳng cho Ultralytics."""
    out_dir = Path(out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)
    out = out_dir / "data_resolved.yaml"
    keep = {k: cfg[k] for k in ("path", "train", "val", "test", "names") if k in cfg}
    keep["names"] = {i: n for i, n in enumerate(cfg["names"])}
    with open(out, "w", encoding="utf-8") as f:
        yaml.safe_dump(keep, f, allow_unicode=True, sort_keys=False)
    return out


def split_image_dir(cfg, split):
    """Thư mục ảnh của một tập (train / val / test)."""
    rel = cfg.get(split)
    if not rel:
        raise SystemExit(f"data.yaml không có khóa '{split}'")
    return Path(cfg["path"]) / rel


def list_images(img_dir):
    """Ảnh của một tập: thư mục ảnh, hoặc file .txt mỗi dòng một đường dẫn ảnh
    (như test_clean.txt do check_leakage.py ghi ra). Dòng tương đối tính theo thư mục chứa file."""
    img_dir = Path(img_dir)
    if img_dir.is_file() and img_dir.suffix.lower() == ".txt":
        paths = []
        for line in img_dir.read_text(encoding="utf-8").splitlines():
            line = line.strip()
            if line:
                p = Path(line)
                paths.append(p if p.is_absolute() else (img_dir.parent / p).resolve())
        return sorted(p for p in paths if p.suffix.lower() in IMG_EXTS)
    if not img_dir.is_dir():
        raise SystemExit(f"Không thấy thư mục ảnh: {img_dir}")
    return sorted(p for p in img_dir.rglob("*") if p.suffix.lower() in IMG_EXTS)


def label_path_for(img_path):
    """Quy ước YOLO: .../images/abc.jpg -> .../labels/abc.txt"""
    parts = list(Path(img_path).parts)
    for i in range(len(parts) - 1, -1, -1):
        if parts[i] == "images":
            parts[i] = "labels"
            break
    return Path(*parts).with_suffix(".txt")


def read_label_file(path):
    """Mảng N x 5: lớp, cx, cy, w, h (chuẩn hóa 0..1). File không có / rỗng = ảnh nền."""
    path = Path(path)
    if not path.is_file():
        return np.zeros((0, 5))
    rows = []
    for line in path.read_text(encoding="utf-8").splitlines():
        v = line.split()
        if len(v) >= 5:
            rows.append([float(x) for x in v[:5]])
    return np.array(rows, dtype=np.float64).reshape(-1, 5)


def load_labels(images):
    """Bảng băm: tên ảnh -> mảng nhãn. Tra nhãn của một ảnh mất O(1) trung bình."""
    return {p.stem: read_label_file(label_path_for(p)) for p in images}
