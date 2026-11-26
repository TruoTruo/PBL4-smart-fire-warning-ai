# AI – Nhận diện lửa và khói (YOLOv11n)

Phần này gồm mô hình đang chạy trên Raspberry Pi 5 và bộ công cụ huấn luyện, đánh giá, kiểm tra dữ liệu.
Mọi lệnh bên dưới chạy trong thư mục `apps/ai-vision`.

## 1. Mô hình đang dùng: `best.pt`

File trọng số không đưa lên Git (5,2 MB, xem `.gitignore`). Tải từ thư mục Drive của nhóm
`training/Cấu hình code train AI/best.pt`, rồi chép vào:

- `apps/ai-vision/models/best.pt` để chạy các script đánh giá;
- cạnh `apps/iot-edge/main_iot.py` trên Pi để chạy hệ thống.

Thông tin đọc trực tiếp từ file (`python src/model_info.py models/best.pt`):

| Thông số | Giá trị |
|---|---|
| Kiến trúc | YOLOv11n (`yolo11n.yaml`), khởi tạo từ trọng số COCO `yolo11n.pt` |
| Lớp | `0 = smoke` (khói), `1 = fire` (lửa) |
| Huấn luyện | 100 epoch, ảnh 320 × 320, batch 16, optimizer `auto` (lr0 0,01, momentum 0,937, weight decay 0,0005) |
| Thư viện | Ultralytics 8.4.143, huấn luyện ngày 16/10/2026 trên máy Windows của nhóm (GPU, khoảng 4,5 giờ) |
| Kết quả tập val (ảnh 320, epoch tốt nhất 99) | Precision 75,76 % · Recall 64,27 % · mAP50 71,75 % · mAP50-95 41,02 % |

Kết quả gốc của lần huấn luyện này (do Ultralytics ghi ra) nằm trong `results/yolo11n_100_epochs/`:
`args.yaml` (toàn bộ tham số), `results.csv` (số liệu từng epoch), `results.png`, ma trận nhầm lẫn, các đường
P, R, F1, PR và ảnh dự đoán mẫu trên tập val.

Trên Pi, mô hình chạy ở ảnh 416 × 416 (`main_iot.py`). Muốn có số đo đúng cỡ ảnh đang chạy, dùng
`src/evaluate.py --imgsz 416` (mục 4).

## 2. Dữ liệu: bộ D-Fire

| | |
|---|---|
| Nguồn | D-Fire, bản đã chia train/val/test trên Kaggle: <https://www.kaggle.com/datasets/sayedgamal99/smoke-fire-detection-yolo> |
| Giấy phép | CC0 (dùng tự do) |
| Số ảnh | 21.527 = 14.122 train / 3.099 val / 4.306 test |
| Thành phần | 1.164 ảnh chỉ có lửa, 5.867 chỉ có khói, 4.658 có cả hai, 9.838 ảnh nền (không có lửa/khói) |
| Số khung bao | 14.692 lửa, 11.865 khói (cả bộ) |
| Trích dẫn | P. V. A. B. de Venâncio, A. C. Lisboa, A. V. Barbosa, "An automatic fire detection system based on deep convolutional neural networks for low-power, resource-constrained devices", *Neural Computing and Applications*, 34(18), 15349–15368, 2022. DOI 10.1007/s00521-022-07467-z |

Tải bộ dữ liệu rồi giải nén vào `datasets/d-fire/` sao cho có `datasets/d-fire/data/train/images`,
`.../labels`, tương tự cho `val`, `test`. Đường dẫn khai báo trong `config/data.yaml`.

### So sánh với bài báo

E. H. Alkhammash, "A Comparative Analysis of YOLOv9, YOLOv10, YOLOv11 for Smoke and Fire Detection",
*Fire*, 8(1), 26, 2025. DOI 10.3390/fire8010026 – <https://www.mdpi.com/2571-6255/8/1/26>.

Bài báo dùng hai bộ dữ liệu. Chỉ so với kết quả trên **D-Fire** (Bảng 7 của bài báo), vì `best.pt` huấn luyện
trên D-Fire:

| 100 epoch | Precision | Recall | mAP50 | mAP50-95 |
|---|:-:|:-:|:-:|:-:|
| YOLOv11n – bài báo, D-Fire | 77,2 | 71,8 | 78,0 | 45,8 |
| YOLOv11n – nhóm (`best.pt`, val, ảnh 320) | 75,76 | 64,27 | 71,75 | 41,02 |

