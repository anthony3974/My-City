using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Every material the city uses. CityStreamer.materials is indexed by this.</summary>
public enum CityMat
{
    Asphalt, LanePaint, WhitePaint, Sidewalk, Storefront,
    BrickRed, BrickTan, Stone, Plaster, PlasterGrey, Office,
    GlassBlue, GlassTeal, GlassDark, GlassSilver,
    Roof, Metal, Grass, Trunk, Leaves,
    Count
}

[Serializable]
public class CitySettings
{
    public int seed = 12345;
    public int blocksPerTile = 8;
    public float blockSize = 40f;
    public float roadWidth = 12f;
    public float sidewalkWidth = 2.5f;
    public float minHeight = 8f;
    public float maxHeight = 150f;
    public int maxLotsPerSide = 3;
    public float lotGap = 1.5f;
    public float skyscraperChance = 0.04f;
    public float parkChance = 0.08f;
    [Tooltip("How fast the city changes between downtown and low-rise, per block. Smaller = bigger downtowns.")]
    public float downtownScale = 0.08f;

    public float Pitch => blockSize + roadWidth;
    public float TileSize => blocksPerTile * Pitch;
}

/// <summary>How a facade texture is laid out. The texture baker draws to these numbers, so they must match.</summary>
public struct FacadeSpec
{
    public float bayWidth, rowHeight;   // metres covered by one window bay / one floor
    public int texBays, texRows;        // bays and floors in one copy of the texture
    public float TexWidth => bayWidth * texBays;
    public float TexHeight => rowHeight * texRows;
}

public static class CityLook
{
    public const float FloorHeight = 3.5f;
    public const float StorefrontHeight = 4.5f;

    public static bool IsFacade(CityMat m) => m >= CityMat.Storefront && m <= CityMat.GlassSilver;

    public static FacadeSpec Facade(CityMat m) => m == CityMat.Storefront
        ? new FacadeSpec { bayWidth = 6f, rowHeight = StorefrontHeight, texBays = 4, texRows = 2 }
        : new FacadeSpec { bayWidth = 3f, rowHeight = FloorHeight, texBays = 6, texRows = 6 };

    /// <summary>Metres covered by one copy of a non-facade texture.</summary>
    public static float MetersPerRepeat(CityMat m) => m switch
    {
        CityMat.Asphalt => 8f,
        CityMat.Sidewalk => 6f,
        CityMat.Roof => 8f,
        CityMat.Metal => 2f,
        CityMat.Grass => 4f,
        CityMat.Trunk => 1f,
        CityMat.Leaves => 2f,
        _ => 4f,
    };
}

/// <summary>Vertex data for one mesh with one submesh per material. Plain lists, so it can be filled off the main thread.</summary>
public class MeshData
{
    public readonly List<Vector3> verts = new();
    public readonly List<Vector3> normals = new();
    public readonly List<Vector4> tangents = new();
    public readonly List<Vector2> uvs = new();
    public readonly List<int>[] tris = new List<int>[(int)CityMat.Count];

    /// <summary>Corners in order bottom-left, top-left, top-right, bottom-right as seen from the front.</summary>
    public void Quad(CityMat m, Vector3 bl, Vector3 tl, Vector3 tr, Vector3 br, Vector3 n, Vector4 tangent,
        Vector2 uvMin, Vector2 uvMax)
    {
        int i = verts.Count;
        verts.Add(bl); verts.Add(tl); verts.Add(tr); verts.Add(br);
        for (int k = 0; k < 4; k++) { normals.Add(n); tangents.Add(tangent); }
        uvs.Add(uvMin);
        uvs.Add(new Vector2(uvMin.x, uvMax.y));
        uvs.Add(uvMax);
        uvs.Add(new Vector2(uvMax.x, uvMin.y));
        var t = tris[(int)m] ??= new List<int>();
        t.Add(i); t.Add(i + 1); t.Add(i + 2);
        t.Add(i); t.Add(i + 2); t.Add(i + 3);
    }
}

