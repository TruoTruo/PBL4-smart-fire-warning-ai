-- ============================================================
-- schema.sql (MySQL 8.0+)
-- Hệ thống giám sát & cảnh báo sớm nguy cơ cháy nổ thông minh
-- Nguồn chân lý duy nhất cho cấu trúc DB — mọi module (iot-edge,
-- web-app) phải tham chiếu đúng theo tên bảng/cột tại đây.
-- ============================================================

CREATE DATABASE IF NOT EXISTS SmartFireDB
    CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;

USE SmartFireDB;

-- ------------------------------------------------------------
-- 1. Users: người dùng hệ thống (admin, người giám sát)
-- ------------------------------------------------------------
CREATE TABLE Users (
    UserId          INT AUTO_INCREMENT PRIMARY KEY,
    Username        VARCHAR(50)     NOT NULL UNIQUE,
    PasswordHash    VARCHAR(255)    NOT NULL,          -- lưu hash PBKDF2-SHA256 (pbkdf2$<vòng>$<salt>$<hash>), không lưu plain text
    FullName        VARCHAR(100)    NOT NULL,
    PhoneNumber     VARCHAR(15)     NULL,              -- dùng để nhận SMS cảnh báo
    Email           VARCHAR(100)    NULL,
    Role            VARCHAR(20)     NOT NULL DEFAULT 'User',
    CreatedAt       DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT CK_Users_Role CHECK (Role IN ('Admin', 'User'))
) ENGINE=InnoDB;

-- ------------------------------------------------------------
-- 2. Zones: khu vực/phòng được lắp thiết bị giám sát
-- ------------------------------------------------------------
CREATE TABLE Zones (
    ZoneId          INT AUTO_INCREMENT PRIMARY KEY,
    ZoneName        VARCHAR(100)    NOT NULL,
    Description     VARCHAR(255)    NULL,
    ManagerUserId   INT             NULL,
    FloorPlan       MEDIUMTEXT      NULL,              -- ảnh sơ đồ mặt bằng (data URL PNG/JPEG/WebP Base64)
    CONSTRAINT FK_Zones_Users FOREIGN KEY (ManagerUserId)
        REFERENCES Users(UserId) ON DELETE SET NULL
) ENGINE=InnoDB;

-- ------------------------------------------------------------
-- 3. Devices: thiết bị Raspberry Pi + cảm biến lắp tại từng khu vực
-- ------------------------------------------------------------
CREATE TABLE Devices (
    DeviceId        VARCHAR(20)     PRIMARY KEY,       -- ví dụ: 'PI-01'
    DeviceName      VARCHAR(100)    NOT NULL,
    ZoneId          INT             NOT NULL,
    DeviceType      VARCHAR(50)     NOT NULL DEFAULT 'Raspberry Pi 5',
    Status          VARCHAR(20)     NOT NULL DEFAULT 'Active',
    IpAddress       VARCHAR(15)     NULL,
    InstallDate     DATE            NOT NULL DEFAULT (CURRENT_DATE),
    PosX            DECIMAL(5,2)    NULL,              -- vị trí trên sơ đồ mặt bằng (% chiều rộng ảnh)
    PosY            DECIMAL(5,2)    NULL,              -- (% chiều cao ảnh)
    MaintenanceIntervalDays INT     NOT NULL DEFAULT 90, -- chu kỳ kiểm tra định kỳ (ngày)
    CONSTRAINT FK_Devices_Zones FOREIGN KEY (ZoneId)
        REFERENCES Zones(ZoneId) ON DELETE CASCADE,
    CONSTRAINT CK_Devices_Status CHECK (Status IN ('Active', 'Inactive', 'Maintenance'))
) ENGINE=InnoDB;

-- ------------------------------------------------------------
-- 4. EnvironmentLogs: log định kỳ dữ liệu cảm biến + kết quả AI
--    (ghi liên tục, ví dụ mỗi 5-10 giây một dòng)
-- ------------------------------------------------------------
CREATE TABLE EnvironmentLogs (
    LogId               BIGINT AUTO_INCREMENT PRIMARY KEY,
    DeviceId            VARCHAR(20)     NOT NULL,
    Temperature         DECIMAL(5,2)    NULL,           -- độ C, từ DHT11
    Humidity            DECIMAL(5,2)    NULL,           -- %, từ DHT11
    SmokeDetected       BOOLEAN         NOT NULL DEFAULT FALSE,  -- 1 = có khói (MQ-2 ngõ ra số HOẶC camera thấy khói), 0 = không
    SmokeLevel          DECIMAL(5,2)    NULL,                    -- mức khói/gas (% thang đo MQ-2) từ ADC; NULL nếu chỉ đọc chân DO
    AlertLevel          TINYINT         NOT NULL DEFAULT 0,      -- Pi tự tính: 0 bình thường, 1 cảnh báo sớm, 2 báo cháy
    AiResult            BOOLEAN         NOT NULL DEFAULT FALSE,  -- YOLOv11n phát hiện lửa
    ConfidenceScore     DECIMAL(5,2)    NULL,           -- % độ tin cậy của model AI
    RecordedAt          DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT FK_EnvironmentLogs_Devices FOREIGN KEY (DeviceId)
        REFERENCES Devices(DeviceId) ON DELETE CASCADE,
    INDEX IX_EnvironmentLogs_Device_Time (DeviceId, RecordedAt DESC),
    INDEX IX_EnvironmentLogs_Time (RecordedAt DESC)          -- lọc/sắp theo thời gian cho mọi thiết bị
) ENGINE=InnoDB;

