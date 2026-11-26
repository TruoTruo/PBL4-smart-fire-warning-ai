"""Bộ đánh giá mô hình trên một tập (mặc định tập test độc lập).

- Nạp nhãn thật vào bảng băm tên ảnh -> danh sách khung.
- Suy luận YOLO (ngưỡng tin cậy rất thấp để dựng được đường Precision – Recall).
- Ma trận IoU G x D (NumPy), ghép tham lam, AP từng lớp ở 10 ngưỡng IoU 0.50 – 0.95.
- Ma trận nhầm lẫn ở các ngưỡng tin cậy chọn trước (0.25 như Ultralytics, 0.7 như Pi đang chạy).

Ví dụ:
    python src/evaluate.py --model models/best.pt --split test --imgsz 416
    python src/evaluate.py --model models/best.pt --split val --imgsz 320 --compare
"""
import argparse
import json
import sys
import time
from pathlib import Path

import numpy as np

from common import list_images, load_data_yaml, load_labels, split_image_dir, write_resolved_yaml
from metrics import (IOU_THRESHOLDS, match_tp, precision_recall_from_confusion, summarize,
                     update_confusion, yolo_to_xyxy)


def save_confusion_png(cm, names, path, title):
    try:
        import matplotlib
        matplotlib.use("Agg")
        import matplotlib.pyplot as plt
    except ImportError:
        return
    labels = list(names) + ["background"]
    col = cm.sum(0, keepdims=True)
    norm = np.divide(cm, col, out=np.zeros_like(cm, dtype=float), where=col > 0)
    fig, ax = plt.subplots(figsize=(6, 5))
    im = ax.imshow(norm, cmap="Blues", vmin=0, vmax=1)
    ax.set_xticks(range(len(labels)), labels)
    ax.set_yticks(range(len(labels)), labels)
    ax.set_xlabel("Thật")
    ax.set_ylabel("Dự đoán")
    for i in range(len(labels)):
        for j in range(len(labels)):
            if cm[i, j]:
                ax.text(j, i, f"{norm[i, j]:.2f}\n({int(cm[i, j])})", ha="center", va="center",
                        color="white" if norm[i, j] > 0.5 else "black", fontsize=8)
    ax.set_title(title)
    fig.colorbar(im)
    fig.tight_layout()
    fig.savefig(path, dpi=150)
    plt.close(fig)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--model", default="models/best.pt")
    ap.add_argument("--data", default="config/data.yaml")
    ap.add_argument("--split", default="test")
    ap.add_argument("--imgsz", type=int, default=416, help="416 = cỡ ảnh Pi đang chạy")
    ap.add_argument("--batch", type=int, default=16)
    ap.add_argument("--device", default=None, help="cpu, 0, mps ... (mặc định để Ultralytics tự chọn)")
    ap.add_argument("--conf", type=float, nargs="+", default=[0.25, 0.7],
                    help="các ngưỡng tin cậy để dựng ma trận nhầm lẫn")
    ap.add_argument("--limit", type=int, default=0, help="chỉ lấy N ảnh đầu (chạy thử)")
    ap.add_argument("--out", default="runs/eval")
    ap.add_argument("--compare", action="store_true", help="chạy thêm model.val() của Ultralytics để đối chiếu")
    args = ap.parse_args()

    from ultralytics import YOLO

    cfg = load_data_yaml(args.data)
    names = cfg["names"]
    k = len(names)
    images = list_images(split_image_dir(cfg, args.split))
    if args.limit:
        images = images[:args.limit]
    labels = load_labels(images)
    out = Path(args.out) / f"{args.split}_imgsz{args.imgsz}"
    out.mkdir(parents=True, exist_ok=True)

    model = YOLO(args.model)
    n_gt = np.zeros(k, dtype=int)
    all_tp, all_conf, all_cls = [], [], []
    cms = {c: np.zeros((k + 1, k + 1), dtype=np.int64) for c in args.conf}

    t0 = time.time()
    print(f"Đánh giá {len(images)} ảnh tập {args.split}, imgsz={args.imgsz}")
    for i in range(0, len(images), args.batch):
        chunk = images[i:i + args.batch]
        results = model.predict([str(p) for p in chunk], imgsz=args.imgsz, conf=0.001, iou=0.7,
                                max_det=300, device=args.device, verbose=False)
        for path, r in zip(chunk, results):
            h, w = r.orig_shape
            lab = labels[path.stem]
            g_cls = lab[:, 0].astype(int)
            g_box = yolo_to_xyxy(lab[:, 1:], w, h)
            p_box = r.boxes.xyxy.cpu().numpy()
            p_conf = r.boxes.conf.cpu().numpy()
            p_cls = r.boxes.cls.cpu().numpy().astype(int)
            n_gt += np.bincount(g_cls, minlength=k)[:k]
            all_tp.append(match_tp(g_cls, g_box, p_cls, p_box))
            all_conf.append(p_conf)
            all_cls.append(p_cls)
            for c, cm in cms.items():
                keep = p_conf >= c
                update_confusion(cm, g_cls, g_box, p_cls[keep], p_box[keep])
        done = min(i + args.batch, len(images))
        if done % (args.batch * 20) == 0 or done == len(images):
            print(f"  {done}/{len(images)} ảnh, {time.time() - t0:.0f} s")

    tp = np.concatenate(all_tp) if all_tp else np.zeros((0, len(IOU_THRESHOLDS)), bool)
    conf = np.concatenate(all_conf) if all_conf else np.zeros(0)
    pcls = np.concatenate(all_cls) if all_cls else np.zeros(0, int)
    s = summarize(tp, conf, pcls, n_gt)

    print(f"\n{'Lớp':<10}{'Số nhãn':>9}{'P':>8}{'R':>8}{'mAP50':>8}{'mAP50-95':>10}")
    for c in range(k):
        print(f"{names[c]:<10}{n_gt[c]:>9}{s['precision'][c]:>8.3f}{s['recall'][c]:>8.3f}"
              f"{s['ap50'][c]:>8.3f}{s['ap50_95'][c]:>10.3f}")
    overall = {
        "precision": float(s["precision"].mean()),
        "recall": float(s["recall"].mean()),
        "map50": float(s["ap50"].mean()),
        "map50_95": float(s["ap50_95"].mean()),
    }
    print(f"{'tất cả':<10}{n_gt.sum():>9}{overall['precision']:>8.3f}{overall['recall']:>8.3f}"
          f"{overall['map50']:>8.3f}{overall['map50_95']:>10.3f}")
    print(f"(P, R lấy tại ngưỡng tin cậy {s['conf_at_best_f1']:.3f} cho F1 trung bình lớn nhất)")

    report = {
        "model": str(args.model), "split": args.split, "imgsz": args.imgsz, "so_anh": len(images),
        "so_nhan": {names[c]: int(n_gt[c]) for c in range(k)},
        "tong": overall,
        "tung_lop": {names[c]: {"precision": float(s["precision"][c]), "recall": float(s["recall"][c]),
                                "ap50": float(s["ap50"][c]), "ap50_95": float(s["ap50_95"][c])}
                     for c in range(k)},
        "ma_tran_nham_lan": {},
    }
    for c, cm in cms.items():
        tag = f"conf{c:g}"
        np.savetxt(out / f"confusion_{tag}.csv", cm, fmt="%d", delimiter=",",
                   header=",".join(names + ["background"]) + "  (hàng = dự đoán, cột = thật)")
        save_confusion_png(cm, names, out / f"confusion_{tag}.png", f"Ma trận nhầm lẫn, conf ≥ {c:g}")
        per = precision_recall_from_confusion(cm)
        report["ma_tran_nham_lan"][tag] = {"ma_tran": cm.tolist(),
                                           "tung_lop": {names[i]: per[i] for i in range(k)}}
        print(f"\nconf ≥ {c:g}: " + "; ".join(
            f"{names[i]} P={per[i]['precision']:.3f} R={per[i]['recall']:.3f} F1={per[i]['f1']:.3f}" for i in range(k)))

    if args.compare:
        data_yaml = write_resolved_yaml(cfg, out)
        m = model.val(data=str(data_yaml), split=args.split, imgsz=args.imgsz, batch=args.batch,
                      device=args.device, plots=False, verbose=False)
        report["ultralytics"] = {"precision": float(m.box.mp), "recall": float(m.box.mr),
                                 "map50": float(m.box.map50), "map50_95": float(m.box.map)}
        print("\nĐối chiếu Ultralytics model.val():", json.dumps(report["ultralytics"]))

    (out / "metrics.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"\nKết quả: {out.resolve()}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
