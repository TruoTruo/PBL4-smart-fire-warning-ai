"""IoU, ghép khung, ma trận nhầm lẫn và AP/mAP cho bài toán phát hiện đối tượng.

Quy ước ma trận nhầm lẫn giống Ultralytics: hàng = lớp dự đoán, cột = lớp thật,
chỉ số cuối là nền (background).
  - nhãn thật không được ghép (bỏ sót)   -> hàng background
  - dự đoán không được ghép (báo nhầm)   -> cột background
"""
import numpy as np

IOU_THRESHOLDS = np.linspace(0.5, 0.95, 10)  # T = 10 ngưỡng cho mAP50-95

_trapezoid = getattr(np, "trapezoid", None) or np.trapz


def yolo_to_xyxy(boxes, w, h):
    """Nhãn YOLO (cx, cy, bw, bh chuẩn hóa) -> toạ độ pixel x1, y1, x2, y2."""
    b = np.asarray(boxes, dtype=np.float64).reshape(-1, 4)
    out = np.empty_like(b)
    out[:, 0] = (b[:, 0] - b[:, 2] / 2) * w
    out[:, 1] = (b[:, 1] - b[:, 3] / 2) * h
    out[:, 2] = (b[:, 0] + b[:, 2] / 2) * w
    out[:, 3] = (b[:, 1] + b[:, 3] / 2) * h
    return out


def iou_matrix(gt, pred):
    """Ma trận IoU kích thước G x D, tính vector hóa (broadcasting) – O(G·D)."""
    gt = np.asarray(gt, dtype=np.float64).reshape(-1, 4)
    pred = np.asarray(pred, dtype=np.float64).reshape(-1, 4)
    if len(gt) == 0 or len(pred) == 0:
        return np.zeros((len(gt), len(pred)))
    x1 = np.maximum(gt[:, None, 0], pred[None, :, 0])
    y1 = np.maximum(gt[:, None, 1], pred[None, :, 1])
    x2 = np.minimum(gt[:, None, 2], pred[None, :, 2])
    y2 = np.minimum(gt[:, None, 3], pred[None, :, 3])
    inter = np.clip(x2 - x1, 0, None) * np.clip(y2 - y1, 0, None)
    area_g = (gt[:, 2] - gt[:, 0]) * (gt[:, 3] - gt[:, 1])
    area_p = (pred[:, 2] - pred[:, 0]) * (pred[:, 3] - pred[:, 1])
    union = area_g[:, None] + area_p[None, :] - inter
    return np.divide(inter, union, out=np.zeros_like(inter), where=union > 0)


def greedy_match(iou, thr):
    """Ghép tham lam: các cặp IoU >= thr xét theo IoU giảm dần, nhận cặp khi cả hai
    khung chưa được ghép. O(G·D·log(G·D)) do bước sắp xếp. Trả về [(g, p), ...]."""
    gi, pi = np.nonzero(iou >= thr)
    if len(gi) == 0:
        return []
    order = np.argsort(-iou[gi, pi], kind="stable")
    used_g, used_p, pairs = set(), set(), []
    for k in order:
        g, p = int(gi[k]), int(pi[k])
        if g in used_g or p in used_p:
            continue
        used_g.add(g)
        used_p.add(p)
        pairs.append((g, p))
    return pairs


def update_confusion(cm, gt_cls, gt_boxes, pred_cls, pred_boxes, iou_thr=0.5):
    """Cộng kết quả một ảnh vào ma trận (K+1) x (K+1). Ghép không phân biệt lớp,
    sau đó so lớp dự đoán với lớp thật."""
    bg = cm.shape[0] - 1
    gt_cls = np.asarray(gt_cls, dtype=int)
    pred_cls = np.asarray(pred_cls, dtype=int)
    pairs = greedy_match(iou_matrix(gt_boxes, pred_boxes), iou_thr)
    matched_g = {g for g, _ in pairs}
    matched_p = {p for _, p in pairs}
    for g, p in pairs:
        cm[pred_cls[p], gt_cls[g]] += 1
    for g in range(len(gt_cls)):
        if g not in matched_g:
            cm[bg, gt_cls[g]] += 1
    for p in range(len(pred_cls)):
        if p not in matched_p:
            cm[pred_cls[p], bg] += 1
    return cm