public class BlockData
{
    public Vector3 localPos;   // block centre, relative to the tile's south-west corner
    public readonly MeshData mesh = new();
    public readonly List<(Vector3 center, Vector3 size)> colliders = new();
}

public class TileData
{
    public Vector2Int cell;
    public readonly MeshData ground = new();   // asphalt, lane dashes, crosswalks
    public readonly List<BlockData> blocks = new();
}

/// <summary>
/// Builds one square tile of city from its grid cell and the seed. The same cell always gives the same city,
/// and tiles line up with their neighbours: each tile has roads on its west and south edges only.
/// Pure C# with no Unity object calls, so CityStreamer runs it on a worker thread.
/// </summary>
public static class CityGenerator
{
    static readonly Vector3[] SideNormals = { Vector3.forward, Vector3.back, Vector3.right, Vector3.left };
    static readonly CityMat[] Glasses = { CityMat.GlassBlue, CityMat.GlassTeal, CityMat.GlassDark, CityMat.GlassSilver };

    public static TileData GenerateTile(CitySettings s, Vector2Int cell)
    {
        var tile = new TileData { cell = cell };
        BuildGround(s, tile.ground);

        float pitch = s.Pitch;
        for (int i = 0; i < s.blocksPerTile; i++)
        {
            for (int j = 0; j < s.blocksPerTile; j++)
            {
                int gx = cell.x * s.blocksPerTile + i;
                int gz = cell.y * s.blocksPerTile + j;
                var block = new BlockData
                {
                    localPos = new Vector3(i * pitch + s.roadWidth + s.blockSize / 2f, 0f,
                                           j * pitch + s.roadWidth + s.blockSize / 2f),
                };
                new BlockBuilder(s, block, gx, gz).Build();
                tile.blocks.Add(block);
            }
        }
        return tile;
    }

    // ------------------------------------------------------------------ Ground and road markings

    static void BuildGround(CitySettings s, MeshData md)
    {
        float T = s.TileSize, pitch = s.Pitch, rw = s.roadWidth;

        // Asphalt as a grid of 16 m squares rather than one big quad, so fog and lighting stay even across it.
        int cells = Mathf.Max(1, Mathf.RoundToInt(T / 16f));
        float step = T / cells, m = CityLook.MetersPerRepeat(CityMat.Asphalt);
        var up = Vector3.up;
        var tan = new Vector4(1f, 0f, 0f, -1f);
        for (int x = 0; x < cells; x++)
            for (int z = 0; z < cells; z++)
            {
                float x0 = x * step, x1 = x0 + step, z0 = z * step, z1 = z0 + step;
                md.Quad(CityMat.Asphalt, new Vector3(x0, 0f, z0), new Vector3(x0, 0f, z1), new Vector3(x1, 0f, z1),
                    new Vector3(x1, 0f, z0), up, tan, new Vector2(x0 / m, z0 / m), new Vector2(x1 / m, z1 / m));
            }

        // Each road segment runs between two intersections. Roads along Z sit at x = i*pitch.
        for (int i = 0; i < s.blocksPerTile; i++)
        {
            for (int j = 0; j < s.blocksPerTile; j++)
            {
                float across = i * pitch;                 // road's low edge
                float start = j * pitch + rw, end = (j + 1) * pitch;
                RoadSegment(md, s, across, start, end, alongZ: true);
                RoadSegment(md, s, across, start, end, alongZ: false);
            }
        }
    }

    static void RoadSegment(MeshData md, CitySettings s, float across, float start, float end, bool alongZ)
    {
        float rw = s.roadWidth;
        const float y1 = 0.012f, y2 = 0.011f;

        // Yellow centre dashes, kept clear of the crosswalks.
        float center = across + rw / 2f;
        for (float a = start + 6f; a + 3f <= end - 5f; a += 7f)
            FlatRect(md, CityMat.LanePaint, alongZ, center - 0.15f, center + 0.15f, a, a + 3f, y1);

        // Zebra crosswalks at both ends of the segment.
        for (float c = across + 1f; c + 0.5f <= across + rw - 0.75f; c += 1f)
        {
            FlatRect(md, CityMat.WhitePaint, alongZ, c, c + 0.5f, start + 0.6f, start + 3.6f, y2);
            FlatRect(md, CityMat.WhitePaint, alongZ, c, c + 0.5f, end - 3.6f, end - 0.6f, y2);
        }
    }

