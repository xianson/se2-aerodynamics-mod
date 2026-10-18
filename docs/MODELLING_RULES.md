# Modelling guide — the big forms

This guide covers how we model a block's large forms (the blockout stage), so each block looks native to Space Engineers 2. It's drawn from the user's design direction and from measurements of Keen's vanilla block meshes. Detail (decals, greebles, small parts) comes as a later pass.

## A. How we work

1. **The drawing is the design.**
   - When there is concept art, the model reproduces it: its pieces, their shapes and their proportions.
   - Fitting the block (grid, flush faces, cell size) is a separate, explicit step, agreed with the user.
2. **All three orthographic views.**
   - Each piece is traced from the side, top and front views using the image tools.
   - Each piece is built as the intersection of its traced outlines, so it matches the drawing from every side.
3. **Data, then geometry.**
   - First comes a piece inventory. Each piece gets a name, an outline in each view, a position and size in block metres, and its colour region.
   - Once the user approves it, the build follows it exactly.
4. **Open questions are asked.** Where the views differ, or the drawing and the cell size pull apart, the options go to the user, and their choice is recorded.
5. **Silhouette first.** The first build is a low-poly blockout of flat faces and few sections. Detail follows once the silhouette is approved.
6. **Checked before review.** Each review comes with:
   - orthographic side, top and front renders beside the drawing;
   - close-ups of every joint;
   - the checks in section F, all passing.
7. **Focused changes.** Each round changes exactly what the user asked for, and everything else stays as approved.

## B. Fitting the block

**What grid-aligned means.** A model's important lines land on the same lattice the player builds with: the cell boundaries, and the 0.5 m lines within them (the small-block size). Those lines are where sections start and end, where steps and seams fall, and where the outer faces sit. The model is built in the same units as everything around it.

**Why.** A ship is many blocks placed side by side, so each block is designed to look right next to its neighbours:
1. **Clean joins.** Where another block attaches, the faces meet flat at the cell boundary, and two blocks read as one surface.
2. **Lines that carry across blocks.** Section breaks, steps and seams on the grid line up with the edges of the armour and parts beside them. A ship of fifty blocks reads as one designed machine.
3. **Scale.** The grid is SE's ruler. Features that follow it show a block's size at a glance, and let players plan around it: a wing root, a conveyor run, a pair of engines.
4. **Native look.** Keen's blocks are built this way, so ours sit naturally beside vanilla parts.

**Aligned is about lines and envelopes.** A section can be stepped, chamfered and panelled. When its envelope and its main breaks sit on the grid, it joins cleanly and carries the lines across (rule 9b).

