// WindowMirror.cs -- shows one of the game's UI windows (default: the planet properties window) on the dome.
//
// WHY: the game's windows are screen UI (ScreenSpaceOverlay canvases), which the 5 dome face cameras cannot see.
// HOW: at the end of every frame the finished game window image is copied into a RenderTexture
// (ScreenCapture.CaptureScreenshotIntoRenderTexture, GPU only, no readback). Every capture frame a flat quad textured with the
// part of that image where the window is (found from the window's RectTransform) is placed on a private layer in the dome scene,
// facing the camera, upright toward the dome centre (like the labels). The face cameras render it and the dome warp bends it.
// Position/size come from [Window] in US2Dome.cfg and the F2 panel.
//
// v1.7: the 3D object view at the top of the properties window ("BodyViewport", a RawImage showing the RenderTexture
// 'property panel preview') can be split off into a second, independent quad on the dome ([ObjectView] section). The
// properties window quad then loses that top strip, so it is shorter.
using System;
using System.Collections;
using System.Reflection;
using UnityEngine;
using BepInEx.Configuration;

namespace US2Dome
{
    public partial class US2Dome
    {
        ConfigEntry<bool>   _cfgWm, _cfgWmFlip;
        ConfigEntry<float>  _cfgWmAng, _cfgWmAz, _cfgWmSize;
        ConfigEntry<string> _cfgWmPath;

        // ---- 3D object view as its own window ----
        ConfigEntry<bool>   _cfgOv, _cfgOvShow, _cfgOvDirect, _cfgOvFlip;
        ConfigEntry<float>  _cfgOvAng, _cfgOvAz, _cfgOvSize;
        ConfigEntry<string> _cfgOvName;
        GameObject _ovGo;
        Material _ovMat;
        Mesh _ovMesh;
        Transform _ovT;                 // the game's 3D view element (RawImage)
        Component _ovRaw;               // its RawImage component
        PropertyInfo _ovTexPi;          // RawImage.texture
        int _ovCheck = -999;
        bool _ovFirstLogged;
        Texture _ovLastTex;
        readonly Vector2[] _ovUv = new Vector2[4];

        // ---- Data Views window (the dock panel on the left) as another quad ----
        ConfigEntry<bool>   _cfgDv, _cfgDvFlip;
        ConfigEntry<float>  _cfgDvAng, _cfgDvAz, _cfgDvSize;
        ConfigEntry<string> _cfgDvPath;
        GameObject _dvGo;
        Material _dvMat;
        Mesh _dvMesh;
        Transform _dvPanel;
        int _dvCheck = -999;
        bool _dvFirstLogged;
        readonly Vector2[] _dvUv = new Vector2[4];

        // ---- draw-on-top: the three window quads live on their own layer and are drawn by extra cameras ----
        int _wmLayer = -1;
        readonly Camera[] _wmCams = new Camera[5];

        RenderTexture _wmRT;
        GameObject _wmGo;
        Material _wmMat;
        Mesh _wmMesh;
        Transform _wmPanel;
        int _wmPanelCheck = -999;
        MethodInfo _wmCapMi;
        bool _wmCapTried, _wmLoopStarted, _wmNeedGrab, _wmBroken, _wmFirstLogged;
        int _wmGrabs;
        Type _wmGroupT;
        bool _wmGroupTried;
        readonly object[] _wmArgs = new object[1];
        readonly Vector2[] _wmUv = new Vector2[4];

