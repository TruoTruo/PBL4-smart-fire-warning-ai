import time
import json
import base64
import logging
import os
import sys
import threading
from types import SimpleNamespace

import lgpio
import paho.mqtt.client as mqtt
import adafruit_dht
import board
import cv2
import torch
from flask import Flask, Response
from ultralytics import YOLO
from ultralytics.utils import LOGGER
from ultralytics.utils.plotting import Annotator, colors

LOGGER.setLevel('ERROR')

# ==========================================
# 0. CAU HINH CHUNG (chi can sua o day)
# ==========================================
# Ten mach Pi nay, PHAI co trong bang Devices (MySQL). Pi thu 2: DEVICE_ID=PI-02 python3 main_iot.py
DEVICE_ID = os.environ.get("DEVICE_ID", "PI-01")
STREAM_PORT = 5000       # xem thu: http://<IP-Pi>:5000/video_feed
STREAM_FPS = 15          # so khung gui di moi giay
JPEG_QUALITY = 70        # giam xuong 55-60 neu mang Wi-Fi yeu

# IP may chay Mosquitto. PHAI trung voi MqttBroker:Host trong appsettings.json cua
# mqtt-subscriber va web-app. Doi khong can sua code: MQTT_BROKER_HOST=... python3 main_iot.py
# ("localhost" neu chay Mosquitto ngay tren Pi 5)
BROKER = os.environ.get("MQTT_BROKER_HOST", "192.168.137.191")
PORT = int(os.environ.get("MQTT_BROKER_PORT", "1883"))
TOPIC = "pbl4/sensors/fire_warning"
STATUS_TOPIC = f"pbl4/status/{DEVICE_ID}"   # "online" / "offline" (retain), broker tu gui "offline" khi Pi mat ket noi

BUZZER_PIN = 22          # coi (muc 0 = bat, 1 = tat)
DHT_PIN = board.D17      # DHT11 (GPIO 17)
MQ2_PIN = 27             # MQ-2 (GPIO 27), muc 0 = co khoi

# --- Camera ---
CAM_INDEX = 0
CAM_WIDTH = 640
CAM_HEIGHT = 480
CAM_FPS = 30
CAMERA_TIMEOUT = 5       # giay khong co khung moi -> coi nhu mat camera (camera_ok = false), chuong trinh van chay

# --- YOLO ---
BASE_DIR = os.path.dirname(os.path.abspath(__file__))
MODEL_PATH = os.path.join(BASE_DIR, "best.pt")   # dat best.pt canh main_iot.py (hoac "best_ncnn_model" sau khi export NCNN)
YOLO_CONF = 0.7          # nguong tin cay toi thieu cua YOLO
YOLO_IMGSZ = 416         # kich thuoc anh dua vao YOLO (boi so cua 32)
YOLO_MAX_FPS = 8         # gioi han so lan YOLO chay moi giay de chua CPU cho camera/stream
YOLO_THREADS = 3         # so nhan CPU cho YOLO (Pi 5 co 4 nhan, chua 1 nhan cho camera + stream)
BOX_TTL = 1.0            # giay: khung vuong cu hon muc nay thi khong ve nua
AI_MOI_GIAY = 1.0        # giay: ket qua YOLO cu hon muc nay thi khong tinh "lua rat chac"
LOP_LUA = ("fire",)      # ten lop trong model.names (best.pt: 0 = smoke, 1 = fire)
LOP_KHOI = ("smoke",)

# --- Logic canh bao ---
NGUONG_NHIET_CAO = 45        # do C: tren muc nay tinh la "nhiet cao"
FRAMES_XAC_NHAN = 5          # so lan YOLO LIEN TIEP phai thay lua (hoac khoi) moi tinh la that
GIU_LUA_GIAY = 3             # giu co lua them bao nhieu giay
GIU_KHOI_CAMERA_GIAY = 3     # giu co khoi do camera thay them bao nhieu giay
GIU_KHOI_GIAY = 6            # giu co khoi MQ-2 them bao nhieu giay (MQ-2 nhay chap chon)
DHT_STALE_GIAY = 10          # so do DHT11 cu hon muc nay thi coi nhu khong co du lieu
SENSOR_INTERVAL = 2.5        # giay giua 2 lan doc DHT11 (DHT11 can >= 2 giay/lan)
DANH_GIA_INTERVAL = 0.5      # giay giua 2 lan danh gia muc canh bao + dieu khien coi
PUBLISH_INTERVAL = 2.5       # giay giua 2 lan gui MQTT (gui ngay khi muc tang)
SNAPSHOT_INTERVAL = 10       # giay: dang canh bao thi gui lai anh toi da 1 lan / khoang nay

