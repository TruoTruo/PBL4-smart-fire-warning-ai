-- ============================================================
-- schema.sql (MySQL 8.0+)
-- Hệ thống giám sát & cảnh báo sớm nguy cơ cháy nổ thông minh
-- Nguồn chân lý duy nhất cho cấu trúc DB — mọi module (iot-edge,
-- desktop-app) phải tham chiếu đúng theo tên bảng/cột tại đây.
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
    PasswordHash    VARCHAR(255)    NOT NULL,          -- lưu hash (bcrypt), không lưu plain text
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
    GasPpm              DECIMAL(6,2)    NULL,           -- ppm, từ MQ-2
    AiResult            BOOLEAN         NOT NULL DEFAULT FALSE,  -- YOLOv8 phát hiện lửa/khói
    ConfidenceScore     DECIMAL(5,2)    NULL,           -- % độ tin cậy của model AI
    RecordedAt          DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT FK_EnvironmentLogs_Devices FOREIGN KEY (DeviceId)
        REFERENCES Devices(DeviceId) ON DELETE CASCADE,
    INDEX IX_EnvironmentLogs_Device_Time (DeviceId, RecordedAt DESC)
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
    AlertTime               DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,
    SmsSent                 BOOLEAN         NOT NULL DEFAULT FALSE,
    Acknowledged            BOOLEAN         NOT NULL DEFAULT FALSE,  -- đã xem/xác nhận trên desktop-app
    AcknowledgedByUserId    INT             NULL,
    AcknowledgedAt          DATETIME        NULL,
    CONSTRAINT FK_AlertHistory_Devices FOREIGN KEY (DeviceId)
        REFERENCES Devices(DeviceId) ON DELETE CASCADE,
    CONSTRAINT FK_AlertHistory_EnvironmentLogs FOREIGN KEY (LogId)
        REFERENCES EnvironmentLogs(LogId) ON DELETE SET NULL,
    CONSTRAINT FK_AlertHistory_Users FOREIGN KEY (AcknowledgedByUserId)
        REFERENCES Users(UserId) ON DELETE SET NULL,
    CONSTRAINT CK_AlertHistory_Severity CHECK (Severity IN ('Low', 'Medium', 'High')),
    INDEX IX_AlertHistory_Device_Time (DeviceId, AlertTime DESC)
) ENGINE=InnoDB;