        void BindWindowMirrorConfig()
        {
            if (_cfgWm != null || _cfgFile == null) return;
            _cfgWm = _cfgFile.Bind("Window", "ShowWindow", true,
                "Show the game's planet properties window (see WindowPath) on the dome whenever it is open in the game.");
            _cfgWmPath = _cfgFile.Bind("Window", "WindowPath", "Gui/Panels/Viewport/BodyPanel2019",
                "Scene path of the game window to copy onto the dome (press F3 / F4 in the game to find the path of other windows in US2Dome.log).");
            _cfgWmAng = _cfgFile.Bind("Window", "AngleFromCenter", 50.0f,
                "Where the window sits on the dome: angle away from the dome centre in degrees (0 = centre, 90 = horizon).");
            _cfgWmAz = _cfgFile.Bind("Window", "Direction", 180.0f,
                "Where the window sits on the dome: direction around the centre in degrees (0 = toward the top of the dome image, 90 = right, 180 = bottom, -90 = left).");
            _cfgWmSize = _cfgFile.Bind("Window", "SizeDegrees", 40.0f,
                "Size of the window on the dome: how many degrees its longest side covers.");
            _cfgWmFlip = _cfgFile.Bind("Window", "FlipVertical", true,
                "Turn this off if the window shows upside-down on the dome (depends on the graphics API).");

            _cfgOv = _cfgFile.Bind("ObjectView", "SeparateView", true,
                "true = cut the 3D picture of the selected object out of the properties window on the dome (then ShowSeparateView decides whether it appears as its own window or is removed). false = leave it in the properties window.");
            _cfgOvShow = _cfgFile.Bind("ObjectView", "ShowSeparateView", true,
                "Only used when SeparateView = true: true = show the 3D view as its own window on the dome, false = remove the 3D view completely (the rest of the properties window stays).");
            _cfgOvName = _cfgFile.Bind("ObjectView", "ViewName", "BodyViewport",
                "Name of the game's 3D view element inside the properties window (see US2Dome.log, F3).");
            _cfgOvAng = _cfgFile.Bind("ObjectView", "AngleFromCenter", 50.0f,
                "Where the 3D view sits on the dome: angle away from the dome centre in degrees (0 = centre, 90 = horizon).");
            _cfgOvAz = _cfgFile.Bind("ObjectView", "Direction", 140.0f,
                "Where the 3D view sits on the dome: direction around the centre in degrees (0 = toward the top of the dome image, 90 = right, 180 = bottom, -90 = left).");
            _cfgOvSize = _cfgFile.Bind("ObjectView", "SizeDegrees", 25.0f,
                "Size of the 3D view on the dome: how many degrees its longest side covers.");
            _cfgOvDirect = _cfgFile.Bind("ObjectView", "UseGameTexture", true,
                "true = use the game's own 3D view picture directly (clean, no buttons on top). false = copy that part of the screen instead (also shows the little buttons over the view).");
            _cfgOvFlip = _cfgFile.Bind("ObjectView", "FlipVertical", false,
                "Turn this on if the 3D view shows upside-down on the dome while UseGameTexture = true. (When UseGameTexture = false the [Window] FlipVertical setting is used.)");

            _cfgDv = _cfgFile.Bind("DataView", "ShowDataView", true,
                "Show the game's Data Views window (graphs; the docked panel on the left) on the dome whenever it is open in the game.");
            _cfgDvFlip = _cfgFile.Bind("DataView", "FlipVertical", true,
                "Turn this off if the Data Views window shows upside-down on the dome.");
            _cfgDvPath = _cfgFile.Bind("DataView", "WindowPath", "Gui/Panels/Viewport/DockPanel",
                "Scene path of the game's Data Views window (found with F3 / F4 in the game, see US2Dome.log).");
            _cfgDvAng = _cfgFile.Bind("DataView", "AngleFromCenter", 50.0f,
                "Where the Data Views window sits on the dome: angle away from the dome centre in degrees (0 = centre, 90 = horizon).");
            _cfgDvAz = _cfgFile.Bind("DataView", "Direction", -140.0f,
                "Where the Data Views window sits on the dome: direction around the centre in degrees (0 = toward the top of the dome image, 90 = right, 180 = bottom, -90 = left).");
            _cfgDvSize = _cfgFile.Bind("DataView", "SizeDegrees", 25.0f,
                "Size of the Data Views window on the dome: how many degrees its longest side covers.");
        }

        // Called every capture frame from Update(), BEFORE the face cameras are enabled.
        void UpdateWindowMirror(Camera main, Vector3 camPos)
        {
            BindWindowMirrorConfig();
            BindNameplateConfig();
            if (_cfgWm == null) return;
            if (!_wmLoopStarted) { _wmLoopStarted = true; StartCoroutine(WmGrabLoop()); }
            UpdatePropsMirror(main, camPos);   // may switch the screen copy off when its window is closed ...
            UpdateDataMirror(main, camPos);    // ... so this one runs last and switches it back on when it needs it
        }

