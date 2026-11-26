# IoT & Phần cứng Edge (Raspberry Pi 5)

Chương trình `main_iot.py` chạy trên Raspberry Pi 5:

- đọc camera USB, nhận diện lửa và khói bằng mô hình **YOLOv11n** (`best.pt`, 2 lớp: `0 = smoke`, `1 = fire`);
- đọc cảm biến DHT11 (nhiệt độ, độ ẩm) và MQ-2 (khói);
- hợp nhất 3 tín hiệu lửa / khói / nhiệt thành mức cảnh báo 0 – 1 – 2, bật còi ở mức 2;
- gửi kết quả qua MQTT và phát luồng video có khung nhận diện tại `http://<IP-Pi>:5000/video_feed`.

Mỗi việc chạy ở một luồng riêng: đọc camera, YOLO, đọc DHT11, máy chủ video (Flask). Vòng lặp chính đọc MQ-2,
đánh giá mức cảnh báo, điều khiển còi và gửi MQTT.

## 1. Đấu nối

| Thiết bị | Chân trên Pi 5 | Ghi chú |
|---|---|---|
| DHT11 (DATA) | **GPIO17** | VCC 3,3 V, nối thẳng |
| MQ-2 (DO, ngõ ra số) | **GPIO27** | **Active-low**: mức 0 = có khói. MQ-2 chạy 5 V nên DO đi qua mạch chuyển mức TXS0108E (B1 → A1) trước khi vào GPIO27 |
| Còi active buzzer (I/O) | **GPIO22** | **Active-low**: ghi 0 = kêu, 1 = tắt |
| Camera | USB, thiết bị số 0 (`/dev/video0`) | Đọc qua V4L2, MJPG 640 × 480 |

Sơ đồ chi tiết (nguồn, GND, chân OE của TXS0108E): `src/hardware_sensors/pin guide.txt`.

## 2. Mô hình `best.pt`

File trọng số không có trong Git. Tải `best.pt` từ thư mục Drive của nhóm `training/Cấu hình code train AI`,
rồi đặt **cạnh `main_iot.py`** (tức `apps/iot-edge/best.pt`). Chương trình tìm file theo thư mục chứa
`main_iot.py`, nên chạy từ thư mục nào cũng được. Thiếu file thì chương trình báo lỗi, chỉ chỗ cần đặt file và
thoát ngay, chưa mở camera hay GPIO. Thông tin về mô hình và cách huấn luyện: `apps/ai-vision/README.md`.

Chương trình đọc tên lớp từ `model.names`: lớp `fire` dùng cho cờ lửa, lớp `smoke` dùng cho cờ khói của camera.
Lúc khởi động, chương trình in danh sách lớp của mô hình. Nếu mô hình thiếu một trong hai lớp này, chương trình
in cảnh báo.

## 3. Cài đặt và chạy

```bash
sudo apt install swig liblgpio-dev        # cần trước khi pip cài lgpio
cd apps/iot-edge
pip install -r requirements.txt
MQTT_BROKER_HOST=192.168.137.191 DEVICE_ID=PI-01 python3 main_iot.py
```

Nhấn `Ctrl+C` để dừng. Khi dừng, chương trình tắt còi, gửi trạng thái `offline` và giải phóng GPIO, camera.

Biến môi trường (không bắt buộc):

| Biến | Mặc định | Ý nghĩa |
|---|---|---|
| `MQTT_BROKER_HOST` | `192.168.137.191` | IP máy chạy Mosquitto, phải trùng `MqttBroker:Host` trong `appsettings.json` của mqtt-subscriber, web-app. Dùng `localhost` nếu Mosquitto chạy ngay trên Pi |
| `MQTT_BROKER_PORT` | `1883` | Cổng MQTT |
| `DEVICE_ID` | `PI-01` | Mã thiết bị, **phải có trong bảng `Devices`** (MySQL) và không trùng giữa các Pi. Pi thứ hai dùng `PI-02` |

Các thông số khác (ngưỡng nhiệt, số khung xác nhận, FPS, cổng video…) là hằng số ở mục `0. CAU HINH CHUNG`
đầu file `main_iot.py`. Thư mục `config/` và `src/` là khung cũ, `main_iot.py` không đọc.

## 4. Mức cảnh báo

Ba tín hiệu:

- **Lửa**: YOLO thấy lớp `fire` ở ≥ 5 lần chạy liên tiếp (ngưỡng tin cậy 70 %), cờ giữ 3 s.
- **Khói**: MQ-2 báo khói (cờ giữ 6 s) **hoặc** YOLO thấy lớp `smoke` ở ≥ 5 lần chạy liên tiếp (cờ giữ 3 s).
- **Nhiệt**: DHT11 đo > 45 °C. Số đo cũ hơn 10 s thì coi như không có, cờ nhiệt = 0.

Thêm đường **"lửa rất chắc"**: lớp `fire` có độ tin cậy ≥ 85 % liên tục 5 s, và kết quả YOLO phải còn mới
(dưới 1 s). Camera treo hoặc YOLO dừng thì bộ đếm 5 s bị xóa.

