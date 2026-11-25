# Database & Mạng lưới
Phụ trách: 

## Nhiệm vụ
- Khởi tạo MySQL 8 (5 bảng, xem sql/migrations/schema.sql)
- Thiết lập MQTT Broker (Eclipse Mosquitto)
- Viết truy vấn lưu trữ và truy xuất log dữ liệu

## Cấu trúc
- `sql/migrations/schema.sql` — nguồn chân lý duy nhất cho cấu trúc DB, mọi module khác (iot-edge, desktop-app, web-app) phải khớp với file này
- `sql/migrations/` — các thay đổi schema theo thời gian
- `mqtt-broker/mosquitto.conf` — cấu hình broker chạy local/demo
