# Sổ nghiên cứu 2.2.0-test2 → test3 (máy Windows, 17–18/09/2026)

Ghi theo thời gian thực trong lúc làm; báo cáo tổng hợp ở `REPORT.md`. Mọi số liệu đo trên máy: i9-13900HK (14 nhân / 20 luồng),
RAM 32 GB, `C:` NVMe KIOXIA 4 TB (NTFS), nguồn `G:` SanDisk Extreme 4 TB qua USB (exFAT, **chỉ đọc**), Windows 11 26200,
Windows Defender bật (real-time), phiên làm việc không có quyền admin.

## Môi trường

- Máy chưa có .NET SDK → cài `Microsoft.DotNet.SDK.10` 10.0.401 qua winget.
- `libs/sony-sdk` trong repo **giống từng byte** bộ gốc `Downloads\sdk-fpkg279-fixdss3` (SHA-256 mọi tệp).
- `prospero-pub-cmd.exe env_info`: *The Maximum Number of Worker Threads: 20*; SDK **tự kết nối mạng** (online check phiên bản
  Publishing Tools, 2.79 "expired") ở cả `env_info` lẫn mỗi lần `img_create`.
- Test trên Windows lúc đầu: 258/264 pass; 6 hỏng đều là lỗi test viết trên macOS (tên tệp có `< > "`, tách dòng `\n`, tạo symlink
  không quyền, so byte với Python ghi CRLF) → đã sửa, **265/265 + test mới đều pass**.

## Lỗi an toàn đã sửa

- `SonySdkPathAliases.DefaultBases` vẫn còn ứng viên "gốc ổ NGUỒN" (có thể tạo junction trên ổ game) — bản sửa 18:10 trên Mac chưa
  có trong working tree này → bỏ hẳn tham số nguồn.

## Độ trung thành với bộ công cụ (fixdss3) trên Windows

- Script Python chạy trên Windows ghi `.gp5`, `.playgo-scenario.json`, `.gp5-assets/.../param.json` bằng **CRLF** (chế độ văn bản),
  ứng dụng ghi LF. Publishing Tools **chép nguyên playgo-scenario.json vào gói** → gói lệch 15 byte (283 vs 268).
- Sửa: `SonySdkProject.WriteScriptText` ghi CRLF trên mọi máy.
- Kiểm chứng: fixture `yotei-mini-1chunk` (51 tệp) build bằng `build.bat` gốc và bằng ứng dụng (tắt 3 sửa đổi param tuỳ chọn):
  cùng 1 327 701 byte; GP5 (trừ đường dẫn), scenario, param.json **giống từng byte**; 1 461 byte khác trong gói đều là dấu thời gian
  lúc build (`0x6aabcf65` = 11:30:45 vs `0x6aabd045` = 11:34:29, `creationDate`, giờ trong zip SI) và digest phụ thuộc.

## Lỗi mất thoại — Ghost of Yōtei (PPSA26344 01.512.000)

- `sce_sys/playgo-chunk.dat` gốc: **35 chunk, 1 kịch bản, initial_chunk_count = 32**, 27 ngôn ngữ hỗ trợ, mặc định en-US.
  Chunk 1–31 `pgc_lang_*` (29 chunk có mask riêng; ja/en mọi ngôn ngữ), 32–34 `pgc_game/lategame/coop`.
- Bảng tệp: 95 370 mục, **đủ 95 370 tệp trên đĩa** (dump đầy đủ). Thoại thật ở 11 chunk (ja, en, fr, es, de, it, pt, ru, pl, br,
  latino): `cache_ps5/lang_<lang>_audio.xpps` + `cache_ps5/loc/conv_*_<xx>.larc`, ~1,7 GB mỗi ngôn ngữ; các chunk còn lại chỉ có
  `cache_ps5/pgc_lang_<x>_dummy_file`.
- `eboot.bin` import `libScePlayGo`, `libScePlayGoDialog`, có chuỗi `CPlayGoProgressThunk`, `Crashing Due to PlayGo Hang` → game dùng
  PlayGo API. Bản dump là **backport** (deckerr97) với `fakelib/libScePlayGo.sprx` giả; fixdss3 loại module này → trong gói FPKG
  game dùng PlayGo thật của máy → hỏi chunk ngôn ngữ (id 1..31) → gói script gốc chỉ có chunk 0 → ngôn ngữ thoại xám / không thoại.
- Engine tích hợp (LibProsperoPkg 0.6.8): tạo 100 chunk tự động "mọi ngôn ngữ" (tài liệu thư viện) → id 1..34 tồn tại → nhiều khả
  năng không bị; cần PS5 xác nhận.
- Fixture `yotei-mini` (bảng PlayGo thật + 49 tệp giả đúng đường dẫn, 2 tệp/chunk) build bằng ứng dụng: SDK nhận GP5 35 chunk (mask
  trùng nhau), gói mới **IDENTICAL-STRUCTURE** (chunk, mask, nhãn, kịch bản, initial, thứ tự, ngôn ngữ mặc định; 49/49 tệp cùng chunk);
  `playgo-chunk.dat` mới cùng 2 816 byte, chỉ khác bảng extent (vị trí dữ liệu).
- SDK chấp nhận `playgo-scenario.json` gốc (có `chunkDefaultLanguage`, `chunkSupportedLanguages`) và chép nguyên vào gói;
  `playgo-chunk.dat` không đổi theo scenario → ứng dụng dùng lại tệp gốc khi khớp cấu trúc (nhiều chunk).
