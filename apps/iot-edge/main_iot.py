import time
import json
import lgpio
import paho.mqtt.client as mqtt
# Cài thêm thư viện DHT: pip install adafruit-circuitpython-dht
import adafruit_dht
import board

# === CẤU HÌNH MQTT ===
BROKER = "test.mosquitto.org"
PORT = 1883
TOPIC = "pbl4/sensors/fire_warning"

client = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2)
client.connect(BROKER, PORT, 60)

# === CẤU HÌNH CHÂN GPIO (Raspberry Pi 5 dùng chip BCM2712) ===
# Khởi tạo handle cho lgpio
h = lgpio.gpiochip_open(0) 

# 1. Còi Buzzer (Chân 15 - GPIO 22): Cấu hình là Output
BUZZER_PIN = 22
lgpio.gpio_claim_output(h, BUZZER_PIN)

# 2. Cảm biến nhiệt độ DHT11 (Chân 11 - GPIO 17)
# Thư viện adafruit dùng board.D17 để trỏ tới GPIO 17
dht_device = adafruit_dht.DHT11(board.D17)

# 3. Cảm biến khói MQ-2 (Chân 13 - GPIO 27): Chờ mai hàn xong mới dùng
MQ2_PIN = 27
lgpio.gpio_claim_input(h, MQ2_PIN)

print("Hệ thống chuẩn bị chạy. Đang khởi động cảm biến...")
time.sleep(2)

try:
    while True:
        try:
            # --- ĐỌC DỮ LIỆU ---
            temperature = dht_device.temperature
            humidity = dht_device.humidity
            
            # Khởi tạo biến khói giả định cho hôm nay
            smoke_detected = 0
            
            
            mq2_state = lgpio.gpio_read(h, MQ2_PIN)
            smoke_detected = 1 if mq2_state == 0 else 0
            print("Gia tri THỰC TẾ của MQ-2:", mq2_state)

            # --- LOGIC CẢNH BÁO ---
            # Nếu nhiệt độ trên 45 độ C hoặc có khói -> Bật còi
            if temperature > 50 or smoke_detected == 1:
                lgpio.gpio_write(h, BUZZER_PIN, 0) # Bật còi hú
                print("⚠️ CẢNH BÁO CHÁY! CÒI ĐANG HÚ!")
            else:
                lgpio.gpio_write(h, BUZZER_PIN, 1) # Tắt còi

            # --- ĐẨY LÊN MQTT CHO APP C# ĐỌC ---
            payload = json.dumps({
                "nhiet_do": temperature,
                "do_am": humidity,
                "co_khoi": smoke_detected
            })
            
            client.publish(TOPIC, payload)
            print(f"Đã gửi dữ liệu: {payload}")

        except RuntimeError as error:
            # Lỗi đọc DHT11 rất hay xảy ra do độ trễ phần cứng, cứ bỏ qua và đọc lại
            print("Lỗi đọc DHT11, đang thử lại...", error.args[0])
            time.sleep(2)
            continue
        except Exception as error:
            dht_device.exit()
            raise error

        time.sleep(2) # Quét 2 giây 1 lần

except KeyboardInterrupt:
    print("\nĐã tắt hệ thống.")
    lgpio.gpio_write(h, BUZZER_PIN, 1) # Tắt còi trước khi thoát
    lgpio.gpiochip_close(h) # Giải phóng chân GPIO
    client.disconnect()
