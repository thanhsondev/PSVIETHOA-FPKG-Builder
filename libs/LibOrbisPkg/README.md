# LibOrbisPkg (PS4 PKG / PFS / SFO)

`LibOrbisPkg.dll` is LibOrbisPkg by **Maxton** (https://github.com/maxton/LibOrbisPkg), licensed under the **GNU LGPL 3.0** (`LICENSE.txt`). The PS4 PKG pages use it to read packages, open the PFS (passcode or the FPKG key) and extract files.

- Built from `LibOrbisPkg-source.zip`. That is the source PSVIETHOA STUDIO (Ngô Phi Phương) ships in `vendor/LibOrbisPkg`, built for .NET 10 with `CORE` defined.
- To rebuild: unzip it, then run `dotnet build src/LibOrbisPkg.csproj -c Release`. Copy `bin/Release/net10.0/LibOrbisPkg.dll` here.
- SHA-256 of `LibOrbisPkg.dll`: `137f2126a2f2bdb584fceb5effe0e277ed77b1eb0611eb0582f520038456a915`

The app links it dynamically as a separate assembly. You can replace it with your own build of the source (LGPL 3.0 §4).

Replace this folder as a whole when upgrading; do not edit the DLL.
