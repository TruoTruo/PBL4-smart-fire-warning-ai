import time
import json
import base64
import logging
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
DEVICE_ID = "PI-01"      # ten mach Pi nay, PHAI co trong bang Devices (MySQL). Pi thu 2 doi thanh "PI-02"[cite: 5]
STREAM_PORT = 5000       # xem thu: http://<IP-Pi>:5000/video_feed[cite: 5]
STREAM_FPS = 15          # so khung gui di moi giay[cite: 5]
JPEG_QUALITY = 70        # giam xuong 55-60 neu mang Wi-Fi yeu[cite: 5]

BROKER = "192.168.137.191"   # DOI LAI "localhost" neu chay Mosquitto ngay tren Pi 5[cite: 4, 5]
PORT = 1883[cite: 5]
TOPIC = "pbl4/sensors/fire_warning"[cite: 5]

BUZZER_PIN = 22          # coi (muc 0 = bat, 1 = tat)[cite: 5]
DHT_PIN = board.D17      # DHT11 (GPIO 17)[cite: 5]
MQ2_PIN = 27             # MQ-2 (GPIO 27), muc 0 = co khoi[cite: 5]

# --- Camera ---
CAM_INDEX = 0[cite: 5]
CAM_WIDTH = 640[cite: 5]
CAM_HEIGHT = 480[cite: 5]
CAM_FPS = 30[cite: 5]
CAMERA_TIMEOUT = 30      # giay: cho du thoi gian de CameraReader tu ket noi lai neu USB chập chờn[cite: 5]

# --- YOLO ---
MODEL_PATH = "best.pt"   # co the doi thanh "best_ncnn_model" sau khi export NCNN[cite: 5]
YOLO_CONF = 0.7          # nguong tin cay toi thieu cua YOLO[cite: 5]
YOLO_IMGSZ = 416         # kich thuoc anh dua vao YOLO (boi so cua 32)[cite: 5]
YOLO_MAX_FPS = 8         # gioi han so lan YOLO chay moi giay de chua CPU cho camera/stream[cite: 5]
YOLO_THREADS = 3         # so nhan CPU cho YOLO (Pi 5 co 4 nhan, chua 1 nhan cho camera + stream)[cite: 5]
BOX_TTL = 1.0            # giay: khung vuong cu hon muc nay thi khong ve nua[cite: 5]

# --- Logic canh bao ---
NGUONG_NHIET_CAO = 45        # do C: tren muc nay tinh la "nhiet cao"[cite: 5]
FRAMES_XAC_NHAN = 5          # so lan YOLO LIEN TIEP phai thay lua moi tinh la lua that[cite: 5]
GIU_LUA_GIAY = 3             # giu co lua them bao nhieu giay[cite: 5]
GIU_KHOI_GIAY = 6            # giu co khoi them bao nhieu giay (MQ-2 nhay chap chon)[cite: 5]
DHT_STALE_GIAY = 10          # so do DHT11 cu hon muc nay thi coi nhu khong co du lieu[cite: 5]
SENSOR_INTERVAL = 2.5        # giay giua 2 lan doc DHT11 + danh gia (DHT11 can >= 2 giay/lan)[cite: 5]

# Duong "lua rat chac" (cho truong hop cam bien dat xa cho chay)
CHO_PHEP_LUA_DON_LE = True   # True: lua dung 1 minh van len muc 2 neu rat tin cay va keo dai[cite: 5]
CONF_RAT_CAO = 85            # % do tin cay[cite: 5]
THOI_GIAN_LUA_RAT_CHAC = 5   # giay lien tuc[cite: 5]

# --- Cau hinh chup anh bang chung khi phat hien canh bao ---
SNAPSHOT_WIDTH = 480
SNAPSHOT_HEIGHT = 360
SNAPSHOT_QUALITY = 65

torch.set_num_threads(YOLO_THREADS)[cite: 5]


