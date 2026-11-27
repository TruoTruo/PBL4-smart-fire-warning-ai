-- ============================================================
-- 008_alert_resolved_by.sql  (MySQL 8.0+)
-- Phiếu sự cố: ghi lại cán bộ nào ghi kết quả xử lý và vào lúc nào.
--   ResolvedByUserId  cán bộ ghi kết quả (lần ghi gần nhất; sửa phiếu thì cập nhật)
--   ResolvedAt        thời điểm ghi kết quả
-- Người xác nhận (AcknowledgedByUserId / AcknowledgedAt) giữ nguyên như trước.
--
-- Chạy trên database ĐÃ CÓ SẴN, sau 002 → 007.
-- Database tạo mới từ migrations/schema.sql thì đã có sẵn 2 cột này.
-- Chạy lại nhiều lần cũng không lỗi (chỉ thêm khi chưa có).
-- ============================================================

SET NAMES utf8mb4;

USE SmartFireDB;

SET @sql := IF(
    (SELECT COUNT(*) FROM information_schema.COLUMNS
     WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'AlertHistory' AND COLUMN_NAME = 'ResolvedByUserId') = 0,
    'ALTER TABLE AlertHistory
        ADD COLUMN ResolvedByUserId INT      NULL AFTER ResolutionNote,
        ADD COLUMN ResolvedAt       DATETIME NULL AFTER ResolvedByUserId,
        ADD CONSTRAINT FK_AlertHistory_ResolvedBy FOREIGN KEY (ResolvedByUserId)
            REFERENCES Users(UserId) ON DELETE SET NULL',
    'SELECT ''ResolvedByUserId đã có, bỏ qua'' AS ThongBao');
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

-- Phiếu đã ghi trước khi có cột này: coi người xác nhận là người ghi kết quả (gần đúng nhất có thể)
UPDATE AlertHistory
SET ResolvedByUserId = AcknowledgedByUserId, ResolvedAt = AcknowledgedAt
WHERE Resolution IS NOT NULL AND ResolvedAt IS NULL;

-- Kiểm tra nhanh:
-- SELECT AlertId, Resolution, ResolvedByUserId, ResolvedAt FROM AlertHistory WHERE Resolution IS NOT NULL ORDER BY AlertId DESC LIMIT 10;
