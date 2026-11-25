import time
import json
import base64
import logging
import os
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
DEVICE_ID = "PI-01"      # ten mach Pi nay, PHAI co trong bang Devices (MySQL). Pi thu 2 doi thanh "PI-02"
STREAM_PORT = 5000       # xem thu: http://<IP-Pi>:5000/video_feed
STREAM_FPS = 15          # so khung gui di moi giay
JPEG_QUALITY = 70        # giam xuong 55-60 neu mang Wi-Fi yeu

# IP may chay Mosquitto. PHAI trung voi MqttBroker:Host trong appsettings.json cua
# mqtt-subscriber, desktop-app va web-app. Doi khong can sua code: MQTT_BROKER_HOST=... python3 main_iot.py
# ("localhost" neu chay Mosquitto ngay tren Pi 5)
BROKER = os.environ.get("MQTT_BROKER_HOST", "192.168.137.191")
PORT = int(os.environ.get("MQTT_BROKER_PORT", "1883"))
TOPIC = "pbl4/sensors/fire_warning"

BUZZER_PIN = 22          # coi (muc 0 = bat, 1 = tat)
DHT_PIN = board.D17      # DHT11 (GPIO 17)
MQ2_PIN = 27             # MQ-2 (GPIO 27), muc 0 = co khoi

# --- Camera ---
CAM_INDEX = 0
CAM_WIDTH = 640
CAM_HEIGHT = 480
CAM_FPS = 30
CAMERA_TIMEOUT = 30      # giay: cho du thoi gian de CameraReader tu ket noi lai neu USB chập chờn

# --- YOLO ---
MODEL_PATH = "best.pt"   # co the doi thanh "best_ncnn_model" sau khi export NCNN
YOLO_CONF = 0.7          # nguong tin cay toi thieu cua YOLO
YOLO_IMGSZ = 416         # kich thuoc anh dua vao YOLO (boi so cua 32)
YOLO_MAX_FPS = 8         # gioi han so lan YOLO chay moi giay de chua CPU cho camera/stream
YOLO_THREADS = 3         # so nhan CPU cho YOLO (Pi 5 co 4 nhan, chua 1 nhan cho camera + stream)
BOX_TTL = 1.0            # giay: khung vuong cu hon muc nay thi khong ve nua

# --- Logic canh bao ---
NGUONG_NHIET_CAO = 45        # do C: tren muc nay tinh la "nhiet cao"
FRAMES_XAC_NHAN = 5          # so lan YOLO LIEN TIEP phai thay lua moi tinh la lua that
GIU_LUA_GIAY = 3             # giu co lua them bao nhieu giay
GIU_KHOI_GIAY = 6            # giu co khoi them bao nhieu giay (MQ-2 nhay chap chon)
DHT_STALE_GIAY = 10          # so do DHT11 cu hon muc nay thi coi nhu khong co du lieu
SENSOR_INTERVAL = 2.5        # giay giua 2 lan doc DHT11 + danh gia (DHT11 can >= 2 giay/lan)

# Duong "lua rat chac" (cho truong hop cam bien dat xa cho chay)
CHO_PHEP_LUA_DON_LE = True   # True: lua dung 1 minh van len muc 2 neu rat tin cay va keo dai
CONF_RAT_CAO = 85            # % do tin cay
THOI_GIAN_LUA_RAT_CHAC = 5   # giay lien tuc

# --- Cau hinh chup anh bang chung khi phat hien canh bao ---
SNAPSHOT_WIDTH = 480
SNAPSHOT_HEIGHT = 360
SNAPSHOT_QUALITY = 65

torch.set_num_threads(YOLO_THREADS)