        // The Data Views window: a plain screen copy of its rectangle, placed like the others.
        void UpdateDataMirror(Camera main, Vector3 camPos)
        {
            if (_cfgDv == null) return;
            if (!_cfgDv.Value || _wmBroken) { HideDv(); return; }

            if ((_dvPanel == null || !_dvPanel.gameObject.activeInHierarchy) && _frame - _dvCheck >= 10)
            {
                _dvCheck = _frame;
                GameObject g = GameObject.Find(_cfgDvPath.Value);
                _dvPanel = (g != null) ? g.transform : null;
            }
            if (_dvPanel == null || !_dvPanel.gameObject.activeInHierarchy) { HideDv(); return; }

            if (!_wmGroupTried) { _wmGroupTried = true; _wmGroupT = Type.GetType("UnityEngine.CanvasGroup, UnityEngine.UIModule"); }
            if (_wmGroupT != null)
            {
                Component cg = _dvPanel.GetComponent(_wmGroupT);
                if (cg != null && ToF(GetP(cg, "alpha")) < 0.02f) { HideDv(); return; }
            }

            Rect r;
            if (!ScreenRectOf(_dvPanel, out r)) { HideDv(); return; }
            float sw = Screen.width, sh = Screen.height;
            float x0 = Mathf.Clamp(r.xMin, 0f, sw), x1 = Mathf.Clamp(r.xMax, 0f, sw);
            float y0 = Mathf.Clamp(r.yMin, 0f, sh), y1 = Mathf.Clamp(r.yMax, 0f, sh);
            if (x1 - x0 < 8f || y1 - y0 < 8f) { HideDv(); return; }

            EnsureWm(main);
            if (_dvGo == null || _wmBroken) return;
            _wmNeedGrab = true;
            if (_wmGrabs == 0 || _wmRT == null) { HideDv(); return; }

            float u0 = x0 / sw, u1 = x1 / sw, v0 = y0 / sh, v1 = y1 / sh;
            float fa = _cfgDvFlip.Value ? 1f - v0 : v0;
            float fb = _cfgDvFlip.Value ? 1f - v1 : v1;
            _dvUv[0] = new Vector2(u0, fa); _dvUv[1] = new Vector2(u1, fa);
            _dvUv[2] = new Vector2(u0, fb); _dvUv[3] = new Vector2(u1, fb);
            _dvMesh.uv = _dvUv;
            if (_dvMat.mainTexture != _wmRT) _dvMat.mainTexture = _wmRT;

            float dist = Mathf.Max(0.2f, _cfgNpDist.Value) * 1.2f;
            Quaternion camRot = main.transform.rotation * Quaternion.Euler(-Mathf.Clamp(_cfgTilt.Value, -90f, 90f), 0f, 0f);
            PlaceOnDome(_dvGo, camRot, camPos, dist, _cfgDvAng.Value, _cfgDvAz.Value, _cfgDvSize.Value, x1 - x0, y1 - y0, Mathf.Max(x1 - x0, y1 - y0));
            if (!_dvFirstLogged)
            {
                _dvFirstLogged = true;
                LogInfo("[US2Dome] WindowMirror: Data Views '" + _cfgDvPath.Value + "' px=(" + x0.ToString("F0") + "," + y0.ToString("F0") + " " + (x1 - x0).ToString("F0") + "x" + (y1 - y0).ToString("F0") +
                        ") screen=" + sw.ToString("F0") + "x" + sh.ToString("F0") + " shader=" + _dvMat.shader.name);
            }
        }

        // ----- draw on top of everything -----
        // The window quads sit on their own layer, which the face cameras do not draw. Instead, when a face camera has
        // finished (OnEndCameraRendering), the windows are painted straight into that face's texture: depth is cleared
        // first, so they land over the sky, objects, orbit lines, grids and labels. The painting is done on the CPU side
        // (project a small grid of points, draw textured triangles), which needs no extra cameras and no matrix-convention guesses.
        const int WinGrid = 12;
        bool _wmDrawLogged;
        float[] _wgX, _wgY, _wgU, _wgV;
        bool[] _wgOk;

        bool WindowQuadsVisible()
        {
            return (_wmGo != null && _wmGo.activeSelf) || (_ovGo != null && _ovGo.activeSelf) || (_dvGo != null && _dvGo.activeSelf);
        }

        // Called for every face camera each capture frame (position/rotation already set).
        void SyncWindowCam(int i, Camera face)
        {
            if (face == null || _wmLayer < 0) return;
            face.cullingMask = ~(1 << _wmLayer);          // the normal face camera does not draw the window quads
        }

        void DisableWindowCams() { }
        void DestroyWindowCams() { }

