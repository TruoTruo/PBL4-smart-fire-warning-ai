# Database & Mạng lưới
Phụ trách: 

## Nhiệm vụ
- Khởi tạo MySQL 8 (5 bảng, xem sql/migrations/schema.sql)
- Thiết lập MQTT Broker (Eclipse Mosquitto)
- Viết truy vấn lưu trữ và truy xuất log dữ liệu
- `mqtt-subscriber`: nhận bản tin của Pi qua MQTT, ghi vào MySQL (đây là nơi **duy nhất** ghi `EnvironmentLogs` và `AlertHistory`)

## Cấu trúc
- `sql/migrations/schema.sql` — nguồn chân lý duy nhất cho cấu trúc DB, mọi module khác (iot-edge, web-app) phải khớp với file này
- `sql/migrations/seed.sql`, `stored_procedures.sql` — dữ liệu mẫu và stored procedure, chạy tự động khi tạo DB mới bằng Docker
- `sql/migrations/002_…` → `005_…` — các thay đổi schema theo thời gian, chỉ dùng cho DB **đã có sẵn** (xem mục Nâng cấp DB cũ)
- `mqtt-broker/mosquitto.conf` — cấu hình broker chạy local/demo
- `mqtt-subscriber/` — dịch vụ C# (.NET 8): `Program.cs`, `appsettings.json`, `Dockerfile`
- `../../infra/docker-compose.yml` — chạy MySQL + Mosquitto + mqtt-subscriber bằng một lệnh

## 1. Chạy toàn bộ bằng Docker Compose (khuyên dùng khi demo)

Cần Docker Desktop. Không cần cài .NET hay MySQL vào máy.

```bash
cd infra
docker compose up -d --build        # lần đầu: build image subscriber + tạo DB từ schema/seed/stored_procedures
docker compose ps                   # cả 3 container phải "Up", mysql và mqtt "healthy"
docker compose logs -f mqtt-subscriber
```

| Service | Container | Cổng trên máy host | Ghi chú |
|---|---|---|---|
| `mqtt-broker` | `smartfire-mqtt` | `1883` | Pi, web-app kết nối vào đây (IP máy chạy Docker, cổng 1883) |
| `mysql-db` | `smartfire-mysql` | `3307` | `Server=localhost;Port=3307;Database=SmartFireDB;Uid=root;Pwd=YourStrong@Passw0rd` |
| `mqtt-subscriber` | `smartfire-subscriber` | không mở cổng | tự nối tới `mqtt-broker:1883` và `mysql-db:3306` trong mạng compose |

Log subscriber khi chạy đúng:

```
Kết nối MQTT broker mqtt-broker:1883 (lần 1)...
Đã kết nối MQTT Broker mqtt-broker:1883
Đã subscribe topic: firealert/sensor-data/+ và pbl4/sensors/fire_warning
Bắt đầu lắng nghe... (Ctrl+C để dừng). Cooldown cảnh báo: 60s
```

Các lệnh hay dùng:

```bash
docker compose up -d --build mqtt-subscriber   # sửa Program.cs xong thì build lại riêng subscriber
docker compose restart mqtt-subscriber
docker compose down                            # dừng, GIỮ dữ liệu
docker compose down -v                         # dừng và XOÁ dữ liệu; lần up sau tạo DB mới từ schema.sql + seed.sql
```

Các file `.sql` chỉ được chạy khi volume `mysql-data` còn trống (lần đầu, hoặc sau `down -v`).
Thứ tự chạy: `1_schema.sql` → `2_seed.sql` → `3_stored_procedures.sql`. Các file `002`–`005` **không** được mount,
vì `schema.sql` đã có sẵn những thay đổi đó.

Múi giờ: `mysql-db` và `mqtt-subscriber` đều đặt `TZ=Asia/Ho_Chi_Minh`, nên `NOW()` của MySQL và giờ ghi
`RecordedAt` / `AlertTime` cùng là giờ Việt Nam, khớp với giờ hiển thị trên web-app.

## 2. Chạy subscriber bằng `dotnet run` (khi sửa code)

Cần .NET 8 SDK.

```bash
cd infra
docker compose up -d mqtt-broker mysql-db      # chỉ chạy broker + MySQL
docker compose stop mqtt-subscriber            # nếu đang chạy bản Docker: tắt đi, kẻo 2 subscriber ghi trùng 2 lần

cd ../services/database-network/mqtt-subscriber
dotnet run
```

`appsettings.json` đang trỏ broker tới `192.168.137.191`. Chạy trên máy của mình thì **không sửa file này** mà tạo
`appsettings.Local.json` (đã nằm trong `.gitignore`, không commit) để ghi đè, ví dụ:

```json
{
  "MqttBroker": { "Host": "localhost", "Port": 1883 },
  "Database": { "ConnectionString": "Server=localhost;Port=3307;Database=SmartFireDB;Uid=root;Pwd=YourStrong@Passw0rd;CharSet=utf8mb4;" }
}
```

Hoặc dùng biến môi trường (dấu `:` đổi thành `__`):

```bash
# macOS / Linux
MqttBroker__Host=localhost dotnet run
# Windows PowerShell
$env:MqttBroker__Host="localhost"; dotnet run
```

## 3. Cấu hình subscriber

Thứ tự ưu tiên (sau ghi đè trước): `appsettings.json` → `appsettings.Local.json` → biến môi trường.

