-- ============================================================
-- 002_smoke_alertlevel.sql  (MySQL 8.0+)
-- Hệ thống đọc MQ-2 ở dạng CÓ KHÓI / KHÔNG (ngõ ra số), không đo ppm.
--   1) Thêm SmokeDetected + AlertLevel vào EnvironmentLogs
--   2) Chuyển dòng dữ liệu mẫu cũ (có GasPpm) sang cột mới
--   3) Xóa cột GasPpm
--
-- CHẠY ĐÚNG 1 LẦN trên database ĐÃ CÓ SẴN.
-- Database tạo mới từ migrations/schema.sql thì đã có sẵn 2 cột này, KHÔNG chạy file này.
-- Chạy SAU KHI đã cập nhật code subscriber + desktop-app (code mới không còn dùng GasPpm).
-- ============================================================

USE SmartFireDB;

ALTER TABLE EnvironmentLogs
    ADD COLUMN SmokeDetected BOOLEAN NOT NULL DEFAULT FALSE AFTER GasPpm,
    ADD COLUMN AlertLevel    TINYINT NOT NULL DEFAULT 0     AFTER SmokeDetected;

-- Dòng mẫu cũ (có GasPpm): coi >= 500 là có khói; AI báo cháy thì mức 2
SET SQL_SAFE_UPDATES = 0;
UPDATE EnvironmentLogs
SET SmokeDetected = (GasPpm >= 500),
    AlertLevel    = CASE WHEN AiResult THEN 2 ELSE 0 END
WHERE GasPpm IS NOT NULL;
SET SQL_SAFE_UPDATES = 1;

ALTER TABLE EnvironmentLogs DROP COLUMN GasPpm;

-- Kiểm tra nhanh:
-- SELECT LogId, DeviceId, Temperature, SmokeDetected, AlertLevel, AiResult, ConfidenceScore
-- FROM EnvironmentLogs ORDER BY LogId DESC LIMIT 10;
