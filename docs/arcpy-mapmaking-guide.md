# ArcPy Map-Making Wiki

Karpathy-style, compact, code-first notes for ArcGIS Pro map/layout automation.

Sources:
- ArcPy reference: https://pro.arcgis.com/en/pro-app/latest/arcpy/main/arcgis-pro-arcpy-reference.htm
- `arcpy.mp`: https://pro.arcgis.com/en/pro-app/latest/arcpy/mapping/introduction-to-arcpy-mp.htm
- `ArcGISProject`: https://pro.arcgis.com/en/pro-app/latest/arcpy/mapping/arcgisproject-class.htm
- `Map`: https://pro.arcgis.com/en/pro-app/latest/arcpy/mapping/map-class.htm
- `Layout`: https://pro.arcgis.com/en/pro-app/latest/arcpy/mapping/layout-class.htm
- `MapFrame`: https://pro.arcgis.com/en/pro-app/latest/arcpy/mapping/mapframe-class.htm
- `MapSeries`: https://pro.arcgis.com/en/pro-app/latest/arcpy/mapping/mapseries-class.htm
- `Layer`: https://pro.arcgis.com/en/pro-app/latest/arcpy/mapping/layer-class.htm
- `Symbology`: https://pro.arcgis.com/en/pro-app/latest/arcpy/mapping/symbology-class.htm
- Exports: https://pro.arcgis.com/en/pro-app/latest/arcpy/mapping/createexportformat.htm
- Cartography toolbox: https://pro.arcgis.com/en/pro-app/latest/tool-reference/cartography/an-overview-of-the-cartography-toolbox.htm
- ArcMap migration: https://pro.arcgis.com/en/pro-app/latest/arcpy/mapping/migratingfrom10xarcpymapping.htm

Scope:
- Yes: `arcpy.mp`, map/layer/layout/export/map series automation.
- Yes: `arcpy.cartography` when preparing map-production layers.
- Limited: `arcpy.management.ApplySymbologyFromLayer`, selections, feature layers as map glue.
- No: spatial analysis, editing, ETL, geocoding, network analysis, GUI instructions.

Version stance:
- ArcGIS Pro = Python 3.x + `arcpy.mp` + `.aprx`.
- ArcMap = Python 2.x + `arcpy.mapping` + `.mxd`.
- `MapDocument` is ArcMap-only; in Pro use `ArcGISProject`.
- Pro 3.4+: prefer `CreateExportFormat(...)` + `.export(...)`; `exportToPDF` etc. are legacy.

---

## Mental Model

```text
ArcGISProject (.aprx)
  Map[]                         # layers/tables/spatial reference/bookmarks
    Layer[]                     # visibility/query/labels/source/symbology
  Layout[]                      # page, elements, export, map series
    MapFrame[]                  # displays one Map on the page
    Text/Legend/Surround/etc.
    MapSeries | BookmarkMapSeries | None
```

```python
import arcpy
from pathlib import Path

aprx = arcpy.mp.ArcGISProject(r"C:\Projects\Atlas\Atlas.aprx")
m = aprx.listMaps("Main Map")[0]
lyt = aprx.listLayouts("County Atlas")[0]
mf = lyt.listElements("MAPFRAME_ELEMENT", "Map Frame")[0]
```

Rules:
- Project owns maps/layouts/styles/connections.
- Map owns layers/tables.
- Layout owns page elements and map series.
- MapFrame displays a Map and controls layout extent/scale.
- Layer owns visibility, labels, query, source, symbology.
- Export is on `Layout`, `MapFrame`, `MapSeries`, or `MapView`.

---

## Quick Start

### Current project, inside ArcGIS Pro

```python
import arcpy

aprx = arcpy.mp.ArcGISProject("CURRENT")
m = aprx.listMaps()[0]
lyt = aprx.listLayouts()[0]
mf = lyt.listElements("MAPFRAME_ELEMENT")[0]
```

`CURRENT` only works in Pro: Python window, notebook, or script tool.

### Stand-alone project path

```python
import arcpy
from pathlib import Path

aprx = arcpy.mp.ArcGISProject(r"C:\Projects\Atlas\Atlas.aprx")
lyt = aprx.listLayouts("Main Layout")[0]

out = Path(r"C:\Projects\Atlas\exports\main.pdf")
out.parent.mkdir(parents=True, exist_ok=True)

pdf = arcpy.mp.CreateExportFormat("PDF", str(out))
pdf.resolution = 300
pdf.embedFonts = True
pdf.georefInfo = True
lyt.export(pdf)
```

### Safer lookup helper

```python
def one(items, label):
    if len(items) != 1:
        raise LookupError(f"Expected one {label}, found {len(items)}")
    return items[0]

aprx = arcpy.mp.ArcGISProject("CURRENT")
m = one(aprx.listMaps("Main Map"), "map")
lyt = one(aprx.listLayouts("Main Layout"), "layout")
mf = one(lyt.listElements("MAPFRAME_ELEMENT", "Map Frame"), "map frame")
```

### Save

```python
if aprx.isReadOnly:
    aprx.saveACopy(r"C:\Projects\Atlas\Atlas_copy.aprx")
else:
    aprx.save()
```

---

## Core Classes

### `ArcGISProject`

Constructor:

```python
aprx = arcpy.mp.ArcGISProject("CURRENT")
aprx = arcpy.mp.ArcGISProject(r"C:\Projects\Atlas\Atlas.aprx")
```