| Khóa trong appsettings | Biến môi trường | Mặc định | Ý nghĩa |
|---|---|---|---|
| `MqttBroker:Host` | `MqttBroker__Host` | `192.168.137.191` (compose: `mqtt-broker`) | địa chỉ broker |
| `MqttBroker:Port` | `MqttBroker__Port` | `1883` | cổng broker |
| `Database:ConnectionString` | `Database__ConnectionString` | `localhost:3307` (compose: `mysql-db:3306`) | chuỗi kết nối MySQL, bắt buộc có |
| `AlertThresholds:CooldownSeconds` | `AlertThresholds__CooldownSeconds` | `60` | chống ghi trùng cảnh báo (giây) |
| `AlertThresholds:TemperatureC` | `AlertThresholds__TemperatureC` | `40` (trong code: `45`) | chỉ dùng khi bản tin **không** có `muc_do`/`alert_level` |
| `Legacy:DefaultDeviceId` | `Legacy__DefaultDeviceId` | `PI-01` | thiết bị gán cho bản tin không có `device_id` |

## 4. Thứ tự khởi động không còn quan trọng

- Broker chưa chạy hoặc bị tắt giữa chừng: subscriber **không thoát**, tự thử kết nối lại mãi
  (chờ 2, 4, 8, 16 rồi 30 giây giữa các lần, mỗi lần đều in log). Broker lên là tự kết nối và subscribe lại.
- MySQL chưa sẵn sàng: bản tin lúc đó không ghi được (log lỗi, lỗi giống nhau chỉ in 30 giây/lần), subscriber vẫn chạy
  và ghi tiếp khi MySQL lên. Với compose, subscriber còn đợi MySQL và broker "healthy" rồi mới chạy.
- Container có `restart: unless-stopped`: Docker Desktop khởi động lại là cả 3 service tự chạy.

## 5. Subscriber xử lý bản tin của Pi thế nào

Topic nghe: `pbl4/sensors/fire_warning` (Pi đang dùng) và `firealert/sensor-data/+`.
Topic trạng thái `pbl4/status/<DEVICE_ID>` ("online"/"offline") **không** được subscribe.

- Trường nhận được: `device_id`, `nhiet_do`, `do_am`, `co_khoi`, `co_lua`, `muc_do`, `confidence`, `snapshot`,
  và 2 trường tùy chọn `khoi_camera` (0/1), `camera_ok` (true/false). Hai trường tùy chọn chỉ in ra log, không lưu CSDL.
  Cờ nhận cả dạng `0/1` lẫn `true/false`.
- Heartbeat (Pi gửi mỗi 2,5 s, kể cả khi DHT11 lỗi): `nhiet_do`/`do_am` là `null` thì ghi `NULL` vào `EnvironmentLogs`.
  Log không in mỗi bản tin một dòng: chỉ in khi trạng thái đổi (mức, mất/có DHT, khói, lửa, camera) hoặc 60 giây/lần,
  kèm số bản tin đã ghi mà không in.
- Mỗi bản tin dùng **một** mốc giờ cho cả `EnvironmentLogs.RecordedAt` và `AlertHistory.AlertTime`.
- `muc_do` 2 → `Fire`/`High`; `muc_do` 1 → `Fire` (camera thấy lửa), `Smoke` (MQ-2 hoặc camera thấy khói) hoặc
  `HighTemperature`, mức `Medium`; `muc_do` 0 → không cảnh báo.
- Chống trùng: cùng thiết bị + loại + mức độ chỉ ghi 1 dòng `AlertHistory` trong 60 giây. Mức **tăng** (Medium → High)
  luôn được ghi ngay; mức thấp hơn mức vừa ghi thì bỏ qua.
- Ảnh bằng chứng: Pi chỉ gửi `snapshot` khi mức tăng hoặc tối đa 10 giây/lần. Dòng cảnh báo mà bản tin không kèm ảnh
  sẽ dùng ảnh gần nhất **của cùng đợt cảnh báo** (từ lúc `muc_do` rời 0). `muc_do` về 0 thì ảnh cũ bị bỏ.
- Sau khi ghi cảnh báo, subscriber publish `firealert/alarm/<DEVICE_ID>` cho web-app.

Thử nhanh không cần Pi:

```bash
docker exec -i smartfire-mqtt mosquitto_pub -t pbl4/sensors/fire_warning \
  -m '{"device_id":"PI-01","nhiet_do":48,"do_am":40,"co_khoi":1,"co_lua":1,"muc_do":2,"confidence":92,"snapshot":null,"khoi_camera":0,"camera_ok":true}'
docker exec smartfire-mysql mysql -uroot -p'YourStrong@Passw0rd' SmartFireDB \
  -e 'SELECT AlertId, DeviceId, AlertType, Severity, AlertTime FROM AlertHistory ORDER BY AlertId DESC LIMIT 5;'
```

## 6. Nâng cấp DB cũ (đã tạo trước đây, không muốn xoá dữ liệu)

Chạy lần lượt các file chưa chạy, theo đúng thứ tự `002` → `003` → `004` → `005` → `006`
(`005_time_indexes.sql` thêm chỉ mục theo thời gian, `006_alert_resolution.sql` thêm cột kết quả
xử lý cho phiếu sự cố; hai file này chạy lại nhiều lần cũng không lỗi):

```bash
docker exec -i smartfire-mysql mysql -uroot -p'YourStrong@Passw0rd' SmartFireDB < services/database-network/sql/migrations/005_time_indexes.sql
docker exec -i smartfire-mysql mysql -uroot -p'YourStrong@Passw0rd' --default-character-set=utf8mb4 SmartFireDB < services/database-network/sql/migrations/006_alert_resolution.sql
```

Muốn làm lại từ đầu thì đơn giản hơn: `docker compose down -v` rồi `docker compose up -d --build`.
