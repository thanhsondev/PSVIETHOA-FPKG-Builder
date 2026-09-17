# Bàn giao 2.2.0-test2 → máy Windows (2026-09-17)

Tài liệu này để một session Claude Code **mới trên Windows** đọc rồi tiếp tục test base game (Ghost of Yōtei) mà không cần lịch sử của session trên Mac. Mọi thứ dưới đây là trạng thái thật của working tree tại thời điểm viết; chưa có gì được commit.

---

## 0. Quy tắc bắt buộc từ chủ dự án (anh Sơn)

1. **KHÔNG commit / KHÔNG push** cho tới khi anh Sơn test xong và nói OK ("build bản test 2.2 không commit push để test ok mới commit push"). Working tree hiện có ~52 tệp sửa + mới, tất cả chưa commit, HEAD vẫn là `9438b56` (2.1.9).
2. **"Bản gốc như nào bản này như vậy"**: chế độ Sony SDK phải trung thành với bộ toolkit `sdk-fpkg279-fixdss3` (GP5 giống từng byte script Python, cùng thứ tự tệp, cùng loại trừ, cùng param.json chuẩn hoá). Mọi tối ưu/khác biệt phải là tuỳ chọn, mặc định = toolkit.
3. **Sony SDK bật mặc định**, ô tích nằm dưới thanh tiến trình / trên nút Build; khi bật, các tuỳ chọn xung đột (preset, Kraken, PFS, PlayGo, DRM, loại gói…) bị khoá như tool gốc.
4. Toolkit **nằm sẵn trong app** ("Sẵn hết trong tool"), chạy cả macOS (Wine) lẫn Windows (native).
5. ⚠️ `libs/sony-sdk/toolchain/` (31 MB: `prospero-pub-cmd.exe`, `libScePubTools.dll`, `ext/*`) và `prospero-dds2png.exe` là Publishing Tools của Sony. Repo `thanhsondev/PSVIETHOA-FPKG-Builder` đang **PUBLIC** → push các binary này lên dễ bị DMCA gỡ cả repo. Trước khi push phải quyết định: gitignore toolchain + tải từ chỗ riêng lúc đóng gói, hoặc chỉ giữ trong repo private. Anh Sơn chưa chọn cách chuyển bản build (draft release / repo private / chép tay) — hỏi lại khi cần.

---

## 1. Bản build đang chờ test

Xuất trên Mac lúc 16:55 (17/09/2026), cùng mã nguồn với working tree hiện tại (sau đó chỉ sửa 1 tệp test):

| Tệp (trên Desktop máy Mac) | Kích thước |
|---|---|
| `PSVIETHOA-FPKG-Builder-2.2.0-test2-Windows-x64-Setup.exe` | 138 MB (NSIS, có VC++ redist) |
| `PSVIETHOA-FPKG-Builder-2.2.0-test2-Windows-x64.zip` | 191 MB (app/ + fpkg-cli/ + sony-sdk) |
| `…-macOS-AppleSilicon.zip`, `…-macOS-Intel.zip`, thư mục `…-macOS` | 219 / 236 MB |

Test đã có: **264/264 test pass** trên macOS (`dotnet test`, 12–30 s; một lần bất thường 21 phút, không điều tra theo yêu cầu).

