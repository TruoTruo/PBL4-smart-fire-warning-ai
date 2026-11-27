-- ============================================================
-- 006_alert_resolution.sql  (MySQL 8.0+)
-- Phiếu sự cố: ghi kết quả xử lý của từng cảnh báo.
--   Resolution      'Handled'    = đã xử lý sự cố thật
--                   'FalseAlarm' = báo nhầm
--                   'Test'       = diễn tập / thử hệ thống
--                   NULL         = chưa ghi kết quả
--   ResolutionNote  ghi chú của cán bộ (tối đa 500 ký tự)
--
-- Chạy trên database ĐÃ CÓ SẴN, sau 002 → 005.
-- Database tạo mới từ migrations/schema.sql thì đã có sẵn 2 cột này.
-- Chạy lại nhiều lần cũng không lỗi (chỉ thêm khi chưa có).
-- ============================================================

SET NAMES utf8mb4;

USE SmartFireDB;

SET @sql := IF(
    (SELECT COUNT(*) FROM information_schema.COLUMNS
     WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'AlertHistory' AND COLUMN_NAME = 'Resolution') = 0,
    'ALTER TABLE AlertHistory
        ADD COLUMN Resolution     VARCHAR(20)  NULL AFTER AcknowledgedAt,
        ADD COLUMN ResolutionNote VARCHAR(500) NULL AFTER Resolution,
        ADD CONSTRAINT CK_AlertHistory_Resolution CHECK (Resolution IN (''Handled'', ''FalseAlarm'', ''Test''))',
    'SELECT ''Resolution đã có, bỏ qua'' AS ThongBao');
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

-- Kiểm tra nhanh:
-- SELECT AlertId, Acknowledged, Resolution, ResolutionNote FROM AlertHistory ORDER BY AlertId DESC LIMIT 10;
