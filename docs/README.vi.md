<div align="center">

<img src="banner.png" alt="PSVIETHOA FPKG Builder" width="100%" />

🇬🇧 English: [README.md](../README.md)

**Tạo gói FPKG (FIH debug) cho PS5 từ thư mục ứng dụng, ảnh đĩa `.exfat` hoặc dự án GP5 — và giải nén các gói có sẵn — trên macOS và Windows.**

Giao diện song ngữ (Tiếng Việt / English) · Preset tốc độ · PFS v2 / v3 · Ảnh `.exfat` / `.ffpfsc` · Dự án GP5 · Giải nén gói · Kiểm tra cập nhật · Kiểm tra dung lượng trống & tệp rác · Thời gian còn lại & thông lượng · Tự động kiểm tra gói · CLI

<a href="https://github.com/thanhsondev/PSVIETHOA-FPKG-Builder/releases/latest"><img alt="Download" src="https://img.shields.io/badge/Download-Releases-22C55E?style=for-the-badge&logo=github" /></a>

![Platform](https://img.shields.io/badge/macOS-Apple%20Silicon%20%2B%20Intel-0F172A?logo=apple)
![Platform](https://img.shields.io/badge/Windows-x64-0F172A?logo=windows)
![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![UI](https://img.shields.io/badge/UI-Avalonia%2011-8B5CF6)
![Languages](https://img.shields.io/badge/UI-VI%20%2F%20EN-22C55E)
![Version](https://img.shields.io/github/v/release/thanhsondev/PSVIETHOA-FPKG-Builder?label=version&color=F59E0B)
![Tests](https://img.shields.io/badge/tests-170%20passing-22C55E)

</div>

<p align="center">
  <img src="screenshots/vi-source.png" width="32%" />
  <img src="screenshots/vi-extract.png" width="32%" />
  <img src="screenshots/vi-advanced.png" width="32%" />
</p>

<div align="center">

**Ghi công:** PSVIETHOA — Nguyễn Thanh Sơn & Ngô Phi Phương · **Main project:** [Drakmor](https://github.com/) (LibProsperoPkg)

</div>

---

## Vì sao?

Công cụ tạo FPKG cho PS5 hiện có (`LibProsperoPkg.Gui`) là ứng dụng **WPF chỉ chạy trên Windows**. PSVIETHOA FPKG Builder được viết lại hoàn toàn bằng **C# / .NET 10 + Avalonia UI**, chạy native trên **macOS (Apple Silicon & Intel) và Windows**, bổ sung **giao diện song ngữ**, nhận **ảnh đĩa `.exfat`** làm nguồn, và chú trọng vào việc **chạy mượt, nhanh khi tạo gói từ những thư mục game rất lớn** (vài chục GB). Lõi tạo gói là **LibProsperoPkg** của **Drakmor** (bản dựng tháng 9/2026 phát hành cùng fpkg‑gui 0.6.8), nên gói tạo ra chính xác đến từng byte.

> Kết quả là một **FPKG debug** (ảnh FIH, signed byte `0x00`) — chỉ cài được trên **PS5 đã bật chế độ debug**.

## Có gì mới trong 2.1.x

- **2.1.9 — engine từ fpkg‑gui 0.6.8, kiểm tra gói, mẫu DLC, sửa lỗi khác ổ**: engine tự tạo lại mọi bảng PlayGo (1–255 khối, mặc định 100; giữ `playgo-chunk.dat` thì chỉ lấy số khối, tệp PlayGo hỏng bị bỏ thay vì làm dừng lượt tạo gói), ép DRM `"standard"` trong bộ nhớ, và công cụ hạ `requiredSystemSoftwareVersion` về SDK của game (`--keep-required-fw`). Mỗi gói tạo xong được engine kiểm tra: chữ ký CNT, toàn bộ bố cục PlayGo, NAPS, inode. Tuỳ chọn **kiểm tra đầy đủ** giải nén thử mọi tệp (`--full-verify`). Chế độ Giải nén gói có thêm nút **Kiểm tra nhanh / Kiểm tra đầy đủ** (`fpkg-cli verify --full`) và **Xuất mẫu DLC** cho gói DLC có dữ liệu (`sce_sys` + dự án `.gp5`, `fpkg-cli pkg-dlc-template`). Thư mục tạm khác ổ với nguồn hoặc thư mục xuất nay chạy được: gương chuyển sang thư mục tạm của hệ thống khi ổ không tạo được liên kết (exFAT/FAT32 trên Windows), ảnh giải nén được sửa tại chỗ, thư mục tạm đã chọn không còn bị đặt lại khi mở app, và ổ FAT32 được cảnh báo giới hạn 4 GB. Trên macOS, gói tạo qua ổ exFAT/FAT không còn lẫn tệp `._*`, và thư mục tạm có tên tiếng Việt lại xoá được.
- **2.1.8 — nguồn `.ffpkg` và macOS hết phải giải nén**: tệp `.ffpkg` là ảnh hệ tệp **UFS2** của FreeBSD mà thư mục gốc chính là thư mục ứng dụng; cả macOS lẫn Windows đều không gắn được UFS nên công cụ tự phân giải (siêu khối, nhóm trụ, inode, khối gián tiếp) và hỗ trợ ở mọi chỗ như `.exfat` — nút **Tệp .ffpkg** mới, kéo thả, `fpkg-cli --source x.ffpkg`. Trên macOS, ảnh `.exfat` nay được **gắn và đọc tại chỗ** ngay cả khi phải bỏ tệp hay sửa `param.json`, vì thư mục gương dựng thẳng trên ổ chỉ đọc thay vì giải nén cả ảnh. Hàng nút chọn nguồn tự xuống dòng khi cửa sổ hẹp.
- **2.1.7 — sửa lỗi "application error" khi mở game ngay từ khâu đóng gói**: gói FPKG là một ảnh liền khối còn PlayGo được thiết kế cho kiểu cài theo khối, nên dữ liệu PlayGo của bản dump làm PS5 đi tìm những khối không tồn tại (còn game đứng ở splash mà không báo lỗi là bệnh khác, nằm ở kernel). Nay mặc định áp dụng đủ cách sửa cho **thư mục, ảnh `.exfat`, `.ffpfsc` và dự án GP5**: bỏ các tệp `sce_sys/playgo*` (thư viện tự tạo bộ mới khớp với ảnh), xoá `versionFileUri` và đặt `attribute3` về 0 — ba công tắc trong tuỳ chọn nâng cao, CLI `--keep-playgo` / `--keep-version-uri` / `--keep-attribute3`. Công cụ không bao giờ đụng vào `fakelib/libScePlayGo.sprx` (chính engine tự loại module này, như `libSceAmpr.sprx`). Tệp `ampr_emu.index` còn sót cũng được bỏ (`--keep-ampr`). Bản thân engine luôn xoá `fakelib/libScePlayGo.sprx` và `fakelib/libSceAmpr.sprx` và không có tuỳ chọn nào giữ lại; công cụ này không bao giờ đụng vào hai tệp đó. Bộ `dlc_emu` của Drakmor vẫn nằm trong gói, và khi bản dump có nó thì thẻ thông tin nguồn mời **tạo một gói DLC riêng cho từng mục trong `dlc_emu.ini`** (`fpkg-cli dlc-from-ini`), mỗi gói mang giấy phép RIF riêng. Hai ô tích "DRM standard" và "Bỏ playgo* cũ" nằm cạnh nút Tạo gói, kèm nút **Khôi phục mặc định**. Khi thư mục xuất đã có gói cùng tên, ứng dụng hỏi ghi đè, giữ bản cũ (`… (1).pkg`) hay huỷ. Thư mục nguồn, ảnh và tệp `.gp5` chỉ được ĐỌC: lúc tạo gói, công cụ dựng một thư mục gương của bản dump trong thư mục tạm bằng các liên kết, bỏ tệp cần bỏ và ghi `param.json` đã sửa ngay trong gương, nên không có gì trong thư mục của bạn bị mở ra để ghi.
- **2.1.6 — Windows cũng gắn được ảnh**: ổ ảo chỉ đọc (Dokan) do chính bộ đọc exFAT của ứng dụng phục vụ, phơi thư mục ứng dụng trong ảnh `.exfat` **hoặc `.ffpfsc`** để engine đọc thẳng từ ảnh như `hdiutil` trên macOS — không còn giải nén 149 GB, không cần thêm dung lượng; tệp rác tự ẩn và DRM được ép ngay trên ổ ảo. **Bộ cài Windows** (`Setup.exe`) cài ứng dụng và driver Dokan kèm theo trong một lần; bản zip portable tự bật driver ngay lần mở đầu (chỉ thấy hộp UAC) — cách nào cũng không phải tải gì thêm. Ô **Thành phần & plugin** mới trong tuỳ chọn nâng cao kiểm tra thật engine, khoá, Kraken, Oodle gốc, gắn ảnh và chống ngủ máy có hoạt động không. Không có driver thì vẫn giải nén như cũ.
- **2.1.5 — engine từ fpkg‑gui 0.6.5 + sửa DRM**: `applicationDrmType` được ép thành `"standard"` trong lúc tạo gói (gói tạo với DRM `"free"` hiện biểu tượng khoá trên PS5 và không chạy được); `param.json` nguồn được khôi phục nguyên vẹn sau đó, ảnh chỉ đọc sẽ được giải nén thay vì gắn khi cần (công tắc trong tuỳ chọn nâng cao, CLI `--keep-drm`). **Chống đầy đĩa**: khi ổ tạm hoặc ổ xuất hết chỗ, quá trình tạo gói tạm dừng để bạn giải phóng dung lượng rồi thử lại.
- **2.1.4 — nguồn `.ffpfsc`**: container PFS PS5 chứa bản dump exFAT nén PFSC mở như ảnh `.exfat` (giải nén trực tiếp, không tạo tệp trung gian; nút **Tệp .ffpfsc** riêng, kéo thả, `fpkg-cli inspect / build --source x.ffpfsc`). **Kiểm tra cập nhật**: huy hiệu xanh ở header khi GitHub có bản mới (kiểm tra mỗi lần mở, nút ↻, `fpkg-cli check-update`).
- **2.1.1 – 2.1.3**: trích xuất theo bố cục Sony (`sce_sys` tạo gói lại được), thư mục xuất tự đặt, preset "Tiêu chuẩn", huy hiệu phiên bản, xoá lịch sử, lưới an toàn giao diện.

- **Engine LibProsperoPkg được cập nhật** — đọc / nén / ghi gối đầu nhau với ít bản sao trung gian hơn, khử trùng lặp khối và bộ đệm nén dùng trong lượt tạo gói, Kraken tích hợp được cải thiện (nhất là mức 8–9), tự chuyển sang Kraken tích hợp khi không có Oodle, sửa lỗi xử lý tệp và hủy giữa chừng, và gói đã có được giữ nguyên nếu lần tạo lại thất bại.
- **Chọn PFS v2 / v3**, **kích thước khối nén Kraken** tuỳ chỉnh (128–256 KiB), **mẫu shuffle trước nén** và **tự phân tích chọn shuffle tốt nhất** (tối ưu texture của PFS v3 bằng cách chọn hoán vị — rất chậm, chỉ phát huy đầy đủ ở Kraken mức 9), **tối ưu bố cục vật lý** tuỳ chọn. Gói PFS v3 cần **firmware PS5 7.00 trở lên**; ứng dụng sẽ cảnh báo về điều này.
- **Preset được ánh xạ lại theo bộ nén mới**: mặc định giờ là **Tiêu chuẩn** = Kraken 4, chính là bộ nén mà engine 2.0.0 dùng cho "Kraken 7" (cùng kích thước, cùng tốc độ, cộng thêm lợi ích từ khử trùng lặp khối / tối ưu bố cục); **Nhỏ nhất** = Kraken 7 *Optimal* là chế độ nén sâu mới (nhỏ hơn ~3 %, chậm gấp 5–6 lần); preset **Tối đa** mới (Kraken 9 + PFS v3 + phân tích shuffle).
- Hiển thị **thông lượng** thực tế trong bảng tạo gói, tiến trình mượt hơn với các tệp nhiều GB, ước tính thời gian còn lại được tinh chỉnh lại.
- CLI: `--pfs`, `--block-size`, `--shuffle`, `--shuffle-analysis`, `--shuffle-prediction-level`, `--skip-pfs-input-check`, `--no-layout-optimization`, `--source-mode`, `--project`, `--preset sony|standard`; lệnh mới `pkg-info`, `pkg-list`, `pkg-extract`.
- **Giải nén gói có sẵn** — chế độ **Giải nén gói** mới (thanh Tạo gói | Giải nén gói ngay dưới header): mở một tệp `.pkg` (hoặc kéo–thả vào cửa sổ) để xem header FIH / CNT, mọi trường `param.json`, icon và toàn bộ danh sách tệp của ảnh trong PPR‑PFS; tích chọn tệp hoặc thư mục rồi giải nén chúng (hoặc cả gói), hay xuất các entry `sce_sys`. Gói PLAINTEXT_NOAUTH được đọc thẳng từ `.pkg` theo từng khối 4 MiB mà không dựng lại ảnh; gói Native (AES‑XTS) được giải mã ra thư mục tạm trước. CLI: `pkg-info`, `pkg-list`, `pkg-extract`.
- **Nguồn là dự án GP5** (`.gp5`, giống bộ chọn Folder | GP5 của fpkg‑gui): chọn dự án Publishing Tools / fpkg‑gui ở bước 1 hoặc kéo–thả vào cửa sổ — bố cục Normal và Flat, mặt nạ loại trừ và đường dẫn tương đối đều được tôn trọng, passcode lấy từ dự án; `fpkg-cli inspect` / `build --source x.gp5`; tuỳ chọn nâng cao **Cách đọc nguồn** cho phép chọn giữa *Tự động* (dùng tệp `.gp5` ở cấp trên cùng) và *Chỉ thư mục*.

Xem [CHANGELOG.md](../CHANGELOG.md) để biết chi tiết.

## Tính năng

| Nhóm | Chi tiết |
|---|---|
| **Nguồn** | Thư mục ứng dụng (chứa `sce_sys`) **hoặc ảnh đĩa exFAT (`.exfat`)** — volume thuần, MBR hay GPT — **hoặc container `.ffpfsc`** (ảnh PFS PS5 chứa bản dump exFAT nén PFSC; giải nén trực tiếp qua thư viện, không tạo tệp trung gian). Bộ đọc exFAT thuần .NET đọc `param.json` / icon / dung lượng trực tiếp từ ảnh. Trên macOS, ảnh được **gắn ở chế độ chỉ đọc bằng `hdiutil` (không sao chép)**; trên Windows, ảnh được **gắn thành ổ ảo chỉ đọc (Dokan, kèm sẵn — cài một cú bấm)** do bộ đọc exFAT của ứng dụng phục vụ, dùng được cả `.ffpfsc`, tự ẩn tệp rác và ép DRM ngay trên ổ. Không có driver, hoặc trên macOS khi ảnh có tệp rác, ảnh được **giải nén ra thư mục tạm** (bỏ qua `.DS_Store`, `._*`, `Thumbs.db`…) rồi tự dọn sau khi xong. **Hoặc dự án GP5 (`.gp5`)** của Publishing Tools / fpkg‑gui — bố cục Normal (`rootdir` + mặt nạ loại trừ) hoặc Flat (liệt kê từng tệp); đường dẫn tương đối tính từ thư mục chứa dự án, thẻ thông tin hiển thị bố cục và thư mục gốc của dự án, và chỉ những tệp dự án liệt kê mới được đếm. |
| **Ngôn ngữ** | Tiếng Việt / English, chuyển ngay trên header, lựa chọn được ghi nhớ. CLI nhận `--lang vi\|en` hoặc biến môi trường `FPKG_LANG`. |
| **Tạo gói** | Ảnh FIH debug, PLAINTEXT_NOAUTH hoặc Native AES‑XTS, APP / Homebrew / DLC, PlayGo tự động (1–64 khối), ghi đè SDK (1–11), passcode, bản dựng xác định (deterministic). |
| **Nén** | **Bộ nén Kraken tích hợp** (thuần .NET, chạy trên mọi hệ điều hành, đa luồng) hoặc **Oodle gốc** qua `libScePubTools.dll` (Windows, tự chuyển về Kraken tích hợp khi không có). **PFS v2** (mặc định, tương thích rộng nhất) hoặc **PFS v3** (mẫu shuffle trước nén, tự phân tích shuffle cho từng khối), kích thước khối Kraken 128–256 KiB, tối ưu bố cục vật lý. |
| **Tốc độ** | Preset **Nhanh · Tiêu chuẩn · Nhỏ nhất · Tối đa**. **Tiêu chuẩn** (mặc định) = Kraken mức 4, chính là bộ nén mà engine 2.0.0 dùng cho "Kraken 7": cùng kích thước, cùng tốc độ. **Nhỏ nhất** = Kraken 7 *Optimal* — chế độ nén sâu mới, nhỏ hơn khoảng 3 % nhưng chậm gấp 5–6 lần. **Tối đa** = Kraken 9 + PFS v3 + phân tích shuffle. Tuỳ chỉnh mức nén `-4…9`, số luồng, và chế độ không nén để test nhanh. |
| **Trước khi tạo gói** | Đọc metadata + icon, quét dung lượng song song, **kiểm tra dung lượng trống** trên ổ tạm / ổ xuất, và **phát hiện & dọn tệp rác hệ điều hành**. |
| **Trong khi tạo gói** | Thanh tiến trình tổng thể theo trọng số giai đoạn kèm **thời gian còn lại (ETA)** và **thông lượng** thực tế, tiến trình tính theo byte ngay bên trong các tệp nhiều GB, nhật ký ảo hoá 20.000 dòng, hủy an toàn, và **chống máy ngủ** (`caffeinate` / `SetThreadExecutionState`). |
| **Sau khi tạo gói** | Kiểm tra cấu trúc FIH / PFS ngoài, SHA‑256 tuỳ chọn, bảng kết quả, sao chép / lưu nhật ký, mở thư mục kết quả. |
| **Giải nén gói** | Chế độ **Giải nén gói**: header FIH / CNT, bản đồ vùng, mọi trường `param.json`, icon và danh sách tệp của một FPKG debug; lọc và tích chọn tệp / thư mục, giải nén phần đã chọn hoặc cả gói với tiến trình và nút hủy, xuất `sce_sys` (param.json, icon0.png, playgo…) và các mục SI, SHA‑256 tuỳ chọn. Gói plaintext được đọc tại chỗ (bộ đệm khối 4 MiB, không tạo ảnh tạm); gói Native được giải mã ra thư mục tạm trước; gói retail chỉ xem được thông tin. |
| **CLI** | `fpkg-cli build / inspect / verify / clean-junk / info / pkg-info / pkg-list / pkg-extract` cho tạo gói hàng loạt, script và CI. |

## Tải về & chạy

Tải gói nén cho nền tảng của bạn từ trang [**Releases**](https://github.com/thanhsondev/PSVIETHOA-FPKG-Builder/releases/latest). Mỗi bản dựng đều **self‑contained** — không cần cài .NET.

- **macOS** — giải nén, lần đầu chạy hãy chuột phải `PSVIETHOA FPKG Builder.app` → chọn **Open** (Mở) (ứng dụng được ký ad‑hoc).
- **Windows** — giải nén và chạy `PSVIETHOA FPKG Builder.exe`. SmartScreen có thể cảnh báo → chọn **Run anyway**.

Mỗi gói nén đều kèm sẵn công cụ dòng lệnh `fpkg-cli`.

## Bắt đầu nhanh

1. Chuẩn bị thư mục ứng dụng PS5 đã giải nén (có thư mục `sce_sys` và `eboot.bin`), **hoặc** trỏ tới một ảnh đĩa `.exfat`.
2. Ở **bước 1**, nhấn **Chọn…** / **Ảnh .exfat**, hoặc kéo–thả thư mục / ảnh vào cửa sổ. Content ID, tên, phiên bản, dung lượng và tệp rác được nhận diện tự động.
3. Chọn preset tốc độ ở **bước 3** (Tiêu chuẩn là mặc định và tương đương "Kraken 7" của engine cũ; Nhỏ nhất = mức 7 Optimal, chậm) hoặc mở phần tuỳ chọn nâng cao.
4. Nhấn **Tạo gói PKG** (`Ctrl/⌘+B` hoặc `F5`). Theo dõi tiến trình, thời gian còn lại và nhật ký; khi xong, cấu trúc gói được kiểm tra tự động.

## Dòng lệnh

```bash
fpkg-cli info --lang en
fpkg-cli inspect "/path/PPSA12345"                 # folder
fpkg-cli inspect "/path/PPSA12345.exfat"           # exFAT image — read directly, no mount
fpkg-cli inspect "/path/PPSA12345.ffpfsc"          # .ffpfsc container — inner exFAT decompressed on the fly
fpkg-cli check-update                              # newer release on GitHub?
fpkg-cli build --source "/path/PPSA12345.exfat" --output "/path/out"           # default: Sony standard (level 4), exfat auto
fpkg-cli build --source "/path/PPSA12345.exfat" --output "/path/out" --exfat extract
fpkg-cli build --source "/path/PPSA12345" --output "/path/out" --preset fast --clean-junk
fpkg-cli build --source "/path/PPSA12345" --output "/path/out" --preset maximum       # Kraken 9 + PFS v3 + shuffle analysis
fpkg-cli build --source "/path/PPSA12345" --output "/path/out" --pfs v3 --shuffle PredictForBc3 --block-size 128
fpkg-cli verify "/path/out/UP9000-PPSA12345_00-XXXX-A0100-V0100.pkg" --sha256
fpkg-cli pkg-info "/path/out/UP9000-PPSA12345_00-XXXX-A0100-V0100.pkg"                # General + param.json
fpkg-cli pkg-list "/path/out/UP9000-PPSA12345_00-XXXX-A0100-V0100.pkg" --include "*.sprx"
fpkg-cli pkg-extract "/path/out/UP9000-PPSA12345_00-XXXX-A0100-V0100.pkg" --output "/path/unpacked" --include "sce_sys/**" --include "eboot.bin" --cnt
```

Mã thoát: `0` thành công · `1` tham số sai · `2` tạo gói thất bại · `3` bị hủy.

## Ảnh đĩa (.exfat / .ffpfsc)

- Nhận diện tự động theo chữ ký `EXFAT   ` ở đầu volume, hoặc bên trong phân vùng MBR/GPT; các offset thông dụng (sector 63, 2048…) cũng được dò.
- Thư mục ứng dụng được tìm tới độ sâu 3 cấp bên trong ảnh (ưu tiên gốc, ví dụ các bản dump có `sce_sys` ngay tại gốc).
- **macOS:** `hdiutil attach -readonly -imagekey diskimage-class=CRawDiskImage` → tạo gói thẳng từ điểm gắn, tháo ảnh khi xong. Chế độ *Tự động* chỉ gắn ảnh khi ảnh không có tệp rác; nếu có tệp rác thì giải nén để có thể bỏ qua chúng.
- **Windows:** bộ đọc exFAT của ứng dụng được phơi thành ổ ảo chỉ đọc qua driver [Dokan](https://github.com/dokan-dev/dokany) (`Z:\<tên ảnh>\…`), engine đọc thẳng từ ảnh — `.exfat` lẫn `.ffpfsc`, tệp rác được ẩn, DRM trong `param.json` được ép ngay trên ổ, không ghi gì vào ảnh. Bộ cài `Dokan_x64.msi` nguyên bản (2.3.1.1000, LGPL/MIT) nằm trong `app/redist/`: **Setup.exe** cài nó cùng ứng dụng, bản zip portable tự cài ngay lần mở đầu (chỉ thấy hộp UAC của Windows; từ chối thì còn nút **Bật gắn ảnh trực tiếp** trong tuỳ chọn nâng cao và `fpkg-cli install-dokan`). Không có driver thì ảnh được giải nén ra thư mục tạm như trước.
- **Windows / Linux:** giải nén bằng bộ đọc thuần .NET (3 luồng, bộ đệm 4 MB) ra `<temp>/exfat-<name>-<hash>/`, cần thêm dung lượng trống ≈ lượng dữ liệu trong ảnh; xoá sau khi tạo gói (kể cả khi hủy).
- **Đã đối chứng:** cùng một ảnh tạo gói theo hai cách — gắn bằng hdiutil và giải nén bằng bộ đọc thuần .NET — cho ra gói **giống hệt nhau từng byte** (cùng SHA‑256), tức bộ đọc khớp chính xác với driver của macOS.

- **Container `.ffpfsc`:** ảnh PFS PS5 (superblock v2, khối 64 KiB) chứa đúng một tệp nén PFSC là bản dump exFAT của game. Ứng dụng nhận diện qua header (bất kỳ đuôi nào) hoặc đuôi `.ffpfsc`, đặt bộ đọc exFAT lên lớp giải nén PFSC của thư viện (~900 MB/s, không tạo ảnh tạm) ; trên Windows có Dokan thì **gắn** như ảnh `.exfat`, còn trên macOS (hdiutil không mở được container) thì **trích** thư mục ứng dụng ra thư mục tạm trước khi tạo gói. Đo thực tế: container 1,2 GB (exFAT 4,29 GB, 2,9 GB dữ liệu game) → đọc thông tin 0,15 giây, tạo gói trọn vẹn 31 giây.

## Hiệu năng

Đo trên Mac Apple Silicon (15 nhân logic), Kraken tích hợp, bản LibProsperoPkg đi kèm 2.1.0:

| Bài đo | Cấu hình | Thời gian | Gói |
|---|---|---|---|
| 400 MB dữ liệu tổng hợp | Nhanh (Kraken 2) / **Tiêu chuẩn (4, mặc định)** | 6.8 giây / 7.5 giây | 254.3 MB / 254.2 MB |
| 400 MB dữ liệu tổng hợp | Nhỏ nhất (Kraken 7) | 16.4 giây | 250.3 MB |
| 400 MB dữ liệu tổng hợp | Tối đa (Kraken 9 + PFS v3 + phân tích shuffle) | 47.6 giây | 249.8 MB |
| Game thật 813 MB (`PPSA06438`, ảnh .exfat được gắn) | Nhỏ nhất / Nhanh | 23.4 giây / 4.9 giây | 257.2 MB / 262.8 MB |
| **Game thật 21.3 GB** (`PPSA27625`, ảnh .exfat được gắn) | Nhanh (Kraken 2) | 1 phút 59 giây | 8.86 GiB |
| **Game thật 21.3 GB** | **Tiêu chuẩn (Kraken 4, mặc định)** | **2 phút 13 giây** | **8.86 GiB** |
| **Game thật 21.3 GB** | Nhỏ nhất (Kraken 7 Optimal) | 12 phút 49 giây | 8.54 GiB |
| Container `.ffpfsc` 1.2 GB (exFAT 4.29 GB bên trong, 2.9 GB dữ liệu game) | Tiêu chuẩn (Kraken 4) | 31 giây (đọc thông tin 0.15 giây) | 1.14 GiB |
| Gói 36 GB (`PPSA21567`, 166,707 tệp) — **Giải nén gói** | liệt kê / trích | 3.8 giây / ~200 MB/s | — |

Để so sánh, bản LibProsperoPkg đi kèm 2.0.0 tạo gói cùng game 21.3 GB này trong 3 phút 40 giây, ra gói 9.03 GiB. Đo song song trên cùng bộ dữ liệu 400 MB, engine 2.0.0 cho ra gói **giống hệt nhau** ở mức 4 và mức 7 (254,212,194 byte) — "mức 7" của nó thực chất là bộ nén Normal — còn engine 2.1.0 ở mức 4 tái tạo đúng kết quả đó (254,211,794 byte) với cùng tốc độ. Vì vậy **Tiêu chuẩn** (mặc định) cho bạn kích thước "Kraken 7" cũ với tốc độ cũ, và với game 21 GB còn nhanh hơn và nhỏ hơn trước (2 phút 13 giây, 8.86 GiB). **Nhỏ nhất** là chế độ *Optimal* thực sự mới: giảm thêm 3.5 % (8.54 GiB) đổi lấy thời gian tạo gói lâu gấp 5–6 lần trên dữ liệu game vốn đã được nén sẵn. Mức 4–6 cho ra kết quả giống hệt nhau; từ mức 7 trở lên chuyển sang bộ phân tích (parser) optimal.

## Build từ mã nguồn

Cần [.NET SDK 10](https://dotnet.microsoft.com/download) (`brew install dotnet` trên macOS).

```bash
dotnet build PsViethoa.FpkgBuilder.slnx -c Release   # build everything
scripts/run-dev.sh                                   # run the GUI (macOS/Linux)
dotnet test                                          # run the test suite

scripts/publish-macos.sh osx-arm64 osx-x64           # dist/: .app + fpkg-cli + zip
scripts/publish-windows.sh                           # dist/win-x64: .exe + fpkg-cli + zip
```

<details>
<summary>Cấu trúc dự án</summary>

```
PsViethoa.FpkgBuilder.slnx
Directory.Build.props                 # net10.0, version (2.1.0), GC/PGO
CHANGELOG.md
libs/                                 # LibProsperoPkg.dll, libScePubTools.dll (Windows)
src/
  PsViethoa.FpkgBuilder.Core/         # engine, validation, exFAT reader, progress, localization
  PsViethoa.FpkgBuilder.App/          # Avalonia UI (MVVM), tokens/styles, views, assets
  PsViethoa.FpkgBuilder.Cli/          # fpkg-cli
tests/PsViethoa.FpkgBuilder.Tests/    # xUnit (170 tests)
scripts/                              # publish + dev scripts
```
</details>

## Ảnh chụp giao diện

| Giao diện sáng | Tuỳ chọn PFS v3 | Giao diện tiếng Việt |
|---|---|---|
| ![](screenshots/en-light.png) | ![](screenshots/en-advanced-pfs.png) | ![](screenshots/vi-advanced.png) |

| Đang tạo gói | Chế độ Giải nén gói (tiếng Việt) | Giải nén xong |
|---|---|---|
| ![](screenshots/en-building.png) | ![](screenshots/vi-extract.png) | ![](screenshots/vi-extract-done.png) |

| Chế độ Giải nén gói, giao diện sáng | Hướng dẫn — Ghi công |
|---|---|
| ![](screenshots/en-extract-light.png) | ![](screenshots/en-credits.png) |

## Ghi công & giấy phép

- **PSVIETHOA — Nguyễn Thanh Sơn & Ngô Phi Phương** — ứng dụng đa nền tảng, giao diện song ngữ, hỗ trợ ảnh exFAT, `fpkg-cli`.
- **Main project: [Drakmor](https://github.com/)** — tác giả **LibProsperoPkg**, lõi tạo gói FPKG (PFS v2/v3, NAPS, Kraken, phân tích shuffle, PlayGo, kiểm tra gói).
- Font: JetBrains Mono & Inter (SIL OFL). Icon: Material Design Icons (Apache 2.0).
- Cảm ơn cộng đồng PS5 homebrew và tất cả những ai đã đóng góp cho LibProsperoPkg.

> Công cụ này tạo gói **debug** phục vụ homebrew và phát triển trên các máy đã bật chế độ debug. Các tệp bên thứ ba `LibProsperoPkg.dll` / `libScePubTools.dll` được đi kèm nguyên vẹn, không chỉnh sửa. Hãy sử dụng có trách nhiệm.

<div align="center">

Thực hiện bởi **PSVIETHOA** với tất cả tâm huyết · 🇻🇳

</div>
