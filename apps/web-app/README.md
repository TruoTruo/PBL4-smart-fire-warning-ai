# web-app — Dashboard giám sát cảnh báo cháy (ASP.NET Core)

Trang web + REST API + SignalR, đọc CSDL `SmartFireDB` (MySQL) và nhận bản tin từ broker MQTT. Đây là giao diện
giám sát duy nhất của hệ thống. Code CSDL/MQTT nằm ở `Services/` (`DatabaseService`, `MqttAlertSubscriber`, `AppSettings`),
model ở `Models/DashboardModels.cs`.

## Chạy

```bash
cd apps/web-app
dotnet run            # mở http://localhost:5000 (máy khác trong LAN: http://<IP-máy-chủ>:5000)
```

Cấu hình: `appsettings.json` (hoặc `appsettings.Local.json` không commit, hoặc biến môi trường
`MqttBroker__Host`, `MqttBroker__Port`, `Database__ConnectionString`). Broker chưa chạy lúc khởi động
thì web vẫn chạy và tự thử kết nối lại (1 s, 2 s, 4 s ... tối đa 30 s).

## Tài khoản demo

Tạo sẵn trong `services/database-network/sql/migrations/seed.sql`:

| Tên đăng nhập | Mật khẩu    | Vai trò       |
|---------------|-------------|---------------|
| `admin`       | `123456`    | Admin         |
| `supervisor1` | `giamsat123` | User (giám sát viên) |

**Đổi mật khẩu ngay sau lần đăng nhập đầu tiên** (nút "Đổi mật khẩu" trên thanh tiêu đề).
Mật khẩu lưu dạng PBKDF2-SHA256, 210.000 vòng, salt ngẫu nhiên 16 byte: `pbkdf2$<vòng>$<salt Base64>$<hash Base64>`.
Tài khoản cũ còn hash SHA-256 không salt vẫn đăng nhập được và được băm lại bằng PBKDF2 ở lần đăng nhập đúng đầu tiên.
CSDL tạo từ seed cũ (hash `$2a$..._placeholder`) thì không đăng nhập được nữa: chạy lại `seed.sql` hoặc cập nhật cột `PasswordHash`.

## Phân quyền

- Chưa đăng nhập: chỉ thấy màn hình đăng nhập. Mọi API (trừ đăng nhập) và SignalR trả `401` dạng JSON.
- User: xem dashboard, xem ảnh bằng chứng và phiếu sự cố. Không thấy SĐT/email cán bộ trong bảng cán bộ
  (riêng SĐT người phụ trách khu vực thì mọi cán bộ đều thấy, để liên hệ khi có sự cố).
- Xác nhận cảnh báo và ghi kết quả phiếu sự cố: Admin, hoặc cán bộ phụ trách khu vực của cảnh báo;
  khu vực chưa phân công thì mọi cán bộ đều làm được. Người xác nhận lấy từ phiên đăng nhập.
- **Leo thang**: cảnh báo Mức 2 quá 5 phút, Mức 1 quá 15 phút chưa ai xác nhận thì "quá hạn", mọi cán bộ đều được
  xác nhận / ghi phiếu thay (nhật ký ghi rõ là xử lý do leo thang). Giờ so với giờ máy chủ web.
- Bảo trì: mọi cán bộ xem và ghi lần bảo trì; Admin đặt ngày lắp đặt, chu kỳ kiểm tra và xoá dòng bảo trì.
- Sơ đồ mặt bằng: mọi cán bộ xem; Admin tải ảnh, xoá ảnh, đặt vị trí trạm. Nhật ký hoạt động: chỉ Admin.
- Admin: thêm/sửa/xoá cán bộ, đổi vai trò, đặt lại mật khẩu; thêm/sửa/xoá trạm và khu vực, phân công người phụ trách.
  Không xoá, không tự đổi vai trò, không tự đặt lại mật khẩu của chính mình (tự đổi ở mục Đổi mật khẩu).
  Đổi vai trò hoặc đặt lại mật khẩu thì các phiên đang mở của cán bộ đó bị đăng xuất.
- Cookie `HttpOnly`, `SameSite=Strict`, hết hạn sau 8 giờ không dùng. Không bật CORS (trang và API cùng origin).
- Đăng nhập tối đa 20 lần/phút/IP.