# Duong "lua rat chac" (cho truong hop cam bien dat xa cho chay)
CHO_PHEP_LUA_DON_LE = True   # True: lua dung 1 minh van len muc 2 neu rat tin cay va keo dai
CONF_RAT_CAO = 85            # % do tin cay
THOI_GIAN_LUA_RAT_CHAC = 5   # giay lien tuc

# --- Cau hinh chup anh bang chung khi phat hien canh bao ---
SNAPSHOT_WIDTH = 480
SNAPSHOT_HEIGHT = 360
SNAPSHOT_QUALITY = 65


# ==========================================
# 1. TRANG THAI DUNG CHUNG GIUA CAC LUONG
# ==========================================
stop_event = threading.Event()
state_lock = threading.Lock()


def tao_trang_thai():
    return SimpleNamespace(
        # ket qua AI (do luong YOLO cap nhat)
        fire_streak=0,
        last_fire_time=0.0,
        last_ai_conf=0.0,
        high_conf_since=None,
        smoke_streak=0,
        last_smoke_cam_time=0.0,
        boxes=[],
        boxes_time=0.0,
        # anh bang chung da ve khung YOLO luc xac nhan lua / khoi
        evidence_img=None,
        evidence_time=0.0,
        # so do DHT11 (do luong DHT cap nhat)
        temperature=None,
        humidity=None,
        dht_time=0.0,
    )


S = tao_trang_thai()

# doi tuong phan cung / mang, tao trong main()
model = None
client = None
h = None
dht_device = None


# ==========================================
# 2. LOGIC CANH BAO (HAM THUAN, KHONG DUNG PHAN CUNG)
# ==========================================
def ten_lop(names, k):
    try:
        return str(names[k])
    except (KeyError, IndexError, TypeError):
        return str(k)


def loai_lop(names, k):
    """'lua', 'khoi' hoac None, tra theo model.names (khong co dinh so lop)."""
    ten = ten_lop(names, k).strip().lower()
    if ten in LOP_LUA:
        return "lua"
    if ten in LOP_KHOI:
        return "khoi"
    return None


def cap_nhat_ai(st, boxes, now, anh=None):
    """Cap nhat chuoi lua / khoi tu 1 lan chay YOLO. Goi khi da giu state_lock.
    boxes: (x1, y1, x2, y2, nhan, k, loai, conf%)"""
    fire_confs = [b[7] for b in boxes if b[6] == "lua"]
    co_box_khoi = any(b[6] == "khoi" for b in boxes)
    xac_nhan = False

    st.boxes = boxes
    st.boxes_time = now

    if fire_confs:
        fire_conf = max(fire_confs)
        st.fire_streak += 1
        if st.fire_streak >= FRAMES_XAC_NHAN:
            st.last_fire_time = now
            st.last_ai_conf = fire_conf
            xac_nhan = True
        if fire_conf >= CONF_RAT_CAO:
            if st.high_conf_since is None:
                st.high_conf_since = now
        else:
            st.high_conf_since = None
    else:
        st.fire_streak = 0
        st.high_conf_since = None

    if co_box_khoi:
        st.smoke_streak += 1
        if st.smoke_streak >= FRAMES_XAC_NHAN:
            st.last_smoke_cam_time = now
            xac_nhan = True
    else:
        st.smoke_streak = 0

    if xac_nhan and anh is not None:
        st.evidence_img = anh
        st.evidence_time = now


def xoa_co_camera(st):
    """Mat camera: bo het co lua / khoi cua camera va bo dem lua rat chac."""
    st.fire_streak = 0
    st.smoke_streak = 0
    st.last_fire_time = 0.0
    st.last_smoke_cam_time = 0.0
    st.high_conf_since = None
    st.boxes = []


