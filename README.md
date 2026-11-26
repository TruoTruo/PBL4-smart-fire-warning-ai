# Hệ thống cảnh báo sớm cháy nổ thông minh ứng dụng AI và đa cảm biến

Đồ án PBL4 – Nhóm 1. Raspberry Pi 5 nhận diện lửa, khói bằng YOLOv11n và kết hợp cảm biến DHT11, MQ-2 để đưa ra
3 mức cảnh báo. Dữ liệu gửi qua MQTT, lưu vào MySQL và hiển thị trên trang web giám sát.

```
Pi 5 (camera USB, DHT11, MQ-2, còi) ──MQTT──► Mosquitto ──► mqtt-subscriber ──► MySQL ◄── web-app (ASP.NET Core + SignalR)
   └──────────────── luồng video MJPEG :5000/video_feed ──────────────────────────────────► trình duyệt
```

| Thư mục | Thành phần | Ngôn ngữ |
|---|---|---|
| `apps/iot-edge` | Chương trình chạy trên Pi: camera, YOLOv11n, cảm biến, còi, MQTT, luồng video | Python |
| `apps/ai-vision` | Huấn luyện, đánh giá mô hình, kiểm tra rò rỉ dữ liệu; kết quả huấn luyện `best.pt` | Python |
| `services/database-network` | Broker Mosquitto, dịch vụ ghi CSDL `mqtt-subscriber`, CSDL MySQL (schema, seed) | C# .NET 8, SQL |
| `apps/web-app` | Trang web giám sát: đăng nhập, camera, cảnh báo, lịch sử, biểu đồ, quản lý trạm | C# .NET 10, HTML/JS |
| `infra` | `docker-compose.yml` chạy MySQL + Mosquitto + mqtt-subscriber | |
| `docs` | Sơ đồ CSDL (`docs/erd/smartfiredb.dbml`) | |

## Chạy demo

Cần: Docker Desktop, .NET SDK 10 (cho web-app), Raspberry Pi 5 đã cài thư viện trong `apps/iot-edge/requirements.txt`.
Máy chủ và Pi cùng một mạng LAN.

1. **Hạ tầng** (MySQL cổng 3307, Mosquitto cổng 1883, dịch vụ ghi CSDL):

   ```bash
   cd infra
   docker compose up -d --build
   ```

   Lần đầu MySQL tự tạo CSDL từ `schema.sql`, `seed.sql`, `stored_procedures.sql`.

2. **Trang web** (trên máy chủ):

   ```bash
   cd apps/web-app
   dotnet run
   ```

   Mở `http://localhost:5000`, đăng nhập bằng tài khoản demo trong `apps/web-app/README.md` và đổi mật khẩu ngay.
   Máy khác trong LAN mở `http://<IP-máy-chủ>:5000`.

3. **Mô hình**: tải `best.pt` (xem `apps/ai-vision/README.md`) và đặt cạnh `apps/iot-edge/main_iot.py` trên Pi.

4. **Pi**:

   ```bash
   cd apps/iot-edge
   MQTT_BROKER_HOST=<IP-máy-chủ> DEVICE_ID=PI-01 python3 main_iot.py
   ```

   `DEVICE_ID` phải có trong bảng `Devices` (seed có sẵn `PI-01`, `PI-02`).

5. **Xem camera**: trên trang web bấm "Kết nối camera Pi", nhập `http://<IP-Pi>:5000/video_feed`.

Thứ tự bật không quan trọng: Pi, `mqtt-subscriber` và web đều tự kết nối lại khi broker chưa chạy hoặc khởi động lại.

## Mức cảnh báo

Mỗi cảm biến được tính 1 phiếu: camera (lửa hoặc khói), MQ-2, DHT11 (> 45 °C).

| Số tín hiệu | Mức | Còi tại Pi |
|---|---|---|
| 0 | 0 – bình thường | tắt |
| 1 | 1 – cảnh báo sớm | tắt |
| ≥ 2, hoặc lửa ≥ 85 % liên tục 5 s | 2 – báo cháy | bật |

Chi tiết: `apps/iot-edge/README.md`.

## Kiểm tra

- `python -m pytest apps/ai-vision/tests -q`: kiểm thử bộ tính IoU, AP, ma trận nhầm lẫn, dHash.
- CI (`.github/workflows/ci.yml`) chạy kiểm thử Python và build `mqtt-subscriber`, `web-app` ở mỗi lần push và pull request.

## Quy trình làm việc

Xem `CONTRIBUTING.md`: làm trên nhánh `feature/...`, tạo pull request vào `develop`; `main` chỉ nhận bản đã ổn định.
