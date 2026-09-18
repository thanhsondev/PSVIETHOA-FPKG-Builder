# PROMPT cho Claude Code (Opus 5) — máy Windows, ổ game `G:\ Extreme SSD`

> Cách dùng: mở Claude Code (model Opus 5) trong thư mục repo `PSVIETHOA-FPKG-Builder` trên máy Windows, rồi gõ:
> **"Đọc và làm theo `docs/PROMPT-OPUS-RESEARCH-ALL-GAMES.md`. Trước đó đọc `docs/HANDOFF-WINDOWS-2.2.0-test2.md`."**
> Toàn bộ phần dưới là prompt.

---

Bạn là kỹ sư phụ trách PSVIETHOA FPKG Builder (Avalonia/.NET 10, engine LibProsperoPkg + chế độ Sony SDK / Publishing Tools 2.79). Chủ dự án là anh Sơn (PSVIETHOA). Ngôn ngữ làm việc: tiếng Việt, thuật ngữ kỹ thuật giữ tiếng Anh.

Đọc trước, theo thứ tự: `docs/HANDOFF-WINDOWS-2.2.0-test2.md` (kiến trúc, trạng thái, quy tắc), `CHANGELOG.md` mục 2.2.0, `libs/sony-sdk/README.md`, `libs/sony-sdk/scripts/create-gp5-from-folder.py`, `src/PsViethoa.FpkgBuilder.Core/Services/SonySdkPlayGo.cs`, `SonySdkProject.cs`, `SonySdkBuilder.cs`, `BuildEngine.cs` (nhánh SDK).

## 0. LUẬT TUYỆT ĐỐI — ổ `G:\` (Extreme SSD) CHỈ ĐƯỢC ĐỌC

