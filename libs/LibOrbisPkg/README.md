# LibOrbisPkg (PS4 PKG / PFS / SFO)

`LibOrbisPkg.dll` is LibOrbisPkg by **Maxton** (https://github.com/maxton/LibOrbisPkg), licensed under the **GNU LGPL 3.0** (`LICENSE.txt`). The PS4 PKG pages use it to read packages, open the PFS (passcode or the FPKG key) and extract files.

- Built from `LibOrbisPkg-source.zip`. That is the source PSVIETHOA STUDIO (Ngô Phi Phương) ships in `vendor/LibOrbisPkg`, built for .NET 10 with `CORE` defined.
- 3.0.1: `src/PFS/XtsDecryptReader.cs` (AES-XTS with one `ThreadLocal<Aes>` ECB call per sector, 64-bit tweak multiply, no per-sector allocation) and `src/Util/MemoryMapped.cs` (`ChunkedMemoryReader` reads only the part of each chunk instead of the whole `count`) — from PSVIETHOA STUDIO 2.1.0. Output byte-identical.
- To rebuild: unzip it, then run `dotnet build src/LibOrbisPkg.csproj -c Release`. Copy `bin/Release/net10.0/LibOrbisPkg.dll` here.
- SHA-256 of `LibOrbisPkg.dll`: `55a880d592b21119de3adc60c1306101c427aae91ff4c5f85809f4412b9152ff`

The app links it dynamically as a separate assembly. You can replace it with your own build of the source (LGPL 3.0 §4).

Replace this folder as a whole when upgrading; do not edit the DLL.
