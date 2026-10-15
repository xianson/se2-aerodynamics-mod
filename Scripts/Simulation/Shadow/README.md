# Shadow - which faces the air reaches (engine-free)

| File | What |
|---|---|
| `PrecomputedShadowMap.cs` | Face visibility for the face loop before a grid has its force table |
| `IShadowMap.cs` | Its interface |

The table bakes its own occlusion per direction (`DampedShadowedDragModel.BakeOcclusion`), so a grid with a table does
not use this.

## Entry points
`DampedShadowedDragModel` (its `ShadowMap`); the background build prepares one (`AeroGridComponent._builtShadow` / `_spareShadow`).

## Threading
Belongs to one grid's model or one build, like `Drag/`.