- Test mới: `Sdk_RebuildsManyLanguageChunksWithDuplicateMasks` (35 chunk tổng hợp qua SDK thật),
  `Create_ReusesTheOriginalScenarioJsonOnlyWhenItMatchesTheStructure`.

## param.json trong gói (SDK 2.79)

- SDK tự ghi lại param.json (CRLF), bỏ `originContentVersion`, `targetContentVersion`, đặt `sdkVersion = 0`,
  `requiredSystemSoftwareVersion = 0x1160000000000000` **bất kể đầu vào** (12.70 hay 11.00), ghi lại `pubtools`.
- Mặc định ứng dụng (từ 2.1.7, vẫn áp khi bật SDK, không bị khoá): xoá `versionFileUri`, `attribute3 → 0`, hạ firmware.
  Bộ công cụ gốc không làm 3 việc này. Hạ firmware vô tác dụng với gói SDK (SDK đặt lại). → Cần anh Sơn quyết mặc định.

## Tốc độ — quan sát lượt Yōtei (đích C: NVMe, nguồn G: USB)

- [1/3] GP5: ~1 phút (liệt kê 95 386 tệp + ghi GP5 23 MB).
- [2/3] img_create: trước khi in "Creating an image..." mất 2 phút 43 giây; sau đó pha "Checking files…" chạy **~45 tệp/giây**
  (tiến trình SDK: 2–5 luồng, ~35 % một nhân, ~40 thao tác đọc 1 KB/giây); MsMpEng (Defender) ~50–90 % một nhân, ~1 100 thao tác/giây.
- Cùng lúc, Python mở + đọc 1 KB **11 000 tệp/giây** trên G: (cả tệp SDK chưa chạm) → mở tệp không phải nút thắt; cần thí nghiệm
  đối chứng (NTFS vs exFAT, số tệp) sau lượt này.
- Cờ SDK: `img_create --compression_level [-4...9]` (mặc định 7), không có cờ số luồng cho img_create (`--multithread` thuộc lệnh
  benchmark nội bộ). `.naps_metric.json` cạnh gói thô ghi `"threads": 20`, `"compression level"`, `"time microseconds"`.

## Pha kiểm tra tệp của img_create = Windows Defender (thí nghiệm có đối chứng, 17/09 ~19:00)

- Fixture tổng hợp trên NVMe: 2 011 / 4 011 / 8 011 tệp nhỏ → "Checked N files in" 24,4 / 49,1 / 97,5 s (tuyến tính, ~12 ms/tệp).
- Chạy lại cùng tệp: 0,36 s. Tệp mới, Python mở trước tuần tự: 22,1 s rồi SDK 0,43 s. Tệp mới, SDK trước: 22,5 s, lần hai 0,37 s.
  → chi phí là Defender quét tệp ở lần mở đầu (tiến trình nào mở cũng vậy), SDK tự nó chỉ ~0,2 ms/tệp.
- Mở trước song song 2 000 tệp mới: 4 luồng 424 tệp/s, 16 luồng 698, 32 luồng 740, 64 luồng 683; sau đó SDK kiểm tra 0,36–0,54 s.
- Đọc 1 byte tệp trên exFAT G: không đổi LastAccessTime (kiểm tra `nptitle.dat`: vẫn 2026-09-14).
- Yōtei (không quét trước, có thí nghiệm chạy song song): "Creating an image" 18:41:15 → bắt đầu nén ~19:04, tức ~23 phút chỉ cho kiểm tra.
- → `SonySdkPrescan` (mặc định bật trên Windows, không đổi gói).

## Pha nén của Yōtei (mức 7)

- 19:10: 25 luồng, CPU tổng 99–100 %, đọc G: 35–59 MB/s. 19:17: SDK ~690 % CPU, G: 95–98 % rảnh (10 ms/đọc, ~1 MB/đọc) → giới hạn là
  thuật toán nén chứ không phải ổ. Mức 7 của Publishing Tools = Oodle "Optimal" (chậm mỗi nhân). Đã đọc 20,3/184 GB lúc 19:17.

## Khảo sát 25 game trên G: (docs/research/game-survey.md)

- Chỉ Ghost of Yōtei còn `playgo-chunk.dat`; 24 bản dump khác đã mất bảng PlayGo (Cyberpunk, Far Cry 6, Spider-Man Remastered,
  TLOU I/II còn `playgo-scenario.json`; TLOU I/II có 2 kịch bản "Left Behind"/"No Return" + ảnh playgo-scenario00/01).
- Keystone 96 byte: đủ cả 25. Tệp ≥ 4 GiB ở 19 game (lớn nhất 67,7 GiB). Returnal 286 230 tệp, đường dẫn tương đối dài nhất 199.
- Tên ngoài ASCII: chỉ tệp quảng cáo `更多资源请访问 2468c.com.url` (Frostpunk 2, PRAGMATA) → SDK từ chối → `*.url` thành tệp rác.
- Tên có `'` (KARMA), `,` (SAROS, Returnal), `#` (Yōtei) → SDK nhận. Thư mục TLOU I / Spider-Man có `U+F028` (khoảng trắng cuối
  do macOS mã hoá) → bí danh ASCII.
- SDK nhận: `sce_sys/playgo-scenario00.png`, `sce_sys/libScePlayGo.sprx` (FATAL FRAME II), `icon0_09.png`, `pic2_09.png`, `*.esbak`,
  `fakelib/debug/`, 100/255 chunk trống, 2 kịch bản.
