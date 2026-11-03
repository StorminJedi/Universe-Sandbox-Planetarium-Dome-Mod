// DomeGpu.cs  (v1.5.0)
// GPU replacement for the CPU DomeRemap/DomeWorker pipeline.
//
// IDEA
//   The 5 face RenderTextures never leave the GPU. A dome-shaped mesh (a grid
//   over the unit square, one set of triangles per face) has the old
//   DomeMath.RemapUV result baked into its per-vertex UVs. Drawing that mesh
//   with a plain unlit-texture shader into ONE dome RenderTexture performs the
//   whole 5-face -> 180deg equidistant warp on the GPU in well under a ms.
//   A black "mask" mesh (square minus unit circle) gives the crisp circle edge.
//
//   For the second OS window the single dome RT is read back with
//   AsyncGPUReadback (non-blocking, BGRA32 == GDI DIB byte order), so the main
//   thread never stalls waiting for the GPU.
//
// Uses only APIs present in the game's UnityEngine.CoreModule.
using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace US2Dome
{
    public sealed class DomeGpu : IDisposable
    {
        public RenderTexture DomeRT;        // dome output (row 0 = bottom, Unity convention)
        public string ShaderName = "";      // which shader we ended up using
        public string Error = "";
        public bool Ready;
        public Action<string> OnDebug;

        readonly Mesh[] _faceMesh = new Mesh[5];
        readonly Material[] _faceMat = new Material[5];
        RenderTexture[] _faceRTs;
        bool _mips;
        Mesh _maskMesh;
        Material _maskMat;
        CommandBuffer _cmd;
        Matrix4x4 _proj;
        int _res;
        int _gridN;
        static float _halfAngle = (float)(Math.PI / 2.0);   // dome rim = this many radians off-axis (PI/2 = 180 deg dome; smaller = zoomed in)
        // v1.6.3: up to MaxInFlight readbacks in flight (was 1). With a single request
        // outstanding the dome could never update faster than 1 / readback-latency
        // (typically 2-3 frames), no matter how fast the game ran.
        public const int MaxInFlight = 3;
        int _inflight;
        int _gen;                                   // bumped by Resize(): readbacks of an older size are dropped
        readonly List<KeyValuePair<float, RenderTexture>> _retired = new List<KeyValuePair<float, RenderTexture>>();
        int _seqIssued, _seqDelivered;
        byte[] _rb;
        bool _disposed;

        void DLog(string m) { if (OnDebug != null) { try { OnDebug(m); } catch { } } }

        // Opaque shaders first (no alpha surprises); blended ones as fallback.
        static readonly string[] ShaderCandidates =
        {
            "Unlit/Texture",
            "Sprites/Default",
            "UI/Default",
            "Hidden/Internal-GUITexture",
        };

        public DomeGpu(RenderTexture[] faceRTs, int res, int gridN, bool mips, float zoom)
        {
            try
            {
                _res = res;
                _halfAngle = (float)(Math.PI / 2.0) / Math.Max(1f, zoom);
                _faceRTs = faceRTs;
                _mips = mips;

                Shader sh = null;
                for (int i = 0; i < ShaderCandidates.Length && sh == null; i++)
                {
                    Shader s = Shader.Find(ShaderCandidates[i]);
                    if (s != null && s.isSupported) { sh = s; ShaderName = ShaderCandidates[i]; }
                }
                if (sh == null) { Error = "no usable unlit texture shader found in the game build (tried: " + string.Join(", ", ShaderCandidates) + ")"; return; }

                // Dome RT. Default read/write => same sRGB-ness as the face RTs, so
                // sample(decode) -> write(encode) is a round trip.
                DomeRT = new RenderTexture(res, res, 0, RenderTextureFormat.ARGB32);
                DomeRT.name = "[US2Dome] DomeRT";
                DomeRT.filterMode = FilterMode.Bilinear;
                DomeRT.wrapMode = TextureWrapMode.Clamp;
                DomeRT.useMipMap = false;
                if (!DomeRT.Create()) { Error = "DomeRT.Create failed"; return; }

                for (int f = 0; f < 5; f++)
                {
                    if (faceRTs[f] == null) continue;
                    faceRTs[f].filterMode = mips ? FilterMode.Trilinear : FilterMode.Bilinear;
                    faceRTs[f].wrapMode = TextureWrapMode.Clamp;
                    Material m = new Material(sh);
                    m.mainTexture = faceRTs[f];
                    if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
                    if (m.HasProperty("_RendererColor")) m.SetColor("_RendererColor", Color.white);
                    if (m.HasProperty("_Flip")) m.SetVector("_Flip", new Vector4(1, 1, 1, 1));
                    _faceMat[f] = m;
                }
                _maskMat = new Material(sh);
                _maskMat.mainTexture = Texture2D.blackTexture;
                if (_maskMat.HasProperty("_Color")) _maskMat.SetColor("_Color", Color.white);
                if (_maskMat.HasProperty("_RendererColor")) _maskMat.SetColor("_RendererColor", Color.white);
                if (_maskMat.HasProperty("_Flip")) _maskMat.SetVector("_Flip", new Vector4(1, 1, 1, 1));

                _gridN = gridN;
                int tris = BuildMeshes(gridN);

                _cmd = new CommandBuffer();
                _cmd.name = "US2Dome";
                _proj = GL.GetGPUProjectionMatrix(Matrix4x4.Ortho(-1f, 1f, -1f, 1f, -1f, 1f), true);

                Ready = true;
                DLog("[US2Dome] DomeGpu ready: shader='" + ShaderName + "' rt=" + res + "px grid=" + gridN +
                     " triangles(all faces, both windings)=" + tris +
                     " asyncReadback=" + SystemInfo.supportsAsyncGPUReadback);
            }
            catch (Exception e) { Error = "exception: " + e; Ready = false; }
        }

        // ------------------------------------------------------------------
        //  Mesh construction (done once)
        // ------------------------------------------------------------------
        // UV on face f for the direction at output position (px,py) in [-1,1]^2.
        // Same equidistant math as DomeMath.RemapUV, but the face is FORCED (so
        // the vertices at a face boundary get valid, continuous UVs for each
        // face) and r is clamped to 1 (vertices just outside the circle).
        // v1.7.5: the face cameras render slightly MORE than 90 degrees: tan(halfFov) = FaceTanHalf instead of 1.
        // The warp assigns each grid cell to ONE face by its centre, so cells at a seam stick out past the exact 90-degree edge
        // of their face by up to ~1 cell. With exactly 90 degrees there was no image data there (the clamped edge row got
        // smeared across a few dome pixels), so anything crossing a seam (e.g. a label) shifted/jumped. With the margin that strip
        // holds the true image, and both faces agree at the seam.
        public const float FaceTanHalf = 1.1f;
        public static float FaceFov { get { return 2f * (float)Math.Atan(FaceTanHalf) * 57.29578f; } }

        static Vector2 ForcedUV(float px, float py, int f)
        {
            float r = (float)Math.Sqrt(px * px + py * py);
            if (r > 1f) r = 1f;
            float theta = _halfAngle * r;
            float az = (float)Math.Atan2(py, px);
            V3 d = new V3((float)(Math.Sin(theta) * Math.Cos(az)),
                          (float)(Math.Sin(theta) * Math.Sin(az)),
                          (float)Math.Cos(theta));
            float dz = V3.Dot(d, DomeMath.Fwd[f]);
            if (dz < 0.02f) dz = 0.02f;
            float u = V3.Dot(d, DomeMath.Right[f]) / dz;
            float v = V3.Dot(d, DomeMath.Up[f]) / dz;
            // the face image now covers +-FaceTanHalf (a little past the exact 90-degree edge); texture is clamped beyond that
            if (u > FaceTanHalf) u = FaceTanHalf; if (u < -FaceTanHalf) u = -FaceTanHalf;
            if (v > FaceTanHalf) v = FaceTanHalf; if (v < -FaceTanHalf) v = -FaceTanHalf;
            return new Vector2((u / FaceTanHalf + 1f) * 0.5f, (v / FaceTanHalf + 1f) * 0.5f);
        }

        // Face whose axis is closest to the direction at (px,py), with r clamped to 1
        // (so cells straddling the circle still get a face; the black mask trims them).
        static int FaceAtClamped(float px, float py)
        {
            float r = (float)Math.Sqrt(px * px + py * py);
            if (r > 1f) r = 1f;
            float theta = _halfAngle * r;
            float az = (float)Math.Atan2(py, px);
            V3 d = new V3((float)(Math.Sin(theta) * Math.Cos(az)),
                          (float)(Math.Sin(theta) * Math.Sin(az)),
                          (float)Math.Cos(theta));
            int bi = 0; float best = -2f;
            for (int i = 0; i < 5; i++)
            {
                float dd = V3.Dot(d, DomeMath.Fwd[i]);
                if (dd > best) { best = dd; bi = i; }
            }
            return bi;
        }

        void DestroyMeshes()
        {
            for (int f = 0; f < 5; f++)
                if (_faceMesh[f] != null) { UnityEngine.Object.Destroy(_faceMesh[f]); _faceMesh[f] = null; }
            if (_maskMesh != null) { UnityEngine.Object.Destroy(_maskMesh); _maskMesh = null; }
        }

        // v1.6.0: change the zoom live (rebuilds only the warp meshes; no RT/camera changes).
        public void SetZoom(float zoom)
        {
            if (!Ready || _disposed) return;
            _halfAngle = (float)(Math.PI / 2.0) / Math.Max(1f, zoom);
            DestroyMeshes();
            int tris = BuildMeshes(_gridN);
            DLog("[US2Dome] Zoom set to " + zoom + " (meshes rebuilt, triangles=" + tris + ")");
        }

        int BuildMeshes(int N)
        {
            int vn = N + 1;
            Vector3[] verts = new Vector3[vn * vn];
            for (int j = 0; j < vn; j++)
                for (int i = 0; i < vn; i++)
                {
                    // v1.6.4: vertices that fall outside the unit circle are pulled radially onto it, so the
                    // outer edge of the warp mesh IS the circle (not the staircase of whole grid cells that
                    // used to overhang it, up to ~1.4 cells = a visible 256x256 stair-step). UVs are computed
                    // from this final position below, so the mapping stays exact at every vertex.
                    float gx = -1f + 2f * i / N, gy = -1f + 2f * j / N;
                    float gr = (float)Math.Sqrt(gx * gx + gy * gy);
                    if (gr > 1f) { gx /= gr; gy /= gr; }
                    verts[j * vn + i] = new Vector3(gx, gy, 0f);
                }

            int totalTris = 0;
            for (int f = 0; f < 5; f++)
            {
                Vector2[] uvs = new Vector2[vn * vn];
                for (int j = 0; j < vn; j++)
                    for (int i = 0; i < vn; i++)
                        uvs[j * vn + i] = ForcedUV(verts[j * vn + i].x, verts[j * vn + i].y, f);

                List<int> idx = new List<int>();
                for (int j = 0; j < N; j++)
                {
                    for (int i = 0; i < N; i++)
                    {
                        float cx = -1f + 2f * (i + 0.5f) / N;
                        float cy = -1f + 2f * (j + 0.5f) / N;
                        // v1.5.4: keep every cell that touches the circle (not just those whose
                        // CENTER is inside) -> the circle edge is trimmed by the smooth mask
                        // mesh instead of following the N x N grid staircase.
                        float cr = (float)Math.Sqrt(cx * cx + cy * cy);
                        if (cr > 1f + 1.5f / N) continue;
                        if (FaceAtClamped(cx, cy) != f) continue;
                        int a = j * vn + i, b = a + 1, c = a + vn, d = c + 1;
                        // both windings => visible regardless of the shader's Cull mode
                        idx.Add(a); idx.Add(c); idx.Add(b);   idx.Add(b); idx.Add(c); idx.Add(d);
                        idx.Add(a); idx.Add(b); idx.Add(c);   idx.Add(b); idx.Add(d); idx.Add(c);
                    }
                }
                totalTris += idx.Count / 3;
                Mesh m = new Mesh();
                m.name = "[US2Dome] FaceMesh" + f;
                m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                m.vertices = verts;
                m.uv = uvs;
                m.SetIndices(idx.ToArray(), MeshTopology.Triangles, 0);
                m.bounds = new Bounds(Vector3.zero, new Vector3(4f, 4f, 4f));
                _faceMesh[f] = m;
            }

            // Mask: everything between the unit circle and the square, in black.
            int M = 1440;
            Vector3[] mv = new Vector3[(M + 1) * 2];
            Vector2[] muv = new Vector2[(M + 1) * 2];
            for (int k = 0; k <= M; k++)
            {
                float a = (float)(2.0 * Math.PI * k / M);
                float cs = (float)Math.Cos(a), sn = (float)Math.Sin(a);
                float mx = Math.Max(Math.Abs(cs), Math.Abs(sn));
                mv[k * 2]     = new Vector3(cs, sn, 0f);                          // on the circle
                mv[k * 2 + 1] = new Vector3(cs / mx * 1.02f, sn / mx * 1.02f, 0f); // just past the square
            }
            List<int> mi = new List<int>();
            for (int k = 0; k < M; k++)
            {
                int i0 = k * 2, o0 = k * 2 + 1, i1 = (k + 1) * 2, o1 = (k + 1) * 2 + 1;
                mi.Add(i0); mi.Add(o0); mi.Add(i1);   mi.Add(i1); mi.Add(o0); mi.Add(o1);
                mi.Add(i0); mi.Add(i1); mi.Add(o0);   mi.Add(i1); mi.Add(o1); mi.Add(o0);
            }
            _maskMesh = new Mesh();
            _maskMesh.name = "[US2Dome] MaskMesh";
            _maskMesh.vertices = mv;
            _maskMesh.uv = muv;
            _maskMesh.SetIndices(mi.ToArray(), MeshTopology.Triangles, 0);
            _maskMesh.bounds = new Bounds(Vector3.zero, new Vector3(4f, 4f, 4f));
            return totalTris;
        }

        // ------------------------------------------------------------------
        //  Per-capture work
        // ------------------------------------------------------------------
        // Warp the 5 (already rendered) face RTs into DomeRT. GPU only.
        public void Draw()
        {
            if (!Ready || _disposed) return;
            PurgeRetired();
            _cmd.Clear();
            if (_mips)
                for (int f = 0; f < 5; f++)
                    if (_faceRTs[f] != null) _cmd.GenerateMips(_faceRTs[f]);   // box-filtered mip chain -> trilinear downsampling
            _cmd.SetRenderTarget(DomeRT);
            _cmd.ClearRenderTarget(true, true, Color.black);
            _cmd.SetViewProjectionMatrices(Matrix4x4.identity, _proj);
            for (int f = 0; f < 5; f++)
                if (_faceMesh[f] != null && _faceMat[f] != null)
                    _cmd.DrawMesh(_faceMesh[f], Matrix4x4.identity, _faceMat[f], 0, 0);
            _cmd.DrawMesh(_maskMesh, Matrix4x4.identity, _maskMat, 0, 0);
            Graphics.ExecuteCommandBuffer(_cmd);
            RenderTexture.active = null;
        }

        // v1.6.5: change the dome RT size live (GPU warp is cheap) so the 2nd window can blit 1:1.
        // The old RT is kept alive ~2 s because readbacks of it may still be in flight.
        public bool Resize(int res)
        {
            if (!Ready || _disposed || res < 16) return false;
            if (DomeRT != null && DomeRT.width == res) return true;
            try
            {
                RenderTexture n = new RenderTexture(res, res, 0, RenderTextureFormat.ARGB32);
                n.name = "[US2Dome] DomeRT";
                n.filterMode = FilterMode.Bilinear;
                n.wrapMode = TextureWrapMode.Clamp;
                n.useMipMap = false;
                if (!n.Create()) { UnityEngine.Object.Destroy(n); return false; }
                if (DomeRT != null) _retired.Add(new KeyValuePair<float, RenderTexture>(Time.realtimeSinceStartup + 2f, DomeRT));
                DomeRT = n;
                _res = res;
                _gen++;
                _rb = null;
                DLog("[US2Dome] DomeRT resized to " + res + "px");
                return true;
            }
            catch (Exception e) { DLog("[US2Dome] DomeGpu.Resize THREW: " + e.Message); return false; }
        }

        void PurgeRetired()
        {
            if (_retired.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            for (int i = _retired.Count - 1; i >= 0; i--)
                if (now >= _retired[i].Key)
                {
                    RenderTexture t = _retired[i].Value;
                    if (t != null) { t.Release(); UnityEngine.Object.Destroy(t); }
                    _retired.RemoveAt(i);
                }
        }

        public bool ReadbackBusy { get { return _inflight >= MaxInFlight; } }
        public bool ReadbackSlotFree { get { return Ready && !_disposed && _inflight < MaxInFlight; } }

        // Non-blocking readback of DomeRT as BGRA32 (row 0 = bottom). `done`
        // is invoked on the main thread a few frames later with a reused buffer
        // (valid only during the call). Several requests may be in flight at once;
        // results older than one already delivered are dropped.
        // Returns false if all slots are busy (caller just skips).
        public bool RequestReadback(Action<byte[]> done)
        {
            if (!Ready || _disposed || _inflight >= MaxInFlight) return false;
            if (!SystemInfo.supportsAsyncGPUReadback) return false;
            _inflight++;
            int seq = ++_seqIssued;
            int gen = _gen;
            AsyncGPUReadback.Request(DomeRT, 0, TextureFormat.BGRA32, (AsyncGPUReadbackRequest req) =>
            {
                if (_inflight > 0) _inflight--;
                if (_disposed) return;
                if (req.hasError) { DLog("[US2Dome] AsyncGPUReadback reported an error"); return; }
                if (gen != _gen) return;                  // requested before a Resize(): wrong size, drop
                if (seq < _seqDelivered) return;          // stale (out of order): never show an older frame
                _seqDelivered = seq;
                try
                {
                    NativeArray<byte> data = req.GetData<byte>();
                    if (_rb == null || _rb.Length != data.Length) _rb = new byte[data.Length];
                    data.CopyTo(_rb);
                    done(_rb);
                }
                catch (Exception e) { DLog("[US2Dome] readback callback THREW: " + e); }
            });
            return true;
        }

        public void Dispose()
        {
            _disposed = true;
            try
            {
                if (_cmd != null) { _cmd.Release(); _cmd = null; }
                for (int f = 0; f < 5; f++)
                {
                    if (_faceMesh[f] != null) UnityEngine.Object.Destroy(_faceMesh[f]);
                    if (_faceMat[f] != null) UnityEngine.Object.Destroy(_faceMat[f]);
                }
                if (_maskMesh != null) UnityEngine.Object.Destroy(_maskMesh);
                if (_maskMat != null) UnityEngine.Object.Destroy(_maskMat);
                if (DomeRT != null) { DomeRT.Release(); UnityEngine.Object.Destroy(DomeRT); DomeRT = null; }
                for (int i = 0; i < _retired.Count; i++) if (_retired[i].Value != null) { _retired[i].Value.Release(); UnityEngine.Object.Destroy(_retired[i].Value); }
                _retired.Clear();
            }
            catch { }
        }
    }
}