Khác biệt chính:
- Ảnh huấn luyện: nhóm dùng 320 để chạy kịp trên Pi 5; cấu hình huấn luyện của bài báo (Bảng 5) dùng 640.
- Cách chia tập val: bài báo dùng 1.723 ảnh, bản Kaggle 3.099 ảnh. Tập test thì giống nhau (4.306 ảnh).

Kết quả YOLOv11n 0,803 / 0,758 / 0,825 / 0,515 trong Bảng 4 của bài báo là trên bộ **Smoke and Fire** (ảnh
cháy rừng), không dùng để so với `best.pt`.

## 3. Cài đặt

```bash
cd apps/ai-vision
python -m venv .venv
source .venv/bin/activate        # Windows: .venv\Scripts\activate
pip install -r requirements.txt
```

## 4. Bộ công cụ

| Script | Chức năng | Lệnh ví dụ |
|---|---|---|
| `src/check_leakage.py` | Kiểm tra rò rỉ dữ liệu giữa train / val / test: trùng tên gốc (bảng băm) và gần trùng nội dung (dHash 64 bit, cả ảnh lật ngang, Hamming ≤ 5). Có thể ghi danh sách ảnh test đã loại ảnh trùng | `python src/check_leakage.py --write-clean` |
| `src/train.py` | Huấn luyện YOLOv11n. Cấu hình `best` (mặc định) lặp lại đúng lần chạy ra `best.pt` theo `results/yolo11n_100_epochs/args.yaml`; cấu hình `bai_bao` là lần chạy theo bài báo (ảnh 640, SGD) | `python src/train.py --cau-hinh best` |
| `src/train_paper_config.py` | Bản gốc `train.py` của nhóm (thư mục Drive `training/Cấu hình code train AI`), giữ nguyên từng ký tự. Đường dẫn `data` trỏ tới máy huấn luyện của nhóm, chạy ở máy khác thì sửa dòng này hoặc dùng `src/train.py --cau-hinh bai_bao` | `python src/train_paper_config.py` |
| `src/evaluate.py` | Bộ đánh giá: ma trận IoU (NumPy), ghép khung tham lam, AP ở 10 ngưỡng IoU, mAP50, mAP50-95, P, R; ma trận nhầm lẫn ở ngưỡng 0,25 và 0,7 (ngưỡng Pi dùng). `--compare` chạy thêm `model.val()` của Ultralytics để đối chiếu | `python src/evaluate.py --split test --imgsz 416 --compare` |
| `src/visualize.py` | Lưới ảnh đối chiếu: hàng trên nhãn thật, hàng dưới dự đoán | `python src/visualize.py --n 8` |
| `src/model_info.py` | In cấu hình huấn luyện và kết quả lưu trong file trọng số | `python src/model_info.py models/best.pt` |
| `src/metrics.py`, `src/dhash.py`, `src/common.py` | Các hàm dùng chung: IoU, ghép khung, AP, dHash, Hamming, đọc nhãn YOLO | |
| `tests/test_metrics.py` | Kiểm thử các hàm trên bằng dữ liệu tự tạo | `python -m pytest tests -q` |

Kết quả các script được ghi vào `runs/` (không đưa lên Git).

Đánh giá trên tập test đã làm sạch:

```bash
python src/check_leakage.py --write-clean
python src/evaluate.py --data runs/leakage/data_clean.yaml --split test --imgsz 416
```

## 5. Cấu trúc thư mục

```
ai-vision/
├── config/data.yaml      # khai báo dataset D-Fire, 2 lớp smoke/fire
├── results/              # kết quả gốc lần huấn luyện best.pt (args.yaml, results.csv, đồ thị)
├── datasets/             # dữ liệu (không commit)
├── models/               # best.pt (không commit)
├── src/                  # các script ở mục 4
├── tests/                # kiểm thử
└── runs/                 # kết quả chạy (không commit)
```

## 6. Hướng phát triển

- Xuất NCNN để tăng tốc trên Pi: `yolo export model=models/best.pt format=ncnn imgsz=416`, rồi trỏ `MODEL_PATH`
  trong `main_iot.py` tới thư mục `best_ncnn_model`.
- Huấn luyện lại ở ảnh 416 (hoặc cho Pi chạy ở 320) để cỡ ảnh lúc huấn luyện và lúc chạy giống nhau.
- Bổ sung ảnh tự chụp trong phòng, ban đêm và các vật dễ nhầm (đèn, áo đỏ/cam) vào tập huấn luyện.
