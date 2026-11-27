-- ============================================================
-- 007_activity_floorplan_maintenance.sql  (MySQL 8.0+)
--   1) ActivityLog        : nhật ký hoạt động của cán bộ trên web
--   2) Zones.FloorPlan    : ảnh sơ đồ mặt bằng của khu vực (data URL Base64)
--      Devices.PosX/PosY  : vị trí trạm trên sơ đồ (% chiều rộng / chiều cao ảnh)
--   3) Devices.MaintenanceIntervalDays + bảng MaintenanceLogs: lịch sử bảo trì, hạn kiểm tra định kỳ
--
-- Chạy trên database ĐÃ CÓ SẴN, sau 002 → 006.
-- Database tạo mới từ migrations/schema.sql thì đã có sẵn các cột / bảng này.
-- Chạy lại nhiều lần cũng không lỗi (chỉ thêm khi chưa có).
-- ============================================================

SET NAMES utf8mb4;

USE SmartFireDB;

CREATE TABLE IF NOT EXISTS ActivityLog (
    ActivityId      BIGINT AUTO_INCREMENT PRIMARY KEY,
    UserId          INT             NULL,
    UserName        VARCHAR(100)    NOT NULL,          -- họ tên / tên đăng nhập lúc thao tác (giữ lại khi tài khoản bị xoá)
    Action          VARCHAR(40)     NOT NULL,          -- ví dụ 'login', 'alert.ack', 'zone.update'
    TargetType      VARCHAR(20)     NULL,              -- 'alert', 'device', 'zone', 'user'
    TargetId        VARCHAR(40)     NULL,
    Detail          VARCHAR(500)    NULL,
    IpAddress       VARCHAR(45)     NULL,
    CreatedAt       DATETIME        NOT NULL,
    CONSTRAINT FK_ActivityLog_Users FOREIGN KEY (UserId)
        REFERENCES Users(UserId) ON DELETE SET NULL,
    INDEX IX_ActivityLog_Time (CreatedAt DESC)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS MaintenanceLogs (
    MaintenanceId   INT AUTO_INCREMENT PRIMARY KEY,
    DeviceId        VARCHAR(20)     NOT NULL,
    UserId          INT             NULL,              -- cán bộ ghi nhận
    PerformedAt     DATE            NOT NULL,
    Kind            VARCHAR(20)     NOT NULL,          -- 'Inspection', 'Clean', 'Repair', 'Replace'
    Note            VARCHAR(500)    NULL,
    CreatedAt       DATETIME        NOT NULL,
    CONSTRAINT FK_MaintenanceLogs_Devices FOREIGN KEY (DeviceId)
        REFERENCES Devices(DeviceId) ON DELETE CASCADE,
    CONSTRAINT FK_MaintenanceLogs_Users FOREIGN KEY (UserId)
        REFERENCES Users(UserId) ON DELETE SET NULL,
    CONSTRAINT CK_MaintenanceLogs_Kind CHECK (Kind IN ('Inspection', 'Clean', 'Repair', 'Replace')),
    INDEX IX_MaintenanceLogs_Device (DeviceId, PerformedAt DESC)
) ENGINE=InnoDB;

-- Thêm một cột nếu chưa có
DROP PROCEDURE IF EXISTS sp_tmp_add_column;
DELIMITER //
CREATE PROCEDURE sp_tmp_add_column(IN tbl VARCHAR(64), IN col VARCHAR(64), IN ddl VARCHAR(255))
BEGIN
    IF (SELECT COUNT(*) FROM information_schema.COLUMNS
        WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = tbl AND COLUMN_NAME = col) = 0 THEN
        SET @sql := CONCAT('ALTER TABLE ', tbl, ' ADD COLUMN ', ddl);
        PREPARE stmt FROM @sql;
        EXECUTE stmt;
        DEALLOCATE PREPARE stmt;
    END IF;
END //
DELIMITER ;

CALL sp_tmp_add_column('Zones',   'FloorPlan',               'FloorPlan MEDIUMTEXT NULL');
CALL sp_tmp_add_column('Devices', 'PosX',                    'PosX DECIMAL(5,2) NULL');
CALL sp_tmp_add_column('Devices', 'PosY',                    'PosY DECIMAL(5,2) NULL');
CALL sp_tmp_add_column('Devices', 'MaintenanceIntervalDays', 'MaintenanceIntervalDays INT NOT NULL DEFAULT 90');

DROP PROCEDURE sp_tmp_add_column;

-- Kiểm tra nhanh:
-- SHOW COLUMNS FROM Devices;
-- SELECT ActivityId, UserName, Action, TargetType, TargetId, CreatedAt FROM ActivityLog ORDER BY ActivityId DESC LIMIT 20;
