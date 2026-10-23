// =============================================================
// FIREGUARD IOT - SENSORS & SYSTEM DATA STORE
// =============================================================

const NODES_DATA = {
  'T-104': {
    name: 'Node #T-104',
    badge: 'CẢNH BÁO NHIỆT',
    hardware: 'ESP32-WROOM-32D Dual-Core MCU',
    ip: '192.168.1.104',
    mac: 'cc:50:e3:12:04:21',
    firmware: 'v2.4.1-pbl_prod',
    rssi: '-68 dBm (3s trước)',
    battery: '94% (4.12V - ổn định)',
    location: 'Vách Bồn Dầu T-1',
    metric: '48.5°C ↑ +3.5°C',
    status: 'warning'
  },
  'MQ-02': {
    name: 'Node #MQ-02',
    badge: 'TRỰC TUYẾN',
    hardware: 'ESP32-S3 + MQ-2 Sensor Module',
    ip: '192.168.1.102',
    mac: 'cc:50:e3:12:04:18',
    firmware: 'v2.4.0-pbl_prod',
    rssi: '-62 dBm (1s trước)',
    battery: '98% (4.18V)',
    location: 'Trần Xưởng Cơ Khí CNC',
    metric: '185 ppm Khói Gas',
    status: 'normal'
  },
  'FL-01': {
    name: 'Node #FL-01 (Vision)',
    badge: 'AI VISION CAM',
    hardware: 'ESP32-CAM + Sony IMX323 Lens',
    ip: '192.168.1.115',
    mac: 'cc:50:e3:12:04:99',
    firmware: 'v1.8.2-pbl_yolo',
    rssi: '-55 dBm (Live RTSP)',
    battery: 'Nguồn DC 12V Ổn định',
    location: 'Góc Trạm Hàn Xì Phía Tây',
    metric: 'Tin cậy 92% (Không ngọn lửa)',
    status: 'normal'
  },
  'T-105': {
    name: 'Node #T-105',
    badge: 'TRỰC TUYẾN',
    hardware: 'ESP32-WROOM-32D',
    ip: '192.168.1.105',
    mac: 'cc:50:e3:12:04:33',
    firmware: 'v2.4.1-pbl_prod',
    rssi: '-70 dBm (4s trước)',
    battery: '88% (4.02V)',
    location: 'Kho Linh Kiện Điện Tử',
    metric: '30.1°C (Ổn định)',
    status: 'normal'
  },
  'MQ-03': {
    name: 'Node #MQ-03',
    badge: 'MẤT TÍN HIỆU',
    hardware: 'ESP32 Standalone',
    ip: '192.168.1.103',
    mac: 'cc:50:e3:12:04:09',
    firmware: 'v2.3.9-pbl_prod',
    rssi: 'No Ping (> 5 phút)',
    battery: 'Cần kiểm tra pin',
    location: 'Trạm Biến Áp Phụ Tải',
    metric: 'Mất kết nối MQTT',
    status: 'offline'
  }
};

// Global API Hook for Backend Integration
window.FireGuardAPI = {
  updateKpis: function({ online, total, fires, warnings, responseMs }) {
    if (online !== undefined && total !== undefined) {
      const el = document.getElementById('kpiSensorsOnline');
      if (el) el.textContent = online;
    }
    if (fires !== undefined) {
      const el = document.getElementById('kpiFiresDetected');
      if (el) el.textContent = String(fires).padStart(2, '0');
    }
    if (warnings !== undefined) {
      const el = document.getElementById('kpiHeatWarnings');
      if (el) el.textContent = String(warnings).padStart(2, '0');
    }
    if (responseMs !== undefined) {
      const el = document.getElementById('kpiResponseTime');
      if (el) el.textContent = responseMs;
    }
  },

  updateNode: function(nodeId, data) {
    if (NODES_DATA[nodeId]) {
      Object.assign(NODES_DATA[nodeId], data);
    }
    if (nodeId === 'T-104' && data.temperature !== undefined) {
      const tempEl = document.getElementById('dashTempDisplay');
      const barEl = document.getElementById('dashTempBar');
      if (tempEl) tempEl.textContent = data.temperature.toFixed(1);
      if (barEl) barEl.style.width = Math.min((data.temperature / 60) * 100, 100) + '%';
    }
    if (nodeId === 'MQ-02' && data.smoke !== undefined) {
      const smokeEl = document.getElementById('dashSmokeDisplay');
      const barEl = document.getElementById('dashSmokeBar');
      if (smokeEl) smokeEl.textContent = data.smoke;
      if (barEl) barEl.style.width = Math.min((data.smoke / 600) * 100, 100) + '%';
    }
  },

  pushAlert: function(alertData) {
    if (typeof showToast === 'function') {
      showToast(`[CẢNH BÁO MỚI] ${alertData.location}: ${alertData.type} (${alertData.value})`, 'error');
    }
  }
};

