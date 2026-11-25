-- ============================================================
-- 003_smoke_level.sql  (MySQL 8.0+)
-- Thêm cột SmokeLevel: mức khói/gas (%) đọc từ MQ-2 qua MCP3008.
-- NULL nghĩa là Pi chỉ đọc chân DO (chỉ biết có / không có khói).
--
-- CHẠY ĐÚNG 1 LẦN, và phải chạy SAU 002_smoke_alertlevel.sql
-- (nếu DB chưa chạy 002 thì chạy 002 trước, rồi mới chạy file này).
-- Database tạo mới từ migrations/schema.sql thì đã có sẵn cột này, KHÔNG chạy file này.
-- ============================================================

USE SmartFireDB;

ALTER TABLE EnvironmentLogs
    ADD COLUMN SmokeLevel DECIMAL(5,2) NULL AFTER SmokeDetected;

-- Kiểm tra nhanh:
-- SELECT LogId, DeviceId, SmokeDetected, SmokeLevel, AlertLevel FROM EnvironmentLogs ORDER BY LogId DESC LIMIT 10;