    /// <summary>Flat rectangle. "across" is the axis across the road, "along" the axis along it.</summary>
    static void FlatRect(MeshData md, CityMat mat, bool alongZ, float a0, float a1, float l0, float l1, float y)
    {
        float x0 = alongZ ? a0 : l0, x1 = alongZ ? a1 : l1;
        float z0 = alongZ ? l0 : a0, z1 = alongZ ? l1 : a1;
        md.Quad(mat, new Vector3(x0, y, z0), new Vector3(x0, y, z1), new Vector3(x1, y, z1), new Vector3(x1, y, z0),
            Vector3.up, new Vector4(1f, 0f, 0f, -1f), Vector2.zero, Vector2.one);
    }

    // ------------------------------------------------------------------ Downtown map

    /// <summary>0 = low-rise, 1 = the heart of a downtown. Smooth noise over the whole infinite grid.</summary>
    public static float Downtown(CitySettings s, int gx, int gz)
    {
        float f = s.downtownScale;
        float n = 0.65f * ValueNoise(gx * f + 31.7f, gz * f - 12.3f, s.seed)
                + 0.35f * ValueNoise(gx * f * 2.3f - 5.1f, gz * f * 2.3f + 44.2f, s.seed + 1);
        float d = Mathf.Clamp01((n - 0.42f) / 0.36f);

        // A guaranteed downtown around where you start.
        float home = 1f - Vector2.Distance(new Vector2(gx, gz), new Vector2(3.5f, 3.5f)) / 7f;
        d = Mathf.Max(d, home);
        return d * d;
    }

    static float ValueNoise(float x, float y, int seed)
    {
        int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
        float fx = x - x0, fy = y - y0;
        fx = fx * fx * (3f - 2f * fx);
        fy = fy * fy * (3f - 2f * fy);
        float a = Hash01(x0, y0, seed), b = Hash01(x0 + 1, y0, seed);
        float c = Hash01(x0, y0 + 1, seed), d = Hash01(x0 + 1, y0 + 1, seed);
        return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
    }

    public static int Hash(int seed, int x, int y)
    {
        unchecked
        {
            uint h = (uint)seed * 0x9E3779B1u ^ (uint)x * 0x85EBCA77u ^ (uint)y * 0xC2B2AE3Du;
            h ^= h >> 16; h *= 0x7FEB352Du; h ^= h >> 15; h *= 0x846CA68Bu; h ^= h >> 16;
            return (int)(h & 0x7FFFFFFF);
        }
    }

    static float Hash01(int x, int y, int seed) => Hash(seed, x, y) / (float)int.MaxValue;

    // ------------------------------------------------------------------ One block

    class BlockBuilder
    {
        readonly CitySettings s;
        readonly BlockData block;
        readonly MeshData md;
        readonly System.Random rng;
        readonly int gx, gz;

        public BlockBuilder(CitySettings s, BlockData block, int gx, int gz)
        {
            this.s = s; this.block = block; this.gx = gx; this.gz = gz;
            md = block.mesh;
            rng = new System.Random(Hash(s.seed, gx, gz));
        }

        float Range(float a, float b) => a + (float)rng.NextDouble() * (b - a);
        bool Chance(float p) => rng.NextDouble() < p;

        public void Build()
        {
            // Raised sidewalk slab covering the whole block. Its top is at y = 0.2.
            SolidBox(new Vector3(0f, 0.1f, 0f), new Vector3(s.blockSize, 0.2f, s.blockSize), CityMat.Sidewalk, CityMat.Sidewalk);

            float downtown = Downtown(s, gx, gz);
            if (Chance(s.parkChance * Mathf.Lerp(1.7f, 0.2f, downtown)))
                BuildPark();
            else
                BuildBuildings(downtown);
        }

