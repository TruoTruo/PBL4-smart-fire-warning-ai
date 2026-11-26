"""In cấu hình huấn luyện và kết quả đánh giá được lưu sẵn trong file trọng số.

Dùng để đối chiếu số liệu Bảng 4.3, 4.4 của báo cáo với chính file best.pt.
Chỉ dùng với file trọng số của nhóm (torch.load cần weights_only=False).

    python src/model_info.py models/best.pt
"""
import json
import sys

KEYS = ["model", "data", "epochs", "batch", "imgsz", "optimizer", "lr0", "lrf", "momentum",
        "weight_decay", "warmup_epochs", "patience", "seed", "project", "name", "device"]


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else "models/best.pt"
    import torch

    ckpt = torch.load(path, map_location="cpu", weights_only=False)
    args = ckpt.get("train_args") or {}
    model = ckpt.get("model") or ckpt.get("ema")
    names = getattr(model, "names", None)
    print(f"File: {path}")
    print(f"Ngày lưu: {ckpt.get('date')}  |  Ultralytics {ckpt.get('version')}  |  epoch {ckpt.get('epoch')}")
    print(f"Lớp: {names}")
    print("\nCấu hình huấn luyện:")
    for k in KEYS:
        if k in args:
            print(f"  {k:<14} {args[k]}")
    metrics = ckpt.get("train_metrics") or {}
    print("\nKết quả trên tập val lúc huấn luyện:")
    for k, v in metrics.items():
        if k.startswith("metrics/"):
            print(f"  {k:<24} {v * 100:.2f} %")
    print(json.dumps({k: v for k, v in metrics.items()}, default=float, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