-- ------------------------------------------------------------
-- 5. AlertHistory: chỉ ghi khi có cảnh báo thực sự
--    (theo cơ chế xác thực kép: AiResult = 1 AND cảm biến vượt ngưỡng)
-- ------------------------------------------------------------
CREATE TABLE AlertHistory (
    AlertId                 BIGINT AUTO_INCREMENT PRIMARY KEY,
    DeviceId                VARCHAR(20)     NOT NULL,
    LogId                   BIGINT          NULL,       -- log gốc đã kích hoạt cảnh báo này
    AlertType               VARCHAR(50)     NOT NULL,   -- 'Fire', 'Smoke', 'HighTemperature', 'HighGas'
    Severity                VARCHAR(20)     NOT NULL DEFAULT 'Medium',
    SnapshotBase64          MEDIUMTEXT      NULL,       -- ảnh bằng chứng JPEG (Base64) do Pi gửi kèm khi có cảnh báo
    AlertTime               DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,
    SmsSent                 BOOLEAN         NOT NULL DEFAULT FALSE,
    Acknowledged            BOOLEAN         NOT NULL DEFAULT FALSE,  -- đã xem/xác nhận trên web-app
    AcknowledgedByUserId    INT             NULL,
    AcknowledgedAt          DATETIME        NULL,
    Resolution              VARCHAR(20)     NULL,       -- kết quả xử lý: 'Handled', 'FalseAlarm', 'Test' (NULL = chưa ghi)
    ResolutionNote          VARCHAR(500)    NULL,       -- ghi chú phiếu sự cố của cán bộ
    ResolvedByUserId        INT             NULL,       -- cán bộ ghi kết quả phiếu (lần ghi gần nhất)
    ResolvedAt              DATETIME        NULL,       -- thời điểm ghi kết quả phiếu
    CONSTRAINT FK_AlertHistory_Devices FOREIGN KEY (DeviceId)
        REFERENCES Devices(DeviceId) ON DELETE CASCADE,
    CONSTRAINT FK_AlertHistory_EnvironmentLogs FOREIGN KEY (LogId)
        REFERENCES EnvironmentLogs(LogId) ON DELETE SET NULL,
    CONSTRAINT FK_AlertHistory_ResolvedBy FOREIGN KEY (ResolvedByUserId)
        REFERENCES Users(UserId) ON DELETE SET NULL,
    CONSTRAINT FK_AlertHistory_Users FOREIGN KEY (AcknowledgedByUserId)
        REFERENCES Users(UserId) ON DELETE SET NULL,
    CONSTRAINT CK_AlertHistory_Severity CHECK (Severity IN ('Low', 'Medium', 'High')),
    CONSTRAINT CK_AlertHistory_Resolution CHECK (Resolution IN ('Handled', 'FalseAlarm', 'Test')),
    INDEX IX_AlertHistory_Device_Time (DeviceId, AlertTime DESC),
    INDEX IX_AlertHistory_Time (AlertTime DESC)              -- lịch sử/thống kê theo ngày cho mọi thiết bị
) ENGINE=InnoDB;

-- ------------------------------------------------------------
-- 6. ActivityLog: nhật ký thao tác của cán bộ trên web-app
-- ------------------------------------------------------------
CREATE TABLE ActivityLog (
    ActivityId      BIGINT AUTO_INCREMENT PRIMARY KEY,
    UserId          INT             NULL,
    UserName        VARCHAR(100)    NOT NULL,          -- họ tên / tên đăng nhập lúc thao tác (giữ lại khi tài khoản bị xoá)
    Action          VARCHAR(40)     NOT NULL,          -- ví dụ 'login', 'login.failed', 'alert.ack', 'zone.update'
    TargetType      VARCHAR(20)     NULL,              -- 'alert', 'device', 'zone', 'user'
    TargetId        VARCHAR(40)     NULL,
    Detail          VARCHAR(500)    NULL,
    IpAddress       VARCHAR(45)     NULL,
    CreatedAt       DATETIME        NOT NULL,
    CONSTRAINT FK_ActivityLog_Users FOREIGN KEY (UserId)
        REFERENCES Users(UserId) ON DELETE SET NULL,
    INDEX IX_ActivityLog_Time (CreatedAt DESC)
) ENGINE=InnoDB;

-- ------------------------------------------------------------
-- 7. MaintenanceLogs: lịch sử bảo trì thiết bị
-- ------------------------------------------------------------
CREATE TABLE MaintenanceLogs (
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
