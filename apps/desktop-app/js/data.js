// =============================================================
// FIREGUARD IOT - SENSORS & SYSTEM DATA STORE
// Khớp 100% với Schema CSDL SmartFireDB (MySQL) và Pi 5
// =============================================================

const NODES_DATA = {
  'PI-01': {
    name: 'Node #PI-01 (Raspberry Pi 5)',
    badge: 'CẢNH BÁO CHÁY',
    hardware: 'Raspberry Pi 5 8GB + DHT11 + MQ-2',
    ip: '192.168.1.101',
    mac: 'b8:27:eb:14:01:01',
    firmware: 'v2.4.1-pbl_pi5',
    rssi: '-58 dBm (2s trước)',
    battery: 'Nguồn Adapter 27W Ổn định',
    location: 'Phòng Lab tầng 3',
    metric: '45.2°C ↑ Khói 620 ppm',
    status: 'warning'
  },
  'PI-02': {
    name: 'Node #PI-02 (Raspberry Pi 5)',
    badge: 'TRỰC TUYẾN',
    hardware: 'Raspberry Pi 5 + DHT11 Sensor',
    ip: '192.168.1.102',
    mac: 'b8:27:eb:14:01:02',
    firmware: 'v2.4.1-pbl_pi5',
    rssi: '-64 dBm (1s trước)',
    battery: 'Nguồn Adapter 27W',
    location: 'Kho vật tư tầng 1',
    metric: '26.0°C (Bình thường)',
    status: 'normal'
  },
  'T-104': {
    name: 'Node #T-104',
    badge: 'TRỰC TUYẾN',
    hardware: 'ESP32-WROOM-32D Dual-Core MCU',
    ip: '192.168.1.104',
    mac: 'cc:50:e3:12:04:21',
    firmware: 'v2.4.1-pbl_prod',
    rssi: '-68 dBm (3s trước)',
    battery: '94% (4.12V - ổn định)',
    location: 'Vách Bồn Dầu T-1',
    metric: '34.8°C (Ổn định)',
    status: 'normal'
  },
  'MQ-02': {
    name: 'Node #MQ-02',
    badge: 'TRỰC TUYẾN',
    hardware: 'ESP32-S3 + MQ-2 Sensor Module',
    ip: '192.168.1.103',
    mac: 'cc:50:e3:12:04:18',
    firmware: 'v2.4.0-pbl_prod',
    rssi: '-62 dBm (1s trước)',
    battery: '98% (4.18V)',
    location: 'Trần Xưởng Cơ Khí CNC',
    metric: '185 ppm Khói Gas',
    status: 'normal'
  }
};

