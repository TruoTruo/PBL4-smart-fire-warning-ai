# Desktop App & Tích hợp

## Trạng thái hiện tại

Ứng dụng giám sát trung tâm SOC **SmartFireApp** xây dựng trên nền tảng **C# .NET 8 WinForms + Microsoft Edge WebView2** (Chromium Engine), kết nối trực tiếp 100% dữ liệu thực từ CSDL **MySQL (SmartFireDB / XAMPP)** và luồng cảnh báo từ **Raspberry Pi 5 qua MQTT**.

## Tính năng chính
- **Dashboard Trung Tâm:** Hiển thị thời gian thực 5 bảng CSDL (`Users`, `Zones`, `Devices`, `EnvironmentLogs`, `AlertHistory`).
- **Khung Camera AI Giám Sát Luồng Lửa:** Hỗ trợ xem trực tiếp qua Webcam máy tính / Camera IP (Raspberry Pi MJPEG stream), tự động vẽ Bounding Box ngọn lửa khi AI nhận diện cháy hoặc có cảnh báo.
- **Tương tác 2 chiều (Bi-directional IPC):** Xác nhận xử lý sự cố trực tiếp trên giao diện và đồng bộ lại vào MySQL.
- **Lắng nghe MQTT:** Nhận số đo nhiệt độ/khói/độ ẩm và cảnh báo cháy từ Raspberry Pi 5.

## Cách chạy ứng dụng
Mở terminal và chạy:

```powershell
dotnet run --project SmartFireApp/SmartFireApp.csproj
```

---

## Cấu trúc thư mục

### 1. Giao diện Web (WebView2)
- `dashboard.html`: Giao diện trung tâm hợp nhất tích hợp Camera AI, giám sát trạm, lịch sử sự cố, cán bộ trực ban và dữ liệu môi trường.
- `css/style.css`: Quy chuẩn phong cách Dark Cyberpunk, font chữ và thanh cuộn.
- `js/tailwind-config.js`: Bảng màu thiết kế Tailwind CSS (tokens: primary, tertiary, secondary).

### 2. Cấu trúc C# (.NET 8)
- `SmartFireApp/Program.cs`: Điểm khởi chạy ứng dụng.
- `SmartFireApp/Forms/DashboardForm.cs`: Cửa sổ Form WinForms tích hợp WebView2, cấp quyền camera, đồng bộ dữ liệu và nhận sự kiện WebMessage.
- `SmartFireApp/Models/DashboardModels.cs`: Định nghĩa cấu trúc Record khớp 100% với Schema MySQL và MQTT.
- `SmartFireApp/Services/DatabaseService.cs`: Truy vấn và cập nhật dữ liệu trực tiếp với MySQL (XAMPP / Docker).
- `SmartFireApp/Services/MqttAlertSubscriber.cs`: Lắng nghe Broker MQTT từ Raspberry Pi 5.
- `SmartFireApp.Tests/`: Thư mục kiểm thử tự động.
