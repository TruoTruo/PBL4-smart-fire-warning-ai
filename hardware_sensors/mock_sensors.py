import time
import json
import random
import paho.mqtt.client as mqtt

# Cấu hình MQTT
BROKER = "broker.hivemq.com"
PORT = 1883
TOPIC = "pbl4/sensors/data"

# Khai báo Client tương thích bản paho-mqtt 2.0.0 trở lên
client = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2)
client.connect(BROKER, PORT, 60)
client.loop_start()

print("=== HỆ THỐNG GIẢ LẬP CẢM BIẾN PBL4 ĐANG CHẠY ===")
print(f"Đang đẩy dữ liệu lên topic: {TOPIC}")

try:
    while True:
        # Giả lập data
        temp = round(random.uniform(28.0, 32.0), 1)
        hum = round(random.uniform(60.0, 75.0), 1)
        smoke = 1 if random.randint(1, 100) > 95 else 0 
        
        # Đóng gói JSON
        payload = {
            "temperature": temp,
            "humidity": hum,
            "smoke_detected": bool(smoke)
        }
        
        client.publish(TOPIC, json.dumps(payload))
        print(f"Đã gửi: {payload}")
        
        time.sleep(2) # Cập nhật mỗi 2 giây

except KeyboardInterrupt:
    print("\nĐã dừng giả lập.")
    client.loop_stop()