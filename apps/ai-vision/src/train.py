"""Huấn luyện YOLOv11n phát hiện khói / lửa trên bộ D-Fire.

Hai cấu hình nhóm đã chạy:

  best     Lần chạy fire_detection_project/yolo11n_100_epochs, tạo ra best.pt đang chạy trên Pi.
           Tham số lấy đúng từ args.yaml của lần chạy (results/yolo11n_100_epochs/args.yaml):
           yolo11n.pt, 100 epoch, ảnh 320, batch 16, optimizer auto, seed 0.
  bai_bao  Lần chạy fire_detection_paper_replication_true/yolov11n_paper_config, theo Bảng 5
           của bài báo Alkhammash (Fire 2025, 8(1), 26): ảnh 640, SGD, momentum 0.8.
           Cùng tham số với src/train_paper_config.py (bản gốc train.py của nhóm).

Ví dụ:
    python src/train.py                              # lặp lại lần huấn luyện của best.pt
    python src/train.py --cau-hinh bai_bao           # cấu hình theo bài báo
    python src/train.py --device cpu --epochs 1 --fraction 0.02   # chạy thử nhanh
"""
import argparse
import sys

from common import load_data_yaml, write_resolved_yaml

CAU_HINH = {
    "best": dict(
        epochs=100, imgsz=320, batch=16, optimizer="auto",
        lr0=0.01, lrf=0.01, momentum=0.937, weight_decay=0.0005,
        warmup_epochs=3.0, warmup_bias_lr=0.1, amp=True, seed=0, deterministic=True,
        project="fire_detection_project", name="yolo11n_100_epochs",
    ),
    "bai_bao": dict(
        epochs=100, imgsz=640, batch=16, optimizer="SGD",
        lr0=0.01, momentum=0.8,
        weight_decay=0.005,   # giữ như train.py của nhóm; Bảng 5 bài báo ghi 0.0005
        warmup_epochs=3.0, warmup_bias_lr=0.1, amp=True,
        project="fire_detection_paper_replication_true", name="yolov11n_paper_config",
    ),
}


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--cau-hinh", choices=sorted(CAU_HINH), default="best")
    ap.add_argument("--data", default="config/data.yaml")
    ap.add_argument("--device", default="0", help="0 = GPU đầu tiên, cpu, mps")
    ap.add_argument("--workers", type=int, default=2)
    ap.add_argument("--epochs", type=int, help="ghi đè số epoch của cấu hình")
    ap.add_argument("--fraction", type=float, default=1.0, help="chỉ dùng một phần tập train (chạy thử)")
    args = ap.parse_args()

    from ultralytics import YOLO

    tham_so = dict(CAU_HINH[args.cau_hinh])
    if args.epochs:
        tham_so["epochs"] = args.epochs
    tham_so["project"] = f"runs/detect/{tham_so['project']}"

    # data.yaml có `path` tương đối -> đổi sang đường dẫn tuyệt đối trước khi đưa cho Ultralytics
    data_yaml = write_resolved_yaml(load_data_yaml(args.data), tham_so["project"])

    model = YOLO("yolo11n.pt")
    model.train(data=str(data_yaml), device=args.device, workers=args.workers,
                fraction=args.fraction, **tham_so)
    print("Trọng số tốt nhất:", model.trainer.best)
    return 0


if __name__ == "__main__":
    sys.exit(main())