Common properties:

```text
activeMap          -> Map | None, in-app only
activeView         -> Layout | MapView | Report | None, in-app only
defaultGeodatabase -> str
defaultToolbox     -> str
documentVersion    -> str
filePath           -> str
homeFolder         -> str
isReadOnly         -> bool
styles             -> list[str]
```

Common methods:

```text
listMaps(wildcard)                  -> list[Map]
listLayouts(wildcard)               -> list[Layout]
listBrokenDataSources()             -> list[Layer | Table]
listColorRamps(wildcard)            -> list[ColorRamp]
listStyleItems(style, class, wild)   -> list[StyleItem]
createMap(name, map_type)           -> Map
createLayout(width, height, units)   -> Layout
copyItem(project_item, new_name)     -> Map | Layout | Report
deleteItem(project_item)            -> None
importDocument(path, ...)           -> Object
updateConnectionProperties(old,new)  -> None
save(), saveACopy(path)             -> None
```

```python
aprx = arcpy.mp.ArcGISProject(r"C:\Projects\Atlas\Atlas.aprx")
for m in aprx.listMaps():
    print(m.name, m.mapType, len(m.listLayers()))

for item in aprx.listBrokenDataSources():
    print(item.longName)
```

```python
m = aprx.createMap("Production Map", "MAP")
m.addBasemap("Light Gray Canvas")
lyt = aprx.createLayout(11, 8.5, "INCH", "Letter Landscape")
```

### `Map`

Gets:

```python
m = aprx.listMaps("Main Map")[0]
m = mf.map
```

Properties:

```text
name             -> str
mapType          -> "MAP" | "SCENE" | "BASEMAP"
mapUnits         -> str
spatialReference -> arcpy.SpatialReference
referenceScale   -> float
defaultCamera    -> Camera
colorModel       -> "RGB" | "CMYK"
URI              -> str
```

Methods:

```text
addDataFromPath(path_or_url, web_service_type="AUTOMATIC", custom_parameters=None) -> Layer
addLayer(layer_or_layerfile, add_position="AUTO_ARRANGE")                         -> list[Layer]
addLayerToGroup(group, layer_or_layerfile, add_position="AUTO_ARRANGE")           -> list[Layer]
insertLayer(reference_layer, layer_or_layerfile, position)                        -> list[Layer]
moveLayer(reference_layer, move_layer, position)                                  -> None
removeLayer(layer)                                                               -> None
listLayers(wildcard)                                                             -> list[Layer]
listTables(wildcard)                                                             -> list[Table]
listBrokenDataSources()                                                          -> list[Layer | Table]
createGroupLayer(name, group_layer=None)                                         -> Layer
createGraphicsLayer(name)                                                        -> Layer
clipLayers(clip_object, selection=False)                                         -> None
clearSelection()                                                                 -> None
updateConnectionProperties(old, new, ...)                                        -> None
getDefinition("V3") / setDefinition(cim)                                         -> CIM
```

```python
lyr = m.addDataFromPath(r"C:\Projects\Atlas\data.gdb\Counties")
lyr.name = "Counties"
lyr.visible = True
```

```python
lf = arcpy.mp.LayerFile(r"C:\Projects\Atlas\styles\Roads.lyrx")
roads = m.addLayer(lf, "TOP")[0]
```

```python
m.spatialReference = arcpy.SpatialReference(102660)
m.referenceScale = 24000
```

### `LayerFile`

```python
lf = arcpy.mp.LayerFile(r"C:\Projects\Atlas\styles\Roads.lyrx")
for lyr in lf.listLayers():
    print(lyr.longName)
```

Notes:
- Pro reads `.lyr` and `.lyrx`.
- Save edits to `.lyrx`.
- `.lyrx` can contain multiple root layers, group layers, and tables.

### `Layer`

Gets:

```python
lyr = m.listLayers("Counties")[0]
sub = group_layer.listLayers("County Seats")[0]
```

Properties:

```text
name, longName                 -> str
visible                        -> bool
isFeatureLayer/isRasterLayer   -> bool
isGroupLayer/isBasemapLayer    -> bool
isBroken                       -> bool
definitionQuery                -> str
showLabels                     -> bool
symbology                      -> Symbology
transparency                   -> int 0..100
minThreshold/maxThreshold      -> float, 0 clears
pageQuery                      -> tuple
URI                            -> str
```

Methods:

```text
supports("PROP")                         -> bool
listLayers(wildcard)                     -> list[Layer]
listLabelClasses(wildcard)               -> list[LabelClass]
createLabelClass(name, expr, sql, lang)  -> LabelClass
listDefinitionQueries(wildcard)          -> list[dict]
updateDefinitionQueries(list[dict])      -> None
setPageQuery(field_name, match=True)      -> None
getSelectionSet()                         -> set[int]
setSelectionSet(oidList, method="NEW")    -> None
updateConnectionProperties(old,new,...)   -> None
getDefinition("V3") / setDefinition(cim)  -> CIM
getSymbologyDefinition("V3")              -> CIM symbology
setSymbologyDefinition(cim_sym)           -> None
saveACopy(path)                           -> None
```

```python
for lyr in m.listLayers():
    if lyr.supports("TRANSPARENCY"):
        lyr.transparency = 35
    if lyr.supports("SHOWLABELS"):
        lyr.showLabels = False
```

```python
parcels = m.listLayers("Parcels")[0]
parcels.definitionQuery = "LANDUSE = 'RES'"
```

