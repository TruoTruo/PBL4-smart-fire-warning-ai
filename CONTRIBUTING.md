# Quy tắc đóng góp

## Branch
- `main` — bản ổn định, chỉ merge từ `develop` sau khi review
- `develop` — nhánh tích hợp chung
- `feature/ai-vision`, `feature/iot-edge`, `feature/database-network`, `feature/desktop-app` — mỗi thành viên làm việc trên nhánh của mình, chỉ commit vào thư mục module tương ứng (+ docs/ nếu cần)

## Quy trình
1. Checkout nhánh feature của bạn, kéo mới nhất từ `develop`
2. Code + test trong đúng thư mục `apps/<module>` hoặc `services/<module>`
3. Tạo Pull Request vào `develop`, điền theo template
4. Ít nhất 1 thành viên khác review trước khi merge

## Quy tắc đặt tên commit
`<module>: <mô tả ngắn>` — ví dụ: `ai-vision: thêm script export ONNX`
