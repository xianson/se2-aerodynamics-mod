using System.Numerics;
using System.Text;

namespace Aero.Core.Blueprint;

/// <summary>
/// Reads the BoundingBox section from SE2 .mwm model files.
///
/// MWM file format (header):
///   [1-byte name length]["Debug"][4-byte section size]
///   [1-byte name length]["Version:XXXXXXXX"][4-byte section size]
///   ... repeated for each section ...
///   Section names include: MaterialAssignment, Sections, Bones,
///   HavokCollisionGeometry, BoundingBox, BoundingSphere, ModelInfo, etc.
///
/// Each entry in the TOC is: [1-byte name length][name string][4-byte LE offset]
/// The offset points to where the section data starts in the file.
/// At that offset, the section name is repeated, followed by the raw data.
///
/// BoundingBox section data: 6 floats (LE) = min XYZ, max XYZ.
/// </summary>
public static class MwmBoundingBoxReader
{
    /// <summary>
    /// Reads the BoundingBox (min, max) from a .mwm file.
    /// Returns null if the file doesn't exist or has no BoundingBox section.
    /// </summary>
    public static (Vector3 Min, Vector3 Max)? ReadBoundingBox(string mwmPath)
    {
        if (!File.Exists(mwmPath))
            return null;

        try
        {
            using var fs = File.OpenRead(mwmPath);
            using var br = new BinaryReader(fs);

            // Parse the TOC to find the BoundingBox offset.
            int? bbOffset = null;

            while (fs.Position < fs.Length - 5)
            {
                int nameLen = br.ReadByte();
                if (nameLen == 0 || nameLen > 128 || fs.Position + nameLen + 4 > fs.Length)
                    break;

                byte[] nameBytes = br.ReadBytes(nameLen);
                string name = Encoding.ASCII.GetString(nameBytes);
                int offset = br.ReadInt32();

                if (name == "BoundingBox")
                {
                    bbOffset = offset;
                    break;
                }

                // After the first material assignment entry, we might hit actual data.
                // The TOC ends when we encounter something that doesn't look like a TOC entry.
                // Heuristic: if the offset is unreasonably large or negative, stop.
                if (offset < 0 || offset > fs.Length)
                    break;
            }

            if (bbOffset == null)
                return null;

            // Seek to the BoundingBox section.
            fs.Seek(bbOffset.Value, SeekOrigin.Begin);

            // The section starts with the name repeated: [1-byte len]["BoundingBox"]
            int repNameLen = br.ReadByte();
            fs.Seek(repNameLen, SeekOrigin.Current); // skip the repeated name

            // Read 6 floats: minX, minY, minZ, maxX, maxY, maxZ
            float minX = br.ReadSingle();
            float minY = br.ReadSingle();
            float minZ = br.ReadSingle();
            float maxX = br.ReadSingle();
            float maxY = br.ReadSingle();
            float maxZ = br.ReadSingle();

            return (new Vector3(minX, minY, minZ), new Vector3(maxX, maxY, maxZ));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the block's dimensions in grid cells (0.25m per cell).
    /// Rounds up to nearest cell boundary.
    /// </summary>
    public static (int X, int Y, int Z)? ReadCellDimensions(string mwmPath)
    {
        var bb = ReadBoundingBox(mwmPath);
        if (bb == null) return null;

        var (min, max) = bb.Value;
        var size = max - min;

        // Round to nearest cell count, minimum 1 per axis.
        int cx = System.Math.Max(1, (int)MathF.Round(size.X / 0.25f));
        int cy = System.Math.Max(1, (int)MathF.Round(size.Y / 0.25f));
        int cz = System.Math.Max(1, (int)MathF.Round(size.Z / 0.25f));

        return (cx, cy, cz);
    }
}
