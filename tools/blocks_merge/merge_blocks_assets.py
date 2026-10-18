# merge_blocks_assets.py [--dry-run] [<blocks mod dir> [<aero mod dir>]] - copy the former companion mod "Aerodynamics Mod
# Blocks"' Assets into this mod's Assets (2026-10-17, the user: fold the Blocks mod back into the Aerodynamics Mod).
# Every file keeps its Assets-relative path and its .meta byte for byte: the asset ids (uuid5 of that path for the
# generated ones) and the definition GUIDs inside must not change, or ships in existing worlds lose their blocks and
# the scripts (AeroBlockGuids, the generated *.g.cs: injection by prefab GUID) stop finding them.
# Never deletes and never overwrites: a file the target already has is kept (the known overlaps below are the same
# definitions); one that differs is listed. Safe to run again (copies only what is missing).
import os, shutil, sys

HERE = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
BLOCKS = r"C:\Users\slob\Documents\SpaceEngineers2\Mods\Aerodynamics Mod Blocks"
# both mods had these: the Aero mod's copy wins (the same CharacterPhysics override - MaxVelocity 1000 - with its own
# asset id; Assets.meta / System.meta are the folders' own metas)
KEEP_TARGET = {"assets.meta", "system.meta", "system/override of characterphysics.partialdef",
               "system/override of characterphysics.partialdef.meta"}

def same_file(a, b):
    """Byte for byte, line ends aside: a git checkout (core.autocrlf) gives the committed text files CRLF."""
    with open(a, "rb") as fa, open(b, "rb") as fb: x, y = fa.read(), fb.read()
    return x == y or x.replace(b"\r\n", b"\n") == y.replace(b"\r\n", b"\n")

def main(argv):
    dry = "--dry-run" in argv
    args = [a for a in argv if a != "--dry-run"]
    src = args[0] if args else BLOCKS
    dst = args[1] if len(args) > 1 else HERE
    copied = kept = same = 0; differ, size = [], 0
    pairs = [("Assets.meta", os.path.join(src, "Assets.meta"))]
    for rt, _, files in os.walk(os.path.join(src, "Assets")):
        for f in files:
            p = os.path.join(rt, f)
            pairs.append((os.path.relpath(p, src).replace("\\", "/"), p))
    for rel, p in sorted(pairs):
        key = rel[len("Assets/"):].lower() if rel.startswith("Assets/") else rel.lower()
        out = os.path.join(dst, rel)
        if os.path.exists(out):
            if same_file(p, out): same += 1
            else:
                kept += 1
                if key not in KEEP_TARGET: differ.append(rel)
            continue
        size += os.path.getsize(p); copied += 1
        if not dry:
            os.makedirs(os.path.dirname(out), exist_ok=True)
            shutil.copy2(p, out)
    print(f"{'would copy' if dry else 'copied'} {copied} file(s), {size / 1048576:.1f} MB; {same} already there (identical), "
          f"{kept} kept as the target has them")
    for rel in differ: print("  DIFFERS (target kept, check by hand): " + rel)
    return 1 if differ else 0

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
