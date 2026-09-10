# Desktop App & Tích hợp
Phụ trách: 

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