        // Paint the visible window quads into face texture fi. Called when that face camera has finished rendering.
        void DrawWindowsIntoFace(int fi)
        {
            if (_wmLayer < 0 || !WindowQuadsVisible()) return;
            Camera face = _faceCams[fi];
            RenderTexture rt = _faceRTs[fi];
            if (face == null || rt == null) return;
            RenderTexture prev = RenderTexture.active;
            bool pushed = false;
            try
            {
                Matrix4x4 vp = face.projectionMatrix * face.worldToCameraMatrix;
                RenderTexture.active = rt;
                GL.Clear(true, false, Color.black);        // depth only: the finished picture stays, nothing in it can cover the windows
                GL.PushMatrix(); pushed = true;
                GL.LoadOrtho();                              // (0,0) bottom-left .. (1,1) top-right of the face texture
                int cells = 0;
                cells += DrawQuadIntoFace(_wmGo, _wmMat, _wmUv, vp);
                cells += DrawQuadIntoFace(_ovGo, _ovMat, _ovUv, vp);
                cells += DrawQuadIntoFace(_dvGo, _dvMat, _dvUv, vp);
                if (!_wmDrawLogged && cells > 0)
                {
                    _wmDrawLogged = true;
                    LogInfo("[US2Dome] WindowMirror: windows painted on top into face " + fi + " (" + cells + " cells)");
                }
            }
            catch (Exception e) { if (_frame % 600 == 1) LogErrorDetail("[US2Dome] DrawWindowsIntoFace THREW", e); }
            finally
            {
                if (pushed) GL.PopMatrix();
                RenderTexture.active = prev;
            }
        }

        // One window quad: a WinGrid x WinGrid grid of points, each projected to the face; cells with every corner in front of the camera are drawn.
        int DrawQuadIntoFace(GameObject go, Material mat, Vector2[] uv, Matrix4x4 vp)
        {
            if (go == null || mat == null || !go.activeSelf || mat.mainTexture == null) return 0;
            int n = WinGrid, np = (n + 1) * (n + 1);
            if (_wgX == null) { _wgX = new float[np]; _wgY = new float[np]; _wgU = new float[np]; _wgV = new float[np]; _wgOk = new bool[np]; }
            Matrix4x4 m = go.transform.localToWorldMatrix;
            bool any = false;
            for (int j = 0; j <= n; j++)
            {
                float t = (float)j / n;
                for (int i = 0; i <= n; i++)
                {
                    float s = (float)i / n;
                    int k = j * (n + 1) + i;
                    Vector3 w = m.MultiplyPoint3x4(new Vector3(s - 0.5f, t - 0.5f, 0f));
                    Vector4 c = vp * new Vector4(w.x, w.y, w.z, 1f);
                    bool ok = c.w > 0.01f;
                    _wgOk[k] = ok;
                    if (ok) { _wgX[k] = c.x / c.w * 0.5f + 0.5f; _wgY[k] = c.y / c.w * 0.5f + 0.5f; any = true; }
                    _wgU[k] = Mathf.Lerp(Mathf.Lerp(uv[0].x, uv[1].x, s), Mathf.Lerp(uv[2].x, uv[3].x, s), t);
                    _wgV[k] = Mathf.Lerp(Mathf.Lerp(uv[0].y, uv[1].y, s), Mathf.Lerp(uv[2].y, uv[3].y, s), t);
                }
            }
            if (!any) return 0;
            int cells = 0;
            bool begun = false;
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    int a = j * (n + 1) + i, b = a + 1, c2 = a + (n + 1), d = c2 + 1;
                    if (!(_wgOk[a] && _wgOk[b] && _wgOk[c2] && _wgOk[d])) continue;
                    // skip cells entirely outside the face
                    if ((_wgX[a] < -0.1f && _wgX[b] < -0.1f && _wgX[c2] < -0.1f && _wgX[d] < -0.1f) || (_wgX[a] > 1.1f && _wgX[b] > 1.1f && _wgX[c2] > 1.1f && _wgX[d] > 1.1f) ||
                        (_wgY[a] < -0.1f && _wgY[b] < -0.1f && _wgY[c2] < -0.1f && _wgY[d] < -0.1f) || (_wgY[a] > 1.1f && _wgY[b] > 1.1f && _wgY[c2] > 1.1f && _wgY[d] > 1.1f)) continue;
                    if (!begun)
                    {
                        mat.SetPass(0);
                        GL.Begin(GL.TRIANGLES);
                        GL.Color(Color.white);
                        begun = true;
                    }
                    GlV(a); GlV(c2); GlV(b);
                    GlV(c2); GlV(d); GlV(b);
                    cells++;
                }
            if (begun) GL.End();
            return cells;
        }

        void GlV(int k)
        {
            GL.TexCoord2(_wgU[k], _wgV[k]);
            GL.Vertex3(_wgX[k], _wgY[k], 0f);
        }

        void HideDv()
        {
            if (_dvGo != null && _dvGo.activeSelf) _dvGo.SetActive(false);
        }