def doc_co(st, now, last_mq2_time, camera_ok):
    """Lay cac co canh bao tai thoi diem now. Goi khi da giu state_lock."""
    if not camera_ok:
        xoa_co_camera(st)
    elif now - st.boxes_time >= AI_MOI_GIAY:
        st.high_conf_since = None   # YOLO khong co ket qua moi -> dem lai tu dau

    if now - st.dht_time <= DHT_STALE_GIAY:
        temperature, humidity = st.temperature, st.humidity
    else:
        temperature, humidity = None, None

    co_lua = (now - st.last_fire_time) < GIU_LUA_GIAY
    khoi_camera = (now - st.last_smoke_cam_time) < GIU_KHOI_CAMERA_GIAY
    khoi_mq2 = (now - last_mq2_time) < GIU_KHOI_GIAY
    nhiet_cao = temperature is not None and temperature > NGUONG_NHIET_CAO
    lua_rat_chac = (CHO_PHEP_LUA_DON_LE
                    and st.high_conf_since is not None
                    and (now - st.boxes_time) < AI_MOI_GIAY
                    and (now - st.high_conf_since) >= THOI_GIAN_LUA_RAT_CHAC)

    return SimpleNamespace(
        co_lua=co_lua,
        khoi_camera=khoi_camera,
        khoi_mq2=khoi_mq2,
        co_khoi=khoi_mq2 or khoi_camera,   # khoi = MQ-2 HOAC camera
        nhiet_cao=nhiet_cao,
        lua_rat_chac=lua_rat_chac,
        temperature=temperature,
        humidity=humidity,
        ai_conf=st.last_ai_conf,
    )


def muc_canh_bao(co_lua, co_khoi, nhiet_cao, lua_rat_chac):
    """LOGIC CANH BAO DA CAP DO (SENSOR FUSION): 0 binh thuong, 1 canh bao som, 2 bao chay."""
    so_tin_hieu = int(co_lua) + int(co_khoi) + int(nhiet_cao)
    if so_tin_hieu >= 2 or lua_rat_chac:
        return 2
    if so_tin_hieu == 1:
        return 1
    return 0


def muc_tu_co(co):
    """Moi cam bien chi duoc 1 phieu: camera (lua HOAC khoi camera), MQ-2, DHT11.
    Camera thay ca lua lan khoi van chi la 1 tin hieu -> muon len muc 2 phai co them
    MQ-2 hoac nhiet cao, tru duong "lua rat chac"."""
    return muc_canh_bao(co.co_lua or co.khoi_camera, co.khoi_mq2, co.nhiet_cao, co.lua_rat_chac)


def can_gui_anh(muc_do, tang_muc, now, last_snapshot_time):
    return muc_do > 0 and (tang_muc or now - last_snapshot_time >= SNAPSHOT_INTERVAL)


def tao_payload(co, muc_do, camera_ok, snapshot_b64=None):
    return {
        "device_id": DEVICE_ID,
        "nhiet_do": co.temperature,
        "do_am": co.humidity,
        "co_khoi": int(co.co_khoi),
        "co_lua": int(co.co_lua),
        "muc_do": muc_do,
        "confidence": round(co.ai_conf, 2) if co.co_lua else None,
        "snapshot": snapshot_b64,
        "khoi_camera": int(co.khoi_camera),
        "camera_ok": bool(camera_ok),
    }


def in_muc(muc_do, co):
    nhiet_text = f"{co.temperature}C" if co.temperature is not None else "N/A"
    if muc_do == 2:
        print(f"[MUC 2] BAO CHAY KHAN CAP! (Lua: {int(co.co_lua)} | Khoi: {int(co.co_khoi)} | "
              f"Khoi camera: {int(co.khoi_camera)} | Nhiet: {nhiet_text} | Lua rat chac: {int(co.lua_rat_chac)})")
    elif muc_do == 1:
        print(f"[MUC 1] Canh bao som! (Lua: {int(co.co_lua)} | Khoi: {int(co.co_khoi)} | "
              f"Khoi camera: {int(co.khoi_camera)} | Nhiet: {nhiet_text})")
    else:
        do_am_text = f"{co.humidity}%" if co.humidity is not None else "N/A"
        print(f"[MUC 0] Binh thuong: Nhiet do {nhiet_text}, Do am {do_am_text}")


# ==========================================
# 3. FLASK WEB SERVER (STREAM CAMERA)
# ==========================================
logging.getLogger("werkzeug").setLevel(logging.ERROR)
app = Flask(__name__)
frame_cond = threading.Condition()
latest_jpeg = None