        void BuildBuildings(float downtown)
        {
            float inner = s.blockSize - s.sidewalkWidth * 2f;
            int lotsX = rng.Next(1, s.maxLotsPerSide + 1);
            int lotsZ = rng.Next(1, s.maxLotsPerSide + 1);
            float lotW = inner / lotsX, lotD = inner / lotsZ;

            for (int x = 0; x < lotsX; x++)
            {
                for (int z = 0; z < lotsZ; z++)
                {
                    var lot = new Vector3(-inner / 2f + lotW * (x + 0.5f), 0.2f, -inner / 2f + lotD * (z + 0.5f));
                    float w = Mathf.Max(4f, (lotW - s.lotGap) * Range(0.8f, 1f));
                    float d = Mathf.Max(4f, (lotD - s.lotGap) * Range(0.8f, 1f));

                    float height = Mathf.Lerp(s.minHeight, s.maxHeight, downtown) * Range(0.5f, 1.15f);
                    if (Chance(s.skyscraperChance))
                        height = Mathf.Max(height, s.maxHeight * Range(0.6f, 1.1f));
                    BuildBuilding(lot, w, d, Mathf.Max(s.minHeight, height), downtown);
                }
            }
        }

        CityMat PickStyle(bool tall, int floors, float downtown)
        {
            if (tall)
                return Chance(0.6f) ? Glasses[rng.Next(Glasses.Length)] : (Chance(0.6f) ? CityMat.Office : CityMat.Stone);

            float r = (float)rng.NextDouble();
            if (floors >= 5 && r < 0.15f + 0.3f * downtown) return CityMat.Office;
            r = (float)rng.NextDouble();
            if (r < 0.28f) return CityMat.BrickRed;
            if (r < 0.45f) return CityMat.BrickTan;
            if (r < 0.65f) return CityMat.Stone;
            if (r < 0.87f) return CityMat.Plaster;
            return CityMat.PlasterGrey;
        }