Ổ `G:\` chứa hàng chục bản dump game PS5 của anh Sơn (mỗi thư mục = 1 game, ví dụ `G:\PPSA26344 Ghost of Yōtei (01.512.000)`, `G:\Cyberpunk 2077 02.120 PPSA04029`, `G:\PPSA03396 The Last of Us Part I`, `G:\[DLPSGAME.COM]-SILENT HILL f 01.001 PPS…`, `G:\PPSA04263-app0 Grand Theft Auto V`…) và các thư mục khác (`DATA`, `dump`, `elf-arsenal_config`, `fpkg-temp`, `homebrew`, `itemzflow`, thư mục `…-pkg` do app tạo trước đây).

**NGHIÊM CẤM chỉnh sửa, xoá, đổi tên, di chuyển, tạo tệp/thư mục, đổi thuộc tính, đổi thời gian, tạo junction/symlink bên trong, hay "sửa giúp" bất cứ thứ gì trên `G:\` — kể cả tệp rác, tệp thừa, thư mục output cũ của app, `ampr_emu.index`, `license.dat`, `.DS_Store`, `Thumbs.db`.** Coi `G:\` là bằng chứng niêm phong. Cụ thể:

1. Không bao giờ chạy trên `G:\` (hoặc với đối số trỏ vào `G:\`): `del`, `erase`, `rd`/`rmdir`, `ren`/`rename`, `move`, `copy`/`xcopy`/`robocopy` có đích trong `G:\`, `robocopy /MIR /MOV /PURGE`, `attrib`, `icacls`, `takeown`, `Remove-Item`, `Move-Item`, `Rename-Item`, `Set-Content`, `Out-File`, `New-Item`, `Set-ItemProperty`, `Clear-Content`, `format`, `chkdsk /f`, `diskpart`, `defrag`, `cipher`, `compact`, `fsutil`, `mklink` (đích hay nguồn trong `G:\`), `git clean`, `git init` trong `G:\`, hay mã .NET/Python mở tệp với quyền ghi (`FileMode.Create/Append/OpenOrCreate`, `open(..., 'w'|'a'|'r+'|'wb')`, `File.WriteAll*`, `Directory.Create*`, `Directory.Delete`, `File.Delete`, `File.Move`, `SetLastWriteTime`, `SetAttributes`).
2. Không chạy `fpkg-cli clean-junk` với bất kỳ đường dẫn nào trong `G:\`; không bấm/kích hoạt tính năng dọn tệp rác của app trên nguồn `G:\`; không dùng hook `PSVIETHOA_AUTOBUILD` trỏ output vào `G:\`.
3. **Mọi output** (gói .pkg, `.gp5`, `.playgo-scenario.json`, `.gp5-assets`, `-build-logs`, tệp tạm, báo cáo, script) phải nằm ở ổ khác: dùng `<Ổ_ĐÍCH>\fpkg-research\…` (hỏi anh Sơn ổ nào, ưu tiên SSD nội bộ còn ≥ 300 GB trống). Trong app phải **bỏ tích "Tự động theo nguồn / Auto from source"** và chọn đích rõ ràng, vì mặc định app tạo `<nguồn>-pkg` cạnh nguồn = trên `G:\`. CLI luôn truyền `--output <Ổ_ĐÍCH>\…`.
4. Bí danh ASCII của app (`SonySdkPathAliases`) tạo junction ở `%TEMP%` → thư mục tạm của lượt → ProgramData → gốc ổ **đích**; từ 18:10 17/09 mã không còn dùng gốc ổ nguồn làm ứng viên (`DefaultBases(temporaryFolder, outputFolder)`), tức không bao giờ tạo gì trên `G:\`. Bản build test2 xuất 16:55 vẫn còn ứng viên "gốc ổ nguồn" ở vị trí cuối cùng (chỉ tới đó khi 4 chỗ trước đều thất bại) — khi chạy bản đó, giữ `%TEMP%` trên NTFS bình thường và đích ngoài `G:\`. Nếu không chắc: **dừng và hỏi**.
5. Đọc thì dùng `Get-ChildItem`, `Get-Content -Encoding Byte -TotalCount`, `fc`, `certutil -hashfile`, `python open(...,'rb')`, `File.OpenRead`, `FileShare.Read`. Script khảo sát phải tự kiểm tra: từ chối chạy nếu bất kỳ đường dẫn ghi nào bắt đầu bằng `G:\`.
6. Không gắn `G:\` vào Dokan, không eject, không đổi nhãn ổ, không chạy trình chống phân mảnh/quét sửa lỗi.
7. Trước MỖI lệnh shell/PowerShell hoặc đoạn mã chạm tới `G:\`, tự hỏi: "lệnh này có thể ghi/xoá gì trong `G:\` không?" — nếu có dù chỉ 1 % khả năng: **không chạy, hỏi anh Sơn**.
8. Không commit, không push (quy tắc chung của dự án: test OK anh Sơn mới cho commit). Không sửa binary trong `libs/sony-sdk/toolchain/`.

## 1. Ba mục tiêu

1. **Sửa lỗi mất thoại / ngôn ngữ thoại xám cho MỌI game** (không chỉ Ghost of Yōtei): xác định game nào bị, vì sao, sửa trong app, chứng minh bằng so sánh gói gốc ↔ gói mới và test PS5.
2. **Độ ổn định cao nhất khi build tất cả game trên `G:\`** bằng chế độ Sony SDK (mặc định) và engine tích hợp (dự phòng): mọi thư mục game phải build được hoặc báo lỗi rõ ràng, có cách xử lý; không treo, Cancel sạch, không đụng nguồn.
3. **Tốc độ nén nhanh nhất có thể** (lượt Yōtei 184 GB mất 2 h 13 min khi nguồn và đích cùng ổ `G:\`), với điều kiện: mặc định vẫn y hệt toolkit, mọi tăng tốc là tuỳ chọn opt-in và gói vẫn cài/chạy trên PS5.

## 2. Giai đoạn A — Khảo sát read-only toàn bộ `G:\` (làm trước, ~1 buổi)

Viết `scripts/research/survey-games.py` (hoặc lệnh `fpkg-cli` mới ở chế độ chỉ đọc — nếu dùng `fpkg-cli inspect`, đọc mã `Inspect()` trong `CommandLine.cs` để chắc chắn nó không ghi gì vào nguồn; `clean-junk` thì cấm). Script chỉ mở tệp `rb`, ghi kết quả vào `docs/research/game-survey.json` + `docs/research/game-survey.md`.

Với mỗi thư mục cấp 1 của `G:\` có `sce_sys/param.json` (bỏ qua `DATA`, `dump`, `homebrew`, `itemzflow`, `elf-arsenal_config`, `fpkg-temp`, `System Volume Information`, `$RECYCLE.BIN`, thư mục `-pkg`), ghi:
- Tên thư mục, có ký tự ngoài ASCII không, độ dài đường dẫn dài nhất (Windows MAX_PATH 260 — SDK có bị không?), tên tệp có ký tự đặc biệt (`& ' " < > # % ! [ ] ( ) ; , =` khoảng trắng đầu/cuối, dấu chấm cuối), tệp trùng tên chỉ khác hoa/thường, symlink/reparse point, tệp 0 byte, tệp > 4 GiB, tổng số tệp, tổng dung lượng, độ sâu cây.
- `param.json`: `titleName`, `contentId`, `titleId`, `contentVersion`, `masterVersion`, `applicationDrmType`, `applicationCategoryType`, `attribute`/`attribute2`/`attribute3`, `localizedParameters` (ngôn ngữ nào có), `sdkVersion`, `requiredSystemSoftwareVersion`, `versionFileUri`, `pubtools`, `kernel`, `playgo`-liên quan nếu có, và **các khoá lạ** (liệt kê mọi khoá cấp 1 để không bỏ sót).
- `sce_sys/`: có `keystone` không và **đúng 96 byte không** (toolkit bắt buộc; thiếu = không build được bằng SDK → đếm bao nhiêu game như vậy), `pfs-version.dat`, `license.dat`/`license.info`, `imagedigs.dat`, `about/right.sprx`, `*.dds`, `pic0/pic1/pic2.png` (thiếu png nào), `icon0.png`, `nptitle.dat`, `npbind.dat` (add-on/DLC — có thể là gói ngôn ngữ!), `playgo-manifest.xml` (nếu có: đọc cấu trúc chunk/scenario/languages dạng XML — so với bản parse nhị phân), `trophy2/`, `uds/`, `cp/`, `keystone`, thư mục lạ.
- **PlayGo** (`playgo-chunk.dat` layout ở handoff §4): số chunk, số scenario, mask ngôn ngữ hỗ trợ + mặc định, từng chunk: id, languages, label; từng scenario: id, initial_chunk_count, thứ tự chunk, label; `playgo-ficm.dat`: số tệp, histogram tệp/chunk; `playgo-hash-table.dat`: count, `\x7fFLT` đúng không; tính `ProsperoPs5FlatPathTable.HashPath` cho toàn bộ tệp thật trong thư mục và đếm bao nhiêu tệp khớp bảng hash (tệp không khớp = tệp thêm sau khi dump/không thuộc gói gốc). **Phân loại**: `1-chunk` / `multi-chunk không ngôn ngữ` / `multi-chunk có chunk ngôn ngữ`.
- Dấu vết giả lập/dump: `ampr_emu.index`, `fakelib/*` (liệt kê), `fakelib2/`, `dlc_emu*`, `sce_module/` nội dung, `.gp5-assets/` (tàn dư lượt build cũ nằm TRONG nguồn — ghi nhận, không xoá), tệp rác (`.DS_Store`, `Thumbs.db`, `desktop.ini`, `._*`).
- **EBOOT đã bị vá?** (cảnh báo từ người phụ trách backport 18/09: game từng chạy dạng thư mục qua ShadowMount/itemzflow có thể mang `eboot.bin` đã patch AMPR/PlayGo → đóng thành FPKG là lỗi, không builder nào sửa được; cần eboot gốc hoặc dump lại). Ghi: SHA-256 của `eboot.bin` và mọi `*.bin/*.prx/*.sprx` trong `sce_module/`, có tệp sao lưu kiểu `eboot.bin.bak/.orig/.old/_orig`, `*.bak` không, kích thước/ngày sửa `eboot.bin` lệch hẳn so với các tệp khác cùng dump, có `ampr_emu.index`/`fakelib`/`dlc_emu` (dấu hiệu từng chạy folder-mode), so hash eboot giữa 2 bản dump cùng game/phiên bản nếu có. Không có cơ sở dữ liệu hash gốc thì chỉ đánh dấu "nghi vá", không kết luận.
- Nguồn dump (tiền tố `[DLPSGAME.COM]`, hậu tố `-app`/`-app0`, dump tool nào nếu đoán được).

