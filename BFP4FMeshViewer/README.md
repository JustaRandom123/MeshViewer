# BFP4F Mesh Viewer

Minimalistischer Betrachter für Refractor-2 `.bundledmesh`- und `.staticmesh`-Dateien (BF2 / BFP4F)
mit Texturdarstellung. Ordner laden, Item anklicken, fertig.

## Bauen

```
dotnet build -c Release
```

Oder `BFP4FMeshViewer.csproj` in Visual Studio öffnen und starten.

Zielframework ist **.NET Framework 4.7.2**, passend zu deiner vorhandenen
VS-Installation. Für .NET 8 in der `.csproj` eine Zeile ändern:

```xml
<TargetFramework>net8.0-windows</TargetFramework>
```

**Keine NuGet-Pakete, keine externen DLLs.** Kein SharpDX, kein `texconv.exe` —
gerendert wird mit dem in WPF eingebauten `Viewport3D`, DDS-Dateien dekodiert
der eigene Decoder in `Bf2/DdsImage.cs`.

## Bedienung

| Eingabe | Wirkung |
|---|---|
| **Ordner laden…** | durchsucht den Ordner rekursiv nach `.bundledmesh`, `.staticmesh` und Texturen |
| Klick auf Listeneintrag | lädt und rendert das Mesh |
| Linke Maustaste ziehen | Orbit |
| Rechte Maustaste ziehen | Verschieben |
| Mausrad | Zoom |
| **Tab** | Panel aus-/einblenden (Render füllt dann das ganze Fenster) |
| **F** | Modell neu einrahmen |
| **F11** | Vollbild, **Esc** zurück |
| **Strg+S** | Screenshot speichern |

Der Ordner lässt sich auch als Startargument übergeben:
`BFP4FMeshViewer.exe "D:\bfp4f\objects\weapons"`

**LOD**: `Geom0` ist die First-Person-Variante, `Geom1` Third-Person,
`Geom2` das Wrack. `Lod0` ist jeweils die höchste Detailstufe.

**Textur-Slot**: Ein Material verweist meist auf mehrere Maps (Diffuse, Normal,
Detail). Der Viewer nimmt standardmäßig Slot 0 und probiert bei Misserfolg der
Reihe nach weiter. Zeigt er die falsche Map, hier den Slot umstellen.

**Alpha immer ignorieren**: BF2 speichert die Specular-Map im Alphakanal der
Diffuse-Textur. WPF würde Alpha als Deckkraft lesen, wodurch Modelle fast
unsichtbar werden. Der Viewer verwirft Alpha deshalb bei Materialien mit
`alphaMode == 0`. Bleibt ein Material trotzdem durchsichtig, erzwingt dieser
Haken das für alle Materialien — dann wird allerdings auch echtes Scope-Glas
undurchsichtig.

## Screenshots

Der Knopf **Screenshot speichern** rendert das Modell in der aktuellen
Ausrichtung in eine feste Bildgröße, standardmäßig **400×180** passend zu den
Attachment-Icons in voller Auflösung. Ablage:

```
<geladener Ordner>\screenshots\<meshname>.png
```

Vorhandene Dateien werden überschrieben, damit sich eine Ansicht schnell
nachjustieren lässt.

- **Transparenter Hintergrund**, echtes RGBA wie in den Original-Icons.
- **8-faches Supersampling**, danach hochwertig heruntergerechnet — sonst
  fransen die Kanten aus. Bei 400×180 rendert der Viewer intern 3200×1440.
- **Auto-Fit** passt das Modell formatfüllend ein und berechnet dafür den
  nötigen Kameraabstand exakt aus den acht Eckpunkten der Bounding Box, nicht
  über eine Hüllkugel. Bei einem langen, schmalen Objekt wie einem Zielfernrohr
  macht das den Unterschied zwischen bildfüllend und verloren in der Mitte.
  Haken raus: der Zoom aus der Ansicht wird übernommen.
- Die Kameraausrichtung kommt immer aus der aktuellen Ansicht. Erst im Fenster
  hindrehen, dann auslösen.

Das Motiv wird dabei **zentriert**. In der Referenzvorlage sitzt es bündig am
linken Rand — ob das Absicht oder ein Nebeneffekt der ursprünglichen
Icon-Pipeline ist, lässt sich aus einem einzelnen Beispiel nicht ableiten.

## Aufbau

