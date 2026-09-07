#!/usr/bin/env bash
# setup_project.sh
# Scaffold cấu trúc thư mục cho dự án PBL4-smart-fire-warning-ai
# Chạy: bash setup_project.sh  (tại thư mục gốc của repo đã git clone về)

set -e

echo "==> Tạo cấu trúc thư mục..."

# --- .github ---
mkdir -p .github/workflows

# --- apps/ai-vision (Nguyên) ---
mkdir -p apps/ai-vision/src/utils
mkdir -p apps/ai-vision/tests
mkdir -p apps/ai-vision/config
mkdir -p apps/ai-vision/datasets/raw
mkdir -p apps/ai-vision/datasets/processed
mkdir -p apps/ai-vision/datasets/annotations
mkdir -p apps/ai-vision/models

# --- apps/iot-edge (Huy) ---
mkdir -p apps/iot-edge/src/camera
mkdir -p apps/iot-edge/src/sensors
mkdir -p apps/iot-edge/src/alarm
mkdir -p apps/iot-edge/src/mqtt
mkdir -p apps/iot-edge/tests
mkdir -p apps/iot-edge/config

# --- apps/desktop-app (Trường) ---
mkdir -p apps/desktop-app/SmartFireApp/Forms
mkdir -p apps/desktop-app/SmartFireApp/Services
mkdir -p apps/desktop-app/SmartFireApp/Models
mkdir -p apps/desktop-app/SmartFireApp.Tests

# --- services/database-network (Duy) ---
mkdir -p services/database-network/sql/migrations
mkdir -p services/database-network/mqtt-broker
mkdir -p services/database-network/scripts

# --- infra ---
mkdir -p infra

# --- docs (dùng chung) ---
mkdir -p docs/proposal
mkdir -p docs/slides
mkdir -p docs/diagrams
mkdir -p docs/reports

echo "==> Tạo file placeholder (.gitkeep) cho các thư mục rỗng..."
for d in \
  apps/ai-vision/datasets/raw apps/ai-vision/datasets/processed apps/ai-vision/datasets/annotations \
  apps/ai-vision/models apps/ai-vision/tests apps/ai-vision/config \
  apps/iot-edge/tests apps/iot-edge/config \
  apps/desktop-app/SmartFireApp.Tests \
  services/database-network/scripts \
  docs/proposal docs/slides docs/diagrams docs/reports
do
  touch "$d/.gitkeep"
done

echo "==> Tạo README.md cho từng module..."

cat > apps/ai-vision/README.md <<'EOF'
# AI & Thị giác máy tính
Phụ trách: Bùi Đình Phước Nguyên

## Nhiệm vụ
- Thu thập & gán nhãn dataset (Roboflow Fire & Smoke + negative dataset tự thu thập)
- Huấn luyện YOLOv8, export .pt / .onnx tối ưu cho ARM (Raspberry Pi 5)
- Xử lý nhiễu quang học (đèn LED, phản quang, áo cam/đỏ)

## Cách chạy
```bash
cd apps/ai-vision
pip install -r requirements.txt
python src/train.py        # huấn luyện
python src/detect.py        # inference realtime
```

## Cấu trúc
- `datasets/` — ảnh + nhãn (không commit ảnh thật, xem .gitignore)
- `models/` — weights .pt/.onnx (không commit, quá nặng)
- `src/` — code train/detect/export
- `tests/` — unit test cho src/
- `config/` — file cấu hình (copy từ config.example.yaml)
EOF
touch apps/ai-vision/requirements.txt
cat > apps/ai-vision/config/config.example.yaml <<'EOF'
model_path: "models/best.onnx"
confidence_threshold: 0.7
camera_source: 0
EOF

cat > apps/iot-edge/README.md <<'EOF'
# IoT & Phần cứng Edge
Phụ trách: Nguyễn Quang Huy

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
EOF
touch apps/iot-edge/requirements.txt
cat > apps/iot-edge/config/config.example.yaml <<'EOF'
mqtt_broker_host: "localhost"
mqtt_broker_port: 1883
gpio_buzzer_pin: 17
gpio_led_pin: 27
EOF

cat > apps/desktop-app/README.md <<'EOF'
# Desktop App & Tích hợp
Phụ trách: Nguyễn Văn Trường

## Nhiệm vụ
- Dashboard C# WinForms (Guna UI / MaterialSkin)
- Tích hợp M2Mqtt để Subscribe cảnh báo từ Pi 5
- Gọi API gửi SMS khẩn cấp, viết báo cáo

## Cách chạy
Mở `SmartFireApp.sln` bằng Visual Studio, restore NuGet packages (M2Mqtt, System.Data.SqlClient), F5 để chạy.

## Cấu trúc
- `Forms/` — giao diện Dashboard
- `Services/` — MqttSubscriber, SmsService, DbService
- `Models/` — model dữ liệu tương ứng 5 bảng SQL
- `SmartFireApp.Tests/` — unit test (NUnit/xUnit)
EOF