        void BuildBuilding(Vector3 lot, float width, float depth, float height, float downtown)
        {
            float y0 = lot.y;
            float sh = CityLook.StorefrontHeight, fh = CityLook.FloorHeight;
            int floors = Mathf.Max(1, Mathf.RoundToInt((height - sh) / fh));
            bool tall = sh + floors * fh > s.maxHeight * 0.4f;
            CityMat style = PickStyle(tall, floors, downtown);
            var spec = CityLook.Facade(style);
            var offset = new Vector2(rng.Next(spec.texBays), rng.Next(spec.texRows));

            // Shops along the street on the ground floor.
            var shop = CityLook.Facade(CityMat.Storefront);
            FacadeBox(new Vector3(lot.x, y0 + sh / 2f, lot.z), new Vector3(width + 0.4f, sh, depth + 0.4f),
                CityMat.Storefront, CityMat.Roof, y0, new Vector2(rng.Next(shop.texBays), rng.Next(shop.texRows)));

            float y = y0 + sh, topW = width, topD = depth;
            if (tall)
            {
                // Stacked tiers with setbacks, like classic downtown towers.
                int tiers = rng.Next(1, 4), remaining = floors;
                for (int t = 0; t < tiers && remaining > 0; t++)
                {
                    int tierFloors = t == tiers - 1 ? remaining : Mathf.Max(1, Mathf.RoundToInt(remaining * Range(0.45f, 0.7f)));
                    float tierH = tierFloors * fh;
                    FacadeBox(new Vector3(lot.x, y + tierH / 2f, lot.z), new Vector3(topW, tierH, topD), style, CityMat.Roof,
                        y0 + sh, offset);
                    y += tierH;
                    remaining -= tierFloors;
                    if (remaining > 0)
                    {
                        topW *= Range(0.65f, 0.85f);
                        topD *= Range(0.65f, 0.85f);
                    }
                }
            }
            else
            {
                float bodyH = floors * fh;
                FacadeBox(new Vector3(lot.x, y + bodyH / 2f, lot.z), new Vector3(width, bodyH, depth), style, CityMat.Roof,
                    y0 + sh, offset);
                y += bodyH;
            }

            // Parapet cap, slightly wider than the top.
            Box(new Vector3(lot.x, y + 0.2f, lot.z), new Vector3(topW + 0.4f, 0.4f, topD + 0.4f), CityMat.Sidewalk, CityMat.Roof, collider: false);
            float roofY = y + 0.4f;

            if (tall)
            {
                // Plant room on top, and sometimes an antenna.
                float pw = topW * Range(0.35f, 0.55f), pd = topD * Range(0.35f, 0.55f), ph = Range(3f, 6f);
                Box(new Vector3(lot.x, roofY + ph / 2f, lot.z), new Vector3(pw, ph, pd), CityMat.Metal, CityMat.Roof, collider: false);
                if (Chance(0.4f))
                {
                    float ah = Range(height * 0.08f, height * 0.2f);
                    Box(new Vector3(lot.x, roofY + ph + ah / 2f, lot.z), new Vector3(0.5f, ah, 0.5f), CityMat.Metal, CityMat.Metal, collider: false);
                }
            }
            else if (Mathf.Min(topW, topD) >= 4f)
            {
                int units = rng.Next(0, 4);
                for (int u = 0; u < units; u++)
                {
                    float sz = Range(1f, Mathf.Min(4f, Mathf.Min(topW, topD) * 0.3f));
                    float h = Range(0.8f, 2.5f);
                    float px = Range(-topW / 2f + sz, topW / 2f - sz), pz = Range(-topD / 2f + sz, topD / 2f - sz);
                    Box(new Vector3(lot.x + px, roofY + h / 2f, lot.z + pz), new Vector3(sz, h, sz), CityMat.Metal, CityMat.Metal, collider: false);
                }
            }
        }

        void BuildPark()
        {
            float inner = s.blockSize - s.sidewalkWidth * 2f;
            SolidBox(new Vector3(0f, 0.25f, 0f), new Vector3(inner, 0.1f, inner), CityMat.Sidewalk, CityMat.Grass);

            // Cross-shaped footpaths.
            TopQuad(CityMat.Sidewalk, new Vector3(0f, 0.305f, 0f), inner, 2.5f);
            TopQuad(CityMat.Sidewalk, new Vector3(0f, 0.306f, 0f), 2.5f, inner);

            int trees = rng.Next(8, 18);
            for (int t = 0; t < trees; t++)
            {
                float px = Range(-inner / 2f + 2f, inner / 2f - 2f), pz = Range(-inner / 2f + 2f, inner / 2f - 2f);
                if (Mathf.Abs(px) < 3f || Mathf.Abs(pz) < 3f) continue;   // keep the paths clear
                float trunkH = Range(1.8f, 3.2f), canopy = Range(2.5f, 4.5f);
                Box(new Vector3(px, 0.3f + trunkH / 2f, pz), new Vector3(0.45f, trunkH, 0.45f), CityMat.Trunk, CityMat.Trunk);
                Box(new Vector3(px, 0.3f + trunkH + canopy / 2f, pz), new Vector3(canopy, canopy * Range(0.8f, 1.1f), canopy),
                    CityMat.Leaves, CityMat.Leaves, collider: false, bottom: true);
            }
        }

        // ---------------------------------------------------------- Boxes

        void SolidBox(Vector3 c, Vector3 size, CityMat side, CityMat top) => Box(c, size, side, top);

