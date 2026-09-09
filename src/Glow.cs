using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealisticNight
{
    // Shared mesh + material helpers. SoftGlow uses Sprites/Default (always blended, never stripped).

    internal static class GlowFX
    {
        public static readonly int BaseColID = Shader.PropertyToID("_BaseColor");
        public static readonly int BaseMapID = Shader.PropertyToID("_BaseMap");
        public static readonly int MainTexID = Shader.PropertyToID("_MainTex");
        public static readonly int ColorID = Shader.PropertyToID("_Color");
        static Mesh billboard;

        // Unit quad in the XY plane (normal -Z). Billboards by setting transform.rotation = camera.rotation.
        public static Mesh Billboard()
        {
            if (billboard != null) return billboard;
            billboard = new Mesh { name = "RN_Billboard" };
            billboard.vertices = new[] { new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(0.5f, 0.5f, 0), new Vector3(-0.5f, 0.5f, 0) };
            billboard.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            // Force white vertex colours (Sprites/Default multiplies by them; black = invisible).
            billboard.colors = new[] { Color.white, Color.white, Color.white, Color.white };
            billboard.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            billboard.RecalculateBounds();
            return billboard;
        }

        // Cantilever lamp post as ONE mesh (shaft up +Y, arm out +Z). Height/arm/width baked in.
        public static Mesh PoleMesh(float height, float armLen, float width)
        {
            const int sides = 6;                 // hexagonal tubes read as round but stay cheap (~110 tris/pole)
            float w = Mathf.Max(0.02f, width);
            var verts = new List<Vector3>();
            var tris = new List<int>();

            // Tapered tube from A to B. Double-sided material, so winding never matters.
            void Tube(Vector3 A, Vector3 B, float rA, float rB, bool capA, bool capB)
            {
                Vector3 axis = B - A; float len = axis.magnitude;
                if (len < 1e-5f) return;
                Vector3 up = axis / len;
                Vector3 refv = Mathf.Abs(up.y) < 0.9f ? Vector3.up : Vector3.forward;
                Vector3 ex = Vector3.Normalize(Vector3.Cross(up, refv));
                Vector3 ez = Vector3.Cross(up, ex);
                int b0 = verts.Count;
                for (int s = 0; s < sides; s++)
                {
                    float a = (s / (float)sides) * Mathf.PI * 2f;
                    Vector3 dir = ex * Mathf.Cos(a) + ez * Mathf.Sin(a);
                    verts.Add(A + dir * rA);   // even = ring A
                    verts.Add(B + dir * rB);   // odd  = ring B
                }
                for (int s = 0; s < sides; s++)
                {
                    int s0 = b0 + s * 2, s1 = b0 + ((s + 1) % sides) * 2;
                    tris.Add(s0); tris.Add(s0 + 1); tris.Add(s1 + 1);
                    tris.Add(s0); tris.Add(s1 + 1); tris.Add(s1);
                }
                if (capB) { int c = verts.Count; verts.Add(B); for (int s = 0; s < sides; s++) { tris.Add(b0 + s * 2 + 1); tris.Add(c); tris.Add(b0 + ((s + 1) % sides) * 2 + 1); } }
                if (capA) { int c = verts.Count; verts.Add(A); for (int s = 0; s < sides; s++) { tris.Add(b0 + ((s + 1) % sides) * 2); tris.Add(c); tris.Add(b0 + s * 2); } }
            }

            Tube(new Vector3(0, 0, 0), new Vector3(0, w * 1.6f, 0), w * 1.25f, w * 0.95f, true, false); // base foot
            Tube(new Vector3(0, 0, 0), new Vector3(0, height, 0), w * 0.55f, w * 0.42f, false, true);   // tapered shaft

            if (armLen > 0.05f)
            {
                // Swept cobra-curve mast arm, sampled into tapering tube segments.
                float rise = Mathf.Min(armLen * 0.45f, height * 0.28f);
                float drop = height * 0.05f;
                Vector3 P0 = new Vector3(0, height, 0);
                Vector3 C = new Vector3(0, height + rise, armLen * 0.35f);
                Vector3 P2 = new Vector3(0, height - drop, armLen);
                const int seg = 6;
                Vector3 prev = P0; float prevR = w * 0.42f;
                for (int i = 1; i <= seg; i++)
                {
                    float t = i / (float)seg, u = 1f - t;
                    Vector3 pt = u * u * P0 + 2f * u * t * C + t * t * P2;
                    float r = Mathf.Lerp(w * 0.42f, w * 0.30f, t);
                    Tube(prev, pt, prevR, r, false, false);
                    prev = pt; prevR = r;
                }
                // Cobra-head luminaire: a rounded housing along the arm tangent at the tip, nosing slightly down.
                Vector3 tan = (P2 - C); tan = tan.sqrMagnitude < 1e-6f ? Vector3.forward : tan.normalized;
                float lumLen = Mathf.Clamp(armLen * 0.55f, 0.8f, 1.7f);
                Tube(P2 - tan * lumLen * 0.35f, P2 + tan * lumLen * 0.65f, w * 1.05f, w * 0.5f, true, true);
            }

            var m = new Mesh { name = "RN_PoleMesh" };
            m.SetVertices(verts);
            m.SetTriangles(tris, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        // Soft alpha-blended glow. Tint via _Color (HDR values bloom).
        public static Material SoftGlow(Texture tex, string name)
        {
            Shader sh = Shader.Find("Sprites/Default");
            if (sh == null) sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null) return null;
            Material m = new Material(sh) { name = name };
            if (tex != null)
            {
                if (m.HasProperty(MainTexID)) m.SetTexture(MainTexID, tex);
                if (m.HasProperty(BaseMapID)) m.SetTexture(BaseMapID, tex);
            }
            if (m.HasProperty(ColorID)) m.SetColor(ColorID, Color.white);
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            return m;
        }

        // Opaque metallic lamp-post material (URP/Lit). Transparent fallbacks made poles see-through.
        public static Material PoleMetal(Color baseCol)
        {
            Shader sh = Shader.Find("Universal Render Pipeline/Lit");
            bool lit = sh != null;
            if (sh == null) sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null) sh = Shader.Find("Unlit/Color");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            if (sh == null) return null;
            Material m = new Material(sh) { name = "RN_PoleMat" };
            if (m.HasProperty(BaseColID)) m.SetColor(BaseColID, baseCol);
            if (m.HasProperty(ColorID)) m.SetColor(ColorID, baseCol);
            if (lit)
            {
                if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0.65f);   // steel; some diffuse so it stays visible at night
                if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.5f);
                // Faint emission floor so posts are never pure black on a moonless night.
                if (m.HasProperty("_EmissionColor")) { m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", new Color(0.02f, 0.021f, 0.026f)); }
                // Double-sided tubes (procedural winding untrusted); poles are thin, cost is trivial.
                if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);
                if (m.HasProperty("_RenderFace")) m.SetFloat("_RenderFace", 0f);
            }
            ForceOpaque(m);
            return m;
        }

        // Force a material opaque + depth-writing regardless of shader defaults.
        static void ForceOpaque(Material m)
        {
            if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 0f);          // URP: 0 = Opaque
            if (m.HasProperty("_Blend")) m.SetFloat("_Blend", 0f);
            if (m.HasProperty("_SrcBlend")) m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
            if (m.HasProperty("_DstBlend")) m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.Zero);
            if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 1f);
            m.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.DisableKeyword("_ALPHABLEND_ON");
            m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry;
        }

        // Radial soft glow, mipmapped so distant markers soften instead of shimmering.
        public static Texture2D RadialTex(int size, float core, float falloff)
        {
            core = Mathf.Clamp01(core); falloff = Mathf.Max(0.1f, falloff);
            var t = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: true, linear: false)
            { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            float half = (size - 1) * 0.5f, maxr = size * 0.5f;
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float r = Mathf.Sqrt((x - half) * (x - half) + (y - half) * (y - half)) / maxr;
                    float a = r <= core ? 1f : (r >= 1f ? 0f : Mathf.Pow(1f - (r - core) / (1f - core), falloff));
                    px[y * size + x] = new Color(1f, 1f, 1f, a);
                }
            t.SetPixels(px); t.Apply(true, false);
            return t;
        }

        // Open beam cone along +Z (apex at z=0, base at z=len). Shared by headlight cones and ship searchlights.
        // Single shell: silhouette softness comes from the beam shader's edge fade, not nested geometry.
        // (An end cap was tried and removed: unfaded, it reads as a detached disc past the dissolved beam end.)
        public static Mesh BeamConeMesh(float len, float baseR, int sides, float apexR = 0.14f)
        {
            var m = new Mesh { name = "RN_BeamCone" };
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var cols = new List<Color>();
            var norms = new List<Vector3>(); // radial shell normals (drive the beam shader's edge fade)
            var tris = new List<int>();
            for (int s = 0; s < sides; s++)
            {
                float a0 = (s / (float)sides) * Mathf.PI * 2f;
                float a1 = ((s + 1) / (float)sides) * Mathf.PI * 2f;
                Vector3 n0 = new Vector3(Mathf.Cos(a0), Mathf.Sin(a0), -0.18f).normalized;
                Vector3 n1 = new Vector3(Mathf.Cos(a1), Mathf.Sin(a1), -0.18f).normalized;
                Vector3 apex0 = new Vector3(Mathf.Cos(a0) * apexR, Mathf.Sin(a0) * apexR, 0f);
                Vector3 apex1 = new Vector3(Mathf.Cos(a1) * apexR, Mathf.Sin(a1) * apexR, 0f);
                Vector3 base0 = new Vector3(Mathf.Cos(a0) * baseR, Mathf.Sin(a0) * baseR, len);
                Vector3 base1 = new Vector3(Mathf.Cos(a1) * baseR, Mathf.Sin(a1) * baseR, len);
                int b = verts.Count;
                verts.Add(apex0); verts.Add(base0); verts.Add(base1);
                verts.Add(apex0); verts.Add(base1); verts.Add(apex1);
                norms.Add(n0); norms.Add(n0); norms.Add(n1);
                norms.Add(n0); norms.Add(n1); norms.Add(n1);
                uvs.Add(new Vector2(0f, 0f)); uvs.Add(new Vector2(0f, 1f)); uvs.Add(new Vector2(1f, 1f));
                uvs.Add(new Vector2(0f, 0f)); uvs.Add(new Vector2(1f, 1f)); uvs.Add(new Vector2(1f, 0f));
                Color ca = new Color(1f, 1f, 1f, 0.55f), cb = new Color(1f, 1f, 1f, 0f);
                cols.Add(ca); cols.Add(cb); cols.Add(cb);
                cols.Add(ca); cols.Add(cb); cols.Add(ca);
                for (int k = 0; k < 6; k++) tris.Add(b + k);
            }
            m.SetVertices(verts);
            m.SetUVs(0, uvs);
            m.SetColors(cols);
            m.SetNormals(norms);
            m.SetTriangles(tris, 0);
            m.RecalculateBounds();
            return m;
        }

        // Axial beam gradient: hot at the lens end (v=0), fast falloff down-beam. Mipmapped.
        public static Texture2D BeamFadeTex()
        {
            int W = 64, H = 64;
            var t = new Texture2D(W, H, TextureFormat.RGBA32, mipChain: true, linear: false)
            { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float v = y / (float)(H - 1); // 0 apex -> 1 base
                    float a = Mathf.Pow(1f - v, 2.0f); // tight axial fade: hot lens end, fast falloff down-beam
                    px[y * W + x] = new Color(1f, 1f, 1f, a);
                }
            t.SetPixels(px); t.Apply(true, false);
            return t;
        }

        // Ship searchlight beam: slow axial falloff (long throw stays lit, then dissolves) with baked
        // streak/grain variation for a foggy read. Mipmapped: distant beams soften instead of shimmering.
        public static Texture2D ShipBeamTex()
        {
            int W = 64, H = 128;
            var t = new Texture2D(W, H, TextureFormat.RGBA32, mipChain: true, linear: false)
            { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float v = y / (float)(H - 1); // 0 apex -> 1 base
                    float a = Mathf.Pow(1f - v, 1.2f); // slow falloff: the throw persists, then dissolves
                    float band = 0.5f + 0.5f * Mathf.Sin(v * 17f + 1.7f) * Mathf.Sin(v * 7.3f + 0.4f); // smooth density drift
                    float grain = 0.5f + 0.5f * Mathf.Sin(x * 12.9898f + y * 78.233f) * Mathf.Sin(x * 3.7f - y * 9.1f); // fine grain (mips eat it far away)
                    a *= 0.70f + 0.30f * band;
                    a *= 0.90f + 0.10f * grain;
                    px[y * W + x] = new Color(1f, 1f, 1f, a);
                }
            t.SetPixels(px); t.Apply(true, false);
            return t;
        }
    }

    // Merged-mesh baking onto regular GameObjects (replaces every instancing path: plain
    // MeshRenderer draws need no variants, no buffers, no per-instance pipeline at all).
    internal static class MergeBake
    {
        static readonly Bounds Huge = new Bounds(Vector3.zero, Vector3.one * 200000f); // never culled
        // Creates (or reuses) a MeshFilter + MeshRenderer GO; keeps the material current.
        public static void EnsureGO(ref GameObject go, ref Mesh mesh, string name, Transform parent, Material mat, bool receiveShadows)
        {
            if (mesh == null) { mesh = new Mesh { name = name }; mesh.MarkDynamic(); mesh.bounds = Huge; } // rewritten per tick
            if (go == null)
            {
                go = new GameObject(name);
                if (parent != null) go.transform.SetParent(parent, worldPositionStays: false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                MeshRenderer mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = ShadowCastingMode.Off; // glow + poles never cast (matches old draws)
                mr.receiveShadows = receiveShadows;
            }
            else { MeshRenderer mr = go.GetComponent<MeshRenderer>(); if (mr != null && mr.sharedMaterial != mat) mr.sharedMaterial = mat; }
        }
        // Push scratch lists into the mesh (explicit tris; empty mesh draws nothing).
        public static void Upload(Mesh mesh, List<Vector3> pos, List<Vector2> uv, List<Vector2> uv1, List<Color> col, List<int> tri)
        {
            if (mesh == null) return;
            mesh.Clear();
            if (tri.Count == 0) { mesh.bounds = Huge; return; }
            mesh.SetVertices(pos);
            if (uv != null) mesh.SetUVs(0, uv);
            if (uv1 != null) mesh.SetUVs(1, uv1);
            if (col != null) mesh.SetColors(col);
            mesh.SetTriangles(tri, 0);
            mesh.bounds = Huge;
        }
        // Opaque variant with normals, no uvs (same layout as the pole template mesh).
        public static void UploadN(Mesh mesh, List<Vector3> pos, List<Vector3> nrm, List<int> tri)
        {
            if (mesh == null) return;
            mesh.Clear();
            if (tri.Count == 0) { mesh.bounds = Huge; return; }
            mesh.SetVertices(pos);
            mesh.SetNormals(nrm);
            mesh.SetTriangles(tri, 0);
            mesh.bounds = Huge;
        }
    }
}
