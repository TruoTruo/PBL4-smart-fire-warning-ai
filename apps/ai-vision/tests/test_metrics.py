"""Kiểm thử các hàm IoU, ghép khung, AP, ma trận nhầm lẫn và dHash bằng dữ liệu tự tạo.

    python -m pytest apps/ai-vision/tests -q
    python apps/ai-vision/tests/test_metrics.py
"""
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from dhash import dhash_image, hamming, hamming_matrix, near_duplicates  # noqa: E402
from metrics import (average_precision, greedy_match, iou_matrix, match_tp, pr_curve,  # noqa: E402
                     precision_recall_from_confusion, summarize, update_confusion, yolo_to_xyxy)


def test_iou_matrix():
    gt = [[0, 0, 10, 10], [20, 20, 30, 30]]
    pred = [[0, 0, 10, 10], [5, 0, 15, 10], [100, 100, 110, 110]]
    iou = iou_matrix(gt, pred)
    assert iou.shape == (2, 3)
    assert np.isclose(iou[0, 0], 1.0)
    assert np.isclose(iou[0, 1], 50 / 150)
    assert iou[0, 2] == 0 and iou[1].sum() == 0
    assert iou_matrix([], pred).shape == (0, 3)


def test_yolo_to_xyxy():
    b = yolo_to_xyxy([[0.5, 0.5, 0.2, 0.4]], 100, 50)
    assert np.allclose(b, [[40, 15, 60, 35]])


def test_greedy_match_takes_highest_iou_first():
    iou = np.array([[0.9, 0.6], [0.8, 0.0]])
    assert greedy_match(iou, 0.5) == [(0, 0)]  # g1 chỉ ghép được với p0, đã bị g0 lấy
    assert greedy_match(iou, 0.95) == []


def test_match_tp_needs_same_class():
    tp = match_tp([0], [[0, 0, 10, 10]], [1, 0], [[0, 0, 10, 10], [0, 0, 10, 10]])
    assert not tp[0].any() and tp[1].all()


def test_ap_perfect_and_half():
    # Bắt đủ, không báo nhầm: AP = 0.995 chứ không phải 1, vì điểm recall = 1 được nối về
    # precision 0 trước khi lấy tích phân 101 điểm (Ultralytics cũng cho đúng 0.995)
    _, p, r = pr_curve([True, True], [0.9, 0.8], 2)
    assert np.isclose(average_precision(p, r), 0.995, atol=1e-6)
    # bắt được 1/2 vật thể, không báo nhầm: đoạn recall 0.5 -> 1 được nội suy tuyến tính
    # từ precision 1 về 0 (giống Ultralytics) nên AP = 0.5 + 0.25
    _, p, r = pr_curve([True], [0.9], 2)
    assert np.isclose(average_precision(p, r), 0.75, atol=0.01)


def test_confusion_matrix():
    cm = np.zeros((3, 3), dtype=int)  # 2 lớp + nền
    update_confusion(cm, [1, 0], [[0, 0, 10, 10], [50, 50, 60, 60]],
                     [1, 1], [[0, 0, 10, 10], [200, 200, 210, 210]])
    # lửa đúng, khói bị bỏ sót, một box lửa báo nhầm
    assert cm[1, 1] == 1 and cm[2, 0] == 1 and cm[1, 2] == 1 and cm.sum() == 3
    per = precision_recall_from_confusion(cm)
    assert per[1]["precision"] == 0.5 and per[1]["recall"] == 1.0
    assert per[0]["recall"] == 0.0


def test_summarize_perfect_detector():
    tp = np.ones((4, 10), dtype=bool)
    s = summarize(tp, np.array([0.9, 0.8, 0.7, 0.6]), np.array([0, 0, 1, 1]), np.array([2, 2]))
    assert np.allclose(s["ap50"], 0.995, atol=1e-6) and np.allclose(s["ap50_95"], 0.995, atol=1e-6)
    assert np.allclose(s["recall"], 1.0) and np.allclose(s["precision"], 1.0)


def _scene(seed, size=(160, 120)):
    rng = np.random.default_rng(seed)
    img = Image.new("L", size, 0)
    d = ImageDraw.Draw(img)
    for _ in range(6):
        x, y = rng.integers(0, size[0] - 30), rng.integers(0, size[1] - 30)
        d.rectangle([x, y, x + rng.integers(10, 30), y + rng.integers(10, 30)], fill=int(rng.integers(60, 255)))
    return img


def test_dhash_duplicates_and_flip():
    a, b = _scene(1), _scene(2)
    ha, hb = dhash_image(a), dhash_image(b)
    assert hamming(ha, dhash_image(a.resize((320, 240)))) <= 5  # cùng ảnh, khác kích thước
    assert hamming(ha, hb) > 5
    flip = a.transpose(Image.FLIP_LEFT_RIGHT)
    hits = near_duplicates([dhash_image(flip)], [dhash_image(flip.transpose(Image.FLIP_LEFT_RIGHT))], [ha, hb])
    assert hits and hits[0][1] == 0 and hits[0][3] is True
    m = hamming_matrix([ha, hb], [ha, hb])
    assert m[0, 0] == 0 and m[0, 1] == hamming(ha, hb)


if __name__ == "__main__":
    tests = [v for k, v in sorted(globals().items()) if k.startswith("test_")]
    for t in tests:
        t()
        print("ĐẠT ", t.__name__)
    print(f"{len(tests)}/{len(tests)} kiểm thử đạt")