@app.after_request
def add_cors(resp):
    resp.headers["Access-Control-Allow-Origin"] = "*"
    return resp


def push_frame(img):
    """Ma hoa JPEG 1 LAN o vong lap chinh, cac client xem chi lay ban da ma hoa."""
    global latest_jpeg
    ok, jpg = cv2.imencode(".jpg", img, [cv2.IMWRITE_JPEG_QUALITY, JPEG_QUALITY])
    if ok:
        with frame_cond:
            latest_jpeg = jpg.tobytes()
            frame_cond.notify_all()


@app.route('/video_feed')
def video_feed():
    def generate():
        while True:
            with frame_cond:
                frame_cond.wait(timeout=5)
                jpg = latest_jpeg
            if jpg is None:
                continue
            yield (b'--frame\r\nContent-Type: image/jpeg\r\nContent-Length: '
                   + str(len(jpg)).encode() + b'\r\n\r\n' + jpg + b'\r\n')
    return Response(generate(), mimetype='multipart/x-mixed-replace; boundary=frame')


def start_flask():
    try:
        app.run(host='0.0.0.0', port=STREAM_PORT, debug=False, use_reloader=False, threaded=True)
    except OSError as error:
        print(f"Khong bat duoc may chu video (cong {STREAM_PORT} dang ban?): {error}")


# ==========================================
# 4. MQTT (TU KET NOI LAI, BROKER CHUA CHAY CUNG KHONG SAP)
# ==========================================
def on_connect(client, userdata, flags, reason_code, properties=None):
    print(f"[MQTT] Ket noi broker {BROKER}:{PORT}: {reason_code}")
    if reason_code == 0:
        client.publish(STATUS_TOPIC, "online", qos=1, retain=True)


def on_connect_fail(client, userdata):
    print(f"[MQTT] Chua ket noi duoc broker {BROKER}:{PORT}, se tu thu lai...")


def on_disconnect(client, userdata, *args):
    print("[MQTT] Mat ket noi broker, dang tu ket noi lai...")


def tao_mqtt():
    # client_id co dinh: Pi khoi dong lai se thay phien cu, "offline" cu khong de len "online" moi
    c = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2, client_id=f"pbl4-edge-{DEVICE_ID}")
    c.on_connect = on_connect
    c.on_connect_fail = on_connect_fail
    c.on_disconnect = on_disconnect
    c.will_set(STATUS_TOPIC, "offline", qos=1, retain=True)
    c.reconnect_delay_set(min_delay=1, max_delay=30)
    c.max_queued_messages_set(50)    # mat broker lau thi khong giu qua nhieu ban tin QoS 1
    c.connect_async(BROKER, PORT, 60)
    c.loop_start()
    return c


# ==========================================
# 5. GPIO (Raspberry Pi 5)
# ==========================================
def khoi_tao_gpio():
    global h, dht_device
    h = lgpio.gpiochip_open(0)
    lgpio.gpio_claim_output(h, BUZZER_PIN)
    lgpio.gpio_write(h, BUZZER_PIN, 1)      # tat coi luc khoi dong (1 la tat, 0 la bat)
    dht_device = adafruit_dht.DHT11(DHT_PIN)
    lgpio.gpio_claim_input(h, MQ2_PIN)