Đầu ra: bảng tổng hợp + phần "Bất thường" cho từng game. Đây là bản đồ cho giai đoạn B và C.

## 3. Giai đoạn B — Lỗi thoại cho tất cả game

Giả thuyết hiện tại (đã cài trong test2, chưa xác nhận trên PS5): script toolkit luôn tạo 1 chunk/1 scenario; game có chunk theo ngôn ngữ hỏi hệ thống "chunk ngôn ngữ đã cài chưa" → không có → xám/không thoại. App test2 tái tạo cấu trúc PlayGo gốc trong GP5 (`SonySdkPlayGo.ChunkInfoXml`, `file chunk="N"`).

B1. Từ khảo sát, lập danh sách game **multi-chunk có chunk ngôn ngữ** (ứng viên bị lỗi) và **multi-chunk không ngôn ngữ** (ứng viên khác: chunk theo tiến độ tải).
B2. Với 2–3 game nhỏ nhất trong mỗi nhóm + Ghost of Yōtei: build bằng test2 vào `<Ổ_ĐÍCH>`; sau đó viết `scripts/research/compare-playgo.py` giải nén `sce_sys/playgo-*` từ gói mới (dùng chế độ Giải nén gói / `PackageReader` — chỉ đọc gói ở `<Ổ_ĐÍCH>`) và so với gốc: số chunk, mask ngôn ngữ từng chunk, label, scenario (initial_chunk_count, thứ tự), **ánh xạ tệp→chunk cho từng tệp** (theo hash), ngôn ngữ mặc định. Khác biệt nào là do SDK sinh lại (chấp nhận được) vs do app bỏ sót (phải sửa).
B3. So `param.json` gốc ↔ trong gói: khoá nào mất/đổi ngoài `applicationDrmType`, `sdkVersion`, `requiredSystemSoftwareVersion`, `pubtools` — đặc biệt `attribute*`, `localizedParameters`, `contentVersion`. Khoá mất có thể ảnh hưởng ngôn ngữ.
B4. Các giả thuyết khác phải loại trừ có bằng chứng: (a) gói ngôn ngữ là add-on (`npbind.dat`/`nptitle.dat`, `addcont`) chứ không phải chunk — dump thiếu DLC thì không tool nào cứu được, phải nói rõ với người dùng; (b) `license.dat` giả (RIF) do SDK sinh khác RIF trong dump → entitlement ngôn ngữ; (c) `playgo-manifest.xml` gốc có thông tin mà bảng nhị phân không có; (d) engine tích hợp (bỏ tích SDK) có bị không — nếu engine 0.6.8 cũng chỉ 1 chunk thì cùng lỗi, cần cùng cách sửa (đọc `MetadataReader.cs:311`, `BuildEngine.cs:597`); (e) **eboot đã bị vá AMPR/PlayGo** từ thời chạy folder-mode (xem khảo sát) — game như vậy lỗi bất kể builder, phải báo người dùng dùng eboot gốc/dump lại; app nên cảnh báo khi thấy dấu hiệu; (f) game đọc `sceAppContent`/`scePlayGo` API nào — tìm chuỗi `sceAppContent`, `PlayGo`, `chunk`, `language` trong `eboot.bin`/`sce_module/*.prx` của game bằng `strings`/`findstr` (chỉ đọc) để biết game có dùng PlayGo API không → game không dùng API thì chunk không ảnh hưởng.
B5. Viết cho anh Sơn checklist test PS5 từng game (cài, Options → ngôn ngữ thoại không xám, cutscene có tiếng, đổi ngôn ngữ, gỡ/cài lại) và bảng ghi kết quả `docs/research/ps5-results.md`. Anh Sơn là người bấm trên PS5 — bạn chuẩn bị gói và checklist, không tự suy đoán kết quả.
B6. Nếu vẫn lỗi ở game nào: thu `sce_sys` gốc (đọc), GP5 + 3 log + playgo mới; thử biến thể GP5 bằng tay (ví dụ label/scenario khác, `initial_chunk_count`, thứ tự chunk) trên **bản copy nhỏ** (fixture tổng hợp trong tests, KHÔNG chép game thật nếu không cần; nếu cần chép 1 game nhỏ thì chép sang `<Ổ_ĐÍCH>` bằng lệnh chỉ đọc nguồn như `robocopy G:\src <Ổ_ĐÍCH>\copy /E` — không `/MIR`, không `/MOV`).
B7. Kết quả phải thành: sửa mã (nếu cần) + test xunit tái hiện bằng fixture tổng hợp (như `Fixtures/playgo-multi`) + mục CHANGELOG + cập nhật handoff.

