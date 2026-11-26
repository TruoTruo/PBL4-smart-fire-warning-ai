"""Vẽ lưới ảnh đối chiếu: hàng trên là nhãn thật, hàng dưới là dự đoán của mô hình.

    python src/visualize.py --model models/best.pt --split test --n 8 --conf 0.7
"""
import argparse
import random
import sys
from pathlib import Path

from PIL import Image, ImageDraw

from common import list_images, load_data_yaml, read_label_file, label_path_for, split_image_dir
from metrics import yolo_to_xyxy

COLORS = {"fire": (255, 64, 32), "smoke": (90, 160, 255)}


def draw(img, boxes, classes, names, scores=None):
    img = img.copy()
    d = ImageDraw.Draw(img)
    for i, (b, c) in enumerate(zip(boxes, classes)):
        name = names[int(c)]
        color = COLORS.get(name, (255, 255, 0))
        d.rectangle([float(v) for v in b], outline=color, width=max(2, img.width // 200))
        text = name if scores is None else f"{name} {scores[i]:.2f}"
        d.text((float(b[0]) + 3, float(b[1]) + 2), text, fill=color)
    return img


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--model", default="models/best.pt")
    ap.add_argument("--data", default="config/data.yaml")
    ap.add_argument("--split", default="test")
    ap.add_argument("--n", type=int, default=8)
    ap.add_argument("--conf", type=float, default=0.7, help="0.7 = ngưỡng Pi đang dùng")
    ap.add_argument("--imgsz", type=int, default=416)
    ap.add_argument("--size", type=int, default=320, help="cạnh mỗi ô trong lưới")
    ap.add_argument("--seed", type=int, default=0)
    ap.add_argument("--out", default="runs/visualize/doi_chieu.jpg")
    args = ap.parse_args()

    from ultralytics import YOLO

    cfg = load_data_yaml(args.data)
    names = cfg["names"]
    images = list_images(split_image_dir(cfg, args.split))
    random.Random(args.seed).shuffle(images)
    images = images[:args.n]
    results = YOLO(args.model).predict([str(p) for p in images], imgsz=args.imgsz, conf=args.conf, verbose=False)

    s = args.size
    grid = Image.new("RGB", (s * len(images), s * 2), "white")
    for i, (path, r) in enumerate(zip(images, results)):
        img = Image.open(path).convert("RGB")
        lab = read_label_file(label_path_for(path))
        gt = draw(img, yolo_to_xyxy(lab[:, 1:], img.width, img.height), lab[:, 0], names)
        pr = draw(img, r.boxes.xyxy.cpu().numpy(), r.boxes.cls.cpu().numpy(), names,
                  r.boxes.conf.cpu().numpy())
        grid.paste(gt.resize((s, s)), (i * s, 0))
        grid.paste(pr.resize((s, s)), (i * s, s))
    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    grid.save(out, quality=92)
    print(f"Đã lưu {out.resolve()}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