```python
roads = m.listLayers("Local Roads")[0]
roads.minThreshold = 24000
roads.maxThreshold = 0
```

### `Symbology`

Workflow:

```python
sym = lyr.symbology
# mutate sym
lyr.symbology = sym
```

Supported feature renderers:

```text
SimpleRenderer
GraduatedColorsRenderer
GraduatedSymbolsRenderer
UnclassedColorsRenderer
UniqueValueRenderer
```

Supported raster colorizers:

```text
RasterClassifyColorizer
RasterStretchColorizer
RasterUniqueValueColorizer
```

Simple:

```python
lyr = m.listLayers("Counties")[0]
sym = lyr.symbology

if hasattr(sym, "renderer"):
    sym.updateRenderer("SimpleRenderer")
    sym.renderer.symbol.color = {"RGB": [240, 240, 240, 100]}
    sym.renderer.symbol.outlineColor = {"RGB": [80, 80, 80, 100]}
    sym.renderer.symbol.outlineWidth = 0.5
    lyr.symbology = sym
```

Unique value:

```python
sym = lyr.symbology
if hasattr(sym, "renderer"):
    sym.updateRenderer("UniqueValueRenderer")
    sym.renderer.fields = ["TYPE"]
    lyr.symbology = sym
```

Graduated color:

```python
sym = lyr.symbology
if hasattr(sym, "renderer"):
    sym.updateRenderer("GraduatedColorsRenderer")
    sym.renderer.classificationField = "POP_DENS"
    sym.renderer.breakCount = 5
    sym.renderer.colorRamp = aprx.listColorRamps("Cyan to Purple")[0]
    lyr.symbology = sym
```

Template `.lyrx`:

```python
arcpy.management.ApplySymbologyFromLayer(
    lyr,
    r"C:\Projects\Atlas\styles\CountyChoropleth.lyrx",
    [["VALUE_FIELD", "#", "POP_DENS"]],
    "UPDATE"
)
```

CIM fallback:

```python
cim = lyr.getDefinition("V3")
if hasattr(cim.renderer, "symbol"):
    symbol = cim.renderer.symbol.symbol
    if symbol.symbolLayers:
        symbol.symbolLayers[0].width = 1.25
lyr.setDefinition(cim)
```

### `Layout`

Gets/create:

```python
lyt = aprx.listLayouts("Main Layout")[0]
lyt = aprx.createLayout(11, 8.5, "INCH", "New Layout")
```

Properties:

```text
name, pageWidth, pageHeight, pageUnits
colorModel -> "RGB" | "CMYK"
mapSeries  -> MapSeries | BookmarkMapSeries | None
URI
```

Methods:

```text
listElements(element_type, wildcard)                    -> list[Element]
createMapFrame(geometry, map, name)                     -> MapFrame
createMapSurroundElement(geometry, type, mapframe, ...) -> MapSurroundElement
createTableFrameElement(geometry, mapframe, table, ...) -> TableFrameElement
createSpatialMapSeries(mapframe, index_layer, name, sort_field=None) -> MapSeries
createBookmarkMapSeries(mapframe, bookmarks=None)       -> BookmarkMapSeries
changePageSize(width, height, resize_elements=True)     -> None
deleteElement(element)                                  -> None
export(export_format, display_options=None)             -> None
openView()                                              -> in-app only
```

```python
coords = [(0.5, 0.75), (0.5, 8.0), (10.5, 8.0), (10.5, 0.75), (0.5, 0.75)]
poly = arcpy.Polygon(arcpy.Array([arcpy.Point(x, y) for x, y in coords]))
mf = lyt.createMapFrame(poly, m, "Map Frame")
```

```python
title = aprx.createTextElement(
    lyt, arcpy.Point(0.5, 8.15), "POINT",
    "County Reference Map", 18, "Aptos", "Regular", None, "Title"
)
```

```python
for elm in lyt.listElements():
    print(elm.type, elm.name)
```

### `MapFrame`

```python
mf = lyt.listElements("MAPFRAME_ELEMENT", "Map Frame")[0]
```

Properties:

```text
name, map, camera, visible, locked
elementWidth, elementHeight
elementPositionX, elementPositionY
```

Methods:

```text
getLayerExtent(layer, selection_only, symbolized_extent) -> Extent
zoomToAllLayers(selection_only=False)                    -> None
zoomToBookmark(bookmark)                                 -> None
createBookmark(name, description)                        -> Bookmark
export(export_format, display_options=None)              -> None
getDefinition("V3") / setDefinition(cim)                 -> CIM
```

```python
extent = mf.getLayerExtent(lyr, False, True)
mf.camera.setExtent(extent)
mf.camera.scale *= 1.05
```

```python
arcpy.management.SelectLayerByAttribute(lyr, "NEW_SELECTION", "NAME = 'Broward'")
mf.zoomToAllLayers(True)
arcpy.management.SelectLayerByAttribute(lyr, "CLEAR_SELECTION")
```

```python
png = arcpy.mp.CreateExportFormat("PNG", r"C:\Projects\Atlas\exports\map_only.png")
png.resolution = 300
mf.export(png)
```

### `Camera`

```python
mf.camera.setExtent(arcpy.Extent(xmin, ymin, xmax, ymax))
mf.camera.scale = 24000
mf.camera.heading = 0
```

```python
extent = mf.getLayerExtent(lyr, False, True)
mf.camera.setExtent(extent)
```

