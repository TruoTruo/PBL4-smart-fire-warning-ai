-- ============================================================
-- 004_alert_snapshot.sql  (MySQL 8.0+)
-- Thêm cột SnapshotBase64 vào AlertHistory: ảnh bằng chứng JPEG
-- (mã hóa Base64) mà main_iot.py gửi kèm khi có cảnh báo.
-- mqtt-subscriber ghi cột này, desktop-app và web-app đọc để hiển thị.
--
-- Dùng MEDIUMTEXT vì ảnh 480x360 sau Base64 có thể vượt 64 KB của TEXT.
--
-- CHẠY ĐÚNG 1 LẦN trên database ĐÃ CÓ SẴN, sau 002 và 003.
-- Database tạo mới từ migrations/schema.sql thì đã có sẵn cột này, KHÔNG chạy file này.
-- ============================================================

USE SmartFireDB;

ALTER TABLE AlertHistory
    ADD COLUMN SnapshotBase64 MEDIUMTEXT NULL AFTER Severity;

-- Kiểm tra nhanh:
-- SELECT AlertId, DeviceId, AlertType, Severity, LENGTH(SnapshotBase64) AS SnapshotBytes
-- FROM AlertHistory ORDER BY AlertId DESC LIMIT 10;