Khi đếm tín hiệu, **mỗi cảm biến chỉ tính 1 phiếu**: camera (lửa **hoặc** khói camera), MQ-2, DHT11.
Camera thấy cả lửa lẫn khói vẫn chỉ là 1 tín hiệu, muốn lên mức 2 phải có thêm MQ-2 hoặc nhiệt cao
(trừ đường "lửa rất chắc"). Trường `co_khoi` gửi lên MQTT vẫn là MQ-2 **hoặc** camera để hiển thị.

| Camera (lửa hoặc khói) | MQ-2 | Nhiệt > 45 °C | Lửa rất chắc | **Mức** | Còi |
|:-:|:-:|:-:|:-:|:-:|:-:|
| 0 | 0 | 0 | 0 | **0** – bình thường | tắt |
| đúng 1 trong 3 | | | 0 | **1** – cảnh báo sớm | tắt |
| ≥ 2 trong 3 | | | bất kỳ | **2** – báo cháy | **bật** |
| bất kỳ | | | 1 | **2** – báo cháy | **bật** |

Mức cảnh báo được đánh giá và còi được điều khiển mỗi **0,5 s**. DHT11 vẫn đọc mỗi 2,5 s.

**Mất camera** (quá 5 s không có khung mới): chương trình **không dừng**. Cảm biến, còi và MQTT vẫn chạy, luồng
camera tự thử mở lại. Trong lúc mất camera, cờ lửa, cờ khói của camera và bộ đếm "lửa rất chắc" bị xóa, payload
gửi `camera_ok = false`. Mỗi lần mất hoặc có lại camera, log in một dòng. Luồng camera, YOLO hay DHT11 gặp lỗi
thì log lỗi rồi chạy tiếp. Nếu một luồng vẫn dừng, vòng lặp chính phát hiện và khởi động lại luồng đó.

## 5. MQTT

| | |
|---|---|
| Topic dữ liệu | `pbl4/sensors/fire_warning` |
| Tần suất | Mỗi 2,5 s (kể cả mức 0 và khi DHT11 lỗi, dùng làm heartbeat). Gửi ngay khi mức tăng |
| QoS | 1 khi `muc_do > 0`, 0 khi mức 0 |
| Topic trạng thái | `pbl4/status/<DEVICE_ID>`: `online` (retain) khi kết nối. Last will `offline` (retain): broker tự gửi khi Pi mất kết nối |

Broker chưa chạy lúc Pi khởi động thì chương trình vẫn chạy và tự kết nối lại (thử lại sau 1 s, giãn dần tới 30 s).

Payload JSON:

| Trường | Kiểu | Ý nghĩa |
|---|---|---|
| `device_id` | string | Mã thiết bị (`DEVICE_ID`) |
| `nhiet_do` | số hoặc `null` | Nhiệt độ °C, `null` khi DHT11 lỗi hoặc số đo cũ hơn 10 s |
| `do_am` | số hoặc `null` | Độ ẩm %, `null` như trên |
| `co_khoi` | 0/1 | Khói từ MQ-2 **hoặc** camera |
| `co_lua` | 0/1 | Lửa do YOLO xác nhận (chỉ lớp `fire`) |
| `muc_do` | 0/1/2 | Mức cảnh báo (bảng ở mục 4) |
| `confidence` | số hoặc `null` | Độ tin cậy (%) của box lửa lúc xác nhận, `null` khi `co_lua = 0` |
| `snapshot` | string hoặc `null` | Ảnh JPEG 480 × 360 mã hóa Base64, đã vẽ khung YOLO. Chỉ gửi khi mức tăng, hoặc tối đa 10 s một lần khi mức > 0. Các bản tin khác để `null` |
| `khoi_camera` | 0/1 | *(thêm mới, không bắt buộc)* Khói do camera thấy (lớp `smoke`) |
| `camera_ok` | true/false | *(thêm mới, không bắt buộc)* `false` khi quá 5 s không có khung hình mới |

Ví dụ:

```json
{"device_id": "PI-01", "nhiet_do": 31.0, "do_am": 60.0, "co_khoi": 1, "co_lua": 1, "muc_do": 2,
 "confidence": 88.4, "snapshot": "<base64>", "khoi_camera": 1, "camera_ok": true}
```

## 6. Log trên màn hình

```
[MUC 0] Binh thuong: Nhiet do 30C, Do am 60%
[MUC 1] Canh bao som! (Lua: 0 | Khoi: 1 | Khoi camera: 0 | Nhiet: 30C)
[MUC 2] BAO CHAY KHAN CAP! (Lua: 1 | Khoi: 1 | Khoi camera: 1 | Nhiet: 31C | Lua rat chac: 0)
   -> da gui MQTT: pbl4/sensors/fire_warning (kem anh)
[Camera] Mat camera (khong co khung moi qua 5 giay). Cam bien, coi va MQTT van chay, dang thu ket noi lai camera...
[Camera] Da co lai camera.
```