### `MapSeries`

Get/create:

```python
ms = lyt.mapSeries
ms = lyt.createSpatialMapSeries(mf, index_layer, "PAGE_NAME", "PAGE_NUM")
```

Properties:

```text
enabled               -> bool
currentPageNumber     -> int | str
pageCount             -> int
pageNameField         -> Field
pageRow               -> Row
indexLayer            -> Layer
mapFrame              -> MapFrame
clipToIndexFeature    -> bool
selectedIndexFeatures -> list[int]
```

Methods:

```text
export(export_format, mapseries_export_options=None, display_options=None) -> None
getPageNumberFromName(page_name)                                          -> int
refresh()                                                                -> None
getDefinition("V3") / setDefinition(cim)                                  -> CIM
```

```python
ms = lyt.mapSeries
if not (ms and ms.enabled):
    raise RuntimeError("No enabled map series")

pdf = arcpy.mp.CreateExportFormat("PDF", r"C:\Projects\Atlas\exports\atlas.pdf")
pdf.resolution = 300
ms.export(pdf)
```

```python
ms.currentPageNumber = ms.getPageNumberFromName("Broward")
pdf.filePath = r"C:\Projects\Atlas\exports\Broward.pdf"
ms.export(pdf)
```

Selected pages:

```python
arcpy.management.SelectLayerByAttribute(ms.indexLayer, "NEW_SELECTION", "REGION = 'South'")

opts = arcpy.mp.CreateExportOptions("MAPSERIES")
opts.setExportPages("SELECTED")

pdf = arcpy.mp.CreateExportFormat("PDF", r"C:\Projects\Atlas\exports\south.pdf")
ms.export(pdf, opts)
```

Legacy:

```python
ms.exportToPDF(r"C:\Projects\Atlas\exports\atlas_legacy.pdf", "ALL", "", "PDF_SINGLE_FILE", 300)
```

### `MapDocument`

```text
ArcMap only. Not the ArcGIS Pro object model.
```

Migration:

| ArcMap | ArcGIS Pro |
|---|---|
| `arcpy.mapping.MapDocument(mxd)` | `arcpy.mp.ArcGISProject(aprx)` |
| `.mxd` | `.aprx` |
| `DataFrame` | `Map` + `MapFrame` + `Camera` |
| `ListLayers(mxd, ..., df)` | `m.listLayers(...)` |
| `ListLayoutElements(mxd, ...)` | `lyt.listElements(...)` |
| `ExportToPDF(mxd, ...)` | `lyt.export(pdf)` |
| `AddLayer(df, lyr)` | `m.addLayer(lyr)` |

```python
aprx = arcpy.mp.ArcGISProject(r"C:\Projects\New.aprx")
aprx.importDocument(r"C:\Maps\Old.mxd")
aprx.save()
```

---

## Export Patterns

Create format:

```python
pdf = arcpy.mp.CreateExportFormat("PDF", r"C:\out\layout.pdf")
png = arcpy.mp.CreateExportFormat("PNG", r"C:\out\map.png")
tif = arcpy.mp.CreateExportFormat("TIFF", r"C:\out\map.tif")
svg = arcpy.mp.CreateExportFormat("SVG", r"C:\out\layout.svg")
aix = arcpy.mp.CreateExportFormat("AIX", r"C:\out\layout.aix")
```

Supported names:

```text
AIX BMP EMF EPS GIF JPEG PDF PNG SVG TGA TIFF
```

PDF, print quality:

```python
pdf = arcpy.mp.CreateExportFormat("PDF", r"C:\out\layout.pdf")
pdf.resolution = 300
pdf.embedFonts = True
pdf.embedColorProfile = True
pdf.georefInfo = True
pdf.compressVectorGraphics = True
pdf.setImageQuality("BEST")
pdf.setLayersAndAttributes("LAYERS_ONLY")
lyt.export(pdf)
```

PDF, smaller:

```python
pdf = arcpy.mp.CreateExportFormat("PDF", r"C:\out\small.pdf")
pdf.resolution = 150
pdf.embedFonts = False
pdf.georefInfo = False
pdf.outputAsImage = True
pdf.imageCompressionQuality = 50
pdf.setImageQuality("FASTER")
pdf.setLayersAndAttributes("NONE")
lyt.export(pdf)
```

Object choice:

```python
lyt.export(pdf)  # whole page
mf.export(png)   # map frame only
ms.export(pdf)   # map series pages
```

---

## Common Patterns

### Add layer, position it

```python
roads = m.addDataFromPath(r"C:\Projects\Atlas\data.gdb\Roads")
boundary = m.listLayers("County Boundary")[0]
m.moveLayer(boundary, roads, "BEFORE")
```

### Group layer

```python
group = m.createGroupLayer("Reference")
lf = arcpy.mp.LayerFile(r"C:\Projects\Atlas\styles\Hydro.lyrx")
m.addLayerToGroup(group, lf, "BOTTOM")
```

### Replace workspace

```python
aprx.updateConnectionProperties(
    r"C:\Projects\Atlas\old_data.gdb",
    r"C:\Projects\Atlas\new_data.gdb",
    validate=True
)
```

### Broken sources

```python
broken = aprx.listBrokenDataSources()
if broken:
    raise RuntimeError("; ".join(x.longName for x in broken))
```

### Visibility set

```python
keep = {"County Boundary", "Major Roads", "Cities"}
for lyr in m.listLayers():
    lyr.visible = lyr.name in keep
```