        // The properties window (+ its 3D view).
        void UpdatePropsMirror(Camera main, Vector3 camPos)
        {
            if (_cfgWm == null) return;
            bool wantPanel = _cfgWm.Value, wantView = _cfgOv.Value;
            if ((!wantPanel && !wantView) || _wmBroken) { HideWm(); return; }

            if (!_wmLoopStarted) { _wmLoopStarted = true; StartCoroutine(WmGrabLoop()); }

            // find the window (GameObject.Find only sees ACTIVE objects, so a closed window is simply "not found")
            if ((_wmPanel == null || !_wmPanel.gameObject.activeInHierarchy) && _frame - _wmPanelCheck >= 10)
            {
                _wmPanelCheck = _frame;
                GameObject g = GameObject.Find(_cfgWmPath.Value);
                _wmPanel = (g != null) ? g.transform : null;
                _ovT = null; _ovRaw = null;
            }
            if (_wmPanel == null || !_wmPanel.gameObject.activeInHierarchy) { HideWm(); return; }

            // fully faded out (opening/closing animation)?
            if (!_wmGroupTried) { _wmGroupTried = true; _wmGroupT = Type.GetType("UnityEngine.CanvasGroup, UnityEngine.UIModule"); }
            if (_wmGroupT != null)
            {
                Component cg = _wmPanel.GetComponent(_wmGroupT);
                if (cg != null && ToF(GetP(cg, "alpha")) < 0.02f) { HideWm(); return; }
            }

            Rect r;
            if (!ScreenRectOf(_wmPanel, out r)) { HideWm(); return; }
            float sw = Screen.width, sh = Screen.height;
            float x0 = Mathf.Clamp(r.xMin, 0f, sw), x1 = Mathf.Clamp(r.xMax, 0f, sw);
            float y0 = Mathf.Clamp(r.yMin, 0f, sh), y1 = Mathf.Clamp(r.yMax, 0f, sh);
            if (x1 - x0 < 8f || y1 - y0 < 8f) { HideWm(); return; }
            float basisPx = Mathf.Max(x1 - x0, y1 - y0);   // size basis: the WHOLE window, so its text keeps the same size when the 3D view is cut off

            // the 3D view (optional)
            bool haveView = false;
            Rect vr = new Rect(0f, 0f, 0f, 0f);
            Texture vtex = null;
            if (wantView) haveView = FindObjectView(out vr, out vtex);
            bool direct = haveView && _cfgOvDirect.Value && vtex != null;

            // cut the 3D view's strip off the top of the properties window
            if (haveView)
            {
                float vy0 = Mathf.Clamp(vr.yMin, 0f, sh), vy1 = Mathf.Clamp(vr.yMax, 0f, sh);
                if (vy1 >= y1 - 4f && vy0 > y0 + 8f) y1 = vy0;
            }

            EnsureWm(main);
            if (_wmGo == null || _wmBroken) return;
            _wmNeedGrab = true;                    // copy the screen at the end of this frame
            if (_wmGrabs == 0 || _wmRT == null) { HideWm(); _wmNeedGrab = true; return; }   // nothing captured yet

            float dist = Mathf.Max(0.2f, _cfgNpDist.Value) * 1.2f;
            Quaternion camRot = main.transform.rotation * Quaternion.Euler(-Mathf.Clamp(_cfgTilt.Value, -90f, 90f), 0f, 0f);

            // ---------------- properties window ----------------
            if (wantPanel)
            {
                // which part of the screen image is the window
                float u0 = x0 / sw, u1 = x1 / sw, v0 = y0 / sh, v1 = y1 / sh;
                // (set on the mesh itself: the game's unlit shader ignores the material's texture scale/offset)
                float fa = _cfgWmFlip.Value ? 1f - v0 : v0;
                float fb = _cfgWmFlip.Value ? 1f - v1 : v1;
                _wmUv[0] = new Vector2(u0, fa); _wmUv[1] = new Vector2(u1, fa);   // bottom-left, bottom-right
                _wmUv[2] = new Vector2(u0, fb); _wmUv[3] = new Vector2(u1, fb);   // top-left, top-right
                _wmMesh.uv = _wmUv;
                if (_wmMat.mainTexture != _wmRT) _wmMat.mainTexture = _wmRT;
                PlaceOnDome(_wmGo, camRot, camPos, dist, _cfgWmAng.Value, _cfgWmAz.Value, _cfgWmSize.Value, x1 - x0, y1 - y0, basisPx);
                if (!_wmFirstLogged)
                {
                    _wmFirstLogged = true;
                    LogInfo("[US2Dome] WindowMirror: showing '" + _cfgWmPath.Value + "' px=(" + x0.ToString("F0") + "," + y0.ToString("F0") + " " + (x1 - x0).ToString("F0") + "x" + (y1 - y0).ToString("F0") +
                            ") screen=" + sw.ToString("F0") + "x" + sh.ToString("F0") + " layer=" + _npLayer + " shader=" + _wmMat.shader.name);
                }
            }
            else if (_wmGo.activeSelf) _wmGo.SetActive(false);

            // ---------------- 3D view window ----------------
            if (haveView && _cfgOvShow.Value && _ovGo != null)
            {
                if (direct)
                {
                    // the game's own RenderTexture: the whole texture is the picture
                    bool fl = _cfgOvFlip.Value;
                    float a = fl ? 1f : 0f, b = fl ? 0f : 1f;
                    _ovUv[0] = new Vector2(0f, a); _ovUv[1] = new Vector2(1f, a);
                    _ovUv[2] = new Vector2(0f, b); _ovUv[3] = new Vector2(1f, b);
                    if (_ovMat.mainTexture != vtex) _ovMat.mainTexture = vtex;
                }
                else
                {
                    // fallback: the same screen copy the properties window uses, cropped to the 3D view's rectangle
                    float cx0 = Mathf.Clamp(vr.xMin, 0f, sw), cx1 = Mathf.Clamp(vr.xMax, 0f, sw);
                    float cy0 = Mathf.Clamp(vr.yMin, 0f, sh), cy1 = Mathf.Clamp(vr.yMax, 0f, sh);
                    float u0 = cx0 / sw, u1 = cx1 / sw, v0 = cy0 / sh, v1 = cy1 / sh;
                    float fa = _cfgWmFlip.Value ? 1f - v0 : v0;
                    float fb = _cfgWmFlip.Value ? 1f - v1 : v1;
                    _ovUv[0] = new Vector2(u0, fa); _ovUv[1] = new Vector2(u1, fa);
                    _ovUv[2] = new Vector2(u0, fb); _ovUv[3] = new Vector2(u1, fb);
                    if (_ovMat.mainTexture != _wmRT) _ovMat.mainTexture = _wmRT;
                    vr = Rect.MinMaxRect(cx0, cy0, cx1, cy1);
                }
                _ovMesh.uv = _ovUv;
                float vw = vr.width, vh = vr.height;
                if (direct && vtex != null) { vw = vtex.width; vh = vtex.height; }
                if (vw < 4f || vh < 4f) { HideOv(); }
                else
                {
                    PlaceOnDome(_ovGo, camRot, camPos, dist, _cfgOvAng.Value, _cfgOvAz.Value, _cfgOvSize.Value, vw, vh, Mathf.Max(vw, vh));
                    if (!_ovFirstLogged)
                    {
                        _ovFirstLogged = true;
                        LogInfo("[US2Dome] WindowMirror: 3D view '" + _ovT.name + "' shown separately, " + (direct ? "game texture " + vtex.width + "x" + vtex.height : "screen copy " + vr.width.ToString("F0") + "x" + vr.height.ToString("F0")) + " shader=" + _ovMat.shader.name);
                    }
                }
            }
            else HideOv();
        }

