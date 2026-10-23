// =============================================================
// FIREGUARD IOT - APPLICATION CONTROLLER & UI LOGIC
// =============================================================

let isAlarmMuted = false;
let mapZoomScale = 1;

// 1. Live Header Clock
function updateLiveClock() {
  const now = new Date();
  const timeStr = now.toLocaleTimeString('vi-VN', { hour: '2-digit', minute: '2-digit', second: '2-digit' });
  const dateStr = now.toLocaleDateString('vi-VN', { day: '2-digit', month: '2-digit', year: 'numeric' });
  const clockEl = document.getElementById('headerLiveClock');
  if (clockEl) {
    clockEl.textContent = `${timeStr} • ${dateStr}`;
  }
}
setInterval(updateLiveClock, 1000);
document.addEventListener('DOMContentLoaded', updateLiveClock);

// 2. Floorplan CAD Interactions
function selectNodeOnMap(nodeId) {
  if (typeof NODES_DATA === 'undefined') return;
  const data = NODES_DATA[nodeId];
  if (!data) return;

  const titleEl = document.getElementById('inspectNodeTitle');
  if (titleEl) titleEl.textContent = data.name;

  const badge = document.getElementById('inspectNodeBadge');
  if (badge) {
    badge.textContent = data.badge;
    if (data.status === 'warning') {
      badge.className = 'font-label-code text-[10px] font-bold px-2 py-0.5 rounded bg-secondary-container/40 text-secondary border border-secondary/40';
    } else if (data.status === 'offline') {
      badge.className = 'font-label-code text-[10px] font-bold px-2 py-0.5 rounded bg-surface-container-highest text-outline border border-outline/40';
    } else {
      badge.className = 'font-label-code text-[10px] font-bold px-2 py-0.5 rounded bg-tertiary-container/30 text-tertiary border border-tertiary/40';
    }
  }

  const setTxt = (id, val) => {
    const el = document.getElementById(id);
    if (el) el.textContent = val;
  };

  setTxt('inspectNodeHardware', data.hardware);
  setTxt('inspectNodeIp', data.ip);
  setTxt('inspectNodeMac', data.mac);
  setTxt('inspectNodeFirmware', data.firmware);
  setTxt('inspectNodeRssi', data.rssi);
  setTxt('inspectNodeBattery', data.battery);
  setTxt('inspectNodeLocation', data.location);
  setTxt('inspectNodeMetricVal', data.metric);

  showToast(`Đã chọn trạm cảm biến: ${data.name} (${data.location})`, 'info');
}

function selectActuator(actuatorId) {
  showToast(`Đã chọn bộ chấp hành: ${actuatorId}`, 'info');
}

function setFloorLayer(layerName) {
  const heatmap = document.getElementById('heatmapLayer');
  const escape = document.getElementById('escapeLayer');
  const btnSensors = document.getElementById('btnLayerSensors');
  const btnHeatmap = document.getElementById('btnLayerHeatmap');
  const btnEscape = document.getElementById('btnLayerEscape');

  if (heatmap) heatmap.classList.add('hidden');
  if (escape) escape.classList.add('hidden');

  [btnSensors, btnHeatmap, btnEscape].forEach(b => {
    if (b) b.className = 'px-2.5 py-1 rounded text-on-surface-variant hover:text-on-surface flex items-center gap-1 transition-all';
  });

  if (layerName === 'sensors') {
    if (btnSensors) btnSensors.className = 'px-2.5 py-1 rounded bg-surface-container-highest text-on-surface font-semibold flex items-center gap-1 transition-all';
    showToast('Hiển thị lớp trạm cảm biến IoT', 'info');
  } else if (layerName === 'heatmap') {
    if (heatmap) heatmap.classList.remove('hidden');
    if (btnHeatmap) btnHeatmap.className = 'px-2.5 py-1 rounded bg-secondary text-on-secondary font-semibold flex items-center gap-1 transition-all';
    showToast('Đang bật bản đồ phân bổ nhiệt độ (Heatmap)', 'warning');
  } else if (layerName === 'escape') {
    if (escape) escape.classList.remove('hidden');
    if (btnEscape) btnEscape.className = 'px-2.5 py-1 rounded bg-tertiary text-on-tertiary font-semibold flex items-center gap-1 transition-all';
    showToast('Hiển thị sơ đồ hướng dẫn thoát hiểm khẩn cấp', 'info');
  }
}

function zoomMap(factor) {
  const svg = document.getElementById('cadFloorSvg');
  if (!svg) return;
  mapZoomScale *= factor;
  mapZoomScale = Math.max(0.7, Math.min(mapZoomScale, 2.5));
  svg.style.transform = `scale(${mapZoomScale})`;
}

function resetMapZoom() {
  const svg = document.getElementById('cadFloorSvg');
  if (!svg) return;
  mapZoomScale = 1;
  svg.style.transform = 'scale(1)';
}

function switchFloorBlock(val) {
  const title = document.getElementById('floorPlanTitle');
  const titles = {
    'block-a': 'Mặt bằng Tầng 1 - Block A (CNC)',
    'block-b': 'Mặt bằng Tầng 1 - Block B (Kho Hóa chất)',
    'block-c': 'Mặt bằng Tầng 1 - Block C (Khu Đúc)',
    'block-d': 'Mặt bằng Tầng 1 - Block D (Dầu Thủy lực)'
  };
  if (title && titles[val]) {
    title.textContent = titles[val];
    showToast(`Đã chuyển sang: ${titles[val]}`, 'info');
  }
}