| Phương thức | Đường dẫn | Quyền |
|---|---|---|
| POST | `/api/auth/login` | ai cũng gọi được |
| POST | `/api/auth/logout` | ai cũng gọi được |
| GET | `/api/auth/me` | đã đăng nhập |
| POST | `/api/auth/change-password` | đã đăng nhập |
| GET | `/api/dashboard` | đã đăng nhập |
| POST | `/api/alerts/acknowledge` `{ "alertId": 5 }` | Admin hoặc người phụ trách khu vực |
| GET | `/api/alerts/{id}` (phiếu sự cố: số đo gốc, khu vực, người phụ trách, kết quả xử lý) | đã đăng nhập |
| POST | `/api/alerts/{id}/resolve` `{ "resolution": "Handled\|FalseAlarm\|Test", "note": "..." }` | Admin hoặc người phụ trách khu vực |
| GET | `/api/alerts/{id}/snapshot` (ảnh JPEG) | đã đăng nhập |
| GET | `/api/alerts/history?fromDate=2026-11-20&toDate=2026-11-26&alertType=Fire` | đã đăng nhập |
| GET | `/api/alerts/report?fromDate=2026-11-20&toDate=2026-11-26` (hiệu quả xử lý: tổng hợp, theo khu vực, theo cán bộ, theo trạm) | đã đăng nhập |
| GET | `/api/environment/{deviceId}?minutes=60` (chuỗi nhiệt độ / độ ẩm) | đã đăng nhập |
| POST | `/api/devices` (thêm / sửa trạm, `status`: Active, Inactive, Maintenance) | Admin |
| DELETE | `/api/devices/{deviceId}` (xoá kèm nhật ký đo và cảnh báo của trạm) | Admin |
| POST | `/api/zones` `{ "zoneId": null, "zoneName": "...", "description": "...", "managerUserId": 2 }` (thêm / sửa khu vực) | Admin |
| DELETE | `/api/zones/{id}` (chỉ khi khu vực không còn trạm) | Admin |
| POST | `/api/auth/register` | Admin |
| POST | `/api/users/{id}` `{ "fullName", "phone", "email", "role" }` (sửa cán bộ, tên đăng nhập không đổi) | Admin |
| POST | `/api/users/{id}/reset-password` `{ "newPassword": "..." }` | Admin |
| DELETE | `/api/users/{id}` | Admin |
| GET | `/api/zones/{id}/floorplan` (ảnh sơ đồ mặt bằng) | đã đăng nhập |
| POST | `/api/zones/{id}/floorplan` `{ "imageBase64": "data:image/png;base64,..." }` (PNG/JPEG/WebP, tối đa 3 MB) | Admin |
| DELETE | `/api/zones/{id}/floorplan` | Admin |
| POST | `/api/devices/{id}/position` `{ "x": 30.5, "y": 40 }` (% ảnh; `null` = gỡ khỏi sơ đồ) | Admin |
| GET | `/api/devices/{id}/maintenance` (lịch sử bảo trì) | đã đăng nhập |
| POST | `/api/devices/{id}/maintenance` `{ "performedAt": "2026-11-27", "kind": "Inspection\|Clean\|Repair\|Replace", "note": "..." }` | đã đăng nhập |
| DELETE | `/api/maintenance/{id}` | Admin |
| POST | `/api/devices/{id}/maintenance-settings` `{ "installDate": "2026-06-01", "intervalDays": 90 }` | Admin |
| GET | `/api/activity?fromDate&toDate&userId&action=alert.` (nhật ký hoạt động, tối đa 500 dòng) | Admin |
| SignalR | `/hubs/firealert` (sự kiện `DashboardUpdated`) | đã đăng nhập |

## Camera

Bấm "Kết nối camera Pi" và nhập `http://<IP-Pi>:5000/video_feed` (mặc định lấy IP thiết bị trong CSDL).
Khung lửa/khói do Pi (YOLOv11n) vẽ sẵn vào ảnh, trang không vẽ thêm. Không nhận được khung hình mới
trong 6 giây (Pi treo camera, mất mạng) thì hiện "Mất tín hiệu camera" và tự thử lại mỗi 5 giây.
FPS hiển thị là số đo thật tại trình duyệt.

## Chạy không cần Internet

Thư viện giao diện đặt sẵn trong `wwwroot/lib`: Tailwind CSS 3.4.17 (bản chạy trên trình duyệt), SignalR 8.0.7 và
icon Material Symbols Outlined (Apache 2.0), chữ Be Vietnam Pro (SIL Open Font License). Demo trong LAN không có Internet vẫn hiện đúng giao diện.

## Âm thanh

Có cảnh báo mới Mức 1/Mức 2 thì trang phát tiếng bíp (Web Audio API). Còn cảnh báo Mức 2 quá hạn chưa xác nhận
(trong 12 giờ gần nhất, trạm đang bật báo động) thì cứ 2 phút nhắc lại một lần. Trình duyệt chỉ cho phát âm thanh
sau một lần bấm vào trang, nên nếu mở lại trang khi còn phiên thì bấm vào trang một lần.