        // Put a quad on the dome at (angle from centre, direction), upright toward the dome centre.
        // sizeDeg = how many degrees the LONGEST side of a window of basisPx pixels covers; this quad is wPx x hPx of that scale.
        void PlaceOnDome(GameObject go, Quaternion camRot, Vector3 camPos, float dist, float angDeg, float azDeg, float sizeDeg, float wPx, float hPx, float basisPx)
        {
            Vector3 zen = camRot * Vector3.forward, rigUp = camRot * Vector3.up, rigRight = camRot * Vector3.right;
            float th = Mathf.Clamp(angDeg, 0f, 89f) * Mathf.Deg2Rad;
            float ph = azDeg * Mathf.Deg2Rad;
            Vector3 dir = (zen * Mathf.Cos(th) + (rigUp * Mathf.Cos(ph) + rigRight * Mathf.Sin(ph)) * Mathf.Sin(th)).normalized;
            Vector3 up, right;
            DomeFrame(dir, zen, rigUp, out up, out right);

            float longest = 2f * dist * Mathf.Tan(Mathf.Clamp(sizeDeg, 1f, 170f) * 0.5f * Mathf.Deg2Rad);
            float k = longest / Mathf.Max(1f, basisPx);
            Transform t = go.transform;
            t.position = camPos + dir * dist;
            t.rotation = Quaternion.LookRotation(dir, up);
            t.localScale = new Vector3(wPx * k, hPx * k, 1f);
            if (!go.activeSelf) go.SetActive(true);
        }

