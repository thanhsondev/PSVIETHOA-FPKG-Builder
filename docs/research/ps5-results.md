# Kết quả test trên PS5 (anh Sơn điền)

Gói do bản 2.2.0-test3 (working tree chưa commit) tạo ở `C:\fpkg-research\out\<Title ID>\`. Claude chỉ chuẩn bị gói + checklist;
kết quả trong bảng dưới **chỉ anh Sơn điền** sau khi bấm trên PS5.

## Checklist cho mỗi gói

1. Gỡ bản cài cũ của game (nếu có) → cài gói mới (etaHEN / itemzflow / Debug Settings → Package Installer).
2. Mở game: vào được màn hình chính, không "application error" / màn hình đen / kẹt logo.
3. **Options → Audio/Language**: danh sách *ngôn ngữ thoại* (voice/spoken language) **không xám**, không đòi tải (download).
4. Chơi tới cutscene/hội thoại đầu tiên: **có tiếng thoại**.
5. Đổi ngôn ngữ thoại sang một ngôn ngữ khác có gói thoại (với Ghost of Yōtei: Japanese ↔ English ↔ French…) → vào lại cutscene → nghe đúng ngôn ngữ mới.
6. Ngôn ngữ phụ đề / giao diện đổi được bình thường.
7. Ảnh splash (pic0/pic1/pic2), icon hiển thị đúng; ứng dụng không hiện biểu tượng khoá.
8. (Tuỳ chọn) Cài lại lần nữa đè lên → vẫn chạy; khởi động lại máy → vẫn chạy.
9. Ghi firmware PS5, phương thức cài, và bất kỳ thông báo lỗi (chụp ảnh).

## Bảng kết quả

| Game (Title ID) | Gói | PlayGo gói (chunk/kịch bản) | Cài | Mở game | Thoại không xám | Có thoại cutscene | Đổi ngôn ngữ thoại | Splash/icon | Ghi chú |
|---|---|---|---|---|---|---|---|---|---|
| Ghost of Yōtei (PPSA26344) — SDK, 35 chunk | `C:\fpkg-research\out\PPSA26344\UP9000-PPSA26344_00-GHOST2SHIP000000-A01512-V01512.pkg` | 35 / 1 | | | | | | | |
| Ghost of Yōtei — gói test1 cũ trên `G:\` (1 chunk, đối chứng) | `G:\UP9000-PPSA26344_00-GHOST2SHIP000000-A01512-V01512.pkg` | 1 / 1 | đã biết: mất thoại | | | | | | |

Các game khác được thêm vào bảng sau khi khảo sát (`game-survey.md`) và build (`build-matrix.md`).
