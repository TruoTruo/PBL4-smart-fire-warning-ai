-- ============================================================
-- seed.sql (MySQL)
-- Dữ liệu mẫu để cả nhóm test ngay, không cần chờ dữ liệu thật
-- từ Raspberry Pi. Chạy sau khi đã chạy schema.sql.
-- ============================================================

USE SmartFireDB;

-- --- Users ---
INSERT INTO Users (Username, PasswordHash, FullName, PhoneNumber, Email, Role)
VALUES
    ('admin',       '$2a$hash_admin_placeholder', 'Đỗ Khánh Duy',       '0900000001', 'duy.admin@example.com', 'Admin'),
    ('supervisor1', '$2a$hash_user_placeholder',  'Nguyễn Văn Trường', '0900000002', 'truong@example.com',    'User');

-- --- Zones ---
INSERT INTO Zones (ZoneName, Description, ManagerUserId)
VALUES
    ('Phòng Lab tầng 3', 'Phòng thực hành, nhiều thiết bị điện', 1),
    ('Kho vật tư tầng 1', 'Kho chứa vật liệu dễ cháy',           1);

-- --- Devices ---
INSERT INTO Devices (DeviceId, DeviceName, ZoneId, DeviceType, Status, IpAddress)
VALUES
    ('PI-01', 'Node giám sát Lab 3', 1, 'Raspberry Pi 5', 'Active', '192.168.1.101'),
    ('PI-02', 'Node giám sát Kho 1', 2, 'Raspberry Pi 5', 'Active', '192.168.1.102');

-- --- EnvironmentLogs: mô phỏng log bình thường + log bất thường ---
INSERT INTO EnvironmentLogs (DeviceId, Temperature, Humidity, GasPpm, AiResult, ConfidenceScore, RecordedAt)
VALUES
    ('PI-01', 28.5, 55.0, 120.0, FALSE, NULL,  DATE_SUB(NOW(), INTERVAL 30 MINUTE)),
    ('PI-01', 29.0, 54.0, 130.0, FALSE, NULL,  DATE_SUB(NOW(), INTERVAL 20 MINUTE)),
    ('PI-01', 45.2, 30.0, 620.0, TRUE,  88.50, DATE_SUB(NOW(), INTERVAL 5 MINUTE)),  -- bất thường: nghi cháy
    ('PI-02', 26.0, 60.0, 100.0, FALSE, NULL,  DATE_SUB(NOW(), INTERVAL 15 MINUTE));

-- --- AlertHistory: cảnh báo được tạo từ dòng log bất thường ở trên ---
INSERT INTO AlertHistory (DeviceId, LogId, AlertType, Severity, AlertTime, SmsSent, Acknowledged)
VALUES
    ('PI-01',
     (SELECT LogId FROM (
         SELECT LogId FROM EnvironmentLogs WHERE DeviceId = 'PI-01' AND AiResult = TRUE ORDER BY LogId DESC LIMIT 1
     ) AS tmp),
     'Fire', 'High', DATE_SUB(NOW(), INTERVAL 5 MINUTE), TRUE, FALSE);

-- --- Kiểm tra nhanh sau khi seed ---
-- SELECT * FROM Users;
-- SELECT * FROM Zones;
-- SELECT * FROM Devices;
-- SELECT * FROM EnvironmentLogs ORDER BY RecordedAt DESC;
-- SELECT * FROM AlertHistory ORDER BY AlertTime DESC;