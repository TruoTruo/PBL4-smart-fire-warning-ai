# AI & Thị giác máy tính
Phụ trách: 

## Nhiệm vụ
- Thu thập & gán nhãn dataset (Roboflow Fire & Smoke + negative dataset tự thu thập)
- Huấn luyện YOLOv8, export .pt / .onnx tối ưu cho ARM (Raspberry Pi 5)
- Xử lý nhiễu quang học (đèn LED, phản quang, áo cam/đỏ)

## Cách chạy
```bash
cd apps/ai-vision
pip install -r requirements.txt
python src/train.py        # huấn luyện
python src/detect.py        # inference realtime
```

## Cấu trúc
- `datasets/` — ảnh + nhãn (không commit ảnh thật, xem .gitignore)
- `models/` — weights .pt/.onnx (không commit, quá nặng)
- `src/` — code train/detect/export
- `tests/` — unit test cho src/
- `config/` — file cấu hình (copy từ config.example.yaml)