# ==========================================
# 1. TRANG THAI DUNG CHUNG GIUA CAC LUONG
# ==========================================
stop_event = threading.Event()
state_lock = threading.Lock()
S = SimpleNamespace(
    # ket qua AI (do luong YOLO cap nhat)
    fire_streak=0,
    last_fire_time=0.0,
    last_ai_conf=0.0,
    high_conf_since=None,
    boxes=[],
    boxes_time=0.0,
    # anh bang chung da ve khung YOLO luc phat hien lua
    fire_snapshot=None,
    fire_snapshot_time=0.0,
    # so do DHT11 (do luong DHT cap nhat)
    temperature=None,
    humidity=None,
    dht_time=0.0,
)


# ==========================================
# 2. FLASK WEB SERVER (STREAM CAMERA)
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


flask_thread = threading.Thread(target=start_flask, daemon=True)
flask_thread.start()


# ==========================================
# 3. MQTT
# ==========================================
def on_connect(client, userdata, flags, reason_code, properties=None):
    print(f"[MQTT] Ket noi broker {BROKER}: {reason_code}")


def on_disconnect(client, userdata, *args):
    print("[MQTT] Mat ket noi broker, dang tu ket noi lai...")


client = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2)
client.on_connect = on_connect
client.on_disconnect = on_disconnect
client.connect(BROKER, PORT, 60)
client.loop_start()


# ==========================================
# 4. GPIO (Raspberry Pi 5)
# ==========================================
h = lgpio.gpiochip_open(0)

lgpio.gpio_claim_output(h, BUZZER_PIN)
lgpio.gpio_write(h, BUZZER_PIN, 1)      # tat coi luc khoi dong (1 la tat, 0 la bat)

dht_device = adafruit_dht.DHT11(DHT_PIN)

lgpio.gpio_claim_input(h, MQ2_PIN)


# ==========================================
# 5. CAMERA (LUONG RIENG, TU KET NOI LAI KHI ROT FRAME)
# ==========================================
class CameraReader(threading.Thread):
    """Doc camera lien tuc o luong rieng va luon giu KHUNG MOI NHAT.
    Tu dong mo lai camera neu webcam USB bi ngat giua chung."""

    def __init__(self):
        super().__init__(daemon=True)
        self._lock = threading.Lock()
        self._frame = None
        self._frame_id = 0
        self.last_frame_time = time.time()
        self.cap = self._open_cam()

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

    def run(self):
        fail_count = 0
        while not stop_event.is_set():
            if not self.is_opened():
                time.sleep(1)
                self.cap = self._open_cam()
                continue

            ok, frame = self.cap.read()
            if not ok or frame is None:
                fail_count += 1
                time.sleep(0.05)
                if fail_count >= 40:  # ~2 giay mat tin hieu -> tu reset USB camera
                    print("[Camera] Mat tin hieu USB, dang tu ket noi lai...")
                    self.cap.release()
                    time.sleep(0.5)
                    self.cap = self._open_cam()
                    fail_count = 0
                continue

            fail_count = 0
            with self._lock:
                self._frame = frame
                self._frame_id += 1
                self.last_frame_time = time.time()

    def get(self):
        with self._lock:
            return self._frame, self._frame_id


# ==========================================
# 6. AI (LUONG RIENG + CHUP ANH BANG CHUNG)
# ==========================================
print("Dang tai mo hinh YOLO AI...")
model = YOLO(MODEL_PATH)


def ve_khung(img, boxes):
    """Ve khung bang chinh bo ve cua ultralytics: mau theo lop, nhan co nen."""
    ann = Annotator(img, line_width=2)
    for x1, y1, x2, y2, label, k in boxes:
        ann.box_label((x1, y1, x2, y2), label, color=colors(k, True))
    return ann.result()