8. **On the grid.**
   - Masses and flush faces begin and end on the 0.5 m grid (the nacelle's grid lines are at ±0.25, ±0.75 … from the block centre).
   - Features (cuts, insets, small parts) sit on the 0.25 m grid.
9. **Flush mount faces.**
   - Where the block meets its neighbours, the surface is flat and flush with the bounding box. It's a composite region (a plate with its rails and clips) whose edges follow the grid.
   - Mount faces are chosen deliberately, e.g. a full-box collar, a top spine, or a flush back section.
   - Round parts, pipes and spinners sit inside the box, meeting it at most as a rim.
9b. **Grid-aligned, with shaped surfaces.**
    - A section aligns to the grid through its envelope: its ends and its outermost faces sit on grid lines.
    - Within that envelope the surface is shaped: stepped edges, raised plates, recessed panels, angled facets, tapers and chamfers.
    - The shaping stays in a shallow band below the grid plane: insets of about 2–6 cm on mount faces (as Keen's do), and up to about 0.25 m elsewhere.
    - The section still reads as aligned. Its flush area is the combined top surfaces of several pieces, and covers most of a mount face (about 75% on vanilla blocks), with the shaping in between.
10. **Proportions by agreement.** When a drawing's proportions differ from the cell's, the user picks the fit:
    - a shorter body (fewer cells);
    - a measured stretch;
    - adjusting the feature that sets the size.

## C. The style of the forms

11. **Simple solids with chamfers.**
    - Each large form is a simple solid with flat faces and chamfered edges.
    - Outlines can be shaped (tapers, wedges, stepped sections, frustums), and every surface stays flat.
    - Curves are circles: cylinders, cones and rings around an axis.
12. **One continuous surface per mass.** Each mass reads as a single solid; panel lines and bolts arrive later as decals.
13. **Pipes as drawn.** Pipes appear where the drawing shows them, at its thickness and routing.
14. **Clear hierarchy.**
    - A few big masses, each with a distinct size.
    - Confident steps between masses, at least 10% of the dimension.
    - The drawing's rhythm of light and dark sections.
15. **Chamfers.**
    - About 8% of a part's size on silhouette edges.
    - About 1–1.5% on the remaining edges.
    - 45°, stepped as 22.5° + 22.5° on large edges.
    - Block ends are uniform frustums, inset evenly on every side.

## D. Joints — every contour has room to breathe

16. **Seated overlaps.** Where pieces meet, one sinks clearly (10 cm or more) into the other, so each surface stays on its own plane.
17. **Smaller within larger.** A smaller piece sits well inside the face it meets, at least 10 cm in from that face's edges.
18. **Margins.** Features sit at least 0.25 m in from the edges of their face, clear of chamfer lines.
19. **Clean seats.** Flat parts meet curved surfaces with a small gap or a flat flange, so every seam is crisp.
20. **Supported footprints.** Each piece sits within the footprint of the piece that carries it.

## C2. The SE aesthetic within general hard-surface practice

The installed `hard-surface` and `blender-modeler` skills, and common hard-surface guides, supply the craft. Space Engineers sets the style:
- **Boxy and grid-aligned.** Forms are box-like and follow the grid, and the block box shapes the design. Generic hard-surface flourishes sit inside this frame.
- **Three tiers.** Primary forms (the masses) set the silhouette. Secondary forms (panels, cut-ins, rails) connect them. Tertiary forms (bolts, small chamfers, decals) add finish.
- **Big, medium, small.** One dominant mass, a medium one and a small one, each clearly different in size.
- **70/30 for detail.** About 70% of the surface stays calm; about 30% carries the detail, gathered at seams and focal points.
- **SE-scale bevels.** Bevel tiers follow Keen's measured scale: the main chamfer is about 8% of a part's size; the micro chamfer is about 1–1.5%. These are larger than the generic AAA 5–20 mm tiers, and give SE its chunky edges.
- **Live booleans.** During the build, cutters live in `COL_Booleans` as `SM_Cutter_<feature>`, using the exact solver, applied at export prep.
- **Shading by stage.**
  - The blockout is flat shaded.
  - The final pass uses auto-smooth at 30–45° plus a Weighted Normal modifier (keep sharp).
- **Instanced repeats.** Repeated details (bolts, vents, clamps) are instances, in their own collection.
- **Matched to the concept.** The blockout is matched to the concept image at about 99%. Check it by toggling between the concept camera and orthographic views, and check silhouettes every 45°.
- **Even progress.** Detail rises evenly across the whole model, one tier at a time.

## E. Shading and materials

21. **Shading by stage.**
    - Blockouts are flat shaded, so every face reads crisp.
    - Final models use auto-smooth plus a Weighted Normal modifier on clean, chamfered geometry.
22. **Library materials** (PaintedMetalColorable, PaintedMetalDarker, MetalDull, Copper), assigned from the drawing's colour regions.

## F. Checks before each review

- **Separate planes:** every pair of overlapping pieces keeps its faces on distinct planes. `zfight_check` is in `turboprop_v6.py`.
  - Diagonal faces can give false hits; confirm each hit by its coordinates.
- **Grid:** mass ends and flush-face edges lie on 0.5 m lines.
- **Flush map:** each box face's flush region is grid-aligned and is the intended mount face. `flushmap.py` maps it.
- **Overlay:** the model's orthographic silhouettes match the traced drawing outlines in all three views.
- **Joints:** close-up renders of every place two pieces meet.

## G. The contour pass: a fast path

This is the first pass on a block: the big contour, built from primary shapes only. It was proven on the 2.5 m turboprop (`tools/meshgen/turboprop_250_blockout.py`). Each step below is a property of the SE style.

1. **Start with the cells.**
   - Draw the block's cell lattice at the block size (2.5 m) before modelling, and set Blender's grid to 2.5 m major lines with 0.5 m subdivisions (`grid_lattice`).
   - Place a few armor cubes where neighbours attach (top, back, sides). Everything is judged against them.
2. **One section per cell.**
   - Each cell holds one section: here nose, middle and tail.
   - Every seam between sections lands on a cell line, so it continues the armor seams beside it.
3. **One section language.**
   - The whole body is lofted from a single 45° octagon (half width, top, bottom, top corner, bottom corner) with one corner size throughout.
   - Grooves and cores are an even inset of that same section (`ins`), so their corner faces run parallel to the shell's.
   - Matching edges stay parallel from station to station, so every face is flat.
4. **Clear hierarchy.**
   - One dominant mass: the full-box middle, flush on all four sides.
   - One long tapering form: the nose, a single flat taper running into the prop layer, with the chin raking up steeply below it.
   - One tail: the core theme repeated, with the top flush as the mount spine.
5. **A repeated theme.** Light shells over a dark internal core: shell, core, shell, core, shell. The core shows as 0.25 m-deep rings on the cell lines.
6. **Flush joints meet at cell lines.**
   - Where two flush faces meet, the pieces butt at the cell line, each with a small end chamfer, and the seam reads as a crisp V.
   - Seated overlaps (rule 16) are for pieces whose faces sit on different planes, such as cores inside shells.
7. **Inset openings stay parallel.** Mouths, windows and recesses are constant-width insets of the face they sit in (`inset_poly`), so every edge runs parallel to the outline around it.
8. **Round parts are true curves.** The spinner is a tangent ogive (`ogive_y`) with a short barrel. Blade roots sit in stout cuffs seated in the spinner.
9. **Moving parts hang from a pivot.**
   - The prop parts are parented to an empty on the axis at the spinner face, ready for export as a subpart.
   - `parent_keep` updates the view layer before setting the inverse, so the parts stay where they are.
10. **Every pass ends with checks and a look.**
    - The scripted checks: `check_coplanar`, `check_manifold` and `check_grid`.
    - Then close-ups of every joint from four or more angles, including below and behind.
    - The scripts confirm planes and topology; the eye confirms the joints read well.
11. **The contour is kept.** Once approved, the contour is frozen into `COL_Contour_Ref` (modifiers applied, unselectable) and saved as a `_contour_vN.blend` copy. The sub-piece pass then refers back to it.

## Tools

`tools/meshgen/blockout_lib.py` holds the contour-pass helpers: sections, lofts, ogive, constant-width inset, side cutters, the cell lattice, `parent_keep`, and the three checks.

These live in the session scratchpad for now and move to `tools/meshgen` as they settle:
- **Colour segmentation and tracing** of flat orthographic drawings: `drawing_extract.py` (side regions, top and front outlines, spinner profile).
- **Three-view build:** `drawing_build.py` (each piece extruded, then intersected with the traced top and front outlines).
- **Vanilla studies:** `shape_study.py` (angles and chamfers), `prims.py` (primitive decomposition), `flushmap.py` (flush faces against the 0.5 m grid).