cat > services/database-network/sql/schema.sql <<'EOF'
-- Schema chuẩn hóa 3NF: 5 bảng
-- TaiKhoan, KhuVuc, ThietBi, NhatKyMoiTruong, LichSuCanhBao
-- TODO: định nghĩa CREATE TABLE tại đây (nguồn chân lý duy nhất cho cả nhóm)
EOF
cat > services/database-network/README.md <<'EOF'
# Database & Mạng lưới
Phụ trách: Đỗ Khánh Duy

## Nhiệm vụ
- Khởi tạo SQL Server (5 bảng, xem sql/schema.sql)
- Thiết lập MQTT Broker (Eclipse Mosquitto)
- Viết truy vấn lưu trữ và truy xuất log dữ liệu

## Cấu trúc
- `sql/schema.sql` — nguồn chân lý duy nhất cho cấu trúc DB, mọi module khác (iot-edge, desktop-app) phải khớp với file này
- `sql/migrations/` — các thay đổi schema theo thời gian
- `mqtt-broker/mosquitto.conf` — cấu hình broker chạy local/demo
EOF

cat > infra/docker-compose.yml <<'EOF'
# Hạ tầng chạy local cho demo: SQL Server + MQTT Broker
version: "3.8"
services:
  mqtt-broker:
    image: eclipse-mosquitto:2
    ports:
      - "1883:1883"
    volumes:
      - ../services/database-network/mqtt-broker/mosquitto.conf:/mosquitto/config/mosquitto.conf

  sql-server:
    image: mcr.microsoft.com/mssql/server:2022-latest
    environment:
      ACCEPT_EULA: "Y"
      SA_PASSWORD: "YourStrong@Passw0rd"
    ports:
      - "1433:1433"
EOF

# --- .github ---
cat > .github/PULL_REQUEST_TEMPLATE.md <<'EOF'
## Mô tả thay đổi

## Module bị ảnh hưởng
- [ ] ai-vision
- [ ] iot-edge
- [ ] desktop-app
- [ ] database-network
- [ ] docs

## Checklist
- [ ] Đã test trên máy cá nhân
- [ ] Không commit file dataset/model/bin nặng
EOF

cat > .github/workflows/ci.yml <<'EOF'
name: CI
on: [push, pull_request]
jobs:
  python-lint:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-python@v5
        with:
          python-version: "3.11"
      - name: Lint ai-vision & iot-edge
        run: |
          pip install flake8
          flake8 apps/ai-vision/src apps/iot-edge/src --max-line-length=120 || true
EOF

# --- root files ---
cat > .env.example <<'EOF'
# Copy file này thành .env và điền giá trị thật (KHÔNG commit .env)
MQTT_BROKER_HOST=localhost
MQTT_BROKER_PORT=1883
SQL_CONNECTION_STRING=Server=localhost;Database=SmartFireDB;User Id=sa;Password=YourStrong@Passw0rd;
SMS_API_KEY=your_sms_api_key_here
EOF

cat > .gitignore <<'EOF'
# Datasets & models (quá nặng cho Git)
apps/ai-vision/datasets/raw/*
apps/ai-vision/datasets/processed/*
apps/ai-vision/models/*.pt
apps/ai-vision/models/*.onnx
!**/.gitkeep

# Python
__pycache__/
*.pyc
.venv/
venv/

# C# / .NET
apps/desktop-app/**/bin/
apps/desktop-app/**/obj/
*.user

# Secrets
.env

# OS
.DS_Store
Thumbs.db
EOF

cat > CONTRIBUTING.md <<'EOF'
# Quy tắc đóng góp

## Branch
- `main` — bản ổn định, chỉ merge từ `develop` sau khi review
- `develop` — nhánh tích hợp chung
- `feature/ai-vision`, `feature/iot-edge`, `feature/database-network`, `feature/desktop-app` — mỗi thành viên làm việc trên nhánh của mình, chỉ commit vào thư mục module tương ứng (+ docs/ nếu cần)

## Quy trình
1. Checkout nhánh feature của bạn, kéo mới nhất từ `develop`
2. Code + test trong đúng thư mục `apps/<module>` hoặc `services/<module>`
3. Tạo Pull Request vào `develop`, điền theo template
4. Ít nhất 1 thành viên khác review trước khi merge

## Quy tắc đặt tên commit
`<module>: <mô tả ngắn>` — ví dụ: `ai-vision: thêm script export ONNX`
EOF

echo "==> Hoàn tất! Cấu trúc thư mục đã được tạo."
echo "    Tiếp theo:"
echo "    1. Di chuyển file docx/pptx hiện có vào docs/proposal/ và docs/slides/"
echo "    2. Điền sql/schema.sql với 5 bảng đã thiết kế"
echo "    3. git add . && git commit -m 'chore: scaffold project structure' && git push"