// 3. Emergency & Actions Logic
function toggleEmergencyAlarm() {
  isAlarmMuted = !isAlarmMuted;
  const btn = document.getElementById('btnHeaderMuteAlarm');
  if (!btn) return;
  if (isAlarmMuted) {
    btn.innerHTML = '<span class="material-symbols-outlined text-[17px]">volume_up</span><span>Bật lại chuông còi</span>';
    btn.classList.remove('bg-primary-container', 'text-on-primary-container');
    btn.classList.add('bg-surface-container-highest', 'text-on-surface');
    showToast('Đã tạm thời ngắt tín hiệu còi hú cơ sở!', 'warning');
  } else {
    btn.innerHTML = '<span class="material-symbols-outlined text-[17px]">volume_off</span><span>Tắt chuông khẩn cấp</span>';
    btn.classList.add('bg-primary-container', 'text-on-primary-container');
    btn.classList.remove('bg-surface-container-highest', 'text-on-surface');
    showToast('Chuông còi cảnh báo đã sẵn sàng phát tín hiệu!', 'info');
  }
}

function triggerEmergencyShutdownModal() {
  toggleEmergencyModal(true);
}

function toggleEmergencyModal(show) {
  const modal = document.getElementById('modalEmergencyShutdown');
  if (modal) {
    if (show) modal.classList.remove('hidden');
    else modal.classList.add('hidden');
  }
}

function executeEmergencyShutdown() {
  toggleEmergencyModal(false);
  showToast('ĐÃ PHÁT LỆNH NGẮT TOÀN BỘ NGUỒN ĐIỆN VÀ KHÓA VAN GAS!', 'error');
}

function triggerEvacuationOrder() {
  if (confirm('LỆNH SƠ TÁN TOÀN KHU: Bạn có chắc chắn muốn kích hoạt loa thông báo sơ tán và gửi SMS hàng loạt tới toàn bộ cán bộ công nhân viên?')) {
    showToast('ĐÃ PHÁT LỆNH SƠ TÁN TOÀN KHU QUA HỆ THỐNG LOA VÀ SMS GATEWAY!', 'error');
  }
}

function testSirenAction() {
  showToast('Đang phát tín hiệu thử nghiệm còi hú (3 giây)...', 'warning');
  setTimeout(() => {
    showToast('Đã hoàn tất kiểm tra còi báo động (Trạng thái: Tốt)', 'success');
  }, 3000);
}

function rebootNodeAction() {
  showToast('Đã gửi lệnh Reboot qua MQTT tới trạm cảm biến. Chờ khởi động lại trong 5s...', 'warning');
}

function triggerNodeAlarmTest() {
  showToast('Đã phát lệnh test còi tại chỗ trên trạm cảm biến (Buzzer 2s)', 'info');
}

function calibrateCurrentNode() {
  showToast('Đang chạy chu trình hiệu chuẩn baseline nồng độ cảm biến...', 'info');
  setTimeout(() => {
    showToast('Hiệu chuẩn hoàn tất! Offset baseline đã được lưu vào EEPROM của ESP32.', 'success');
  }, 2000);
}

function exportReportAction(format) {
  showToast(`Đang đóng gói hồ sơ sự cố & dữ liệu đo đạc định dạng ${format}...`, 'info');
  setTimeout(() => {
    showToast(`Tệp báo cáo FireGuard_${format}_${Date.now().toString().slice(-4)} đã sẵn sàng tải xuống!`, 'success');
  }, 1500);
}

function toggleSopStep(stepNum) {
  showToast(`Đã cập nhật tiến độ bước ${stepNum} trong quy trình SOP`, 'info');
}

function completeCurrentSop() {
  const pctEl = document.getElementById('sopProgressPct');
  if (pctEl) pctEl.textContent = '100%';
  const badge = document.getElementById('navAlertBadge');
  if (badge) badge.textContent = '02 MỚI';
  showToast('Đã đóng sự cố INC-001 và lưu biên bản nghiệm thu thành công!', 'success');
}

function onZoneFilterChange(val) {
  showToast(`Đã lọc hiển thị theo: ${val.toUpperCase()}`, 'info');
}

function onGlobalSearch(query) {
  if (!query) return;
}

function openAddDeviceModal() {
  showToast('Mở hộp thoại đăng ký trạm cảm biến ESP32 mới', 'info');
}

function refreshDeviceNodes() {
  showToast('Đang đồng bộ trạng thái 32 node qua MQTT...', 'info');
}

function selectIncidentRow(incidentId) {
  showToast(`Đã nạp hồ sơ luồng xử lý sự cố: ${incidentId}`, 'info');
}

// 4. Toast Notification Utility
function showToast(message, type = 'info') {
  const container = document.getElementById('toastContainer');
  if (!container) return;

  const toast = document.createElement('div');
  const colors = {
    info: 'bg-surface-container-highest border-tertiary text-on-surface',
    success: 'bg-surface-container-highest border-tertiary text-tertiary font-medium',
    warning: 'bg-secondary-container text-on-secondary font-medium border-secondary',
    error: 'bg-error-container text-on-error-container font-bold border-primary-container animate-pulse'
  };

  toast.className = `p-3 rounded-lg border shadow-xl text-[12px] font-body-sm flex items-center gap-2 transition-all transform duration-300 pointer-events-auto ${colors[type] || colors.info}`;
  
  const iconName = type === 'error' ? 'error' : type === 'warning' ? 'warning' : type === 'success' ? 'check_circle' : 'info';
  toast.innerHTML = `<span class="material-symbols-outlined text-[18px]">${iconName}</span><span>${message}</span>`;

  container.appendChild(toast);

  setTimeout(() => {
    toast.style.opacity = '0';
    toast.style.transform = 'translateY(10px)';
    setTimeout(() => toast.remove(), 300);
  }, 3500);
}

