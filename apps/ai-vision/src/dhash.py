"""Dấu vân tay ảnh dHash 64 bit và khoảng cách Hamming (XOR + bảng tra số bit 1)."""
import numpy as np
from PIL import Image, ImageOps

# Bảng tra số bit 1 của mọi giá trị 1 byte
POPCOUNT_LUT = np.array([bin(i).count("1") for i in range(256)], dtype=np.uint8)


def dhash_image(img):
    """Thu nhỏ về 9 x 8 mức xám, so sánh từng cặp điểm ảnh liền kề theo hàng -> 64 bit."""
    small = np.asarray(img.convert("L").resize((9, 8), Image.BILINEAR), dtype=np.int16)
    bits = (small[:, 1:] > small[:, :-1]).flatten()
    value = 0
    for b in bits:
        value = (value << 1) | int(b)
    return value


def dhash_file(path):
    """Trả về (mã ảnh gốc, mã ảnh lật ngang) – V = 2 biến thể."""
    with Image.open(path) as img:
        img.load()
        return dhash_image(img), dhash_image(ImageOps.mirror(img))


def hamming(a, b):
    return bin(int(a) ^ int(b)).count("1")


def hamming_matrix(a, b):
    """Khoảng cách Hamming giữa mọi cặp mã: mảng len(a) x len(b)."""
    a = np.asarray(a, dtype=np.uint64)
    b = np.asarray(b, dtype=np.uint64)
    x = np.bitwise_xor(a[:, None], b[None, :])
    return POPCOUNT_LUT[x.view(np.uint8).reshape(len(a), len(b), 8)].sum(axis=2, dtype=np.int32)


def near_duplicates(query, query_flip, ref, threshold=5, chunk=512):
    """Với mỗi ảnh trong `query`, tìm ảnh trong `ref` có Hamming <= threshold
    (so cả bản gốc lẫn bản lật). Trả về [(i_query, j_ref, khoảng cách, có_lật), ...].
    Tổng chi phí O(V · Na · Nb) phép XOR, chia khối để giới hạn bộ nhớ."""
    query = np.asarray(query, dtype=np.uint64)
    query_flip = np.asarray(query_flip, dtype=np.uint64)
    ref = np.asarray(ref, dtype=np.uint64)
    hits = []
    for s in range(0, len(query), chunk):
        d0 = hamming_matrix(query[s:s + chunk], ref)
        d1 = hamming_matrix(query_flip[s:s + chunk], ref)
        flipped = d1 < d0
        d = np.where(flipped, d1, d0)
        qi, rj = np.nonzero(d <= threshold)
        for i, j in zip(qi, rj):
            hits.append((s + int(i), int(j), int(d[i, j]), bool(flipped[i, j])))
    return hits