# ==========================================
# 6. CAMERA (LUONG RIENG, TU KET NOI LAI KHI ROT FRAME)
# ==========================================
class CameraReader:
    """Doc camera lien tuc o luong rieng va luon giu KHUNG MOI NHAT.
    Tu dong mo lai camera neu webcam USB bi ngat giua chung, khong bao gio tu thoat."""

    def __init__(self):
        self._lock = threading.Lock()
        self._frame = None
        self._frame_id = 0
        self.last_frame_time = time.time()
        self._fail_count = 0
        self._last_err_print = 0.0
        self.cap = None
        try:
            self.cap = self._open_cam()
        except Exception as error:
            print(f"[Camera] Loi mo camera: {error}")

    def _open_cam(self):
        cap = cv2.VideoCapture(CAM_INDEX, cv2.CAP_V4L2)
        cap.set(cv2.CAP_PROP_FOURCC, cv2.VideoWriter_fourcc(*'MJPG'))
        cap.set(cv2.CAP_PROP_FRAME_WIDTH, CAM_WIDTH)
        cap.set(cv2.CAP_PROP_FRAME_HEIGHT, CAM_HEIGHT)
        cap.set(cv2.CAP_PROP_FPS, CAM_FPS)
        cap.set(cv2.CAP_PROP_BUFFERSIZE, 1)
        return cap

    def is_opened(self):
        return self.cap is not None and self.cap.isOpened()

    def _mo_lai(self):
        if self.cap is not None:
            self.cap.release()
            self.cap = None
        self.cap = self._open_cam()

    def _doc_mot_khung(self):
        if not self.is_opened():
            stop_event.wait(1)
            self._mo_lai()
            return

        ok, frame = self.cap.read()
        if not ok or frame is None:
            self._fail_count += 1
            time.sleep(0.05)
            if self._fail_count >= 40:  # ~2 giay mat tin hieu -> tu reset USB camera
                now = time.time()
                if now - self._last_err_print >= 30:
                    print("[Camera] Mat tin hieu USB, dang tu ket noi lai...")
                    self._last_err_print = now
                self._fail_count = 0
                self.cap.release()
                time.sleep(0.5)
                self._mo_lai()
            return

        self._fail_count = 0
        with self._lock:
            self._frame = frame
            self._frame_id += 1
            self.last_frame_time = time.time()

    def run(self):
        while not stop_event.is_set():
            try:
                self._doc_mot_khung()
            except Exception as error:
                print(f"[Camera] Loi luong camera: {error}")
                stop_event.wait(1)

    def get(self):
        with self._lock:
            return self._frame, self._frame_id


# ==========================================
# 7. AI (LUONG RIENG + CHUP ANH BANG CHUNG)
# ==========================================
def kiem_tra_lop(names):
    ds = names.items() if isinstance(names, dict) else enumerate(names)
    loai = {int(k): loai_lop(names, int(k)) for k, _ in ds}
    ten = {k: ten_lop(names, k) for k in loai}
    print(f"[AI] Lop cua mo hinh: {ten}")
    if "lua" not in loai.values():
        print(f"CANH BAO: mo hinh khong co lop {LOP_LUA}, AI se khong bao lua!")
    if "khoi" not in loai.values():
        print(f"CANH BAO: mo hinh khong co lop {LOP_KHOI}, camera se khong bao khoi!")


def ve_khung(img, boxes):
    """Ve khung bang chinh bo ve cua ultralytics: mau theo lop, nhan co nen."""
    ann = Annotator(img, line_width=2)
    for b in boxes:
        x1, y1, x2, y2, label, k = b[:6]
        ann.box_label((x1, y1, x2, y2), label, color=colors(k, True))
    return ann.result()


def tach_khung(results, names):
    boxes = []
    for r in results:
        if len(r.boxes) == 0:
            continue
        xyxy = r.boxes.xyxy.cpu().numpy().astype(int)
        confs = r.boxes.conf.cpu().numpy() * 100
        clss = r.boxes.cls.cpu().numpy().astype(int)
        for (x1, y1, x2, y2), c, k in zip(xyxy, confs, clss):
            k = int(k)
            boxes.append((int(x1), int(y1), int(x2), int(y2),
                          f"{ten_lop(names, k)} {c / 100:.2f}", k, loai_lop(names, k), float(c)))
    return boxes


def detector_loop(cam):
    last_id = -1
    min_interval = 1.0 / YOLO_MAX_FPS if YOLO_MAX_FPS else 0.0
    fps_count = 0
    fps_t0 = time.time()

    while not stop_event.is_set():
        t0 = time.time()
        try:
            frame, fid = cam.get()
            if frame is None or fid == last_id:
                time.sleep(0.005)
                continue
            last_id = fid

            results = model(frame, verbose=False, conf=YOLO_CONF, imgsz=YOLO_IMGSZ)
            boxes = tach_khung(results, model.names)

            now = time.time()
            # Ve san anh co khung ngay luc AI bat duoc de khong bi lech nhip
            annotated = ve_khung(frame.copy(), boxes) if boxes else None

            with state_lock:
                cap_nhat_ai(S, boxes, now, annotated)

            fps_count += 1
            if now - fps_t0 >= 10:
                print(f"[AI] YOLO ~{fps_count / (now - fps_t0):.1f} khung/giay")
                fps_count = 0
                fps_t0 = now
        except Exception as error:
            print(f"[AI] Loi luong YOLO: {error}")
            stop_event.wait(1)
            continue

        sleep_t = min_interval - (time.time() - t0)
        if sleep_t > 0:
            time.sleep(sleep_t)