### Definition query by field

```python
county = "Broward"
for lyr in m.listLayers():
    if lyr.isFeatureLayer and lyr.supports("DEFINITIONQUERY"):
        fields = {f.name.upper() for f in arcpy.ListFields(lyr)}
        if "COUNTY" in fields:
            lyr.definitionQuery = f"COUNTY = '{county}'"
```

### Labels

```python
lyr = m.listLayers("Cities")[0]
if lyr.supports("SHOWLABELS"):
    lyr.showLabels = True
    lbl = lyr.listLabelClasses()[0]
    lbl.expression = "$feature.NAME"
    lbl.visible = True
```

### New label class

```python
roads = m.listLayers("Roads")[0]
roads.showLabels = True
lc = roads.createLabelClass("Highways", "$feature.ROUTE", "CLASS = 'HIGHWAY'", "Arcade")
lc.visible = True
```

### Page query

```python
labels = m.listLayers("Inset Labels")[0]
labels.setPageQuery("PAGE_NAME", True)
```

### Create layout and frame

```python
lyt = aprx.createLayout(11, 8.5, "INCH", "Auto Layout")
box = arcpy.Polygon(arcpy.Array([
    arcpy.Point(0.5, 0.5), arcpy.Point(0.5, 8.0),
    arcpy.Point(10.5, 8.0), arcpy.Point(10.5, 0.5),
    arcpy.Point(0.5, 0.5),
]))
mf = lyt.createMapFrame(box, m, "Map Frame")
mf.zoomToAllLayers(False)
```

### Text elements

```python
for elm in lyt.listElements("TEXT_ELEMENT", "Title"):
    elm.text = "Broward County"
```

### Legend, scale bar, north arrow

```python
legend_box = arcpy.Polygon(arcpy.Array([
    arcpy.Point(8.2, 0.8), arcpy.Point(8.2, 3.2),
    arcpy.Point(10.6, 3.2), arcpy.Point(10.6, 0.8),
    arcpy.Point(8.2, 0.8),
]))
legend = lyt.createMapSurroundElement(legend_box, "LEGEND", mf, None, "Legend")
scale = lyt.createMapSurroundElement(arcpy.Point(0.75, 0.35), "SCALE_BAR", mf, None, "Scale Bar")
north = lyt.createMapSurroundElement(arcpy.Point(10.1, 7.55), "NORTH_ARROW", mf, None, "North Arrow")
```

### Legend-only fake layers

Use a fake, empty feature class when the map layer needs one symbol but the legend needs a different symbol. This is useful for offset route lines: the real map layer can keep its geometric offset, while the legend patch uses a clean non-offset line that does not overlap adjacent legend rows.

The reliable pattern is:

- Keep the real layer visible in the map.
- Hide the real layer's legend item.
- Create an empty feature class in the project geodatabase with the same geometry type and spatial reference.
- Add that empty feature class to the map as a legend-only layer.
- Give it the desired legend symbol and keep it visible. Because it has zero features, it draws nothing on the map but still appears in the legend.
- Turn off legend auto-sync flags while curating item order and visibility.

Do not use a duplicate layer with a definition query such as `1 = 0` for this purpose. ArcGIS Pro can still behave inconsistently in legends, and real-data duplicates can accidentally draw on the map. Empty feature classes are cleaner and safer.

Minimal line-layer example:

```python
import arcpy
from pathlib import Path

aprx = arcpy.mp.ArcGISProject("CURRENT")
m = aprx.listMaps("Map")[0]
lyt = aprx.listLayouts("North")[0]
legend = lyt.listElements("LEGEND_ELEMENT", "Legend")[0]

gdb = Path(aprx.defaultGeodatabase)
sr = m.spatialReference
name = "LegendOnly_Brightline_Route"
fc = str(gdb / name)

if not arcpy.Exists(fc):
    arcpy.management.CreateFeatureclass(str(gdb), name, "POLYLINE", spatial_reference=sr)

legend_only = m.addDataFromPath(fc)
legend_only.name = "Brightline (Legend)"

sym = legend_only.symbology
sym.updateRenderer("SimpleRenderer")
sym.renderer.symbol.color = {"RGB": [255, 217, 0, 100]}
sym.renderer.symbol.size = 2.5
legend_only.symbology = sym

legend.syncNewLayer = False
legend.syncLayerOrder = False
legend.syncLayerVisibility = False

for item in legend.items:
    if item.name == "Brightline":
        item.visible = False
        item.autoVisibility = False
    elif item.name == "Brightline (Legend)":
        item.visible = True
        item.autoVisibility = False
        item.showLayerName = False
        item.showHeading = False
        item.showLabels = True
        item.patchWidth = 24
        item.patchHeight = 12
```

If item order or display options are not exposed cleanly through the public legend API, edit the layout CIM narrowly: get the layout definition, find the `CIMLegend`, reorder `legend.items`, update only the affected legend item properties, then `layout.setDefinition(cim)`. Preview before saving.

### Export each feature