// Global API Hook for Backend Integration
window.FireGuardAPI = {
  updateKpis: function({ online, total, fires, warnings, responseMs }) {
    if (online !== undefined) {
      const el = document.getElementById('kpiSensorsOnline');
      if (el) el.textContent = String(online).padStart(2, '0');
    }
    if (total !== undefined) {
      const elTotal = document.getElementById('kpiSensorsTotal');
      if (elTotal) elTotal.textContent = `/ ${String(total).padStart(2, '0')} On`;
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

  renderRecentAlerts: function(alerts) {
    const container = document.getElementById('recentAlertsContainer');
    if (!container || !alerts || alerts.length === 0) return;
    container.innerHTML = '';
    alerts.forEach(a => {
      const isHigh = a.severity === 'High';
      const badgeBg = isHigh ? 'bg-error-container/40 text-primary' : 'bg-secondary-container/30 text-secondary';
      const statusText = a.acknowledged ? 'Đã xử lý' : 'Chưa xử lý';
      const statusBg = a.acknowledged ? 'bg-tertiary-container/20 text-tertiary' : 'bg-error-container/30 text-primary';
      
      const timeStr = a.alertTime ? new Date(a.alertTime).toLocaleTimeString('vi-VN', { hour: '2-digit', minute: '2-digit', second: '2-digit' }) : 'Vừa xong';
      const html = `
        <div class="flex flex-col sm:flex-row sm:items-center justify-between p-2.5 rounded-lg bg-surface-container hover:bg-surface-container-high transition-colors gap-2">
          <div class="flex items-center gap-2.5">
            <span class="font-label-code text-[11px] ${badgeBg} px-2 py-0.5 rounded font-bold">${timeStr}</span>
            <div>
              <div class="font-body-md text-[13px] text-on-surface font-medium">${a.zoneName || a.deviceId} (${a.deviceId}): <span class="${isHigh ? 'text-primary' : 'text-secondary'} font-bold">${a.alertType}</span></div>
              <div class="text-[12px] text-on-surface-variant">Mức độ: ${a.severity} • SMS: ${a.smsSent ? 'Đã gửi' : 'Chưa gửi'}</div>
            </div>
          </div>
          <span class="font-label-code text-[11px] ${statusBg} px-2 py-0.5 rounded self-end sm:self-center font-bold">${statusText}</span>
        </div>
      `;
      container.insertAdjacentHTML('beforeend', html);
    });
  },

  renderFullDashboard: function(data) {
    if (!data) return;

    // 1. Cập nhật các chỉ số KPI HUD
    if (data.kpi) {
      window.FireGuardAPI.updateKpis({
        online: data.kpi.sensorsOnline,
        total: data.kpi.sensorsTotal,
        fires: data.kpi.fireDetected,
        warnings: data.kpi.heatWarnings,
        responseMs: data.kpi.responseMs
      });
    }

    // 2. Cập nhật Vùng trọng điểm & Số đo môi trường từ bản ghi mới nhất
    if (data.latestReading) {
      const r = data.latestReading;
      const zoneEl = document.getElementById('kpiFocusZone');
      if (zoneEl) zoneEl.textContent = `${r.zoneName} (${r.deviceId})`;

      // Card 1: Nhiệt độ
      const tempNode = document.getElementById('dashTempNode');
      if (tempNode) tempNode.textContent = `Node #${r.deviceId} • ${r.zoneName}`;
      const tempEl = document.getElementById('dashTempDisplay');
      if (tempEl) tempEl.textContent = Number(r.temperature).toFixed(1);
      const tempBar = document.getElementById('dashTempBar');
      if (tempBar) tempBar.style.width = Math.min((r.temperature / 60) * 100, 100) + '%';
      const tempTrend = document.getElementById('dashTempTrend');
      if (tempTrend) tempTrend.textContent = r.temperature > 40 ? '⚠ Nhiệt độ cao vượt ngưỡng' : 'Nhiệt độ ổn định';

      // Card 2: Độ ẩm
      const humNode = document.getElementById('dashHumidityNode');
      if (humNode) humNode.textContent = `Node #${r.deviceId} • ${r.zoneName}`;
      const humEl = document.getElementById('dashHumidityDisplay');
      if (humEl) humEl.textContent = Number(r.humidity).toFixed(1);
      const humBar = document.getElementById('dashHumidityBar');
      if (humBar) humBar.style.width = Math.min(r.humidity, 100) + '%';
      const humTrend = document.getElementById('dashHumidityTrend');
      if (humTrend) humTrend.textContent = r.humidity < 35 ? 'Độ ẩm thấp, tăng nguy cơ bắt lửa' : 'Khô ráo, an toàn';

      // Card 3: Khói & Gas
      const smokeNode = document.getElementById('dashSmokeNode');
      if (smokeNode) smokeNode.textContent = `Node #${r.deviceId} • ${r.zoneName}`;
      const smokeEl = document.getElementById('dashSmokeDisplay');
      if (smokeEl) smokeEl.textContent = Math.round(r.gasPpm);
      const smokeBar = document.getElementById('dashSmokeBar');
      if (smokeBar) smokeBar.style.width = Math.min((r.gasPpm / 600) * 100, 100) + '%';
      const smokeBadge = document.getElementById('dashSmokeBadge');
      if (smokeBadge) smokeBadge.textContent = r.gasPpm > 300 ? 'BÁO ĐỘNG' : 'Theo dõi sát';

      // Card 4: AI Fire Risk
      const riskVal = r.aiDetected ? Math.round(r.confidenceScore || 88.5) : 15;
      const riskEl = document.getElementById('dashRiskDisplay');
      if (riskEl) riskEl.textContent = riskVal;
      const riskLbl = document.getElementById('dashRiskLabel');
      if (riskLbl) riskLbl.textContent = r.aiDetected ? 'Mức Nguy Cấp' : 'Mức Thấp';
      const riskInd = document.getElementById('dashRiskIndicator');
      if (riskInd) riskInd.style.left = Math.min(riskVal, 100) + '%';
    }

    // 3. Cập nhật danh sách sự cố gần nhất từ bảng AlertHistory
    if (data.alerts && data.alerts.length > 0) {
      window.FireGuardAPI.renderRecentAlerts(data.alerts);
    }

    // 4. Cập nhật biểu đồ nếu có danh sách điểm đo
    if (data.chartPoints && data.chartPoints.length > 0) {
      let maxTemp = 0;
      let peakDev = '';
      data.chartPoints.forEach(p => {
        if (p.temperature > maxTemp) {
          maxTemp = p.temperature;
          peakDev = p.deviceId;
        }
      });
      const peakEl = document.getElementById('dashChartPeak');
      if (peakEl && maxTemp > 0) {
        peakEl.textContent = `Đỉnh nhiệt độ ca: ${maxTemp.toFixed(1)} °C (${peakDev} Cảnh báo)`;
      }
    }
  },

  updateNode: function(nodeId, data) {
    if (NODES_DATA[nodeId]) {
      Object.assign(NODES_DATA[nodeId], data);
    }
    if (nodeId === 'PI-01' && data.temperature !== undefined) {
      const tempEl = document.getElementById('dashTempDisplay');
      const barEl = document.getElementById('dashTempBar');
      if (tempEl) tempEl.textContent = data.temperature.toFixed(1);
      if (barEl) barEl.style.width = Math.min((data.temperature / 60) * 100, 100) + '%';
    }
  },

  pushAlert: function(alertData) {
    if (typeof showToast === 'function') {
      showToast(`[CẢNH BÁO MỚI] ${alertData.location}: ${alertData.type} (${alertData.value})`, 'error');
    }
  },

  onPiReading: function(temp, hum, smoke) {
    const tempEl = document.getElementById('dashTempDisplay');
    const barEl = document.getElementById('dashTempBar');
    if (tempEl) tempEl.textContent = Number(temp).toFixed(1);
    if (barEl) barEl.style.width = Math.min((temp / 60) * 100, 100) + '%';

    const humEl = document.getElementById('dashHumidityDisplay');
    const humBar = document.getElementById('dashHumidityBar');
    if (humEl) humEl.textContent = Number(hum).toFixed(1);
    if (humBar) humBar.style.width = Math.min((hum / 100) * 100, 100) + '%';

    const smokeEl = document.getElementById('dashSmokeDisplay');
    const smokeBar = document.getElementById('dashSmokeBar');
    const smokeVal = smoke === 1 ? 620 : 185;
    if (smokeEl) smokeEl.textContent = smokeVal;
    if (smokeBar) smokeBar.style.width = Math.min((smokeVal / 600) * 100, 100) + '%';

    if (smoke === 1 || temp > 45) {
      if (typeof showToast === 'function') {
        showToast(`🔥 [BÁO ĐỘNG] Pi 5 phát hiện: Nhiệt độ ${temp}°C, Khói: ${smoke === 1 ? 'CÓ' : 'KHÔNG'}`, 'error');
      }
    }
  }
};