## 4. Giai đoạn C — Ổn định khi build tất cả game

C1. Lập ma trận build: mọi game trên `G:\` (nhỏ trước, lớn sau; game > 100 GB làm ban đêm, hỏi anh Sơn trước khi chạy lượt > 1 giờ), 2 chế độ: Sony SDK (mặc định) và engine tích hợp (`--no-sony-sdk`). Ưu tiên `fpkg-cli build --source "G:\…" --output "<Ổ_ĐÍCH>\fpkg-research\out\<id>"`, ghi thời gian, kích thước, mã thoát, cảnh báo, lỗi, vào `docs/research/build-matrix.md`. Kiểm tra dung lượng trống trước mỗi lượt (`DiskSpaceAdvisor`), xoá gói **ở `<Ổ_ĐÍCH>`** sau khi verify để lấy chỗ (chỉ ở `<Ổ_ĐÍCH>`!).
C2. Sau mỗi gói: `fpkg-cli verify <pkg>` (thêm `--full` cho vài gói nhỏ), giải nén thử vài tệp, so hash với nguồn (đọc nguồn).
C3. Phân loại lỗi và sửa tận gốc, mỗi lỗi = 1 test xunit với fixture tổng hợp (không bao giờ dùng game thật làm fixture trong repo): reserved node mới, ký tự đặc biệt trong tên (escape XML `EscapeAttribute`, SDK parse), đường dẫn > 260 (bí danh ngắn hơn có đủ không? cần prefix `\\?\`?), tên chỉ khác hoa/thường (PFS phân biệt? SDK từ chối?), tệp 0 byte, tệp > 4 GiB, > 150 000 tệp, thiếu/sai keystone (SDK không xây được → app phải nói rõ và tự chuyển engine tích hợp? — hỏi anh Sơn vì quy tắc "bản gốc thế nào bản này thế ấy"), thiếu `pic*.png` mà không có `.dds`, `.gp5-assets` tàn dư trong nguồn (app bỏ qua ở gốc — kiểm tra), `param.json` thiếu khoá/`localizedParameters` rỗng, nguồn exFAT/NTFS/ReFS, tệp đang bị chương trình khác giữ (antivirus), `%TEMP%` không NTFS (junction thất bại → thông báo?), Cancel ở mỗi pha (GP5, img_create, convert, verify) phải dừng < 5 s và dọn tạm + bí danh, mất kết nối USB giữa chừng (rút ổ khác — KHÔNG rút `G:\`; mô phỏng bằng ổ USB khác hoặc fixture), đầy ổ đích, chạy 2 lượt song song (bí danh/prefix có đụng nhau không), ứng dụng bị tắt giữa chừng (dọn `psviethoa-sdk-*` lượt sau), Defender quét chậm.
C4. Kiểm tra cả tính năng mở lại/lịch sử nguồn, hộp Components, cài VC++ trên máy sạch (VM nếu có), NSIS installer/uninstaller, chạy từ đường dẫn có dấu, người dùng không quyền admin.
C5. Kết quả: `docs/research/stability-report.md` — mỗi game: build được? thời gian? lỗi? sửa ở commit nào (chưa commit thì ghi tệp).

## 5. Giai đoạn D — Tốc độ nén

D1. Đo cơ sở trên máy này với 1 game vừa (10–30 GB) và Yōtei: thời gian từng pha từ `-build-logs/0{1,2,3}-*.log` (`elapsed=`) + log app; CPU (1 lõi 100 %?), tốc độ đọc `G:\` và ghi đích (Task Manager/`typeperf`/PowerShell `Get-Counter`), với 3 cấu hình: đích cùng ổ `G:\` (chỉ để so — tạo thư mục ra đích... **KHÔNG**, không tạo gì trên `G:\`: bỏ cấu hình này, lấy số 2 h 13 min đã có), đích SSD nội bộ, đích SSD nội bộ + loại trừ Defender (thư mục đích, `%TEMP%\psviethoa-sdk-*`, `prospero-pub-cmd.exe`, app) — việc đổi Defender là của anh Sơn, bạn chỉ hướng dẫn.
D2. Tìm cờ tăng tốc của SDK: chạy `libs\sony-sdk\toolchain\prospero-pub-cmd.exe --help`, `prospero-pub-cmd.exe img_create --help`, `help img_create`; `findstr /i "compression_level thread jobs parallel num_threads"` trên `prospero-pub-cmd.exe` và `libScePubTools.dll` (chỉ đọc); đọc `libs/sony-sdk/README.md`. Ghi nguyên văn mọi tuỳ chọn tìm thấy.
D3. Thử nghiệm với fixture nhỏ rồi game vừa: `--compression_level` các mức (0/1/…/9 nếu có), số luồng nếu có, ảnh hưởng tới kích thước gói, thời gian, và **gói có cài/chạy trên PS5 không** (anh Sơn test). Lưu ý PFS không nén = gói ≈ dung lượng nguồn (Yōtei 184 GB thay vì 110 GB) — trade-off phải ghi rõ.
D4. Phía app: (a) tuỳ chọn opt-in "Nén nhanh (SDK)" chỉ hiện khi SDK bật, mặc định tắt, lưu settings, CLI `--sdk-compression-level N`; (b) không đổi thứ tự/nội dung GP5; (c) cảnh báo khi nguồn và đích cùng ổ vật lý (đọc + ghi tranh nhau) — gợi ý đổi đích; (d) cân nhắc `ProcessPriorityClass.AboveNormal` cho `prospero-pub-cmd.exe` (đo có lợi không); (e) bỏ qua pha verify cấu trúc chỉ khi người dùng tắt (đo xem verify mất bao lâu — nếu < 1 % thì không cần); (f) so với engine tích hợp Kraken level 2/4/7 cùng máy để đưa bảng cho người dùng chọn.
D5. Kết quả: `docs/research/speed-report.md` với bảng số liệu + khuyến nghị mặc định (không đổi) và tuỳ chọn.

## 6. Cách làm việc

- Chia nhỏ task, sau mỗi thay đổi mã chạy `dotnet build` + `dotnet test` (264 test hiện có phải pass; test cần Wine tự skip trên Windows).
- Mọi chuỗi hiển thị qua `Loc` với khoá trong `vi.json` + `en.json`; log tiếng Việt/Anh song song như mã hiện có.
- Thay đổi tối thiểu, trung thành toolkit; tuỳ chọn mới = opt-in, mặc định = hành vi hiện tại.
- Ghi CHANGELOG (mục 2.2.0) và cập nhật `docs/HANDOFF-WINDOWS-2.2.0-test2.md` khi có phát hiện mới.
- Báo cáo cuối: `docs/research/REPORT.md` (tiếng Việt): kết luận từng mục tiêu, bằng chứng, việc còn lại, đề xuất phiên bản 2.2.0 chính thức. Không commit/push — anh Sơn quyết.
- Hỏi anh Sơn khi: cần ổ đích, cần test PS5, lượt build > 1 giờ, cần đổi Defender/quyền admin, bất kỳ lệnh nào có khả năng ghi lên `G:\`, hoặc phát hiện dump thiếu dữ liệu (không sửa được).

## 7. Checklist trước mỗi lệnh (đọc lại mỗi lần)

- [ ] Lệnh này có đường dẫn `G:\` không? Nếu có: chỉ đọc chứ? Không có cờ xoá/di chuyển/mirror? Không tạo tệp/thư mục/junction trong `G:\`?
- [ ] Output/temp/log/bí danh nằm ở `<Ổ_ĐÍCH>` hoặc `%TEMP%` (NTFS), không phải `G:\`?
- [ ] App/CLI: đã tắt "Auto from source", `--output` trỏ ra ngoài `G:\`, không dùng `clean-junk`?
- [ ] Không commit/push, không sửa `libs/sony-sdk/toolchain/*`?
- [ ] Nếu không chắc 100 % → dừng, hỏi anh Sơn.

## 8. Bàn giao cuối

`docs/research/{game-survey.md,.json, build-matrix.md, stability-report.md, speed-report.md, ps5-results.md, REPORT.md}`, `scripts/research/{survey-games.py, compare-playgo.py}` (read-only, có kiểm tra tự vệ), mã sửa + test trong repo (chưa commit), CHANGELOG/handoff cập nhật, tóm tắt tiếng Việt cho anh Sơn.