        // Find the game's 3D view (a RawImage called ViewName somewhere inside the properties window) and its picture.
        bool FindObjectView(out Rect vr, out Texture tex)
        {
            vr = new Rect(0f, 0f, 0f, 0f); tex = null;
            if (_wmPanel == null) return false;
            if ((_ovT == null || !_ovT.gameObject.activeInHierarchy) && _frame - _ovCheck >= 10)
            {
                _ovCheck = _frame;
                _ovT = FindDeep(_wmPanel, _cfgOvName.Value, 0);
                _ovRaw = null;
                if (_ovT != null)
                {
                    Component[] cs = _ovT.GetComponents<Component>();
                    for (int i = 0; i < cs.Length; i++)
                        if (cs[i] != null && cs[i].GetType().Name == "RawImage") { _ovRaw = cs[i]; break; }
                    _ovTexPi = (_ovRaw == null) ? null : _ovRaw.GetType().GetProperty("texture", BindingFlags.Public | BindingFlags.Instance);
                }
            }
            if (_ovT == null || !_ovT.gameObject.activeInHierarchy) return false;
            if (!ScreenRectOf(_ovT, out vr)) return false;
            if (vr.width < 8f || vr.height < 8f) return false;
            try { if (_ovTexPi != null) tex = _ovTexPi.GetValue(_ovRaw, null) as Texture; } catch { tex = null; }
            return true;
        }