Gói Yōtei anh Sơn build trên Windows lúc 15:26 (ảnh chụp) là bản **v2.2.0 test1** (log ghi "PlayGo: Publishing Tools generates 1 scenario / 1 chunk … the source's 4 playgo* files are not used") → gói đó **vẫn dính lỗi mất thoại**, phải build lại bằng test2. Thông số lượt đó: `G:\PPSA26344 Ghost of Yōtei (01.512.000)`, 95,401 tệp / 184 GB → pkg 110 GB (59.9 %), **2 h 13 min**, nguồn và đích cùng ổ `G:\`.

---

## 2. Kiến trúc chế độ Sony SDK (2.2.0)

Luồng 3 bước, y hệt `build-from-folder.ps1` của toolkit:

1. **GP5** — `SonySdkProject.Create` viết `<tên>.gp5` phẳng (liệt kê từng tệp) + `<tên>.playgo-scenario.json` cạnh gói, byte-identical với `scripts/create-gp5-from-folder.py --keep-keystone --absolute-paths`. Thứ tự tệp = `sorted(key=str.casefold)` của CPython (bảng `PythonCaseFold.txt`, 1490 mục, so theo code point) — thứ tự quyết định layout gói (đảo thứ tự → 109/110 MB khác). XML dạng ElementTree; `Path.resolve()` = `SonySdkProject.RealPath`; symlink bị từ chối (`Sdk.SymlinkNotAllowed`). Duyệt thư mục song song (tối đa 8 thư mục con, `Parallel.ForEach`), kiểm tra huỷ mỗi 2000 mục.
2. **Raw pkg** — `SonySdkRunner.CreateImageAsync`: `prospero-pub-cmd.exe img_create --oformat nwonly <gp5> <raw>` (không truyền `--compression_level`, toolkit cũng không; SDK tự nén mức mặc định). Thanh tiến trình đọc dải 51 ký tự `=` của SDK + poll kích thước tệp raw. Trên Windows chạy native (cần VC++ 2015-2022 x64, app tự cài từ `vc_redist.x64.exe` kèm theo); trên macOS qua Wine 11.17 bundled.
3. **Convert** — `SonySdkConverter.ConvertInPlace`: port C# byte-identical của `postprocess-sdk279-plaintext.py` (marker `PPRPLAIN-NOAUTH!`, xoá cờ mã hoá CNT, niêm phong lại digest/FIH/rollup, RSA-3072 wrap qua `ProsperoPublisherRsa.BuildCntHeaderWrap`, CRC32C PlayGo trong SI zip). Chỉ băm các vùng header/bảng, **không đọc lại toàn bộ 110 GB**.

Sau đó `BuildEngine.VerifyOutputAsync` kiểm tra cấu trúc như engine tích hợp.

Tệp chính (tất cả trong `src/PsViethoa.FpkgBuilder.Core/Services/` trừ khi ghi khác):

| Tệp | Vai trò |
|---|---|
| `SonySdkToolchain.cs` | Tìm toolkit (`PSVIETHOA_SONY_SDK`, thư mục app, cha, `app/`, Resources, **repo `libs/`** khi chạy từ source), tìm Wine (`PSVIETHOA_WINE`), `VcRuntimeInstalled`/`InstallVcRuntime`, `ToWinePath`. |
| `SonySdkProject.cs` | Tạo GP5 + scenario; `SonySdkSourcePlan(Skip, Replace, SkipJunk, ParamPatch, PlayGo)`; `WriteStandardParam` (param.json chuẩn hoá vào `.gp5-assets/<tên>/sce_sys/param.json`, `PythonJson`); `RecoverPresentationPngs` (pic0/pic1/pic2 từ `.dds`, `--preserve-alpha` cho pic2); `GeneratedSceSysFiles`, `ExcludedRootFiles`, `ExcludedFakeLibraries`. |
| `SonySdkPlayGo.cs` | Đọc/ghi cấu trúc PlayGo gốc (mục 4). |
| `SonySdkRunner.cs` | Chạy SDK/Wine, parse output, `Classify` ([Warn] thường → Info), `StopWineServer`. |
| `SonySdkBuilder.cs` | Ghép 3 bước, log `01-create-gp5.log` / `02-img-create.log` / `03-postprocess.log` (khoá giống ps1: command, started_utc, finished_utc, **elapsed**, exit_code) trong `<tên>-build-logs/`; chạy `prospero-dds2png.exe`. |
| `SonySdkPathAliases.cs` | Bí danh ASCII cho đường dẫn có dấu (Windows: **junction, cần thư mục chứa bí danh là NTFS** — thư mục đích có thể là exFAT/USB); thư mục `psviethoa-sdk-<pid hex>-<6 ký tự>` trong `%TEMP%`/ProgramData/gốc ổ; dọn bí danh mồ côi theo PID. |
| `SonySdkConverter.cs` | Bước 3. |
| `PythonCaseFold.cs/.txt`, `PythonJson.cs` | Mô phỏng CPython. |
| `BuildEngine.cs` | Nhánh SDK: `PlanSonySdkSource`, `ReadPlayGoStructure`, `RequireKeystone` (sce_sys/keystone đúng 96 byte), `BuildWithSonySdkAsync`, `LogPlan(sonySdk)`. |
| `ParamJsonPatch.ApplyTo`, `BuildRequest.UseSonySdk/SonySdkActive/ParamPatch`, `PhaseCatalog` (SdkRuntime 1.5 / SdkProject 0.5 / SdkImage 90 / SdkConvert 1), `DiskSpaceAdvisor.Check(sonySdk)`, `ComponentProbe.SonySdk()` | Hạ tầng. |
| App: `ViewModels/MainViewModel.cs` (`UseSonySdk`, `SdkActive`, `EngineOptionsEnabled`, `CanEditDrm`, `ForceStandardDrmUi`, `RemovePlayGoFilesUi`, `ApplySdkMode`), `Views/MainWindow.axaml`, `Services/AppSettings.cs` (`UseSonySdk`) | UI. |
| CLI `CommandLine.cs` | `--no-sony-sdk` / `--builtin-engine`; cảnh báo cờ bị bỏ qua khi SDK. |
| `Localization/vi.json`, `en.json` | Khoá `Sdk.*`, `Phase.sdk-*`, `Build.SonySdk*`, `Plan.DrmPolicySdk`, `Sdk.PlayGoStructure/Invalid/Mapped`… |
| `libs/sony-sdk/` | Toolkit nguyên layout: `README.md`, `build.bat`, `build-from-folder.ps1`, `prospero-dds2png.exe`, `scripts/create-gp5-from-folder.py`, `scripts/postprocess-sdk279-plaintext.py`, `toolchain/`. |
| `scripts/publish-windows.sh` (copy sony-sdk vào `app/`, tải `vc_redist.x64.exe` vào `app/redist`, `makensis -DVERSION_NUMERIC`), `scripts/publish-macos.sh`, `scripts/fetch-wine.sh`, `scripts/gen-casefold.py`, `installer/windows/PSVIETHOA.nsi` (section VcRedist) | Đóng gói. |
| Tests: `SonySdkTests.cs` (14), `SonySdkFidelityTests.cs` (12, so byte với script Python khi có `python3`/`python` trong PATH), `SonySdkPlayGoTests.cs` (6); fixtures `Fixtures/bc7-icon0.dds.gz`, `Fixtures/playgo-multi/{playgo-chunk,playgo-ficm,playgo-hash-table}.dat` | Test. |

Sự thật thực nghiệm về Publishing Tools 2.79 (đã đo dưới Wine, áp dụng cả Windows):
- Nút `sce_sys` bị từ chối "reserved node found": `license.dat`, `license.info`, `playgo-chunk.dat`, `playgo-ficm.dat`, `playgo-hash-table.dat`, `imagedigs.dat`, `about/right.sprx`, `*.dds`; `pfs-version.dat` → "Could not create system file". SDK tự sinh lại `license.dat` (RIF 1024 B) + `license.info` (512 B).
- Dòng lệnh ANSI, GP5 `src_path` ngoài ASCII → "File not found (…Yotei…)" / "invalid attribute value src_path" → phải đi qua bí danh.
- `prospero-dds2png.exe` chỉ giải mã BC7 DX10; pic0/pic1 sai lệch ≤ 1/255; pic2 cần `--preserve-alpha`; pic0.png 512×512 được SDK chấp nhận.
- Không có `prospero-llvm-readelf.exe` → `sdkVersion`/`requiredSystemSoftwareVersion` trong param.json của gói = `0x0000000000000000` (toolkit gốc cũng vậy).

---

## 3. Cập nhật theo `sdk-fpkg279-fixdss3` (đã làm, test2)

Script mới (binary SDK giống hệt bản 729-fix) — app tái tạo từng byte:
- `EXCLUDED_ROOT_FILES = {ampr_emu.index}`; `EXCLUDED_FAKE_LIBRARIES = {libsceampr.sprx, libsceplaygo.sprx}` (so casefold) + `license.dat`/`license.info` bỏ.
- `param.json` chuẩn hoá `applicationDrmType = standard` ghi vào `.gp5-assets/<stem>/sce_sys/param.json` (`json.dumps(indent=2, ensure_ascii=False) + "\n"` → `PythonJson`); GP5 trỏ vào bản này.
- Khôi phục `sce_sys/pic0/pic1/pic2.png` thiếu từ `.dds` bằng `prospero-dds2png.exe`; mục PNG đứng ngay sau mục scenario trong GP5; `01-create-gp5.log` có các dòng "Recovering sce_sys/X from Y..." trước.
- UI: khi SDK bật, ô **DRM standard** tích + khoá (`ForceStandardDrmUi`), ô **Bỏ sce_sys/playgo*** tích + khoá, nén tắt (`CompressionEnabled => BackendIndex != 3 && !SdkActive`).
- Test so sánh trực tiếp với script `.py` (`SonySdkFidelityTests`, cần python3; trên mac dùng wrapper `dds2png-wine.py`, trên Windows script gọi exe thẳng).

---

## 4. Sửa lỗi mất thoại / ngôn ngữ thoại xám (Ghost of Yōtei) — CHƯA XÁC NHẬN TRÊN PS5

**Báo cáo (Discord):** Ghost of Yōtei build bằng toolkit không có thoại, danh sách ngôn ngữ thoại xám, bấm tải không chạy; gói làm bằng tool SDK dòng lệnh cũng vậy.

**Nguyên nhân tìm ra:** script gốc luôn ghi `<chunk_info chunk_count="1" scenario_count="1">`. Game chia thoại theo ngôn ngữ (mỗi ngôn ngữ một chunk PlayGo có `languages="…"`) hỏi hệ thống chunk ngôn ngữ đã cài chưa → chunk không tồn tại → xám/"download".

**Cách sửa (đã làm):** `SonySdkPlayGo.TryRead(sce_sys)` đọc 3 tệp gốc `playgo-chunk.dat` + `playgo-ficm.dat` + `playgo-hash-table.dat` → `PlayGoStructure(SupportedLanguageMask, DefaultLanguageId, DefaultScenarioId, Chunks, Scenarios, FileChunks)`; `SonySdkProject` ghi vào GP5:

```xml
<chunk_info chunk_count="3" scenario_count="2">
  <chunks supported_languages="ja-JP en-US fr-FR de-DE" default_language="en-US">
    <chunk id="0" label="Chunk #0" />
    <chunk id="1" languages="ja-JP en-US" label="Voice JP EN" />
    <chunk id="2" languages="fr-FR" label="Voice FR" />
  </chunks>
  <scenarios default_id="0">
    <scenario id="0" type="playmode" initial_chunk_count="1" label="Scenario #0">0 2 1</scenario>
    …
  </scenarios>