## Các mục trên trang

- **Giám sát trực tiếp**: camera Pi, thẻ trạm, biểu đồ nhiệt độ / độ ẩm (canvas, không dùng thư viện):
  khi chọn trạm thì lấy số đo trong khoảng đã chọn từ CSDL, sau đó cộng dồn số đo mới (tối đa 240 điểm / trạm).
  Đường nét đứt đỏ là ngưỡng 45 °C của Pi; đường bị ngắt khi mất cảm biến (giá trị null) hoặc quá 60 giây không có số đo.
- **Lịch sử & thống kê**: lọc theo khoảng ngày (mặc định 7 ngày), loại, mức độ, trạng thái; biểu đồ số cảnh báo
  mỗi ngày; "Xuất CSV" (UTF-8 có BOM để Excel hiện đúng tiếng Việt, ô bắt đầu bằng `= + - @` được thêm `'` phía trước).
  Mục "Hiệu quả xử lý" (cùng khoảng ngày): tỉ lệ đã xác nhận, thời gian phản hồi trung bình / chậm nhất
  (từ lúc ghi cảnh báo đến lúc xác nhận), số xác nhận trong 5 phút, tỉ lệ báo nhầm theo phiếu sự cố;
  bảng theo khu vực (kể cả khu vực không có cảnh báo), theo cán bộ xác nhận và theo trạm
  (số cảnh báo cháy / khói / nhiệt độ cao, lấy từ stored procedure `sp_GetAlertStatistics`).
- **Cảnh báo quá hạn**: dòng tô đỏ nhạt, đưa lên đầu bảng, ghi "Quá hạn, chờ X phút"; dải trạng thái đếm số cảnh báo quá hạn.
- **Sơ đồ mặt bằng** (tab Giám sát): chọn khu vực để xem ảnh sơ đồ, mỗi trạm là một chấm màu theo mức
  (xanh bình thường, cam cảnh báo sớm, đỏ nhấp nháy báo cháy, xám đậm mất kết nối, xám nhạt tắt báo động).
  Có trạm báo cháy thì sơ đồ tự chuyển sang khu vực đó. Admin chọn trạm rồi bấm vào ảnh để đặt vị trí.
- **Bảo trì thiết bị**: hạn kiểm tra = lần bảo trì gần nhất (chưa có thì ngày lắp đặt) + chu kỳ; trạm quá hạn tô đỏ
  và hiện trên dải trạng thái. Bấm "Ghi bảo trì, lịch sử" để ghi lần kiểm tra / vệ sinh / sửa chữa / thay thế.
- **Nhật ký hoạt động** (Admin): đăng nhập (cả đăng nhập sai), xác nhận / ghi phiếu, sửa trạm, khu vực, sơ đồ,
  tài khoản, bảo trì; lọc theo ngày, cán bộ, nhóm thao tác.
- **Trạm & cán bộ**: bảng khu vực (mô tả, người phụ trách, số trạm, số cảnh báo chưa xác nhận); Admin thêm / sửa / xoá
  khu vực, trạm và cán bộ. Tài khoản giám sát viên không thấy phần quản lý trạm.
- **Phiếu sự cố**: bấm mã cảnh báo hoặc nút "Phiếu" để xem số đo cảm biến lúc báo, người phụ trách, người xác nhận,
  thời gian phản hồi; người có quyền ghi kết quả (Đã xử lý / Báo nhầm / Diễn tập) và ghi chú.
- **Trạng thái trạm**: trạm đang hoạt động mà quá 60 giây không có số đo mới (Pi gửi 2,5 giây/lần) hoặc Pi báo ngắt
  qua MQTT thì hiện "Mất kết nối" kèm thời điểm số đo cuối; dải trạng thái chuyển sang "MẤT KẾT NỐI TRẠM".
  Trạm mất kết nối khi đang báo cháy vẫn giữ báo cháy (ghi rõ là số đo cũ). Tuổi số đo tính theo giờ máy chủ web,
  nên web-app và mqtt-subscriber phải chạy cùng múi giờ.
- **Trạm bảo trì / ngừng hoạt động**: web tắt báo động của trạm (không phát âm thanh, dải trạng thái không chuyển đỏ,
  cảnh báo ghi nhãn "trạm bảo trì"); CSDL vẫn ghi số đo và cảnh báo làm bằng chứng, còi tại Pi vẫn do Pi điều khiển.
- Thanh tiêu đề có 3 trạng thái: CSDL, MQTT (máy chủ web có đang nối broker không), CAMERA (luồng đang xem
  và `camera_ok` do Pi báo qua MQTT).
