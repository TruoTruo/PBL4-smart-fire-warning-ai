-- ============================================================
-- stored_procedures.sql (MySQL 8.0+)
-- Các stored procedure phục vụ Desktop App (Trường) gọi qua
-- Services/DbService.cs. Chạy sau schema.sql + seed.sql.
--
-- LƯU Ý: MySQL không hỗ trợ giá trị mặc định cho tham số IN
-- như SQL Server (@Param = NULL). Khi gọi CALL, nếu muốn "không
-- lọc" theo tham số nào, bạn PHẢI truyền NULL rõ ràng cho tham
-- số đó, ví dụ: CALL sp_GetRecentEnvironmentLogs(NULL, 24);
-- ============================================================

USE SmartFireDB;

-- ------------------------------------------------------------
-- 1. Lấy log môi trường gần nhất theo thiết bị (mặc định 24h)
-- ------------------------------------------------------------
DROP PROCEDURE IF EXISTS sp_GetRecentEnvironmentLogs;
DELIMITER $$
CREATE PROCEDURE sp_GetRecentEnvironmentLogs(
    IN p_device_id VARCHAR(20),   -- truyền NULL để lấy tất cả thiết bị
    IN p_hours INT                -- truyền NULL để dùng mặc định 24h
)
BEGIN
    IF p_hours IS NULL THEN
        SET p_hours = 24;
    END IF;

    SELECT
        l.LogId, l.DeviceId, d.DeviceName, z.ZoneName,
        l.Temperature, l.Humidity, l.GasPpm,
        l.AiResult, l.ConfidenceScore, l.RecordedAt
    FROM EnvironmentLogs l
    JOIN Devices d ON d.DeviceId = l.DeviceId
    JOIN Zones z ON z.ZoneId = d.ZoneId
    WHERE l.RecordedAt >= DATE_SUB(NOW(), INTERVAL p_hours HOUR)
      AND (p_device_id IS NULL OR l.DeviceId = p_device_id)
    ORDER BY l.RecordedAt DESC;
END$$
DELIMITER ;

-- ------------------------------------------------------------
-- 2. Lấy lịch sử cảnh báo (lọc theo mức độ, khoảng ngày - tùy chọn)
-- ------------------------------------------------------------
DROP PROCEDURE IF EXISTS sp_GetAlertHistory;
DELIMITER $$
CREATE PROCEDURE sp_GetAlertHistory(
    IN p_device_id VARCHAR(20),
    IN p_severity VARCHAR(20),           -- 'Low' | 'Medium' | 'High', NULL = tất cả
    IN p_from_date DATETIME,
    IN p_to_date DATETIME,
    IN p_only_unacknowledged BOOLEAN     -- TRUE = chỉ lấy cảnh báo chưa xác nhận
)
BEGIN
    SELECT
        a.AlertId, a.DeviceId, d.DeviceName, z.ZoneName,
        a.AlertType, a.Severity, a.AlertTime,
        a.SmsSent, a.Acknowledged, a.AcknowledgedAt,
        u.FullName AS AcknowledgedBy
    FROM AlertHistory a
    JOIN Devices d ON d.DeviceId = a.DeviceId
    JOIN Zones z ON z.ZoneId = d.ZoneId
    LEFT JOIN Users u ON u.UserId = a.AcknowledgedByUserId
    WHERE (p_device_id IS NULL OR a.DeviceId = p_device_id)
      AND (p_severity IS NULL OR a.Severity = p_severity)
      AND (p_from_date IS NULL OR a.AlertTime >= p_from_date)
      AND (p_to_date IS NULL OR a.AlertTime <= p_to_date)
      AND (p_only_unacknowledged IS NULL OR p_only_unacknowledged = FALSE OR a.Acknowledged = FALSE)
    ORDER BY a.AlertTime DESC;
END$$
DELIMITER ;

-- ------------------------------------------------------------
-- 3. Đánh dấu 1 cảnh báo đã được người dùng xác nhận (trên desktop-app)
-- ------------------------------------------------------------
DROP PROCEDURE IF EXISTS sp_AcknowledgeAlert;
DELIMITER $$
CREATE PROCEDURE sp_AcknowledgeAlert(
    IN p_alert_id BIGINT,
    IN p_user_id INT
)
BEGIN
    UPDATE AlertHistory
    SET Acknowledged = TRUE,
        AcknowledgedByUserId = p_user_id,
        AcknowledgedAt = NOW()
    WHERE AlertId = p_alert_id;

    SELECT ROW_COUNT() AS RowsUpdated;  -- desktop-app kiểm tra =1 nghĩa là update thành công
END$$
DELIMITER ;

-- ------------------------------------------------------------
-- 4. Thống kê số lần cảnh báo theo thiết bị, theo ngày (phục vụ báo cáo)
-- ------------------------------------------------------------
DROP PROCEDURE IF EXISTS sp_GetAlertStatistics;
DELIMITER $$
CREATE PROCEDURE sp_GetAlertStatistics(
    IN p_from_date DATETIME,   -- truyền NULL để mặc định 30 ngày gần nhất
    IN p_to_date DATETIME
)
BEGIN
    IF p_from_date IS NULL THEN
        SET p_from_date = DATE_SUB(NOW(), INTERVAL 30 DAY);
    END IF;
    IF p_to_date IS NULL THEN
        SET p_to_date = NOW();
    END IF;

    SELECT
        d.DeviceId, d.DeviceName, z.ZoneName,
        DATE(a.AlertTime) AS AlertDate,
        a.AlertType,
        COUNT(*) AS AlertCount
    FROM AlertHistory a
    JOIN Devices d ON d.DeviceId = a.DeviceId
    JOIN Zones z ON z.ZoneId = d.ZoneId
    WHERE a.AlertTime BETWEEN p_from_date AND p_to_date
    GROUP BY d.DeviceId, d.DeviceName, z.ZoneName, DATE(a.AlertTime), a.AlertType
    ORDER BY AlertDate DESC, AlertCount DESC;
END$$
DELIMITER ;


-- ============================================================
-- TEST NHANH — giả sử đã chạy schema.sql + seed.sql trước đó
-- ============================================================

-- Test 1: lấy log 24h của PI-01 (kỳ vọng: 3 dòng, có 1 dòng AiResult=1)
CALL sp_GetRecentEnvironmentLogs('PI-01', 24);

-- Test 2: lấy toàn bộ log 24h, không lọc thiết bị (kỳ vọng: 4 dòng)
CALL sp_GetRecentEnvironmentLogs(NULL, 24);

-- Test 3: lấy cảnh báo chưa xác nhận (kỳ vọng: 1 dòng, Acknowledged=0)
CALL sp_GetAlertHistory(NULL, NULL, NULL, NULL, TRUE);

-- Test 4: xác nhận cảnh báo đó (giả sử AlertId=1, UserId=1 là admin)
CALL sp_AcknowledgeAlert(1, 1);

-- Test 5: kiểm tra lại — giờ Acknowledged phải = 1
CALL sp_GetAlertHistory('PI-01', NULL, NULL, NULL, NULL);

-- Test 6: thống kê cảnh báo 30 ngày gần nhất (kỳ vọng: 1 dòng PI-01 / Fire / count=1)
CALL sp_GetAlertStatistics(NULL, NULL);