</chunk_info>
… <file targ_path="…" orig_path="…" chunk="2"/>   (chỉ khi ChunkOf(relative) > 0)
```
- 1 chunk (`IsTrivial`) → giữ nguyên 1/1 như script; đọc lỗi → log `Sdk.PlayGoStructureInvalid` và dùng 1/1.
- Log khi có nhiều chunk: `Sdk.PlayGoStructure` ("PlayGo của gói gốc: N chunk, M kịch bản (k chunk theo ngôn ngữ; ngôn ngữ hỗ trợ: …; bảng ánh xạ F tệp)") và `Sdk.PlayGoMapped` ("GP5: N chunk như gói gốc; X tệp gán vào chunk ngôn ngữ/phụ, Y tệp ở chunk 0").
- Dokan/ảnh: `SonySdkSourcePlan.Pure with { ParamPatch, PlayGo = ReadPlayGoStructure(...) }`; `ImageCleanupPaths` không giấu playgo* khi `SonySdkActive`.
- Đã round-trip trên bảng PlayGo do chính SDK tạo (fixture `playgo-multi`: 3 chunk, 2 kịch bản, data0→1, data1→2, data2→2) — test `Build_ReproducesTheOriginalChunksInTheNewPackage` pass. **Chưa thử trên dump Yōtei thật / PS5.**

Layout nhị phân (để debug): `playgo-chunk.dat` magic `plgx`, header 0x1000: u16@10 số chunk, u16@14 số scenario, u16@20 scenario mặc định, u16@36 language id mặc định, u64@56 mask ngôn ngữ (bit 63−id); con trỏ section @192 (chunk attrs 32 B: u64@+16 mask, u32@+28 label offset), @208 chunk labels, @224 scenario attrs (32 B: u16@+20 initial, u16@+22 total, u32@+24 chunk-list offset, u32@+28 label offset), @232 danh sách chunk của scenario (u16), @240 scenario labels. `playgo-ficm.dat`: header 16 B, 2 B/tệp (chunk, reserved), cùng chỉ số với `playgo-hash-table.dat` (header 56 B, `\x7fFLT`@24, count u32@36, 8 B hash/tệp = `ProsperoPs5FlatPathTable.HashPath`). Language id 0..30: ja-JP en-US fr-FR es-ES de-DE it-IT nl-NL pt-PT ru-RU ko-KR zh-Hant zh-Hans fi-FI sv-SE da-DK no-NO pl-PL pt-BR en-GB tr-TR es-419 ar-AE fr-CA cs-CZ hu-HU el-GR ro-RO th-TH vi-VN id-ID uk-UA.

**Câu hỏi "mấy game kia có bị không?":** chỉ game có **> 1 chunk** trong `sce_sys/playgo-chunk.dat` gốc (thường game first-party có gói thoại từng ngôn ngữ) mới bị; game 1 chunk thì gói của script gốc đã đúng cấu trúc, không mất gì. Cách kiểm tra nhanh trên Windows: build (hoặc chỉ nhìn log kế hoạch) — có dòng `Sdk.PlayGoStructure` = game nhiều chunk; hoặc đọc u16 tại offset 10 của `playgo-chunk.dat` (Python: `struct.unpack_from('<H', data, 10)`).

---

## 5. Các lỗi từ ảnh Windows trước đó (đã sửa, cần xác nhận lại trên test2)

- Treo ở "[1/3] creating the GP5 project" sau "95,387 files listed" và Cancel treo: do `ToolPath` gọi `RealPath` từng tệp (mở ~500k handle trên USB) + vòng ghi XML không kiểm tra huỷ → đã bỏ RealPath từng tệp, kiểm tra huỷ mỗi 2000 mục.
- "reserved node found (sce_sys/license.dat)" → loại trừ.
- Đường dẫn có dấu (`Ghost of Yōtei`) → bí danh junction; nếu `%TEMP%` không phải NTFS thì thử ProgramData rồi gốc ổ đích (không bao giờ ổ nguồn); thất bại hết → lỗi `Sdk.AliasFailed`.

---

## 6. Chạy / build / test trên Windows

Yêu cầu: .NET SDK 10, (tuỳ chọn) Python 3 trong PATH để chạy 1 test so byte với script; VC++ 2015-2022 x64 (test build thật qua SDK cần).

```powershell
dotnet build PsViethoa.FpkgBuilder.slnx -c Release
dotnet test                                   # 264 test; test Wine/mac tự skip trên Windows
dotnet run --project src/PsViethoa.FpkgBuilder.App   # GUI; tự tìm libs/sony-sdk trong repo
dotnet run --project src/PsViethoa.FpkgBuilder.Cli -- build --source "G:\PPSA26344 Ghost of Yōtei (01.512.000)" --output "D:\out"   # thêm --no-sony-sdk để dùng engine cũ
```
Đóng gói: `scripts/publish-windows.sh` là zsh (chạy từ Git Bash/WSL có dotnet + makensis), hoặc chạy tay 2 lệnh `dotnet publish … -r win-x64 --self-contained -p:PublishSingleFile=true` trong script rồi chép `libs/sony-sdk` vào cạnh exe (`app/sony-sdk`). Phiên bản ở `Directory.Build.props` (`<Version>2.2.0-test2</Version>`); NSIS dùng `VERSION_NUMERIC` (bỏ hậu tố).

Hook dev (đọc/ghi **settings.json thật** — backup trước): `PSVIETHOA_SCREENSHOT`, `PSVIETHOA_AUTOBUILD`, `PSVIETHOA_SCROLL`. Settings: `SettingsService.Path` = `JsonFileStore.AppDataDirectory("PSVIETHOA FPKG Builder")\settings.json`.

---

## 7. Kế hoạch test base game trên Windows (việc của session mới)

1. Cài `…-Windows-x64-Setup.exe` (hoặc chạy từ source). Kiểm tra hộp Components có dòng **Sony SDK (Publishing Tools 2.79)** + VC++ OK.
2. Build lại `PPSA26344 Ghost of Yōtei (01.512.000)` với **đích ở ổ khác ổ nguồn** (ưu tiên SSD nội bộ) và ghi lại thời gian từng bước từ `<pkg>-build-logs/0{1,2,3}-*.log` (`elapsed=`) + log app (nút Copy details / lưu log).
3. Trong log app phải thấy: `Sdk.PlayGoStructure` (N chunk > 1, ngôn ngữ), `Sdk.PlayGoMapped`, `Sdk.ParamNormalized`, (có thể) `Sdk.PngRecovered`, `Sdk.Artifacts`. Nếu Yōtei chỉ có 1 chunk thì giả thuyết sai → xem lại (mục 4).
4. Kiểm tra gói: chế độ Giải nén gói / `fpkg-cli inspect` → `sce_sys/playgo-chunk.dat` mới có cùng số chunk/scenario với gốc; `param.json` có `applicationDrmType: standard`; có `pic0/pic1/pic2.png`; không còn `ampr_emu.index`, `libSceAmpr.sprx`, `libScePlayGo.sprx`, `license.*` cũ.
5. Trên PS5: cài, vào game → Options → ngôn ngữ thoại **không xám**, có tiếng thoại trong cutscene; đổi thử ngôn ngữ thoại khác; splash/pic hiển thị đúng; game 1 chunk (ví dụ Stellar Blade PPSA13197 dump `PPSA13197-app0` có trong lịch sử) không hồi quy.
6. Test Cancel giữa GP5 và giữa img_create (không treo, dọn tệp tạm + bí danh trong `%TEMP%\psviethoa-sdk-*`).
7. Báo kết quả cho anh Sơn → anh OK mới commit/push (nhớ mục 0.5).

---

## 8. Câu hỏi đang mở: "build 1 bản rất lâu (2 h 13 min), có cách nào nhanh hơn?"

Đã biết:
- Toolkit gốc gọi `img_create --oformat nwonly` không cờ khác; app cũng vậy (`compressionLevel: null`). SDK tự nén (184 GB → 110 GB) và (rất có thể) chạy **1 luồng**; bước GP5 giờ nhanh, bước convert chỉ băm header, verify không đọc hết gói → ~95 % thời gian là img_create.
- Lượt 2 h 13 min đọc nguồn và ghi đích **cùng ổ G:\ (USB)** → đọc/ghi tranh nhau, throughput ~23 MB/s tổng.

Việc cần làm tiếp (chưa làm):
1. Chạy `libs\sony-sdk\toolchain\prospero-pub-cmd.exe img_create --help` (và `prospero-pub-cmd.exe --help`) xem có cờ số luồng / `--compression_level` / bỏ nén; ghi lại nguyên văn.
2. Đo lại với đích ở SSD nội bộ, nguồn USB; và với Windows Defender loại trừ thư mục đích + `prospero-pub-cmd.exe` (quét 95k tệp + 110 GB ghi rất tốn).
3. Nếu có cờ tăng tốc: thêm tuỳ chọn **opt-in** trong SDK mode (mặc định vẫn như toolkit), kiểm tra gói vẫn cài/chạy trên PS5 và kích thước/tên tệp không đổi ngoài ý muốn. Không đổi mặc định (quy tắc mục 0.2).
4. So sánh: engine tích hợp (bỏ tích Sony SDK) trên cùng máy — tham khảo mac M5 Pro 21.3 GB: level 2 ≈ 2 phút, level 7 ≈ 13 phút.

---

## 9. Danh sách thay đổi trong working tree (chưa commit)

Sửa: `CHANGELOG.md` (mục 2.2.0), `Directory.Build.props` (2.2.0-test2), `README.md`, `docs/README.vi.md`, `installer/windows/PSVIETHOA.nsi`, `scripts/publish-macos.sh`, `scripts/publish-windows.sh`, `src/…App/Services/AppSettings.cs`, `ViewModels/MainViewModel.cs`, `Views/MainWindow.axaml`, `src/…Cli/CommandLine.cs`, `Core/Localization/en.json`, `vi.json`, `Core/Models/BuildRequest.cs`, `Core/PsViethoa.FpkgBuilder.Core.csproj` (EmbeddedResource `PythonCaseFold.txt`), `Core/Services/{BuildEngine,ComponentProbe,DiskSpaceAdvisor,ParamJsonPatch,PhaseCatalog,ProgressTracker}.cs`, nhiều tệp test cũ (chỉnh theo SDK mặc định).

Mới: `libs/sony-sdk/` (toolkit fixdss3), `scripts/fetch-wine.sh`, `scripts/gen-casefold.py`, `Core/Services/{PythonCaseFold.cs, PythonCaseFold.txt, PythonJson.cs, SonySdkBuilder.cs, SonySdkConverter.cs, SonySdkPathAliases.cs, SonySdkPlayGo.cs, SonySdkProject.cs, SonySdkRunner.cs, SonySdkToolchain.cs}`, tests `SonySdk{,Fidelity,PlayGo}Tests.cs`, `Fixtures/bc7-icon0.dds.gz`, `Fixtures/playgo-multi/`, tài liệu này.

`.cache/` (Wine, Dokan MSI, vc_redist) là gitignored — máy Windows không cần Wine.

---

## 10. test3 (gộp bản PC 17/09 19:16 + Linux, 17/09 tối)

- Đã gộp toàn bộ working tree từ máy Windows (prescan Defender, CRLF, mức nén opt-in, PlayGo fallback, lỗi tên tệp, `*.url` rác, 281 test) vào repo Mac; nghiên cứu ở `docs/research/`, script ở `scripts/research/`.
- Linux: `scripts/publish-linux.sh` (linux-x64, linux-arm64 → `.tar.gz` gồm `app/`, `fpkg-cli/`, `sony-sdk/`, `install-desktop-entry.sh`, `README-Linux.txt`); `SonySdkToolchain.FindWine` thêm wine64 / /opt/wine-* / PATH; `UpdateChecker` nhận `Linux-x64`/`Linux-arm64` + `.tar.gz`; `SleepInhibitor` dùng `systemd-inhibit`; `Comp.SleepSystemd`; chuỗi "macOS, Windows & Linux". Chưa chạy thử trên máy Linux thật (Mac không có Docker) — cần test: mở app (X11/XWayland), `fpkg-cli info` thấy dòng Sony SDK + Wine, build 1 game nhỏ qua Wine.