```python
from pathlib import Path

out_dir = Path(r"C:\Projects\Atlas\exports\counties")
out_dir.mkdir(parents=True, exist_ok=True)
lyr = m.listLayers("Counties")[0]
pdf = arcpy.mp.CreateExportFormat("PDF")
pdf.resolution = 300

with arcpy.da.SearchCursor(lyr, ["OID@", "NAME", "SHAPE@"]) as rows:
    for oid, name, geom in rows:
        arcpy.management.SelectLayerByAttribute(lyr, "NEW_SELECTION", f"OBJECTID = {oid}")
        mf.camera.setExtent(geom.extent)
        mf.camera.scale *= 1.1
        safe = "".join(c if c.isalnum() or c in " _-" else "_" for c in name)
        pdf.filePath = str(out_dir / f"{safe}.pdf")
        lyt.export(pdf)

arcpy.management.SelectLayerByAttribute(lyr, "CLEAR_SELECTION")
```

### Grid index for spatial map series

```python
arcpy.env.workspace = r"C:\Projects\Atlas\data.gdb"
arcpy.env.outputCoordinateSystem = m.spatialReference

arcpy.cartography.GridIndexFeatures(
    "Atlas_Pages",
    "County_Boundary",
    "INTERSECTFEATURE",
    "NO_USEPAGEUNIT",
    "",
    "10000 Meters",
    "10000 Meters"
)
```

### Strip map index

```python
arcpy.cartography.StripMapIndexFeatures(
    "Route",
    "Route_Pages",
    "NO_USEPAGEUNIT",
    "",
    "8 Kilometers",
    "4 Kilometers"
)
```

### Create/export map series

```python
index_layer = m.listLayers("Atlas_Pages")[0]
ms = lyt.createSpatialMapSeries(mf, index_layer, "PageName", "PageNumber")
ms.enabled = True
ms.refresh()

pdf = arcpy.mp.CreateExportFormat("PDF", r"C:\Projects\Atlas\exports\atlas.pdf")
pdf.resolution = 300
pdf.georefInfo = True
ms.export(pdf)
```

### Clip to index feature

```python
ms.clipToIndexFeature = True
ms.refresh()
```

### Generalize for small-scale display

```python
arcpy.cartography.SimplifyLine("Roads", "Roads_100k", "POINT_REMOVE", "100 Meters")
arcpy.cartography.SmoothLine("Roads_100k", "Roads_100k_smooth", "PAEK", "200 Meters")
arcpy.cartography.SimplifyPolygon("Buildings", "Buildings_s", "POINT_REMOVE", "3 Meters")
arcpy.cartography.AggregatePolygons("Buildings_s", "Buildings_a", "10 Meters")
arcpy.cartography.CreateCartographicPartitions("Roads", "Road_Partitions", 5000)
arcpy.env.cartographicPartitions = "Road_Partitions"
```

### Minimal end-to-end

```python
import arcpy
from pathlib import Path

aprx = arcpy.mp.ArcGISProject(r"C:\Projects\Atlas\Atlas.aprx")
m = aprx.listMaps("Main Map")[0]
lyt = aprx.listLayouts("Atlas Layout")[0]
mf = lyt.listElements("MAPFRAME_ELEMENT", "Map Frame")[0]

if aprx.listBrokenDataSources():
    raise RuntimeError("Broken data sources exist")

counties = m.listLayers("Counties")[0]
counties.definitionQuery = "STATE = 'FL'"

sym = counties.symbology
if hasattr(sym, "renderer"):
    sym.updateRenderer("GraduatedColorsRenderer")
    sym.renderer.classificationField = "POP_DENS"
    sym.renderer.breakCount = 5
    sym.renderer.colorRamp = aprx.listColorRamps("Cyan to Purple")[0]
    counties.symbology = sym

mf.camera.setExtent(mf.getLayerExtent(counties, False, True))
mf.camera.scale *= 1.05

for elm in lyt.listElements("TEXT_ELEMENT", "Title"):
    elm.text = "Florida County Atlas"

out = Path(r"C:\Projects\Atlas\exports\county_atlas.pdf")
out.parent.mkdir(parents=True, exist_ok=True)
pdf = arcpy.mp.CreateExportFormat("PDF", str(out))
pdf.resolution = 300
pdf.embedFonts = True
pdf.georefInfo = True
lyt.export(pdf)
```

---

## Symbology Mutation and Export Debugging

These notes come from a layout export workflow where a layout batch needed to:

- filter one choropleth layer by layout area, for example `PA_Name = 'South'`
- switch the graduated-color field for each mode
- show only the matching street child layer under a `Streets` group
- export without breaking the open ArcGIS Pro editor state

### Field Names vs. Aliases

ArcGIS Pro symbology dropdowns often show field aliases, not real field names.

Example:

```text
field name:  MS_WFH
alias:       Worked From Home
```

Use `arcpy.ListFields(...)` to confirm the real field names and aliases before writing automation:

```python
for field in arcpy.ListFields(dataset):
    print(field.name, field.aliasName, field.type)
```

Robust field lookup should accept either real names or aliases:

```python
def normalize_field(value: str) -> str:
    return value.strip().split(".")[-1].casefold().replace(" ", "_")


def field_or_alias_matches(field, candidate: str) -> bool:
    return (
        field.name.casefold() == candidate.casefold()
        or normalize_field(field.name) == normalize_field(candidate)
        or field.aliasName.casefold() == candidate.casefold()
        or normalize_field(field.aliasName) == normalize_field(candidate)
    )
```

If `arcpy.ListFields(layer)` fails for a layer inside a group or join-heavy map, try the layer object, `Describe(layer).catalogPath`, and `layer.dataSource`:

