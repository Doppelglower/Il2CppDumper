# Il2CppDumper

[![Build status](https://ci.appveyor.com/api/projects/status/anhqw33vcpmp8ofa?svg=true)](https://ci.appveyor.com/project/Perfare/il2cppdumper/branch/master/artifacts)

中文说明请戳[这里](README.zh-CN.md)

Unity il2cpp reverse engineer

## About This Fork

This repository is based on [HZBHZB1234/Il2CppDumper](https://github.com/HZBHZB1234/Il2CppDumper), which itself is based on [c01ns/Il2CppDumper](https://github.com/c01ns/Il2CppDumper) and [Perfare/Il2CppDumper](https://github.com/Perfare/Il2CppDumper).

It keeps the experimental Unity 6000.x / metadata v35–v39 work from those forks, and adds IDA-oriented `il2cpp.h` layout plus DummyDll enum constant fixes.

### Changes in this fork

* DummyDll: write enum member constants as the underlying integer so ILSpy/dnSpy can recover values (enable **Always show enum member values** if sequential enums omit `= n`)
* `il2cpp.h`: `#pragma pack(1)` C layout, embed parent `_Fields` instead of C++ inheritance, drop `__declspec(align(8))`, insert padding from field offsets
* Enums: emit `typedef enum Name { ... } Name;` (empty IL2CPP stubs stay `typedef int32_t Name`) so IDA `parse_decls` accepts field and signature types
* Generic valuetypes: if every field reports the same offset (usually `0`), emit fields sequentially instead of commenting later members as `OVERLAP`
* VTable: fill by slot index, length = `vtable_count`; empty slots are named `empty`
* File dialog: metadata picker accepts `*.*`, not only `global-metadata.dat`

Use **`ida_with_struct_py3.py`** with the generated `il2cpp.h` and `script.json` (plain `ida.py` only sets names).

The newer-version support has only been self-tested on a small number of games. IL2CPP layouts, metadata formats, and protections vary by Unity version, platform, and project. If a specific game cannot be dumped, you need to debug and modify the source code yourself. I do not provide one-by-one adaptation for every game or protected build.

## Disclaimer

This project is provided for learning, research, interoperability, and analysis of software you own or are authorized to inspect. Do not use it to infringe copyright, bypass access control, cheat in online services, or violate any law or terms of service. You are responsible for how you use this tool.

## Features

* Complete DLL restore (except code), can be used to extract `MonoBehaviour` and `MonoScript`
* Supports ELF, ELF64, Mach-O, PE, NSO and WASM format
* Supports Unity 5.3 - 2022.2, with experimental support for newer Unity metadata versions such as v35/v38/v39 (Unity 6000.x)
* Supports generate IDA, Ghidra and Binary Ninja scripts to help them better analyze il2cpp files
* Supports generate an IDA-oriented packed `il2cpp.h` header
* Supports Android memory dumped `libil2cpp.so` file to bypass protection
* Support bypassing simple PE protection

## Usage

Run `Il2CppDumper.exe` and choose the il2cpp executable file and `global-metadata.dat` file, then enter the information as prompted

The program will then generate all the output files in current working directory

### Command-line

```
Il2CppDumper.exe <executable-file> <global-metadata> <output-directory>
```

### Outputs

#### DummyDll

Folder, containing all restored dll files

Use [dnSpy](https://github.com/0xd4d/dnSpy), [ILSpy](https://github.com/icsharpcode/ILSpy) or other .Net decompiler tools to view. Enum members keep their integer constants; ILSpy may still hide sequential `= 0, 1, 2, …` unless **Always show enum member values** is enabled.

Can be used to extract Unity `MonoBehaviour` and `MonoScript`, for [UtinyRipper](https://github.com/mafaca/UtinyRipper), [UABE](https://7daystodie.com/forums/showthread.php?22675-Unity-Assets-Bundle-Extractor)

#### ida.py

For IDA

#### ida_with_struct.py / ida_with_struct_py3.py

For IDA: parse `il2cpp.h` and apply structure / signature types. Prefer the Python 3 script.

#### il2cpp.h

structure information header file

#### ghidra.py

For Ghidra

#### Il2CppBinaryNinja

For BinaryNinja

#### ghidra_wasm.py

For Ghidra, work with [ghidra-wasm-plugin](https://github.com/nneonneo/ghidra-wasm-plugin)

#### script.json

For ida.py, ghidra.py and Il2CppBinaryNinja

#### stringliteral.json

Contains all stringLiteral information

### Configuration

All the configuration options are located in `config.json`

Available options:

* `DumpMethod`, `DumpField`, `DumpProperty`, `DumpAttribute`, `DumpFieldOffset`, `DumpMethodOffset`, `DumpTypeDefIndex`
  * Whether to output these information to dump.cs

* `GenerateDummyDll`, `GenerateScript`
  * Whether to generate these things

* `DummyDllAddToken`
  * Whether to add token in DummyDll

* `RequireAnyKey`
  * Whether to press any key to exit at the end

* `ForceIl2CppVersion`, `ForceVersion`
  * If `ForceIl2CppVersion` is `true`, the program will use the version number specified in `ForceVersion` to choose parser for il2cpp binaries (does not affect the choice of metadata parser). This may be useful on some older il2cpp version (e.g. the program may need to use v16 parser on il2cpp v20 (Android) binaries in order to work properly)

* `ForceDump`
  * Force files to be treated as dumped

* `NoRedirectedPointer`
  * Treat pointers in dumped files as unredirected, This option needs to be `true` for files dumped from some devices

## Common errors

#### `ERROR: Metadata file supplied is not valid metadata file.`  

Make sure you choose the correct file. Sometimes games may obfuscate this file for content protection purposes and so on. Deobfuscating of such files is beyond the scope of this program, so please **DO NOT** file an issue regarding to deobfuscating.

If your file is `libil2cpp.so` and you have a rooted Android phone, you can try my other project [Zygisk-Il2CppDumper](https://github.com/Perfare/Zygisk-Il2CppDumper), it can bypass this protection.

#### `ERROR: Can't use auto mode to process file, try manual mode.`

Please note that the executable file for the PC platform is `GameAssembly.dll` or `*Assembly.dll`

For newer Unity versions or protected builds, please investigate the failing parser path and patch the source locally. Per-game adaptation is not guaranteed.

#### `ERROR: This file may be protected.`

Il2CppDumper detected that the executable file has been protected, use `GameGuardian` to dump `libil2cpp.so` from the game memory, then use Il2CppDumper to load and follow the prompts, can bypass most protections.

If you have a rooted Android phone, you can try my other project [Zygisk-Il2CppDumper](https://github.com/Perfare/Zygisk-Il2CppDumper), it can bypass almost all protections.

## Credits

- Perfare - [Il2CppDumper](https://github.com/Perfare/Il2CppDumper)
- c01ns - [Il2CppDumper](https://github.com/c01ns/Il2CppDumper) (Unity 6000 / metadata v35–v39)
- HZBHZB1234 - [Il2CppDumper](https://github.com/HZBHZB1234/Il2CppDumper) (v39 attribute / enum DummyDll fixes)
- Jumboperson - [Il2CppDumper](https://github.com/Jumboperson/Il2CppDumper)