        /// <summary>Box whose sides use a facade texture: whole window bays across each face, floors counted from baseY.</summary>
        void FacadeBox(Vector3 c, Vector3 size, CityMat side, CityMat top, float baseY, Vector2 offset)
        {
            var f = CityLook.Facade(side);
            Vector3 h = size * 0.5f;
            for (int k = 0; k < 4; k++)
            {
                Vector3 n = SideNormals[k];
                Vector3 r = Vector3.Cross(n, Vector3.up);
                float hw = Mathf.Abs(Vector3.Dot(r, h)), hd = Mathf.Abs(Vector3.Dot(n, h));
                int bays = Mathf.Max(1, Mathf.RoundToInt(hw * 2f / f.bayWidth));
                float u0 = (offset.x + k * 2) / f.texBays;
                float v0 = ((c.y - h.y - baseY) / f.rowHeight + offset.y) / f.texRows;
                float v1 = ((c.y + h.y - baseY) / f.rowHeight + offset.y) / f.texRows;
                SideQuad(side, c, n, r, hw, hd, h.y, new Vector2(u0, v0), new Vector2(u0 + (float)bays / f.texBays, v1));
            }
            TopFace(top, c, h);
            block.colliders.Add((c, size));
        }

        /// <summary>Box with textures mapped in metres, continuous across the block.</summary>
        void Box(Vector3 c, Vector3 size, CityMat side, CityMat top, bool collider = true, bool bottom = false)
        {
            float m = CityLook.MetersPerRepeat(side);
            Vector3 h = size * 0.5f;
            for (int k = 0; k < 4; k++)
            {
                Vector3 n = SideNormals[k];
                Vector3 r = Vector3.Cross(n, Vector3.up);
                float hw = Mathf.Abs(Vector3.Dot(r, h)), hd = Mathf.Abs(Vector3.Dot(n, h));
                float u0 = (Vector3.Dot(c + block.localPos, r) - hw) / m;
                SideQuad(side, c, n, r, hw, hd, h.y,
                    new Vector2(u0, (c.y - h.y) / m), new Vector2(u0 + hw * 2f / m, (c.y + h.y) / m));
            }
            TopFace(top, c, h);
            if (bottom)
            {
                // Seen from below (tree canopies): front is -Y.
                float mb = CityLook.MetersPerRepeat(side);
                Vector3 p = c - Vector3.up * h.y;
                md.Quad(side, p + new Vector3(-h.x, 0, h.z), p + new Vector3(-h.x, 0, -h.z), p + new Vector3(h.x, 0, -h.z),
                    p + new Vector3(h.x, 0, h.z), Vector3.down, new Vector4(1f, 0f, 0f, -1f),
                    Vector2.zero, new Vector2(size.x / mb, size.z / mb));
            }
            if (collider) block.colliders.Add((c, size));
        }

        void SideQuad(CityMat mat, Vector3 c, Vector3 n, Vector3 r, float hw, float hd, float hh, Vector2 uvMin, Vector2 uvMax)
        {
            Vector3 fc = c + n * hd, up = Vector3.up;
            var tangent = new Vector4(r.x, r.y, r.z, Vector3.Dot(Vector3.Cross(n, r), up) > 0f ? 1f : -1f);
            md.Quad(mat, fc - r * hw - up * hh, fc - r * hw + up * hh, fc + r * hw + up * hh, fc + r * hw - up * hh,
                n, tangent, uvMin, uvMax);
        }

        void TopFace(CityMat mat, Vector3 c, Vector3 h) => TopQuad(mat, c + Vector3.up * h.y, h.x * 2f, h.z * 2f);

        void TopQuad(CityMat mat, Vector3 center, float sizeX, float sizeZ)
        {
            float m = CityLook.MetersPerRepeat(mat);
            float hx = sizeX / 2f, hz = sizeZ / 2f;
            Vector3 w = center + block.localPos;   // texture coordinates continuous across the tile
            md.Quad(mat,
                center + new Vector3(-hx, 0f, -hz), center + new Vector3(-hx, 0f, hz),
                center + new Vector3(hx, 0f, hz), center + new Vector3(hx, 0f, -hz),
                Vector3.up, new Vector4(1f, 0f, 0f, -1f),
                new Vector2((w.x - hx) / m, (w.z - hz) / m), new Vector2((w.x + hx) / m, (w.z + hz) / m));
        }
    }
}