def match_tp(gt_cls, gt_boxes, pred_cls, pred_boxes, thresholds=IOU_THRESHOLDS):
    """Mảng bool D x T: dự đoán p có là TP ở ngưỡng IoU thứ j không (chỉ ghép cùng lớp)."""
    tp = np.zeros((len(pred_cls), len(thresholds)), dtype=bool)
    if len(gt_cls) == 0 or len(pred_cls) == 0:
        return tp
    same = np.asarray(gt_cls)[:, None] == np.asarray(pred_cls)[None, :]
    iou = iou_matrix(gt_boxes, pred_boxes) * same
    for j, t in enumerate(thresholds):
        for _, p in greedy_match(iou, t):
            tp[p, j] = True
    return tp


def pr_curve(tp, conf, n_gt):
    """Đường Precision – Recall khi hạ dần ngưỡng tin cậy (tp: mảng bool 1 chiều)."""
    order = np.argsort(-np.asarray(conf), kind="stable")
    tp = np.asarray(tp, dtype=np.float64)[order]
    tpc = np.cumsum(tp)
    fpc = np.cumsum(1.0 - tp)
    recall = tpc / max(n_gt, 1)
    precision = tpc / np.maximum(tpc + fpc, 1e-16)
    return np.asarray(conf)[order], precision, recall


def average_precision(precision, recall):
    """AP = diện tích dưới đường PR đã làm đơn điệu, nội suy 101 điểm (chuẩn COCO)."""
    mrec = np.concatenate(([0.0], recall, [1.0]))
    mpre = np.concatenate(([1.0], precision, [0.0]))
    mpre = np.flip(np.maximum.accumulate(np.flip(mpre)))
    x = np.linspace(0, 1, 101)
    return float(_trapezoid(np.interp(x, mrec, mpre), x))


def _smooth(y, frac=0.1):
    nf = round(len(y) * frac * 2) // 2 + 1
    pad = np.ones(nf // 2)
    yp = np.concatenate((pad * y[0], y, pad * y[-1]))
    return np.convolve(yp, np.ones(nf) / nf, mode="valid")


def summarize(tp, conf, pred_cls, n_gt_per_class):
    """Tính P, R (tại ngưỡng tin cậy cho F1 trung bình lớn nhất), AP50, AP50-95 từng lớp.

    tp: D x T (toàn bộ dự đoán của tập), conf: D, pred_cls: D, n_gt_per_class: K.
    """
    k = len(n_gt_per_class)
    t = tp.shape[1]
    ap = np.zeros((k, t))
    grid = np.linspace(0, 1, 1000)
    p_grid = np.zeros((k, len(grid)))
    r_grid = np.zeros((k, len(grid)))
    for c in range(k):
        sel = pred_cls == c
        n_gt = int(n_gt_per_class[c])
        if n_gt == 0 or not sel.any():
            continue
        for j in range(t):
            c_sorted, prec, rec = pr_curve(tp[sel, j], conf[sel], n_gt)
            ap[c, j] = average_precision(prec, rec)
            if j == 0:  # P/R báo cáo ở IoU 0.5, giống Ultralytics
                r_grid[c] = np.interp(-grid, -c_sorted, rec, left=0)
                p_grid[c] = np.interp(-grid, -c_sorted, prec, left=1)
    f1 = 2 * p_grid * r_grid / np.maximum(p_grid + r_grid, 1e-16)
    best = int(_smooth(f1.mean(0)).argmax())
    return {
        "precision": p_grid[:, best],
        "recall": r_grid[:, best],
        "conf_at_best_f1": float(grid[best]),
        "ap50": ap[:, 0],
        "ap50_95": ap.mean(1),
    }


def precision_recall_from_confusion(cm):
    """P, R, F1 từng lớp đọc từ ma trận nhầm lẫn ở một ngưỡng tin cậy cố định."""
    k = cm.shape[0] - 1
    out = []
    for c in range(k):
        tp = cm[c, c]
        fp = cm[c, :].sum() - tp
        fn = cm[:, c].sum() - tp
        p = tp / (tp + fp) if tp + fp else 0.0
        r = tp / (tp + fn) if tp + fn else 0.0
        f1 = 2 * p * r / (p + r) if p + r else 0.0
        out.append({"tp": int(tp), "fp": int(fp), "fn": int(fn), "precision": p, "recall": r, "f1": f1})
    return out