        static Transform FindDeep(Transform root, string name, int depth)
        {
            if (root == null || depth > 12) return null;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform c = root.GetChild(i);
                if (c.name == name) return c;
            }
            for (int i = 0; i < root.childCount; i++)
            {
                Transform f = FindDeep(root.GetChild(i), name, depth + 1);
                if (f != null) return f;
            }
            return null;
        }

        void EnsureWm(Camera main)
        {
            if (_wmGo != null) return;
            try
            {
                if (_npLayer < 0)
                {
                    _npLayer = 31;
                    for (int l = 31; l >= 8; l--)
                        if (((main.cullingMask >> l) & 1) == 0) { _npLayer = l; break; }
                }
                // a second free layer just for the window quads: extra cameras draw it AFTER everything else (see SyncWindowCam)
                _wmLayer = -1;
                for (int l = 31; l >= 8; l--)
                    if (l != _npLayer && ((main.cullingMask >> l) & 1) == 0) { _wmLayer = l; break; }
                Shader sh = Shader.Find("Unlit/Texture");
                if (sh == null) sh = Shader.Find("Sprites/Default");
                if (sh == null) sh = Shader.Find("UI/Default");
                if (sh == null) { _wmBroken = true; LogWarn("[US2Dome] WindowMirror: no unlit texture shader found; window copy disabled."); return; }
                _wmMat = new Material(sh);
                if (_wmMat.HasProperty("_Color")) _wmMat.SetColor("_Color", Color.white);
                if (_wmRT != null) _wmMat.mainTexture = _wmRT;

                _wmMesh = new Mesh();
                _wmMesh.name = "[US2Dome] WindowQuad";
                _wmMesh.vertices = new Vector3[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f) };
                _wmMesh.uv = new Vector2[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) };
                _wmMesh.normals = new Vector3[] { -Vector3.forward, -Vector3.forward, -Vector3.forward, -Vector3.forward };
                _wmMesh.triangles = new int[] { 0, 2, 1, 2, 3, 1 };
                _wmMesh.RecalculateBounds();

                _wmGo = new GameObject("[US2Dome] WindowMirror");
                _wmGo.layer = (_wmLayer >= 0) ? _wmLayer : _npLayer;
                if (_holder != null) _wmGo.transform.SetParent(_holder.transform, false);
                MeshFilter mf = _wmGo.AddComponent<MeshFilter>();
                mf.sharedMesh = _wmMesh;
                MeshRenderer mr = _wmGo.AddComponent<MeshRenderer>();
                mr.sharedMaterial = _wmMat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                _wmGo.SetActive(false);

                // the 3D view quad (own material + mesh so its UVs/texture are independent)
                _ovMat = new Material(sh);
                if (_ovMat.HasProperty("_Color")) _ovMat.SetColor("_Color", Color.white);
                _ovMesh = UnityEngine.Object.Instantiate(_wmMesh);
                _ovMesh.name = "[US2Dome] ObjectViewQuad";
                _ovGo = new GameObject("[US2Dome] ObjectView");
                _ovGo.layer = (_wmLayer >= 0) ? _wmLayer : _npLayer;
                if (_holder != null) _ovGo.transform.SetParent(_holder.transform, false);
                MeshFilter mf2 = _ovGo.AddComponent<MeshFilter>();
                mf2.sharedMesh = _ovMesh;
                MeshRenderer mr2 = _ovGo.AddComponent<MeshRenderer>();
                mr2.sharedMaterial = _ovMat;
                mr2.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr2.receiveShadows = false;
                _ovGo.SetActive(false);

                // the Data Views quad
                _dvMat = new Material(sh);
                if (_dvMat.HasProperty("_Color")) _dvMat.SetColor("_Color", Color.white);
                _dvMesh = UnityEngine.Object.Instantiate(_wmMesh);
                _dvMesh.name = "[US2Dome] DataViewQuad";
                _dvGo = new GameObject("[US2Dome] DataView");
                _dvGo.layer = (_wmLayer >= 0) ? _wmLayer : _npLayer;
                if (_holder != null) _dvGo.transform.SetParent(_holder.transform, false);
                MeshFilter mf3 = _dvGo.AddComponent<MeshFilter>();
                mf3.sharedMesh = _dvMesh;
                MeshRenderer mr3 = _dvGo.AddComponent<MeshRenderer>();
                mr3.sharedMaterial = _dvMat;
                mr3.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr3.receiveShadows = false;
                _dvGo.SetActive(false);
            }
            catch (Exception e)
            {
                _wmBroken = true;
                LogErrorDetail("[US2Dome] WindowMirror: setup failed", e);
            }
        }

        void HideWm()
        {
            _wmNeedGrab = false;
            if (_wmGo != null && _wmGo.activeSelf) _wmGo.SetActive(false);
            HideOv();
        }

        void HideOv()
        {
            if (_ovGo != null && _ovGo.activeSelf) _ovGo.SetActive(false);
        }

        // Runs for the life of the plugin: after the frame (including the game's UI) is finished, copy it into _wmRT.
        IEnumerator WmGrabLoop()
        {
            while (true)
            {
                yield return new WaitForEndOfFrame();
                if (_wmNeedGrab) { _wmNeedGrab = false; WmGrab(); }
            }
        }

        void WmGrab()
        {
            try
            {
                if (!_wmCapTried)
                {
                    _wmCapTried = true;
                    Type tsc = Type.GetType("UnityEngine.ScreenCapture, UnityEngine.ScreenCaptureModule");
                    _wmCapMi = (tsc == null) ? null : tsc.GetMethod("CaptureScreenshotIntoRenderTexture");
                    if (_wmCapMi == null) { _wmBroken = true; LogWarn("[US2Dome] WindowMirror: ScreenCapture.CaptureScreenshotIntoRenderTexture not available; window copy disabled."); return; }
                }
                int w = Mathf.Max(1, Screen.width), h = Mathf.Max(1, Screen.height);
                if (_wmRT == null || _wmRT.width != w || _wmRT.height != h)
                {
                    if (_wmRT != null) { _wmRT.Release(); UnityEngine.Object.Destroy(_wmRT); }
                    _wmRT = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
                    _wmRT.name = "[US2Dome] WindowMirrorRT";
                    _wmRT.filterMode = FilterMode.Bilinear;
                    _wmRT.wrapMode = TextureWrapMode.Clamp;
                    _wmRT.Create();
                    if (_wmMat != null) _wmMat.mainTexture = _wmRT;
                }
                _wmArgs[0] = _wmRT;
                _wmCapMi.Invoke(null, _wmArgs);
                _wmGrabs++;
            }
            catch (Exception e)
            {
                _wmBroken = true;
                LogErrorDetail("[US2Dome] WindowMirror: screen capture failed; window copy disabled", e);
            }
        }

        void DestroyWindowMirror()
        {
            DestroyWindowCams();
            if (_dvGo != null) UnityEngine.Object.Destroy(_dvGo);
            if (_dvMat != null) UnityEngine.Object.Destroy(_dvMat);
            if (_dvMesh != null) UnityEngine.Object.Destroy(_dvMesh);
            _dvGo = null; _dvMat = null; _dvMesh = null; _dvPanel = null;
            if (_ovGo != null) UnityEngine.Object.Destroy(_ovGo);
            if (_ovMat != null) UnityEngine.Object.Destroy(_ovMat);
            if (_ovMesh != null) UnityEngine.Object.Destroy(_ovMesh);
            _ovGo = null; _ovMat = null; _ovMesh = null; _ovT = null; _ovRaw = null;
            if (_wmGo != null) UnityEngine.Object.Destroy(_wmGo);
            if (_wmMat != null) UnityEngine.Object.Destroy(_wmMat);
            if (_wmMesh != null) UnityEngine.Object.Destroy(_wmMesh);
            if (_wmRT != null) { _wmRT.Release(); UnityEngine.Object.Destroy(_wmRT); }
            _wmGo = null; _wmMat = null; _wmMesh = null; _wmRT = null;
        }
    }
}