# ==========================================
# 1. TRANG THAI DUNG CHUNG GIUA CAC LUONG
# ==========================================
stop_event = threading.Event()[cite: 5]
state_lock = threading.Lock()[cite: 5]
S = SimpleNamespace(
    # ket qua AI (do luong YOLO cap nhat)
    fire_streak=0,[cite: 5]
    last_fire_time=0.0,[cite: 5]
    last_ai_conf=0.0,[cite: 5]
    high_conf_since=None,[cite: 5]
    boxes=[],[cite: 5]
    boxes_time=0.0,[cite: 5]
    # anh bang chung da ve khung YOLO luc phat hien lua
    fire_snapshot=None,
    fire_snapshot_time=0.0,
    # so do DHT11 (do luong DHT cap nhat)
    temperature=None,[cite: 5]
    humidity=None,[cite: 5]
    dht_time=0.0,[cite: 5]
)


# ==========================================
# 2. FLASK WEB SERVER (STREAM CAMERA)
# ==========================================
logging.getLogger("werkzeug").setLevel(logging.ERROR)[cite: 5]
app = Flask(__name__)[cite: 5]
frame_cond = threading.Condition()[cite: 5]
latest_jpeg = None[cite: 5]


@app.after_request
def add_cors(resp):[cite: 5]
    resp.headers["Access-Control-Allow-Origin"] = "*"[cite: 5]
    return resp[cite: 5]


def push_frame(img):[cite: 5]
    """Ma hoa JPEG 1 LAN o vong lap chinh, cac client xem chi lay ban da ma hoa."""[cite: 5]
    global latest_jpeg
    ok, jpg = cv2.imencode(".jpg", img, [cv2.IMWRITE_JPEG_QUALITY, JPEG_QUALITY])[cite: 5]
    if ok:[cite: 5]
        with frame_cond:[cite: 5]
            latest_jpeg = jpg.tobytes()[cite: 5]
            frame_cond.notify_all()[cite: 5]


@app.route('/video_feed')[cite: 5]
def video_feed():[cite: 5]
    def generate():[cite: 5]
        while True:[cite: 5]
            with frame_cond:[cite: 5]
                frame_cond.wait(timeout=5)[cite: 5]
                jpg = latest_jpeg[cite: 5]
            if jpg is None:[cite: 5]
                continue[cite: 5]
            yield (b'--frame\r\nContent-Type: image/jpeg\r\nContent-Length: '[cite: 5]
                   + str(len(jpg)).encode() + b'\r\n\r\n' + jpg + b'\r\n')[cite: 5]
    return Response(generate(), mimetype='multipart/x-mixed-replace; boundary=frame')[cite: 5]


def start_flask():[cite: 5]
    try:[cite: 5]
        app.run(host='0.0.0.0', port=STREAM_PORT, debug=False, use_reloader=False, threaded=True)[cite: 5]
    except OSError as error:[cite: 5]
        print(f"Khong bat duoc may chu video (cong {STREAM_PORT} dang ban?): {error}")[cite: 5]


flask_thread = threading.Thread(target=start_flask, daemon=True)[cite: 5]
flask_thread.start()[cite: 5]


# ==========================================
# 3. MQTT
# ==========================================
def on_connect(client, userdata, flags, reason_code, properties=None):[cite: 5]
    print(f"[MQTT] Ket noi broker {BROKER}: {reason_code}")[cite: 5]


def on_disconnect(client, userdata, *args):[cite: 5]
    print("[MQTT] Mat ket noi broker, dang tu ket noi lai...")[cite: 5]


client = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2)[cite: 5]
client.on_connect = on_connect[cite: 5]
client.on_disconnect = on_disconnect[cite: 5]
client.connect(BROKER, PORT, 60)[cite: 5]
client.loop_start()[cite: 5]


# ==========================================
# 4. GPIO (Raspberry Pi 5)
# ==========================================
h = lgpio.gpiochip_open(0)[cite: 5]

