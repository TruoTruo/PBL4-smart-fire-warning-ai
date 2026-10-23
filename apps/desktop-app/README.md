# Desktop App & Tích hợp

## Trạng thái hiện tại

Đã có skeleton chạy được của dashboard WinForms tại `SmartFireApp/`. Giao diện dùng dữ liệu minh họa; các adapter MQTT/MySQL/SMS sẽ được tích hợp sau khi nhóm thống nhất contract.

## Nhiệm vụ
- Dashboard C# WinForms (Guna UI / MaterialSkin) / WebView2
- Tích hợp M2Mqtt để Subscribe cảnh báo từ Pi 5
- Gọi API gửi SMS khẩn cấp, viết báo cáo

## Cách chạy WinForms
Mở `SmartFireApp.sln` bằng Visual Studio hoặc chạy:

```powershell
dotnet run --project SmartFireApp/SmartFireApp.csproj
```

> Stack CSDL hiện tại của repo là **MySQL** (`MySqlConnector`), và broker là MQTT. Không dùng `System.Data.SqlClient` cho phần tích hợp mới.

---

## Kiến trúc Giao diện Giám sát (Web / WebView2 / Live Server)

Bộ giao diện HTML/CSS/JS mô phỏng bảng điều khiển trung tâm SOC FireGuard IoT đã được module hóa sạch sẽ thành các file riêng biệt:

### 1. Cấu trúc thư mục
- `css/style.css`: Quy chuẩn phong cách Dark Cyberpunk / Material Design 3, lưới CAD và hiệu ứng cảnh báo.
- `js/tailwind-config.js`: Định nghĩa bảng màu (tokens: primary, tertiary, secondary), font chữ và spacing.
- `js/data.js`: Dữ liệu mẫu (32 node cảm biến, tọa độ CAD, log sự cố) và API hook `window.FireGuardAPI`.
- `js/app.js`: Xử lý tương tác mặt bằng CAD (zoom, bật tắt lớp, chọn node), đồng hồ thời gian thực, chuông báo động, quy trình SOP và toast thông báo.

### 2. Danh sách các màn hình
- [dashboard.html](file:///c:/IT/Nam3_Ki1/PBL4/PBL4-smart-fire-warning-ai/apps/desktop-app/dashboard.html): Tổng quan tình trạng hệ thống, biểu đồ đa thông số nhiệt độ/khói/độ ẩm và KPI vận hành.
- [device-monitor.html](file:///c:/IT/Nam3_Ki1/PBL4/PBL4-smart-fire-warning-ai/apps/desktop-app/device-monitor.html): Mặt bằng xưởng CAD tương tác trực quan, thanh tra node cảm biến và danh sách 32 trạm IoT.
- [alert-center.html](file:///c:/IT/Nam3_Ki1/PBL4/PBL4-smart-fire-warning-ai/apps/desktop-app/alert-center.html): Trung tâm cảnh báo sự cố khẩn cấp cấp 1, quy trình phản ứng SOP 4 bước, đồ thị 60s và cổng dispatch SMS.
- [ai-analytics.html](file:///c:/IT/Nam3_Ki1/PBL4/PBL4-smart-fire-warning-ai/apps/desktop-app/ai-analytics.html): Phân tích xu hướng rủi ro AI, xếp hạng điểm nóng và tỷ lệ nhận diện chính xác.
- [history-reports.html](file:///c:/IT/Nam3_Ki1/PBL4/PBL4-smart-fire-warning-ai/apps/desktop-app/history-reports.html): Nhật ký sự kiện toàn khu, bộ lọc tìm kiếm theo zone/thời gian và trích xuất biên bản PCCC.
- [user-mgmt.html](file:///c:/IT/Nam3_Ki1/PBL4/PBL4-smart-fire-warning-ai/apps/desktop-app/user-mgmt.html): Quản lý cán bộ trực SOC, đội trưởng PCCC cơ động và phân quyền vận hành.
- [settings.html](file:///c:/IT/Nam3_Ki1/PBL4/PBL4-smart-fire-warning-ai/apps/desktop-app/settings.html): Cấu hình ngưỡng cảnh báo nhiệt độ/khói và kết nối MQTT Broker.

---

## Cấu trúc C# WinForms
- `Forms/` — giao diện Dashboard
- `Services/` — MqttAlertSubscriber, SmsService, DbService
- `Models/` — model dữ liệu tương ứng 5 bảng SQL
- `SmartFireApp.Tests/` — unit test (NUnit/xUnit)