# ==========================================
# 8. DHT11 (LUONG RIENG)
# ==========================================
def dht_loop():
    last_err_print = 0.0
    while not stop_event.is_set():
        try:
            t = dht_device.temperature
            hum = dht_device.humidity
            if t is not None and hum is not None:
                with state_lock:
                    S.temperature, S.humidity, S.dht_time = t, hum, time.time()
        except RuntimeError as error:
            now = time.time()
            with state_lock:
                chua_doc_duoc = now - S.dht_time
            if chua_doc_duoc > 5 and now - last_err_print >= 10:
                print(f"[DHT11] Chua doc duoc: {error}")
                last_err_print = now
        except Exception as error:
            print(f"[DHT11] Loi khac: {error}")
        stop_event.wait(SENSOR_INTERVAL)


# ==========================================
# 9. GIAM SAT LUONG (LUONG CHET THI BAT LAI)
# ==========================================
LUONG = {}   # ten -> (ham, tham so, thread)


def bat_luong(ten, ham, *args):
    t = threading.Thread(target=ham, args=args, name=ten, daemon=True)
    t.start()
    LUONG[ten] = (ham, args, t)


def kiem_tra_luong():
    for ten, (ham, args, t) in list(LUONG.items()):
        if not t.is_alive() and not stop_event.is_set():
            print(f"[He thong] Luong {ten} da dung bat thuong, dang khoi dong lai...")
            bat_luong(ten, ham, *args)


# ==========================================
# 10. VONG LAP CHINH: STREAM + MQ-2 + DANH GIA CANH BAO + MQTT
# ==========================================
def ma_hoa_anh(img):
    if img is None:
        return None
    small_img = cv2.resize(img, (SNAPSHOT_WIDTH, SNAPSHOT_HEIGHT))
    ok_jpg, jpg_buf = cv2.imencode(".jpg", small_img, [cv2.IMWRITE_JPEG_QUALITY, SNAPSHOT_QUALITY])
    if not ok_jpg:
        return None
    return base64.b64encode(jpg_buf.tobytes()).decode("ascii")


def vong_lap_chinh(cam):
    last_stream_time = 0.0
    last_pushed_id = -1
    last_eval_time = 0.0
    last_publish_time = 0.0
    last_snapshot_time = 0.0
    last_mq2_time = 0.0
    last_thread_check = time.time()
    last_loop_err = 0.0
    last_mqtt_err = 0.0
    muc_truoc = 0
    camera_ok_truoc = True

    while not stop_event.is_set():
        now = time.time()
        try:
            # Doc MQ-2 moi vong lap de khong bo lo khoi chi xuat hien thoang qua
            if lgpio.gpio_read(h, MQ2_PIN) == 0:
                last_mq2_time = now

            frame, fid = cam.get()
            camera_ok = (now - cam.last_frame_time) <= CAMERA_TIMEOUT
            if camera_ok != camera_ok_truoc:
                if camera_ok:
                    print("[Camera] Da co lai camera.")
                else:
                    print(f"[Camera] Mat camera (khong co khung moi qua {CAMERA_TIMEOUT} giay). "
                          "Cam bien, coi va MQTT van chay, dang thu ket noi lai camera...")
                camera_ok_truoc = camera_ok

            # --- STREAM: khung moi nhat cua camera + khung vuong YOLO gan nhat ---
            if frame is not None and fid != last_pushed_id and now - last_stream_time >= 1.0 / STREAM_FPS:
                with state_lock:
                    boxes = S.boxes if (now - S.boxes_time) < BOX_TTL else []
                img = ve_khung(frame.copy(), boxes) if boxes else frame
                push_frame(img)
                last_pushed_id = fid
                last_stream_time = now

            # --- DANH GIA CANH BAO + COI (moi DANH_GIA_INTERVAL giay) ---
            if now - last_eval_time >= DANH_GIA_INTERVAL:
                last_eval_time = now

                with state_lock:
                    co = doc_co(S, now, last_mq2_time, camera_ok)
                    evidence = S.evidence_img if (now - S.evidence_time) <= GIU_LUA_GIAY else None
                    boxes = S.boxes if (now - S.boxes_time) < BOX_TTL else []

                muc_do = muc_tu_co(co)
                lgpio.gpio_write(h, BUZZER_PIN, 0 if muc_do == 2 else 1)   # chi muc 2 moi hu coi
                tang_muc = muc_do > muc_truoc
                muc_truoc = muc_do

                # --- MQTT: moi PUBLISH_INTERVAL giay (heartbeat) hoac ngay khi muc tang ---
                if tang_muc or now - last_publish_time >= PUBLISH_INTERVAL:
                    last_publish_time = now
                    in_muc(muc_do, co)

                    snapshot_b64 = None
                    if can_gui_anh(muc_do, tang_muc, now, last_snapshot_time):
                        img_to_save = evidence
                        if img_to_save is None and camera_ok and frame is not None:
                            img_to_save = ve_khung(frame.copy(), boxes) if boxes else frame
                        snapshot_b64 = ma_hoa_anh(img_to_save)
                        if snapshot_b64:
                            last_snapshot_time = now

                    payload = json.dumps(tao_payload(co, muc_do, camera_ok, snapshot_b64))
                    info = client.publish(TOPIC, payload, qos=1 if muc_do > 0 else 0)
                    if info.rc == mqtt.MQTT_ERR_SUCCESS:
                        print(f"   -> da gui MQTT: {TOPIC}" + (" (kem anh)" if snapshot_b64 else ""))
                    elif now - last_mqtt_err >= 10:
                        print(f"   -> chua gui duoc MQTT (chua ket noi broker {BROKER}:{PORT})")
                        last_mqtt_err = now

            if now - last_thread_check >= 5:
                last_thread_check = now
                kiem_tra_luong()

        except Exception as error:
            if now - last_loop_err >= 5:
                print(f"[He thong] Loi vong lap chinh: {error}")
                last_loop_err = now

        time.sleep(0.005)


