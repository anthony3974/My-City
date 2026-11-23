using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Draws the city's textures in code (no downloaded art), saves them as PNGs in Assets/City/Textures and
/// builds the URP materials in Assets/City/Materials. Every texture tiles seamlessly.
/// Facades are drawn to the bay and floor sizes in CityLook, so windows line up with floors in the game.
/// Albedo alpha holds smoothness (glass shiny, walls matte). A matching normal map is made from a height map.
/// Menu: Tools > City > Bake Textures and Materials.
/// </summary>
public static class CityTextureBaker
{
    const string TexFolder = "Assets/City/Textures";
    const string MatFolder = "Assets/City/Materials";

    delegate void Painter(int px, int py, float u, float v, float mx, float my, out Color c, out float smooth, out float height);

    [MenuItem("Tools/City/Bake Textures and Materials")]
    public static void BakeMenu() => BakeAll();

    public static Material[] BakeAll()
    {
        EnsureFolder("Assets/City");
        EnsureFolder(TexFolder);
        EnsureFolder(MatFolder);

        var brick = CityLook.Facade(CityMat.BrickRed);
        var shop = CityLook.Facade(CityMat.Storefront);

        var t = new (string name, int w, int h, float wm, float hm, Painter p, float normal)[]
        {
            ("BrickRed", 2048, 2048, brick.TexWidth, brick.TexHeight, BrickFacade(new Color(0.56f, 0.26f, 0.19f), 11), 0.6f),
            ("BrickTan", 2048, 2048, brick.TexWidth, brick.TexHeight, BrickFacade(new Color(0.76f, 0.63f, 0.46f), 12), 0.6f),
            ("Stone", 2048, 2048, brick.TexWidth, brick.TexHeight, StoneFacade, 0.6f),
            ("Plaster", 2048, 2048, brick.TexWidth, brick.TexHeight, PlasterFacade, 0.6f),
            ("Office", 2048, 2048, brick.TexWidth, brick.TexHeight, OfficeFacade, 0.5f),
            ("Glass", 2048, 2048, brick.TexWidth, brick.TexHeight, CurtainWall, 0.5f),
            ("Storefront", 2048, 1024, shop.TexWidth, shop.TexHeight, Storefront, 0.6f),
            ("Asphalt", 1024, 1024, 8f, 8f, Asphalt, 0.4f),
            ("Sidewalk", 1024, 1024, 6f, 6f, Sidewalk, 0.6f),
            ("Roof", 1024, 1024, 8f, 8f, Roof, 0.5f),
            ("Grass", 1024, 1024, 4f, 4f, Grass, 0.3f),
            ("Leaves", 512, 512, 2f, 2f, Leaves, 0.5f),
            ("Trunk", 512, 512, 1f, 1f, Trunk, 0.6f),
            ("Metal", 512, 512, 2f, 2f, Metal, 0.5f),
        };

        var albedo = new System.Collections.Generic.Dictionary<string, Texture2D>();
        var normals = new System.Collections.Generic.Dictionary<string, Texture2D>();
        foreach (var (name, w, h, wm, hm, p, normal) in t)
        {
            Bake(name, w, h, wm, hm, p, normal);
            albedo[name] = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexFolder}/{name}.png");
            normals[name] = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexFolder}/{name}_Normal.png");
        }

        var mats = new Material[(int)CityMat.Count];
        Material M(string tex, string matName, Color tint, float bump = 1f) =>
            MakeMaterial(matName, albedo[tex], normals[tex], tint, bump, 0f);

        mats[(int)CityMat.Asphalt] = M("Asphalt", "Asphalt", Color.white);
        mats[(int)CityMat.LanePaint] = MakeMaterial("LanePaint", null, null, new Color(0.95f, 0.82f, 0.3f), 1f, 0.3f);
        mats[(int)CityMat.WhitePaint] = MakeMaterial("WhitePaint", null, null, new Color(0.9f, 0.9f, 0.88f), 1f, 0.3f);
        mats[(int)CityMat.Sidewalk] = M("Sidewalk", "Sidewalk", Color.white);
        mats[(int)CityMat.Storefront] = M("Storefront", "Storefront", Color.white);
        mats[(int)CityMat.BrickRed] = M("BrickRed", "BrickRed", Color.white);
        mats[(int)CityMat.BrickTan] = M("BrickTan", "BrickTan", Color.white);
        mats[(int)CityMat.Stone] = M("Stone", "Stone", Color.white);
        mats[(int)CityMat.Plaster] = M("Plaster", "Plaster", new Color(1f, 0.96f, 0.9f));
        mats[(int)CityMat.PlasterGrey] = M("Plaster", "PlasterGrey", new Color(0.78f, 0.8f, 0.83f));
        mats[(int)CityMat.Office] = M("Office", "Office", Color.white);
        mats[(int)CityMat.GlassBlue] = M("Glass", "GlassBlue", new Color(0.72f, 0.85f, 1f));
        mats[(int)CityMat.GlassTeal] = M("Glass", "GlassTeal", new Color(0.68f, 0.95f, 0.9f));
        mats[(int)CityMat.GlassDark] = M("Glass", "GlassDark", new Color(0.5f, 0.52f, 0.56f));
        mats[(int)CityMat.GlassSilver] = M("Glass", "GlassSilver", new Color(1f, 1f, 1f));
        mats[(int)CityMat.Roof] = M("Roof", "Roof", Color.white);
        mats[(int)CityMat.Metal] = M("Metal", "Metal", Color.white);
        mats[(int)CityMat.Grass] = M("Grass", "Grass", Color.white);
        mats[(int)CityMat.Trunk] = M("Trunk", "Trunk", Color.white);
        mats[(int)CityMat.Leaves] = M("Leaves", "Leaves", Color.white);
        mats[(int)CityMat.TrafficPole] = MakeMaterial("TrafficPole", null, null, new Color(0.14f, 0.16f, 0.19f), 1f, 0.65f);
        mats[(int)CityMat.TrafficYellow] = MakeMaterial("TrafficYellow", null, null, new Color(0.95f, 0.68f, 0.06f), 1f, 0.55f);
        mats[(int)CityMat.TrafficDark] = MakeMaterial("TrafficDark", null, null, new Color(0.05f, 0.05f, 0.06f), 1f, 0.25f);

        AssetDatabase.SaveAssets();
        return mats;
    }

    // ------------------------------------------------------------------ Baking

    static void Bake(string name, int w, int h, float wm, float hm, Painter paint, float normalStrength)
    {
        var colors = new Color32[w * h];
        var heights = new float[w * h];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float u = (x + 0.5f) / w, v = (y + 0.5f) / h;
                paint(x, y, u, v, u * wm, v * hm, out Color c, out float s, out float ht);
                colors[y * w + x] = new Color32(B(c.r), B(c.g), B(c.b), B(s));
                heights[y * w + x] = ht;
            }
        });
        WritePng($"{TexFolder}/{name}.png", w, h, colors, normal: false);

        // Height (metres) to tangent-space normals, wrapping at the edges so the texture still tiles.
        float pxW = wm / w, pxH = hm / h;
        var n = new Color32[w * h];
        Parallel.For(0, h, y =>
        {
            int yu = (y + 1) % h, yd = (y - 1 + h) % h;
            for (int x = 0; x < w; x++)
            {
                int xr = (x + 1) % w, xl = (x - 1 + w) % w;
                float dx = (heights[y * w + xr] - heights[y * w + xl]) / (2f * pxW);
                float dy = (heights[yu * w + x] - heights[yd * w + x]) / (2f * pxH);
                var nv = new Vector3(-dx * normalStrength, -dy * normalStrength, 1f).normalized;
                n[y * w + x] = new Color32(B(nv.x * 0.5f + 0.5f), B(nv.y * 0.5f + 0.5f), B(nv.z * 0.5f + 0.5f), 255);
            }
        });
        WritePng($"{TexFolder}/{name}_Normal.png", w, h, n, normal: true);
    }

    static byte B(float f) => (byte)Mathf.Clamp(Mathf.RoundToInt(f * 255f), 0, 255);

    static void WritePng(string path, int w, int h, Color32[] pixels, bool normal)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, linear: normal);
        tex.SetPixels32(pixels);
        tex.Apply();
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);

        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        var ti = (TextureImporter)AssetImporter.GetAtPath(path);
        ti.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
        ti.sRGBTexture = !normal;
        ti.alphaSource = normal ? TextureImporterAlphaSource.None : TextureImporterAlphaSource.FromInput;
        ti.alphaIsTransparency = false;
        ti.mipmapEnabled = true;
        ti.wrapMode = TextureWrapMode.Repeat;
        ti.filterMode = FilterMode.Trilinear;
        ti.anisoLevel = 4;
        ti.maxTextureSize = Mathf.Max(w, h);
        ti.textureCompression = TextureImporterCompression.Compressed;
        ti.SaveAndReimport();
    }

    static Material MakeMaterial(string name, Texture2D albedo, Texture2D normal, Color tint, float bump, float smoothness)
    {
        string path = $"{MatFolder}/City_{name}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        bool created = mat == null;
        if (created) mat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "City_" + name };

        mat.SetColor("_BaseColor", tint);
        mat.SetFloat("_Metallic", 0f);
        mat.SetTexture("_BaseMap", albedo);
        if (albedo != null)
        {
            // Smoothness comes from the albedo's alpha channel.
            mat.SetFloat("_SmoothnessTextureChannel", 1f);
            mat.EnableKeyword("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A");
            mat.SetFloat("_Smoothness", 1f);
        }
        else
        {
            mat.SetFloat("_SmoothnessTextureChannel", 0f);
            mat.DisableKeyword("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A");
            mat.SetFloat("_Smoothness", smoothness);
        }
        mat.SetTexture("_BumpMap", normal);
        mat.SetFloat("_BumpScale", bump);
        if (normal != null) mat.EnableKeyword("_NORMALMAP"); else mat.DisableKeyword("_NORMALMAP");

        if (created) AssetDatabase.CreateAsset(mat, path);
        else EditorUtility.SetDirty(mat);
        return mat;
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        int slash = path.LastIndexOf('/');
        AssetDatabase.CreateFolder(path.Substring(0, slash), path.Substring(slash + 1));
    }

    // ------------------------------------------------------------------ Noise (all of it tiles)

    static uint Hash(uint x)
    {
        x ^= x >> 16; x *= 0x7feb352dU; x ^= x >> 15; x *= 0x846ca68bU; x ^= x >> 16;
        return x;
    }

    static float H(int a, int b, int seed) =>
        Hash((uint)a * 0x8da6b343U ^ (uint)b * 0xd8163841U ^ (uint)seed * 0xcb1ab31fU) / 4294967295f;

    static int Mod(int a, int m) => ((a % m) + m) % m;

    /// <summary>Smooth value noise over texture space (u, v in 0..1), repeating fx by fy cells.</summary>
    static float Noise(float u, float v, int fx, int fy, int seed)
    {
        float x = u * fx, y = v * fy;
        int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
        float tx = x - x0, ty = y - y0;
        tx = tx * tx * (3f - 2f * tx);
        ty = ty * ty * (3f - 2f * ty);
        int xa = Mod(x0, fx), xb = Mod(x0 + 1, fx), ya = Mod(y0, fy), yb = Mod(y0 + 1, fy);
        return Mathf.Lerp(Mathf.Lerp(H(xa, ya, seed), H(xb, ya, seed), tx),
                          Mathf.Lerp(H(xa, yb, seed), H(xb, yb, seed), tx), ty);
    }

    static float Fbm(float u, float v, int fx, int fy, int octaves, int seed)
    {
        float sum = 0f, amp = 0.5f, total = 0f;
        for (int o = 0; o < octaves; o++)
        {
            sum += Noise(u, v, fx << o, fy << o, seed + o * 101) * amp;
            total += amp;
            amp *= 0.5f;
        }
        return sum / total;
    }

    static bool In(float x, float y, float x0, float y0, float x1, float y1) => x >= x0 && x < x1 && y >= y0 && y < y1;

    static Color Shade(Color c, float f) => new Color(c.r * f, c.g * f, c.b * f);

    // ------------------------------------------------------------------ Shared window glass

    /// <summary>One window pane. gx, gy are 0..1 across the pane; id picks blinds and interior per window.</summary>
    static Color Pane(float gx, float gy, int idX, int idY, int seed, out float smooth)
    {
        float r1 = H(idX, idY, seed), r2 = H(idX, idY, seed + 1), r3 = H(idX, idY, seed + 2), r4 = H(idX, idY, seed + 3);
        Color c = Color.Lerp(new Color(0.07f, 0.08f, 0.1f), new Color(0.2f, 0.18f, 0.15f), r1 * r1);

        // Blinds pulled down part of the way in some windows.
        float blind = r2 < 0.45f ? Mathf.Lerp(0.15f, 0.85f, r3) : 0f;
        if (gy > 1f - blind)
        {
            Color b = r4 < 0.5f ? new Color(0.78f, 0.75f, 0.67f) : r4 < 0.8f ? new Color(0.6f, 0.61f, 0.6f) : new Color(0.86f, 0.83f, 0.76f);
            float slat = Mathf.Repeat(gy * 40f, 1f) < 0.2f ? 0.85f : 1f;
            c = Shade(b, slat * 0.8f);
        }

        // Sky reflection, brighter towards the top of the pane.
        c += new Color(0.16f, 0.2f, 0.25f) * (0.35f + 0.65f * gy);
        smooth = 0.92f;
        return c;
    }

    // ------------------------------------------------------------------ Facades

    static Painter BrickFacade(Color brickColor, int seed) =>
        (int px, int py, float u, float v, float mx, float my, out Color c, out float s, out float h) =>
        {
            const float bayW = 3f, rowH = CityLook.FloorHeight;
            int bx = Mathf.FloorToInt(mx / bayW), by = Mathf.FloorToInt(my / rowH);
            float lx = mx - bx * bayW, ly = my - by * rowH;

            const float wx0 = 0.85f, wx1 = 2.15f, wy0 = 0.9f, wy1 = 2.7f;
            if (In(lx, ly, wx0, wy0, wx1, wy1))
            {
                float gx = (lx - wx0) / (wx1 - wx0), gy = (ly - wy0) / (wy1 - wy0);
                bool frame = lx < wx0 + 0.07f || lx > wx1 - 0.07f || ly < wy0 + 0.07f || ly > wy1 - 0.07f
                             || Mathf.Abs(gy - 0.52f) * (wy1 - wy0) < 0.035f || Mathf.Abs(gx - 0.5f) * (wx1 - wx0) < 0.025f;
                if (frame) { c = new Color(0.86f, 0.85f, 0.82f); s = 0.4f; h = -0.03f; }
                else { c = Pane(gx, gy, bx, by, seed, out s); h = -0.07f; }
                return;
            }
            if (In(lx, ly, wx0 - 0.12f, wy1, wx1 + 0.12f, wy1 + 0.22f) || In(lx, ly, wx0 - 0.1f, wy0 - 0.14f, wx1 + 0.1f, wy0))
            {
                // Stone lintel above and sill below the window.
                c = Shade(new Color(0.76f, 0.73f, 0.67f), 0.9f + 0.15f * Fbm(u, v, 64, 64, 2, seed + 5));
                s = 0.15f; h = 0.02f;
                return;
            }

            // Running-bond brick: 225 x 75 mm with 10 mm mortar. The texture is a whole number of bricks, so it tiles.
            const float bl = 0.225f, bh = 0.075f, mortar = 0.011f;
            int course = Mathf.FloorToInt(my / bh);
            float off = (course & 1) * bl * 0.5f;
            float x = mx + off;
            int bi = Mathf.FloorToInt(x / bl);
            float bxl = x - bi * bl, byl = my - course * bh;
            int bricksAcross = Mathf.RoundToInt(6 * bayW / bl);
            bi = Mod(bi, bricksAcross);

            float grime = Fbm(u, v, 12, 12, 3, seed + 9);
            // Streaks of dirt running down from the sills.
            float streak = lx > wx0 + 0.1f && lx < wx1 - 0.1f && ly < wy0 ? 0.07f * Noise(u, v, 400, 6, seed + 3) * (ly / wy0) * (ly / wy0) : 0f;
            if (bxl < mortar || byl < mortar)
            {
                c = Shade(new Color(0.72f, 0.7f, 0.66f), 0.85f + 0.1f * grime - streak);
                s = 0.05f; h = -0.01f;
            }
            else
            {
                float r = H(bi, course, seed), r2 = H(bi, course, seed + 7);
                c = Shade(brickColor, 0.78f + 0.35f * r - 0.12f * grime - streak);
                c.r += (r2 - 0.5f) * 0.05f;
                c *= 0.95f + 0.1f * H(px, py, seed + 2);
                s = 0.12f; h = 0f;
            }
        };

    static void StoneFacade(int px, int py, float u, float v, float mx, float my, out Color c, out float s, out float h)
    {
        const int seed = 21;
        const float bayW = 3f, rowH = CityLook.FloorHeight;
        int bx = Mathf.FloorToInt(mx / bayW), by = Mathf.FloorToInt(my / rowH);
        float lx = mx - bx * bayW, ly = my - by * rowH;
        Color stone = new Color(0.8f, 0.75f, 0.64f);

        const float wx0 = 0.9f, wx1 = 2.1f, wy0 = 0.75f, wy1 = 2.85f;
        if (In(lx, ly, wx0, wy0, wx1, wy1))
        {
            float gx = (lx - wx0) / (wx1 - wx0), gy = (ly - wy0) / (wy1 - wy0);
            bool frame = lx < wx0 + 0.06f || lx > wx1 - 0.06f || ly < wy0 + 0.06f || ly > wy1 - 0.06f
                         || Mathf.Abs(gx - 0.5f) * (wx1 - wx0) < 0.025f || Mathf.Abs(gy - 0.7f) * (wy1 - wy0) < 0.03f;
            if (frame) { c = new Color(0.2f, 0.21f, 0.22f); s = 0.4f; h = -0.06f; }
            else { c = Pane(gx, gy, bx, by, seed, out s); h = -0.1f; }
            return;
        }

        float grime = Fbm(u, v, 10, 10, 3, seed + 4);
        float fine = 0.94f + 0.08f * Fbm(u, v, 256, 256, 2, seed + 6);
        if (In(lx, ly, wx0 - 0.16f, wy0 - 0.16f, wx1 + 0.16f, wy1 + 0.16f))
        {
            // Carved surround around each window.
            c = Shade(stone, (1.05f - 0.1f * grime) * fine); s = 0.12f; h = 0.03f;
            return;
        }
        if (ly < 0.2f)
        {
            // Cornice line at each floor.
            c = Shade(stone, (ly < 0.05f ? 0.8f : 1.02f) * fine); s = 0.12f; h = ly < 0.05f ? 0f : 0.05f;
            return;
        }

        // Ashlar blocks, 750 x 500 mm.
        const float bl = 0.75f, bh = 0.5f, joint = 0.008f;
        int course = Mathf.FloorToInt(my / bh);
        float x = mx + (course & 1) * bl * 0.5f;
        int bi = Mod(Mathf.FloorToInt(x / bl), Mathf.RoundToInt(6 * bayW / bl));
        float jx = x - Mathf.Floor(x / bl) * bl, jy = my - course * bh;
        if (jx < joint || jy < joint) { c = Shade(stone, 0.7f); s = 0.05f; h = -0.006f; return; }
        c = Shade(stone, (0.9f + 0.14f * H(bi, course, seed) - 0.15f * grime) * fine);
        s = 0.1f; h = 0f;
    }

    static void PlasterFacade(int px, int py, float u, float v, float mx, float my, out Color c, out float s, out float h)
    {
        const int seed = 31;
        const float bayW = 3f, rowH = CityLook.FloorHeight;
        int bx = Mathf.FloorToInt(mx / bayW), by = Mathf.FloorToInt(my / rowH);
        float lx = mx - bx * bayW, ly = my - by * rowH;

        const float wx0 = 0.85f, wx1 = 2.15f, wy0 = 0.95f, wy1 = 2.7f;
        float rs = H(bx, by, seed + 50);
        bool shutters = rs < 0.35f;
        if (In(lx, ly, wx0, wy0, wx1, wy1))
        {
            float gx = (lx - wx0) / (wx1 - wx0), gy = (ly - wy0) / (wy1 - wy0);
            bool frame = lx < wx0 + 0.06f || lx > wx1 - 0.06f || ly < wy0 + 0.06f || ly > wy1 - 0.06f
                         || Mathf.Abs(gx - 0.5f) * (wx1 - wx0) < 0.03f;
            if (frame) { c = new Color(0.3f, 0.3f, 0.3f); s = 0.35f; h = -0.04f; }
            else { c = Pane(gx, gy, bx, by, seed, out s); h = -0.08f; }
            return;
        }
        if (shutters && ly > wy0 && ly < wy1 && (In(lx, ly, wx0 - 0.5f, wy0, wx0 - 0.04f, wy1) || In(lx, ly, wx1 + 0.04f, wy0, wx1 + 0.5f, wy1)))
        {
            // Painted wooden shutters with slats.
            float pick = H(bx, by, seed + 51);
            Color sc = pick < 0.33f ? new Color(0.22f, 0.36f, 0.27f) : pick < 0.66f ? new Color(0.25f, 0.33f, 0.45f) : new Color(0.42f, 0.27f, 0.2f);
            bool slat = Mathf.Repeat(ly * 14f, 1f) < 0.25f;
            c = Shade(sc, slat ? 0.75f : 1f); s = 0.3f; h = slat ? 0.005f : 0.015f;
            return;
        }
        if (In(lx, ly, wx0 - 0.08f, wy0 - 0.1f, wx1 + 0.08f, wy0)) { c = new Color(0.82f, 0.81f, 0.78f); s = 0.2f; h = 0.025f; return; }

        float grime = Fbm(u, v, 8, 8, 4, seed + 1);
        float fine = 0.96f + 0.06f * Fbm(u, v, 300, 300, 2, seed + 2);
        bool band = ly < 0.18f;
        c = Shade(new Color(0.93f, 0.91f, 0.87f), (band ? 0.92f : 1f) * fine * (1f - 0.12f * grime));
        s = 0.08f;
        h = (band ? 0.02f : 0f) + 0.0015f * Fbm(u, v, 300, 300, 2, seed + 2);
    }

    static void OfficeFacade(int px, int py, float u, float v, float mx, float my, out Color c, out float s, out float h)
    {
        const int seed = 41;
        const float bayW = 3f, rowH = CityLook.FloorHeight;
        int bx = Mathf.FloorToInt(mx / bayW), by = Mathf.FloorToInt(my / rowH);
        float lx = mx - bx * bayW, ly = my - by * rowH;
        float fine = 0.95f + 0.07f * Fbm(u, v, 256, 256, 2, seed + 3);

        if (ly < 1.05f || ly > 3.3f)
        {
            // Precast concrete spandrel band, panel joints at each bay.
            bool joint = lx < 0.015f || Mathf.Abs(ly - 1.04f) < 0.01f;
            c = Shade(new Color(0.74f, 0.73f, 0.7f), (joint ? 0.65f : 1f) * fine * (1f - 0.12f * Fbm(u, v, 8, 8, 3, seed)));
            s = 0.1f; h = joint ? -0.01f : 0.03f;
            return;
        }
        // Ribbon window, a mullion every 1.5 m.
        int pane = Mathf.FloorToInt(lx / 1.5f);
        float plx = lx - pane * 1.5f;
        if (plx < 0.04f || plx > 1.46f || ly < 1.1f || ly > 3.25f)
        {
            c = new Color(0.33f, 0.35f, 0.38f); s = 0.55f; h = -0.02f;
            return;
        }
        c = Pane((plx - 0.04f) / 1.42f, (ly - 1.1f) / 2.15f, bx * 2 + pane, by, seed, out s);
        h = -0.06f;
    }

    static void CurtainWall(int px, int py, float u, float v, float mx, float my, out Color c, out float s, out float h)
    {
        const int seed = 51;
        const float bayW = 3f, rowH = CityLook.FloorHeight;
        int bx = Mathf.FloorToInt(mx / bayW), by = Mathf.FloorToInt(my / rowH);
        float lx = mx - bx * bayW, ly = my - by * rowH;
        int pane = Mathf.FloorToInt(lx / 1.5f);
        float plx = lx - pane * 1.5f;

        bool mullion = plx < 0.035f || plx > 1.465f || ly < 0.05f || Mathf.Abs(ly - 0.9f) < 0.025f;
        if (mullion) { c = new Color(0.55f, 0.57f, 0.6f); s = 0.6f; h = 0.02f; return; }

        float r = H(bx * 2 + pane, by, seed);
        if (ly < 0.9f)
        {
            // Opaque spandrel glass hiding the floor slab.
            c = Shade(new Color(0.24f, 0.27f, 0.31f), 0.95f + 0.1f * r); s = 0.9f; h = 0f;
            return;
        }
        // Vision glass: mostly reflection, a hint of the office behind, and slight tint changes per pane.
        float gy = (ly - 0.9f) / (rowH - 0.9f);
        c = new Color(0.27f, 0.31f, 0.36f) * (0.9f + 0.2f * r) + new Color(0.1f, 0.12f, 0.14f) * gy;
        if (H(bx * 2 + pane, by, seed + 1) < 0.3f && gy < 0.85f)
            c += new Color(0.05f, 0.05f, 0.04f);   // lights on inside
        s = 0.96f; h = 0f;
    }

    static readonly Color[] SignColors =
    {
        new(0.62f, 0.1f, 0.1f), new(0.1f, 0.35f, 0.2f), new(0.1f, 0.16f, 0.35f), new(0.75f, 0.55f, 0.12f),
        new(0.1f, 0.1f, 0.1f), new(0.88f, 0.86f, 0.8f), new(0.1f, 0.4f, 0.42f), new(0.42f, 0.1f, 0.2f),
    };

    static void Storefront(int px, int py, float u, float v, float mx, float my, out Color c, out float s, out float h)
    {
        const int seed = 61;
        const float bayW = 6f, rowH = CityLook.StorefrontHeight;
        int bx = Mathf.FloorToInt(mx / bayW), by = Mathf.FloorToInt(my / rowH);
        float lx = mx - bx * bayW, ly = my - by * rowH;
        float fine = 0.95f + 0.07f * Fbm(u, v, 256, 128, 2, seed);

        if (lx < 0.3f || lx > 5.7f)
        {
            c = Shade(new Color(0.5f, 0.46f, 0.42f), fine); s = 0.15f; h = 0.04f;   // pier between shops
            return;
        }
        if (ly < 0.4f) { c = Shade(new Color(0.16f, 0.16f, 0.18f), fine); s = 0.6f; h = 0.01f; return; }   // granite plinth
        if (ly > 4.1f) { c = Shade(new Color(0.48f, 0.46f, 0.43f), fine); s = 0.15f; h = 0.05f; return; }   // cornice

        int pick = Mathf.FloorToInt(H(bx, by, seed) * SignColors.Length) % SignColors.Length;
        if (ly > 3.35f)
        {
            // Shop sign with blocky "lettering".
            Color sign = SignColors[pick];
            Color text = sign.grayscale > 0.5f ? new Color(0.12f, 0.12f, 0.12f) : (H(bx, by, seed + 1) < 0.5f ? new Color(0.95f, 0.93f, 0.88f) : new Color(0.9f, 0.75f, 0.35f));
            int letters = 5 + Mathf.FloorToInt(H(bx, by, seed + 2) * 6);
            float tx0 = 3f - letters * 0.16f, tx1 = 3f + letters * 0.16f;
            if (In(lx, ly, tx0, 3.55f, tx1, 3.9f))
            {
                int li = Mathf.FloorToInt((lx - tx0) / 0.32f);
                float f = (lx - tx0) / 0.32f - li;
                if (f > 0.12f && f < 0.82f && H(li, bx * 8 + by, seed + 3) > 0.08f)
                {
                    c = text; s = 0.5f; h = 0.015f;
                    return;
                }
            }
            c = sign; s = 0.55f; h = 0.03f;
            return;
        }
        if (ly > 3.2f) { c = new Color(0.12f, 0.12f, 0.13f); s = 0.5f; h = 0.02f; return; }   // frame under the sign

        // Door on the left, display window on the right, transom bar across both.
        bool door = lx < 1.6f;
        float gx0 = door ? 0.55f : 1.75f, gx1 = door ? 1.55f : 5.55f, gy0 = door ? 0f : 0.4f;
        bool inGlass = In(lx, ly, gx0, gy0, gx1, 3.2f);
        bool frame = !inGlass || lx < gx0 + 0.07f || lx > gx1 - 0.07f || Mathf.Abs(ly - 2.6f) < 0.04f || (door && ly < 0.1f);
        if (frame) { c = new Color(0.13f, 0.13f, 0.14f); s = 0.5f; h = 0.01f; return; }

        float gx = (lx - gx0) / (gx1 - gx0), gy = (ly - gy0) / (3.2f - gy0);
        c = new Color(0.08f, 0.08f, 0.09f);
        if (!door && ly < 1.6f)
        {
            // Goods on three display shelves.
            float shelfY = (ly - 0.4f) / 0.4f;
            int shelf = Mathf.FloorToInt(shelfY);
            float sy = shelfY - shelf;
            if (sy < 0.08f) c = new Color(0.3f, 0.29f, 0.27f);
            else
            {
                int item = Mathf.FloorToInt(gx * 22f);
                float ix = gx * 22f - item;
                float r = H(item, shelf * 16 + bx * 4 + by, seed + 4);
                if (r > 0.25f && ix > 0.12f && ix < 0.88f && sy < 0.4f + 0.5f * r)
                    c = Color.HSVToRGB(H(item, shelf, seed + 5), 0.3f, 0.28f + 0.25f * r);
            }
        }
        else if (ly < 2.6f)
        {
            c = Color.Lerp(new Color(0.08f, 0.08f, 0.09f), new Color(0.35f, 0.3f, 0.24f), 0.5f * H(bx, by, seed + 6));   // lit interior
        }
        c += new Color(0.15f, 0.18f, 0.22f) * (0.3f + 0.7f * gy);
        s = 0.93f; h = -0.05f;
    }

    // ------------------------------------------------------------------ Ground and materials

    static void Asphalt(int px, int py, float u, float v, float mx, float my, out Color c, out float s, out float h)
    {
        const int seed = 71;
        float patch = Fbm(u, v, 4, 4, 4, seed);
        float stone = H(px, py, seed + 1);
        float g = 0.21f + 0.06f * (patch - 0.5f);
        if (stone > 0.93f) g += 0.1f * (stone - 0.93f) / 0.07f;
        else if (stone < 0.05f) g -= 0.05f;
        float crackN = Noise(u, v, 24, 24, seed + 3);
        if (Mathf.Abs(crackN - 0.5f) < 0.004f && Fbm(u, v, 3, 3, 2, seed + 4) > 0.62f) g *= 0.7f;   // thin cracks here and there
        c = new Color(g, g, g * 1.04f);
        s = 0.18f + 0.08f * patch;
        h = 0.002f * stone;
    }

    static void Sidewalk(int px, int py, float u, float v, float mx, float my, out Color c, out float s, out float h)
    {
        const int seed = 81;
        const float slab = 1.5f;
        int sx = Mathf.FloorToInt(mx / slab), sy = Mathf.FloorToInt(my / slab);
        float lx = mx - sx * slab, ly = my - sy * slab;
        bool joint = lx < 0.015f || ly < 0.015f;
        float stain = Fbm(u, v, 6, 6, 4, seed);
        float grain = H(px, py, seed + 1);
        float g = joint ? 0.42f : (0.64f + 0.06f * H(Mod(sx, 4), Mod(sy, 4), seed + 2) - 0.1f * stain + 0.04f * (grain - 0.5f));
        c = new Color(g, g * 0.99f, g * 0.96f);
        s = 0.12f;
        h = joint ? -0.005f : 0.0008f * grain;
    }

    static void Roof(int px, int py, float u, float v, float mx, float my, out Color c, out float s, out float h)
    {
        const int seed = 91;
        float gravel = H(px, py, seed);
        float patch = Fbm(u, v, 5, 5, 4, seed + 1);
        float g = 0.36f + 0.12f * gravel + 0.08f * (patch - 0.5f);
        c = new Color(g, g * 0.98f, g * 0.95f);
        s = 0.06f;
        h = 0.004f * gravel;
    }

    static void Grass(int px, int py, float u, float v, float mx, float my, out Color c, out float s, out float h)
    {
        const int seed = 101;
        float patch = Fbm(u, v, 6, 6, 4, seed);
        float blade = Noise(u, v, 512, 96, seed + 1);
        c = Color.Lerp(new Color(0.2f, 0.36f, 0.12f), new Color(0.36f, 0.52f, 0.2f), 0.6f * patch + 0.4f * blade);
        if (patch < 0.35f) c = Color.Lerp(c, new Color(0.42f, 0.4f, 0.24f), (0.35f - patch) * 2f);   // worn patches
        s = 0.08f;
        h = 0.004f * blade;
    }

    static void Leaves(int px, int py, float u, float v, float mx, float my, out Color c, out float s, out float h)
    {
        const int seed = 111;
        float clump = Fbm(u, v, 8, 8, 4, seed);
        float leaf = H(px / 3, py / 3, seed + 1);
        float t = Mathf.Clamp01(clump * 1.4f - 0.2f) * 0.8f + leaf * 0.2f;
        c = Color.Lerp(new Color(0.08f, 0.2f, 0.07f), new Color(0.32f, 0.5f, 0.18f), t);
        s = 0.15f;
        h = 0.03f * clump + 0.004f * leaf;
    }

    static void Trunk(int px, int py, float u, float v, float mx, float my, out Color c, out float s, out float h)
    {
        const int seed = 121;
        float ridge = Fbm(u, v, 24, 3, 3, seed);
        float t = Mathf.Abs(ridge - 0.5f) * 2f;
        c = Color.Lerp(new Color(0.18f, 0.12f, 0.08f), new Color(0.4f, 0.3f, 0.2f), t);
        s = 0.08f;
        h = 0.01f * t;
    }

    static void Metal(int px, int py, float u, float v, float mx, float my, out Color c, out float s, out float h)
    {
        const int seed = 131;
        bool seam = Mathf.Repeat(mx, 1f) < 0.01f || Mathf.Repeat(my, 1f) < 0.01f;
        float brushed = Noise(u, v, 4, 256, seed);
        float dirt = Fbm(u, v, 4, 4, 3, seed + 1);
        float g = seam ? 0.3f : 0.55f + 0.08f * brushed - 0.1f * dirt;
        c = new Color(g, g * 1.01f, g * 1.04f);
        s = seam ? 0.2f : 0.45f;
        h = seam ? -0.004f : 0f;
    }
}