lgpio.gpio_claim_output(h, BUZZER_PIN)[cite: 5]
lgpio.gpio_write(h, BUZZER_PIN, 1)      # tat coi luc khoi dong (1 la tat, 0 la bat)[cite: 5]

dht_device = adafruit_dht.DHT11(DHT_PIN)[cite: 5]

lgpio.gpio_claim_input(h, MQ2_PIN)[cite: 5]


# ==========================================
# 5. CAMERA (LUONG RIENG, TU KET NOI LAI KHI ROT FRAME)
# ==========================================
class CameraReader(threading.Thread):[cite: 5]
    """Doc camera lien tuc o luong rieng va luon giu KHUNG MOI NHAT.[cite: 5]
    Tu dong mo lai camera neu webcam USB bi ngat giua chung."""

    def __init__(self):[cite: 5]
        super().__init__(daemon=True)[cite: 5]
        self._lock = threading.Lock()[cite: 5]
        self._frame = None[cite: 5]
        self._frame_id = 0[cite: 5]
        self.last_frame_time = time.time()[cite: 5]
        self.cap = self._open_cam()

    def _open_cam(self):
        cap = cv2.VideoCapture(CAM_INDEX, cv2.CAP_V4L2)
        cap.set(cv2.CAP_PROP_FOURCC, cv2.VideoWriter_fourcc(*'MJPG'))[cite: 5]
        cap.set(cv2.CAP_PROP_FRAME_WIDTH, CAM_WIDTH)[cite: 5]
        cap.set(cv2.CAP_PROP_FRAME_HEIGHT, CAM_HEIGHT)[cite: 5]
        cap.set(cv2.CAP_PROP_FPS, CAM_FPS)[cite: 5]
        cap.set(cv2.CAP_PROP_BUFFERSIZE, 1)[cite: 5]
        return cap

    def is_opened(self):[cite: 5]
        return self.cap is not None and self.cap.isOpened()[cite: 5]

    def run(self):[cite: 5]
        fail_count = 0
        while not stop_event.is_set():[cite: 5]
            if not self.is_opened():
                time.sleep(1)
                self.cap = self._open_cam()
                continue

            ok, frame = self.cap.read()[cite: 5]
            if not ok or frame is None:[cite: 5]
                fail_count += 1
                time.sleep(0.05)[cite: 5]
                if fail_count >= 40:  # ~2 giay mat tin hieu -> tu reset USB camera
                    print("[Camera] Mat tin hieu USB, dang tu ket noi lai...")
                    self.cap.release()
                    time.sleep(0.5)
                    self.cap = self._open_cam()
                    fail_count = 0
                continue[cite: 5]

            fail_count = 0
            with self._lock:[cite: 5]
                self._frame = frame[cite: 5]
                self._frame_id += 1[cite: 5]
                self.last_frame_time = time.time()[cite: 5]

    def get(self):[cite: 5]
        with self._lock:[cite: 5]
            return self._frame, self._frame_id[cite: 5]


# ==========================================
# 6. AI (LUONG RIENG + CHUP ANH BANG CHUNG)
# ==========================================
print("Dang tai mo hinh YOLO AI...")[cite: 5]
model = YOLO(MODEL_PATH)[cite: 5]


def ve_khung(img, boxes):[cite: 5]
    """Ve khung bang chinh bo ve cua ultralytics: mau theo lop, nhan co nen."""[cite: 5]
    ann = Annotator(img, line_width=2)[cite: 5]
    for x1, y1, x2, y2, label, k in boxes:[cite: 5]
        ann.box_label((x1, y1, x2, y2), label, color=colors(k, True))[cite: 5]
    return ann.result()[cite: 5]