# ==========================================
# 11. KHOI DONG / DUNG
# ==========================================
def don_dep(cam):
    stop_event.set()
    try:
        if h is not None:
            lgpio.gpio_write(h, BUZZER_PIN, 1)
    except Exception:
        pass
    for _, _, t in LUONG.values():
        t.join(timeout=3)
    if client is not None:
        try:
            if client.is_connected():
                client.publish(STATUS_TOPIC, "offline", qos=1, retain=True).wait_for_publish(timeout=2)
            client.disconnect()
        except Exception:
            pass
        client.loop_stop()
    if h is not None:
        lgpio.gpiochip_close(h)
    if cam is not None and cam.cap is not None:
        cam.cap.release()
    if dht_device is not None:
        dht_device.exit()
    print("\nUng dung da dung hoat dong.")


def main():
    global model, client

    if not os.path.exists(MODEL_PATH):
        print(f"LOI: Khong tim thay mo hinh YOLO: {MODEL_PATH}")
        print("  Tai best.pt tu thu muc Drive 'training/Cấu hình code train AI' va dat canh main_iot.py:")
        print(f"  {BASE_DIR}")
        print("  Chi tiet: apps/ai-vision/README.md")
        return 1

    torch.set_num_threads(YOLO_THREADS)
    print("Dang tai mo hinh YOLO AI...")
    try:
        model = YOLO(MODEL_PATH)
    except Exception as error:
        print(f"LOI: Khong tai duoc mo hinh {MODEL_PATH}: {error}")
        return 1
    kiem_tra_lop(model.names)

    cam = None
    try:
        khoi_tao_gpio()
        threading.Thread(target=start_flask, daemon=True).start()
        client = tao_mqtt()

        cam = CameraReader()
        if not cam.is_opened():
            print("LOI: Khong mo duoc camera! Co the dang bi chuong trinh khac dung. "
                  "Cam bien va MQTT van chay, camera se tu thu lai.")
        bat_luong("camera", cam.run)
        bat_luong("AI", detector_loop, cam)
        bat_luong("DHT11", dht_loop)

        print("He thong IoT + AI + Video Stream chuan bi chay...")
        time.sleep(2)
        vong_lap_chinh(cam)
    except KeyboardInterrupt:
        print("\nDa nhan lenh tat he thong (Ctrl+C).")
    finally:
        don_dep(cam)
    return 0


if __name__ == "__main__":
    sys.exit(main())
