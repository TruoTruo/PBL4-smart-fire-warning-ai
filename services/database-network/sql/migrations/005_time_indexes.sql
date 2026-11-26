-- ============================================================
-- 005_time_indexes.sql  (MySQL 8.0+)
-- Thêm chỉ mục theo thời gian cho 2 bảng ghi nhiều nhất:
--   EnvironmentLogs(RecordedAt)  -> bảng log mới nhất, lọc 24h
--   AlertHistory(AlertTime)      -> lịch sử cảnh báo, thống kê theo ngày
-- Chỉ mục cũ (DeviceId, thời gian) không dùng được khi lọc/sắp xếp cho MỌI thiết bị.
--
-- Chạy trên database ĐÃ CÓ SẴN, sau 002, 003, 004.
-- Database tạo mới từ migrations/schema.sql thì đã có sẵn 2 chỉ mục này.
-- Chạy lại nhiều lần cũng không lỗi (chỉ tạo khi chưa có).
-- ============================================================

SET NAMES utf8mb4;

USE SmartFireDB;

SET @sql := IF(
    (SELECT COUNT(*) FROM information_schema.STATISTICS
     WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'EnvironmentLogs' AND INDEX_NAME = 'IX_EnvironmentLogs_Time') = 0,
    'CREATE INDEX IX_EnvironmentLogs_Time ON EnvironmentLogs (RecordedAt DESC)',
    'SELECT ''IX_EnvironmentLogs_Time đã có, bỏ qua'' AS ThongBao');
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @sql := IF(
    (SELECT COUNT(*) FROM information_schema.STATISTICS
     WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'AlertHistory' AND INDEX_NAME = 'IX_AlertHistory_Time') = 0,
    'CREATE INDEX IX_AlertHistory_Time ON AlertHistory (AlertTime DESC)',
    'SELECT ''IX_AlertHistory_Time đã có, bỏ qua'' AS ThongBao');
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

-- Kiểm tra nhanh:
-- SHOW INDEX FROM EnvironmentLogs;
-- SHOW INDEX FROM AlertHistory;
-- EXPLAIN SELECT * FROM AlertHistory ORDER BY AlertTime DESC LIMIT 20;