def detector_loop(cam):[cite: 5]
    last_id = -1[cite: 5]
    min_interval = 1.0 / YOLO_MAX_FPS if YOLO_MAX_FPS else 0.0[cite: 5]
    fps_count = 0[cite: 5]
    fps_t0 = time.time()[cite: 5]

    while not stop_event.is_set():[cite: 5]
        t0 = time.time()[cite: 5]
        frame, fid = cam.get()[cite: 5]
        if frame is None or fid == last_id:[cite: 5]
            time.sleep(0.005)[cite: 5]
            continue[cite: 5]
        last_id = fid[cite: 5]

        try:[cite: 5]
            results = model(frame, verbose=False, conf=YOLO_CONF, imgsz=YOLO_IMGSZ)[cite: 5]
        except Exception as error:[cite: 5]
            print(f"[AI] Loi YOLO: {error}")[cite: 5]
            time.sleep(1)[cite: 5]
            continue[cite: 5]

        boxes = [][cite: 5]
        best_conf = 0.0[cite: 5]
        for r in results:[cite: 5]
            if len(r.boxes) == 0:[cite: 5]
                continue[cite: 5]
            xyxy = r.boxes.xyxy.cpu().numpy().astype(int)[cite: 5]
            confs = r.boxes.conf.cpu().numpy() * 100[cite: 5]
            clss = r.boxes.cls.cpu().numpy().astype(int)[cite: 5]
            for (x1, y1, x2, y2), c, k in zip(xyxy, confs, clss):[cite: 5]
                boxes.append((int(x1), int(y1), int(x2), int(y2),[cite: 5]
                              f"{model.names[int(k)]} {c / 100:.2f}", int(k)))[cite: 5]
                best_conf = max(best_conf, float(c))[cite: 5]

        now = time.time()[cite: 5]
        # Ve san anh co khung do ngay luc AI bat duoc lua de khong bi lech nhip
        annotated_fire = ve_khung(frame.copy(), boxes) if boxes else None

        with state_lock:[cite: 5]
            S.boxes = boxes[cite: 5]
            S.boxes_time = now[cite: 5]
            if boxes:[cite: 5]
                S.fire_streak += 1[cite: 5]
                if S.fire_streak >= FRAMES_XAC_NHAN:[cite: 5]
                    S.last_fire_time = now[cite: 5]
                    S.last_ai_conf = best_conf[cite: 5]
                    S.fire_snapshot = annotated_fire
                    S.fire_snapshot_time = now
                if best_conf >= CONF_RAT_CAO:[cite: 5]
                    if S.high_conf_since is None:[cite: 5]
                        S.high_conf_since = now[cite: 5]
                else:[cite: 5]
                    S.high_conf_since = None[cite: 5]
            else:[cite: 5]
                S.fire_streak = 0[cite: 5]
                S.high_conf_since = None[cite: 5]

        fps_count += 1[cite: 5]
        if now - fps_t0 >= 10:[cite: 5]
            print(f"[AI] YOLO ~{fps_count / (now - fps_t0):.1f} khung/giay")[cite: 5]
            fps_count = 0[cite: 5]
            fps_t0 = now[cite: 5]

        sleep_t = min_interval - (time.time() - t0)[cite: 5]
        if sleep_t > 0:[cite: 5]
            time.sleep(sleep_t)[cite: 5]


# ==========================================
# 7. DHT11 (LUONG RIENG)
# ==========================================
def dht_loop():[cite: 5]
    last_err_print = 0.0[cite: 5]
    while not stop_event.is_set():[cite: 5]
        try:[cite: 5]
            t = dht_device.temperature[cite: 5]
            hum = dht_device.humidity[cite: 5]
            if t is not None and hum is not None:[cite: 5]
                with state_lock:[cite: 5]
                    S.temperature, S.humidity, S.dht_time = t, hum, time.time()[cite: 5]
        except RuntimeError as error:[cite: 5]
            now = time.time()[cite: 5]
            with state_lock:[cite: 5]
                chua_doc_duoc = now - S.dht_time[cite: 5]
            if chua_doc_duoc > 5 and now - last_err_print >= 10:[cite: 5]
                print(f"[DHT11] Chua doc duoc: {error}")[cite: 5]
                last_err_print = now[cite: 5]
        except Exception as error:[cite: 5]
            print(f"[DHT11] Loi khac: {error}")[cite: 5]
        stop_event.wait(SENSOR_INTERVAL)[cite: 5]


