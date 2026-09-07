# IoT & Phần cứng Edge
Phụ trách: 

## Nhiệm vụ
- Đấu nối GPIO Raspberry Pi 5 với MQ-2, DHT11, còi/đèn báo động
- Chương trình Python đa luồng: Frame Capture / AI Inference / Sensor Reading
- Đóng gói dữ liệu JSON, publish qua MQTT

## Cách chạy
```bash
cd apps/iot-edge
pip install -r requirements.txt
python src/main.py
```

## Cấu trúc
- `src/camera/` — thread đọc khung hình
- `src/sensors/` — đọc MQ-2, DHT11
- `src/alarm/` — điều khiển còi/đèn qua GPIO
- `src/mqtt/` — publisher gửi JSON lên broker
- `config/` — địa chỉ MQTT broker, chân GPIO (copy từ config.example.yaml)