```python
def list_fields_any_source(layer):
    sources = [layer]
    try:
        sources.append(arcpy.Describe(layer).catalogPath)
    except Exception:
        pass
    try:
        sources.append(layer.dataSource)
    except Exception:
        pass

    for source in sources:
        try:
            fields = arcpy.ListFields(source)
            if fields:
                return fields
        except Exception:
            continue
    raise RuntimeError(f"Could not list fields for {layer.longName}")
```

### Apply Queries Before Classifying

For layout-specific exports, set the definition query before calculating class breaks or exporting.

```python
layer.definitionQuery = "PA_Name = 'South'"
```

If the renderer keeps stale breaks from a previous field or previous query, the layer may draw blank even though the data table has values. The export can look like a missing choropleth because the active field values fall outside old class ranges.

### Avoid Rebuilding Renderer When Possible

The high-level pattern:

```python
sym = layer.symbology
sym.renderer.classificationField = "MS_WFH"
layer.symbology = sym
```

can cause ArcGIS Pro to regenerate class labels, flip normalization, or show `There was an error displaying the symbology` in the Symbology pane.

When the existing renderer is already valid, prefer a narrow CIM field swap:

```python
cim = layer.getDefinition("V3")
renderer = cim.renderer
renderer.field = "MS_WFH"
renderer.heading = "Worked From Home"

if hasattr(renderer, "normalizationField"):
    renderer.normalizationField = ""
if hasattr(renderer, "normalizationType"):
    renderer.normalizationType = "Nothing"

layer.setDefinition(cim)
```

This still mutates symbology, but it avoids rebuilding the renderer object. Always capture the original layer definition before doing this in `CURRENT`:

```python
original_cim = layer.getDefinition("V3")
try:
    # mutate/export
    ...
finally:
    layer.setDefinition(original_cim)
```

### Refresh Class Breaks After Field Changes

Changing only the renderer field may leave stale class breaks from the previous field. Recalculate breaks from the queried layer before export.

Simple quantile upper bounds:

```python
def quantile_upper_bounds(layer, field_name: str, break_count: int = 5) -> list[float]:
    values = []
    with arcpy.da.SearchCursor(layer, [field_name]) as rows:
        for (value,) in rows:
            if value is None:
                continue
            try:
                values.append(float(value))
            except (TypeError, ValueError):
                continue

    values.sort()
    if not values:
        return []

    break_count = max(1, min(break_count, len(values)))
    bounds = []
    for class_index in range(1, break_count + 1):
        value_index = min(len(values) - 1, round((class_index * len(values)) / break_count) - 1)
        bounds.append(values[value_index])
    bounds[-1] = max(values)
    return bounds
```

Apply those bounds to the CIM class breaks:

```python
cim = layer.getDefinition("V3")
renderer = cim.renderer
for class_break, upper_bound in zip(renderer.breaks, bounds):
    class_break.upperBound = upper_bound
renderer.normalizationField = ""
renderer.normalizationType = "Nothing"
layer.setDefinition(cim)
```

### Percent Labels for Class Breaks

ArcGIS may export class labels as raw decimals after field mutation:

```text
0.000000 - 2.526316
```

For percent fields, explicitly freeze and rewrite labels:

```python
cim = layer.getDefinition("V3")
renderer = cim.renderer
renderer.alwaysUpdateClassLabels = False

previous_upper = None
for index, class_break in enumerate(renderer.breaks):
    upper = float(class_break.upperBound)
    if index == 0:
        lower = 0.0
    else:
        lower = round(previous_upper, 1) + 0.1
    class_break.label = f"{lower:.1f}% - {upper:.1f}%"
    previous_upper = upper

layer.setDefinition(cim)
```

### Group Layer Child Visibility

When a layout needs one street child layer under a `Streets` group, toggle the children, not a query on the group:

```python
target = "Street_South"
for lyr in map_obj.listLayers():
    if lyr.name.startswith("Street_"):
        lyr.visible = lyr.name == target

    # Keep parent groups visible for the target child.
    if lyr.longName == "Streets":
        lyr.visible = True
```

If the target child is nested, derive ancestor group names from `target_layer.longName.split("\\")` and set those ancestors visible too.

### Debug Renderer State

When an export changes symbology, log both public renderer state and CIM renderer state before and after every mutation:

```python
def log_renderer_state(label: str, layer) -> None:
    print(f"--- {label} ---")
    print("query:", getattr(layer, "definitionQuery", ""))

    try:
        r = layer.symbology.renderer
        print("public:", r.type, r.classificationField, r.classificationMethod, r.breakCount)
        for b in r.classBreaks:
            print("  break:", b.upperBound, b.label)
    except Exception as exc:
        print("public renderer unavailable:", exc)

    try:
        r = layer.getDefinition("V3").renderer
        print("CIM:", getattr(r, "field", None), getattr(r, "heading", None),
              getattr(r, "normalizationType", None))
        for b in r.breaks:
            print("  CIM break:", b.upperBound, b.label)
    except Exception as exc:
        print("CIM renderer unavailable:", exc)
```

The critical checkpoints are:

```text
after definition query
before renderer field change
after renderer field setDefinition
after class-break update
after percent-label update
before export
after export
after restore
```

### Export From `CURRENT` vs. Project Copy

`saveACopy()` is attractive because it protects the editor state, but joined or in-memory layer state may not resolve the same way in the copied project. If a copied project cannot see fields that are visible in the editor, export from `CURRENT` and restore layer CIM in `finally`.