# ==========================================
# 8. KHOI DONG
# ==========================================
cam = CameraReader()[cite: 5]
if not cam.is_opened():[cite: 5]
    print("LOI: Khong mo duoc camera! Co the dang bi chuong trinh khac dung.")[cite: 5]
cam.start()[cite: 5]

detector_thread = threading.Thread(target=detector_loop, args=(cam,), daemon=True)[cite: 5]
detector_thread.start()[cite: 5]

dht_thread = threading.Thread(target=dht_loop, daemon=True)[cite: 5]
dht_thread.start()[cite: 5]

print("He thong IoT + AI + Video Stream chuan bi chay...")[cite: 5]
time.sleep(2)[cite: 5]

last_stream_time = 0.0[cite: 5]
last_pushed_id = -1[cite: 5]
last_eval_time = 0.0[cite: 5]
last_smoke_time = 0.0[cite: 5]


# ==========================================
# 9. VONG LAP CHINH: STREAM + MQ-2 + DANH GIA CANH BAO + MQTT
# ==========================================
try:[cite: 5]
    while not stop_event.is_set():[cite: 5]
        now = time.time()[cite: 5]

        if now - cam.last_frame_time > CAMERA_TIMEOUT:[cite: 5]
            print("Loi: Camera mat ket noi qua lau!")[cite: 5]
            break[cite: 5]

        # Doc MQ-2 moi vong lap de khong bo lo khoi chi xuat hien thoang qua[cite: 5]
        if lgpio.gpio_read(h, MQ2_PIN) == 0:[cite: 5]
            last_smoke_time = now[cite: 5]

        # --- STREAM: khung moi nhat cua camera + khung vuong YOLO gan nhat ---[cite: 5]
        frame, fid = cam.get()[cite: 5]
        if frame is not None and fid != last_pushed_id and now - last_stream_time >= 1.0 / STREAM_FPS:[cite: 5]
            with state_lock:[cite: 5]
                boxes = S.boxes if (now - S.boxes_time) < BOX_TTL else [][cite: 5]
            if boxes:[cite: 5]
                img = ve_khung(frame.copy(), boxes)[cite: 5]
            else:[cite: 5]
                img = frame[cite: 5]
            push_frame(img)[cite: 5]
            last_pushed_id = fid[cite: 5]
            last_stream_time = now[cite: 5]

        # --- DANH GIA CANH BAO + MQTT (moi SENSOR_INTERVAL giay) ---[cite: 5]
        if now - last_eval_time >= SENSOR_INTERVAL:[cite: 5]
            last_eval_time = now[cite: 5]

            with state_lock:[cite: 5]
                last_fire_time = S.last_fire_time[cite: 5]
                ai_conf = S.last_ai_conf[cite: 5]
                high_since = S.high_conf_since[cite: 5]
                snap_fire = S.fire_snapshot if (now - S.fire_snapshot_time) <= GIU_LUA_GIAY else None
                if now - S.dht_time <= DHT_STALE_GIAY:[cite: 5]
                    temperature, humidity = S.temperature, S.humidity[cite: 5]
                else:[cite: 5]
                    temperature, humidity = None, None[cite: 5]

            co_lua = (now - last_fire_time) < GIU_LUA_GIAY[cite: 5]
            co_khoi = (now - last_smoke_time) < GIU_KHOI_GIAY[cite: 5]
            nhiet_cao = temperature is not None and temperature > NGUONG_NHIET_CAO[cite: 5]
            lua_rat_chac = (CHO_PHEP_LUA_DON_LE[cite: 5]
                            and high_since is not None[cite: 5]
                            and (now - high_since) >= THOI_GIAN_LUA_RAT_CHAC)[cite: 5]

            so_tin_hieu = int(co_lua) + int(co_khoi) + int(nhiet_cao)[cite: 5]
            nhiet_text = f"{temperature}C" if temperature is not None else "N/A"[cite: 5]

            # LOGIC CANH BAO DA CAP DO (SENSOR FUSION)[cite: 5]
            if so_tin_hieu >= 2 or lua_rat_chac:[cite: 5]
                muc_do = 2[cite: 5]
                lgpio.gpio_write(h, BUZZER_PIN, 0)      # bat coi hu[cite: 5]
                print(f"[MUC 2] BAO CHAY KHAN CAP! (Lua: {int(co_lua)} | Khoi: {int(co_khoi)} | "[cite: 5]
                      f"Nhiet: {nhiet_text} | Lua rat chac: {int(lua_rat_chac)})")[cite: 5]

            elif so_tin_hieu == 1:[cite: 5]
                muc_do = 1[cite: 5]
                lgpio.gpio_write(h, BUZZER_PIN, 1)[cite: 5]
                print(f"[MUC 1] Canh bao som! (Lua: {int(co_lua)} | Khoi: {int(co_khoi)} | Nhiet: {nhiet_text})")[cite: 5]

            else:[cite: 5]
                muc_do = 0[cite: 5]
                lgpio.gpio_write(h, BUZZER_PIN, 1)[cite: 5]
                print(f"[MUC 0] Binh thuong: Nhiet do {nhiet_text}, Do am {humidity}%")[cite: 5]

            # Dong goi anh bang chung Base64 neu dang co canh bao (muc_do >= 1)
            snapshot_b64 = None
            if muc_do > 0:
                img_to_save = snap_fire if snap_fire is not None else (frame.copy() if frame is not None else None)
                if img_to_save is not None:
                    small_img = cv2.resize(img_to_save, (SNAPSHOT_WIDTH, SNAPSHOT_HEIGHT))
                    ok_jpg, jpg_buf = cv2.imencode(".jpg", small_img, [cv2.IMWRITE_JPEG_QUALITY, SNAPSHOT_QUALITY])
                    if ok_jpg:
                        snapshot_b64 = base64.b64encode(jpg_buf.tobytes()).decode("ascii")

            # DAY LEN MQTT[cite: 5]
            if muc_do > 0 or temperature is not None:[cite: 5]
                payload = json.dumps({
                    "device_id": DEVICE_ID,[cite: 5]
                    "nhiet_do": temperature,[cite: 5]
                    "do_am": humidity,[cite: 5]
                    "co_khoi": int(co_khoi),[cite: 5]
                    "co_lua": int(co_lua),[cite: 5]
                    "muc_do": muc_do,[cite: 5]
                    "confidence": round(ai_conf, 2) if co_lua else None,[cite: 5]
                    "snapshot": snapshot_b64
                })
                client.publish(TOPIC, payload)[cite: 5]
                print(f"   -> da gui MQTT: {TOPIC}" + (" (kem anh)" if snapshot_b64 else ""))

        time.sleep(0.005)[cite: 5]

except KeyboardInterrupt:[cite: 5]
    print("\nDa nhan lenh tat he thong (Ctrl+C).")[cite: 5]
finally:[cite: 5]
    stop_event.set()[cite: 5]
    cam.join(timeout=2)[cite: 5]
    detector_thread.join(timeout=3)[cite: 5]
    dht_thread.join(timeout=3)[cite: 5]
    client.loop_stop()[cite: 5]
    client.disconnect()[cite: 5]
    lgpio.gpio_write(h, BUZZER_PIN, 1)[cite: 5]
    lgpio.gpiochip_close(h)[cite: 5]
    if cam.cap is not None:
        cam.cap.release()[cite: 5]
    cv2.destroyAllWindows()[cite: 5]
    dht_device.exit()[cite: 5]
    print("\nUng dung da dung hoat dong.")[cite: 5]