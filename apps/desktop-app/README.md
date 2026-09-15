# Desktop App & Tích hợp

## Trạng thái hiện tại

Đã có skeleton chạy được của dashboard WinForms tại `SmartFireApp/`. Giao diện dùng dữ liệu minh họa; các adapter MQTT/MySQL/SMS sẽ được tích hợp sau khi nhóm thống nhất contract.

## Nhiệm vụ
- Dashboard C# WinForms (Guna UI / MaterialSkin)
- Tích hợp M2Mqtt để Subscribe cảnh báo từ Pi 5
- Gọi API gửi SMS khẩn cấp, viết báo cáo

## Cách chạy
Mở `SmartFireApp.sln` bằng Visual Studio hoặc chạy:

```powershell
dotnet run --project SmartFireApp/SmartFireApp.csproj
```

> Stack CSDL hiện tại của repo là **MySQL** (`MySqlConnector`), và broker là MQTT. Không dùng `System.Data.SqlClient` cho phần tích hợp mới.

## Cấu trúc
- `Forms/` — giao diện Dashboard
- `Services/` — MqttAlertSubscriber, SmsService, DbService
- `Models/` — model dữ liệu tương ứng 5 bảng SQL
- `SmartFireApp.Tests/` — unit test (NUnit/xUnit)