Use a project copy when all data sources and joins are stable on disk. Use `CURRENT` when the editor state is the authoritative state.

---

## Gotchas

`CURRENT`:
- In-app only.
- Stand-alone scripts need a real `.aprx` path.

Project locks:
- Only first reference to an `.aprx` is directly writable.
- Check `aprx.isReadOnly` before `save()`.

Views:
- `activeMap`, `activeView`, `openView`, `closeViews` are application-view operations.
- Stand-alone export does not need a view to be open.

`Map` vs `MapFrame`:
- `Map` = layers.
- `MapFrame` = layout viewport for a map.
- One `Map` can be displayed in multiple frames.

Exports:
- `lyt.export(pdf)` = full layout page.
- `mf.export(png)` = frame contents only.
- `ms.export(pdf)` = map series pages.
- `exportToPDF` remains for legacy scripts; new scripts should use export objects.

Layer support:

```python
if lyr.supports("DEFINITIONQUERY"):
    lyr.definitionQuery = "STATUS = 'ACTIVE'"
```

Symbology:
- Mutate `sym = lyr.symbology`, then assign `lyr.symbology = sym`.
- Template symbology must match layer/raster type and feature geometry.
- Unsupported renderer/colorizer details require CIM.

Paths:

```python
from pathlib import Path
out = Path(aprx.homeFolder) / "exports" / "map.pdf"
```

Coordinate systems:
- Set `arcpy.env.outputCoordinateSystem` for stand-alone index/generalization tools.
- Map series index layers should align with the map/map frame coordinate system.

Selections:
- Clear temporary selections after zoom/export.
- `showSelectionSymbology` controls export display, not selection state.

Map series:
- `lyt.mapSeries` can be `None`.
- Check `ms.enabled`.
- `currentPageNumber` may be string if page numbering field is string.

Naming:
- Use unique map/layout/layer/element names.
- Wildcards return lists; duplicate names make `[0]` fragile.

CIM:
- Use `"V3"` for Pro 3.x.
- Inspect nested objects before editing.
- Prefer public `arcpy.mp` properties when available.

PDF size:
- `pdf.setLayersAndAttributes("NONE")` shrinks outputs.
- `pdf.outputAsImage = True` rasterizes vector content and disables some vector-specific behavior.

Syntax check in this workspace:

```powershell
python -c "import ast, pathlib; ast.parse(pathlib.Path('script.py').read_text(encoding='utf-8')); print('syntax ok')"
```

---

## Cheat Sheet

| Task | API | Returns |
|---|---|---|
| Current project | `arcpy.mp.ArcGISProject("CURRENT")` | `ArcGISProject` |
| Project path | `arcpy.mp.ArcGISProject(path)` | `ArcGISProject` |
| Maps/layouts | `aprx.listMaps()`, `aprx.listLayouts()` | lists |
| Create map/layout | `aprx.createMap()`, `aprx.createLayout()` | `Map`, `Layout` |
| Import MXD | `aprx.importDocument(path)` | Object |
| Broken sources | `aprx.listBrokenDataSources()` | `list` |
| Save | `aprx.save()`, `aprx.saveACopy(path)` | `None` |
| Layers | `m.listLayers(wildcard)` | `list[Layer]` |
| Add data | `m.addDataFromPath(path)` | `Layer` |
| Add `.lyrx` | `m.addLayer(LayerFile, "TOP")` | `list[Layer]` |
| Move layer | `m.moveLayer(ref, lyr, "BEFORE")` | `None` |
| Remove layer | `m.removeLayer(lyr)` | `None` |
| Group | `m.createGroupLayer(name)` | `Layer` |
| Map frame | `lyt.createMapFrame(geometry, map, name)` | `MapFrame` |
| Elements | `lyt.listElements(type, wildcard)` | `list[Element]` |
| Export format | `arcpy.mp.CreateExportFormat("PDF", path)` | format object |
| Export layout | `lyt.export(fmt)` | `None` |
| Export frame | `mf.export(fmt)` | `None` |
| Layer extent | `mf.getLayerExtent(lyr, sel, sym)` | `Extent` |
| Zoom all | `mf.zoomToAllLayers(sel)` | `None` |
| Renderer | `sym.updateRenderer(name)` | `None` |
| Colorizer | `sym.updateColorizer(name)` | `None` |
| Apply symbology | `arcpy.management.ApplySymbologyFromLayer(...)` | Result |
| Supports | `lyr.supports("PROP")` | `bool` |
| Definition query | `lyr.definitionQuery = sql` | `None` |
| Labels | `lyr.showLabels`, `lyr.listLabelClasses()` | bool/list |
| Page query | `lyr.setPageQuery(field, match)` | `None` |
| Source update | `updateConnectionProperties(old, new)` | `None` |
| Create series | `lyt.createSpatialMapSeries(mf, index, name, sort)` | `MapSeries` |
| Export series | `ms.export(fmt, opts)` | `None` |
| Page by name | `ms.getPageNumberFromName(name)` | `int` |
| Grid index | `arcpy.cartography.GridIndexFeatures(...)` | feature class |
| Strip index | `arcpy.cartography.StripMapIndexFeatures(...)` | feature class |
| Simplify | `SimplifyLine`, `SimplifyPolygon` | feature class |
| Smooth | `SmoothLine`, `SmoothPolygon` | feature class |
| Aggregate | `AggregatePolygons` | feature class |
| Partitions | `CreateCartographicPartitions` | feature class |