| Datei | Inhalt |
|---|---|
| `Bf2/Bf2Mesh.cs` | Parser für das Dateiformat (Bundled + Static) |
| `Bf2/DdsImage.cs` | DDS-Decoder: BC1/DXT1, BC2/DXT3, BC3/DXT5, unkomprimiert |
| `Bf2/MeshBuilder.cs` | baut `Model3DGroup`, löst Texturpfade im Ordner auf |
| `Bf2/Snapshot.cs` | Offscreen-Rendering in feste Bildgröße, PNG-Export |
| `MainWindow.xaml(.cs)` | Oberfläche und Orbit-Kamera |

## Dateiformat

Gelesen in dieser Reihenfolge, alles Little Endian:

```
Header      u32 u1, u32 version, u32 u2, u32 u3, u32 u4, u8 u5
Geometry    u32 geomCount
            u32 lodCount   x geomCount
            u32 vertexElementCount
            {u16 flag, u16 offset, u16 varType, u16 usage}  x count
            u32 vertexFormat, u32 vertexStride, u32 vertexCount
            float  x (vertexCount * vertexStride / vertexFormat)
            u32 indexCount, u16 x indexCount
u32         (unbekannt)
Lods        x Summe aller lodCount
  bundled   v6:  vec3 min, vec3 max, vec3 pivot, u32 nodeCount
            v10: vec3 min, vec3 max, u32 nodeCount,
                 bei header.u5==1: {4x4 float Matrix, String} x nodeCount
  static    vec3 min, vec3 max, (nur v4: vec3 pivot), u32 nodeCount,
            4x4 float Matrix x nodeCount
Materials   x Summe aller lodCount
            u32 materialCount
            { u32 alphaMode, String shader, String technique,
              u32 mapCount, String x mapCount,
              u32 vertexStart, u32 indexStart, u32 indexCount, u32 vertexCount,
              u32 u1, u16 u2, u16 u3,
              nur static v11: vec3 min, vec3 max }
```

Der Mesh-Typ kommt aus der Dateiendung. Bei StaticMeshes bestimmt der
Textur-Slot auch den UV-Satz (Slot 0 Base → UV1, 1 Detail → UV2, 2 Dirt → UV3,
3 Crack → UV4, wie bei `BaseDetailDirtCrack`); BundledMeshes nutzen immer UV1.

`String` = `u32` Länge + ASCII ohne Nullterminator.

Die Vertexattribut-Tabelle wird tatsächlich ausgewertet, statt die UV-Position
fest zu verdrahten. `flag == 0` heißt benutzt, `offset` ist ein **Byte**-Offset.
Usage-Werte nach `D3DDECLUSAGE`: 0 Position, 3 Normal, 5 UV1, 6 Tangent,
261 UV2. Beim üblichen BFP4F-Layout (Position, Normal, BlendIndices, UV1)
landet UV dadurch auf Float-Index 7/8 — genau wie im Explorer fest kodiert,
nur eben auch korrekt, wenn ein Mesh davon abweicht.

Positionen und Normalen werden in Z gespiegelt (Refractor ist linkshändig, WPF
rechtshändig), die Dreiecksumlaufrichtung entsprechend getauscht. Beide
Materialseiten werden gerendert, weil die Umlaufrichtung in BF2-Meshes nicht
durchgängig konsistent ist.

## Grenzen

- Nur `.bundledmesh` und `.staticmesh`. `.skinnedmesh` hat abweichende
  LOD- (Rig-) und Materialblöcke.
- BundledMesh nur Version 6 und 10, wie im Explorer. Andere Versionen brechen mit
  einer klaren Meldung ab statt stillschweigend Müll zu lesen.
- Loose Dateien im Ordner, keine `.zip`-Archive.
- Nur die Diffuse-Map, keine Normal Maps und keine Shader.
- Bones werden übersprungen, Meshes erscheinen in Bindepose.
- Meldet die Statuszeile ungeparste Bytes am Dateiende, ist das Mesh eine
  Variante, die der Parser noch nicht vollständig kennt.

## Stand der Prüfung

Der DDS-Decoder ist gegen ImageMagick verifiziert: DXT1 und DXT5 stimmen bis
auf eine Stufe (Rundung der 5/6-Bit-Expansion), Alpha exakt, unkomprimiertes
32-Bit bit-genau. DXT3 ist mitimplementiert, aber mangels Testdatei nicht
gegengeprüft.

Der Mesh-Parser folgt dem Quellcode des BFP4F Explorers, wurde aber noch an
keiner echten `.bundledmesh`-Datei ausgeführt — dafür fehlte hier ein Sample.
