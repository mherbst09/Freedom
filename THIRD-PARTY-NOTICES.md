# Drittanbieterhinweise

Freedom enthält beziehungsweise verwendet die folgenden Komponenten und Datenquellen. Diese Datei ist keine Lizenz für den Freedom-eigenen Quellcode.

## Microsoft .NET

Freedom wird mit dem Microsoft-.NET-Desktop-Runtime veröffentlicht.

- Projekt: <https://github.com/dotnet/runtime>
- Lizenz: MIT

## Microsoft Edge WebView2

Der GLB-Viewer verwendet das Microsoft.Web.WebView2 SDK. Die gemeinsam installierte Evergreen WebView2 Runtime wird nicht als Teil der portablen EXE gebündelt.

- Produkt: <https://developer.microsoft.com/microsoft-edge/webview2/>
- NuGet-Paket: `Microsoft.Web.WebView2` 1.0.4258.31
- Lizenz- und Produktbedingungen: <https://www.nuget.org/packages/Microsoft.Web.WebView2/>

## PdfPig 0.1.16

FileFlow verwendet PdfPig zum Lesen und Extrahieren von Text aus PDF-Dokumenten. Das NuGet-Paket wird mit Freedom ausgeliefert.

- Projekt: <https://github.com/UglyToad/PdfPig>
- NuGet-Paket: `PdfPig` 0.1.16
- Lizenz: Apache License 2.0

## Optionale FileFlow-Werkzeuge

FileFlow kann lokal installierte Programme als getrennte Prozesse aufrufen. Sie werden nicht mit `Freedom.exe` gebündelt und unterliegen ihren eigenen Lizenzen und Nutzungsbedingungen.

- FFmpeg: <https://ffmpeg.org/> – LGPL 2.1+ oder, abhängig vom verwendeten Build und dessen aktivierten Komponenten, GPL 2+
- LibreOffice: <https://www.libreoffice.org/> – Mozilla Public License 2.0
- yt-dlp: <https://github.com/yt-dlp/yt-dlp> – Unlicense
- Deno: <https://deno.com/> – MIT; wird von aktuellen yt-dlp-Versionen für bestimmte YouTube-Verarbeitungen benötigt

## three.js r169

Der Offline-GLB-Viewer enthält three.js, GLTFLoader, OrbitControls, DRACOLoader, KTX2Loader, RoomEnvironment und zugehörige Hilfsdateien.

- Projekt: <https://github.com/mrdoob/three.js>
- Lizenz: MIT
- Vollständiger Lizenztext: `ViennaAtomicClock/GlbViewer/Web/lib/THREE-LICENSE.txt`
- Copyright © 2010-2024 three.js authors

## meshoptimizer

Der GLB-Viewer enthält den Meshopt-Decoder.

- Projekt: <https://github.com/zeux/meshoptimizer>
- Lizenz: MIT
- Copyright © 2016-2022 Arseny Kapoulkine

## Draco, Basis Universal, KTX und Zstandard-Decoder

Die lokalen Decoderdateien werden vom three.js-GLTF-Ladepfad verwendet. Die jeweiligen Upstream-Lizenzbedingungen gelten weiterhin.

- Draco: <https://github.com/google/draco>
- Basis Universal: <https://github.com/BinomialLLC/basis_universal>
- KTX-Software: <https://github.com/KhronosGroup/KTX-Software>
- zstddec: Bestandteil der von three.js bereitgestellten Loader-Abhängigkeiten

## Wetter- und Kartendaten

- Open-Meteo: <https://open-meteo.com/> – Wetter-, Luftqualitäts-, Geocoding- und Kartendaten gemäß den dort ausgewiesenen Bedingungen, unter anderem CC BY 4.0.
- GeoSphere Austria: <https://www.geosphere.at/> – österreichische Wetterwarnungen und offene Daten gemäß den Bedingungen des Anbieters.
- OpenStreetMap: <https://www.openstreetmap.org/copyright> – Kartendaten © OpenStreetMap-Mitwirkende, ODbL.

## GLB-Testmodelle

Die GLB-Dateien unter `Freedom.Tests/Fixtures/glb` stammen aus den Khronos glTF Sample Assets und werden ausschließlich für automatisierte Tests verwendet. Quellen und Modelllizenzen sind in `Freedom.Tests/Fixtures/glb/SOURCES.txt` dokumentiert.