def detector_loop(cam):
    last_id = -1
    min_interval = 1.0 / YOLO_MAX_FPS if YOLO_MAX_FPS else 0.0
    fps_count = 0
    fps_t0 = time.time()

    while not stop_event.is_set():
        t0 = time.time()
        frame, fid = cam.get()
        if frame is None or fid == last_id:
            time.sleep(0.005)
            continue
        last_id = fid

        try:
            results = model(frame, verbose=False, conf=YOLO_CONF, imgsz=YOLO_IMGSZ)
        except Exception as error:
            print(f"[AI] Loi YOLO: {error}")
            time.sleep(1)
            continue

        boxes = []
        best_conf = 0.0
        for r in results:
            if len(r.boxes) == 0:
                continue
            xyxy = r.boxes.xyxy.cpu().numpy().astype(int)
            confs = r.boxes.conf.cpu().numpy() * 100
            clss = r.boxes.cls.cpu().numpy().astype(int)
            for (x1, y1, x2, y2), c, k in zip(xyxy, confs, clss):
                boxes.append((int(x1), int(y1), int(x2), int(y2),
                              f"{model.names[int(k)]} {c / 100:.2f}", int(k)))
                best_conf = max(best_conf, float(c))

        now = time.time()
        # Ve san anh co khung do ngay luc AI bat duoc lua de khong bi lech nhip
        annotated_fire = ve_khung(frame.copy(), boxes) if boxes else None

        with state_lock:
            S.boxes = boxes
            S.boxes_time = now
            if boxes:
                S.fire_streak += 1
                if S.fire_streak >= FRAMES_XAC_NHAN:
                    S.last_fire_time = now
                    S.last_ai_conf = best_conf
                    S.fire_snapshot = annotated_fire
                    S.fire_snapshot_time = now
                if best_conf >= CONF_RAT_CAO:
                    if S.high_conf_since is None:
                        S.high_conf_since = now
                else:
                    S.high_conf_since = None
            else:
                S.fire_streak = 0
                S.high_conf_since = None

        fps_count += 1
        if now - fps_t0 >= 10:
            print(f"[AI] YOLO ~{fps_count / (now - fps_t0):.1f} khung/giay")
            fps_count = 0
            fps_t0 = now

        sleep_t = min_interval - (time.time() - t0)
        if sleep_t > 0:
            time.sleep(sleep_t)


# ==========================================
# 7. DHT11 (LUONG RIENG)
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
# 8. KHOI DONG
# ==========================================
cam = CameraReader()
if not cam.is_opened():
    print("LOI: Khong mo duoc camera! Co the dang bi chuong trinh khac dung.")
cam.start()

detector_thread = threading.Thread(target=detector_loop, args=(cam,), daemon=True)
detector_thread.start()

dht_thread = threading.Thread(target=dht_loop, daemon=True)
dht_thread.start()

print("He thong IoT + AI + Video Stream chuan bi chay...")
time.sleep(2)

last_stream_time = 0.0
last_pushed_id = -1
last_eval_time = 0.0
last_smoke_time = 0.0


# ==========================================
# 9. VONG LAP CHINH: STREAM + MQ-2 + DANH GIA CANH BAO + MQTT
# ==========================================
try:
    while not stop_event.is_set():
        now = time.time()

        if now - cam.last_frame_time > CAMERA_TIMEOUT:
            print("Loi: Camera mat ket noi qua lau!")
            break

        # Doc MQ-2 moi vong lap de khong bo lo khoi chi xuat hien thoang qua
        if lgpio.gpio_read(h, MQ2_PIN) == 0:
            last_smoke_time = now

        # --- STREAM: khung moi nhat cua camera + khung vuong YOLO gan nhat ---
        frame, fid = cam.get()
        if frame is not None and fid != last_pushed_id and now - last_stream_time >= 1.0 / STREAM_FPS:
            with state_lock:
                boxes = S.boxes if (now - S.boxes_time) < BOX_TTL else []
            if boxes:
                img = ve_khung(frame.copy(), boxes)
            else:
                img = frame
            push_frame(img)
            last_pushed_id = fid
            last_stream_time = now

        # --- DANH GIA CANH BAO + MQTT (moi SENSOR_INTERVAL giay) ---
        if now - last_eval_time >= SENSOR_INTERVAL:
            last_eval_time = now

            with state_lock:
                last_fire_time = S.last_fire_time
                ai_conf = S.last_ai_conf
                high_since = S.high_conf_since
                snap_fire = S.fire_snapshot if (now - S.fire_snapshot_time) <= GIU_LUA_GIAY else None
                if now - S.dht_time <= DHT_STALE_GIAY:
                    temperature, humidity = S.temperature, S.humidity
                else:
                    temperature, humidity = None, None

            co_lua = (now - last_fire_time) < GIU_LUA_GIAY
            co_khoi = (now - last_smoke_time) < GIU_KHOI_GIAY
            nhiet_cao = temperature is not None and temperature > NGUONG_NHIET_CAO
            lua_rat_chac = (CHO_PHEP_LUA_DON_LE
                            and high_since is not None
                            and (now - high_since) >= THOI_GIAN_LUA_RAT_CHAC)

            so_tin_hieu = int(co_lua) + int(co_khoi) + int(nhiet_cao)
            nhiet_text = f"{temperature}C" if temperature is not None else "N/A"

            # LOGIC CANH BAO DA CAP DO (SENSOR FUSION)
            if so_tin_hieu >= 2 or lua_rat_chac:
                muc_do = 2
                lgpio.gpio_write(h, BUZZER_PIN, 0)      # bat coi hu
                print(f"[MUC 2] BAO CHAY KHAN CAP! (Lua: {int(co_lua)} | Khoi: {int(co_khoi)} | "
                      f"Nhiet: {nhiet_text} | Lua rat chac: {int(lua_rat_chac)})")

            elif so_tin_hieu == 1:
                muc_do = 1
                lgpio.gpio_write(h, BUZZER_PIN, 1)
                print(f"[MUC 1] Canh bao som! (Lua: {int(co_lua)} | Khoi: {int(co_khoi)} | Nhiet: {nhiet_text})")

            else:
                muc_do = 0
                lgpio.gpio_write(h, BUZZER_PIN, 1)
                print(f"[MUC 0] Binh thuong: Nhiet do {nhiet_text}, Do am {humidity}%")

            # Dong goi anh bang chung Base64 neu dang co canh bao (muc_do >= 1)
            snapshot_b64 = None
            if muc_do > 0:
                img_to_save = snap_fire if snap_fire is not None else (frame.copy() if frame is not None else None)
                if img_to_save is not None:
                    small_img = cv2.resize(img_to_save, (SNAPSHOT_WIDTH, SNAPSHOT_HEIGHT))
                    ok_jpg, jpg_buf = cv2.imencode(".jpg", small_img, [cv2.IMWRITE_JPEG_QUALITY, SNAPSHOT_QUALITY])
                    if ok_jpg:
                        snapshot_b64 = base64.b64encode(jpg_buf.tobytes()).decode("ascii")

            # DAY LEN MQTT
            if muc_do > 0 or temperature is not None:
                payload = json.dumps({
                    "device_id": DEVICE_ID,
                    "nhiet_do": temperature,
                    "do_am": humidity,
                    "co_khoi": int(co_khoi),
                    "co_lua": int(co_lua),
                    "muc_do": muc_do,
                    "confidence": round(ai_conf, 2) if co_lua else None,
                    "snapshot": snapshot_b64
                })
                client.publish(TOPIC, payload)
                print(f"   -> da gui MQTT: {TOPIC}" + (" (kem anh)" if snapshot_b64 else ""))

        time.sleep(0.005)

except KeyboardInterrupt:
    print("\nDa nhan lenh tat he thong (Ctrl+C).")
finally:
    stop_event.set()
    cam.join(timeout=2)
    detector_thread.join(timeout=3)
    dht_thread.join(timeout=3)
    client.loop_stop()
    client.disconnect()
    lgpio.gpio_write(h, BUZZER_PIN, 1)
    lgpio.gpiochip_close(h)
    if cam.cap is not None:
        cam.cap.release()
    cv2.destroyAllWindows()
    dht_device.exit()
    print("\nUng dung da dung hoat dong.")