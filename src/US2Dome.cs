// US2Dome.cs
// BepInEx 5 plugin for Universe Sandbox 2 (Windows, URP 14).
//
// Renders the game scene from 5 face cameras (F/R/L/U/D) into a 180-degree
// equidistant "planetarium dome" (fisheye) image -- a circle -- and can show it:
//   * as an in-game overlay, or
//   * full-screen (for projecting the game window directly), or
//   * in a SEPARATE OS window (recommended: game window stays normal, a second
//     window shows the live dome).
//
// Robustness notes:
//   * Awake() only binds config (known-safe). All Unity object creation is in
//     EnsureInitialized(), which is also (re)called on the first Update(). If
//     anything throws, it is caught and logged to BOTH the BepInEx log and a
//     plain file (BepInEx/US2Dome.log) so it is never invisible.
//   * GUI styles are created lazily (GUI.skin may be null during Awake in
//     standalone builds -- a classic cause of a silent plugin death).
//
using System;
using System.IO;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace US2Dome
{
    [BepInPlugin("f3a6b8c9-1d2e-4f5a-8b9c-0d1e2f3a4b5c", "US2Dome", "1.6.6")]
    public partial class US2Dome : BaseUnityPlugin
    {
        // ---- Config entries ----
        ConfigFile          _cfgFile;     // BepInEx/config/US2Dome.cfg (own file, not <GUID>.cfg)
        ConfigEntry<float>  _cfgZoom;
        ConfigEntry<bool>   _cfgEnabled;
        ConfigEntry<int>    _cfgDomeRes;
        ConfigEntry<int>    _cfgFaceRes;
        ConfigEntry<int>    _cfgEveryN;
        ConfigEntry<bool>   _cfgOverlay;
        ConfigEntry<float>  _cfgOverlayScale;
        ConfigEntry<float>  _cfgMvX, _cfgMvY;      // mini-view position: 0..1 across the free screen area
        ConfigEntry<bool>   _cfgFullDome;
        ConfigEntry<bool>   _cfgWindow;
        ConfigEntry<int>    _cfgWindowRes;
        ConfigEntry<bool>   _cfgWindowTopmost;
        ConfigEntry<bool>   _cfgWindowAuto;
        ConfigEntry<float>  _cfgTilt;
        ConfigEntry<bool>   _cfgWindowFull;
        ConfigEntry<bool>   _cfgMips;
        ConfigEntry<int>    _cfgMsaa;
        ConfigEntry<bool>   _cfgFaceShadows;
        ConfigEntry<bool>   _cfgFacePost;

        // ---- Runtime state ----
        GameObject          _holder;
        Camera[]            _faceCams = new Camera[5];
        RenderTexture[]     _faceRTs  = new RenderTexture[5];
        DomeGpu             _gpu;         // v1.5.0: GPU remap + async readback
        Texture2D           _saveTex;     // only used by Save PNG
        GUIStyle            _labelStyle;
        GUIStyle            _btnStyle;
        GUIStyle            _fullBtnStyle;

        bool                _initDone = false;
        bool                _initFailed = false;
        int                 _frame;
        bool                _willCapture;
        bool                _diagDone = false;
        string              _lastSaveMsg = "";
        float               _lastSaveTime = -999f;

        // ---- Second window (dome output) ----
        DomeWindow          _domeWindow;
        bool                _windowDisabledByClose = false;
        int                 _windowRes;

        // ---- v1.5.0 GPU path ----
        byte[]              _winBytes;    // window DIB bytes (rtRes*rtRes*4, B,G,R,A), flipped to top-down
        int                 _rtResCfg;    // dome RT size chosen by config at init (the live size _rtRes may follow the window)
        int                 _autoPending; float _autoSince; bool _dibFailLogged;
        int                 _rtRes;       // dome RT size (== window size when the 2nd window is on)
        bool                _subscribed;

        int _domeRes;
        int _faceRes;
        string _logFile = null;
        // "Make log file" (Shift+F2 menu): off by default so US2Dome.log does not grow with every run.
        // Static + volatile because the worker thread logs too.
        internal static volatile bool LogFileOn = false;
        ConfigEntry<bool> _cfgLogFile;

        // ---- counters (throttled logging) ----
        int _dbgCapture = 0;
        int _dbgOnGui = 0;     // OnGUI() invocations (alive-beat)   // captures scheduled (Update)
        int _dbgDraw = 0;      // GPU dome draws
        int _dbgReadback = 0;  // async readbacks delivered
        int _dbgReadbackSkip = 0; // readback skipped (previous still in flight)
        int _dbgPush = 0;      // PushBytes calls
        double _drawMsAccum = 0;
        int _dbgGated = 0;     // v1.6.3: frames where face rendering was skipped (no free readback slot)

        // =========================================================================
        //  LOGGING  (BepInEx logger + plain file so it is ALWAYS visible)
        // =========================================================================
        // Thread-safe lock: the worker thread ALSO logs (DomeWorker/DomeRemap),
        // and File.AppendAllText is NOT safe for concurrent writers.
        static readonly object _logLock = new object();

        void LogInfo(string msg)
        {
            Logger.LogInfo(msg);
            LogToFile("INFO", msg);
        }
        void LogWarn(string msg)
        {
            Logger.LogWarning(msg);
            LogToFile("WARN", msg);
        }
        void LogError(string msg)
        {
            Logger.LogError(msg);
            LogToFile("ERROR", msg);
        }
        // Throttled, NON-DEAD diagnostic logger (safe to call from ANY thread).
        // The CALLER decides when to log (e.g. "first + every Nth"); this just
        // writes the line at DBG level so we always see the pipeline flow without
        // flooding. (The old `_frame % 300 == 0` gates were dead code -- they
        //  never aligned with the `_frame % 3 == 0` capture cadence, so nothing
        //  ever printed.)
        void Dbg(string msg) { LogToFile("DBG", msg); }

        void LogToFile(string lvl, string msg)
        {
            if (!LogFileOn) return;
            try
            {
                lock (_logLock)
                {
                    if (string.IsNullOrEmpty(_logFile))
                    {
                        string dir = Path.Combine(Application.dataPath, "..", "BepInEx");
                        if (Directory.Exists(dir))
                            _logFile = Path.Combine(dir, "US2Dome.log");
                        else
                            _logFile = Path.Combine(Application.dataPath, "US2Dome.log");
                    }
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + lvl + "] " + msg + Environment.NewLine;
                    File.AppendAllText(_logFile, line);
                }
            }
            catch { /* never let logging throw */ }
        }

        // =========================================================================
        //  STRUCT <-> BYTE BRIDGE  (v1.4.1 facade-safe memcpy)
        //
        //  The game's netstandard.dll facade STRIPS OUT the generic
        //  Marshal.Copy(Array, int, Array, int, int) and Buffer.BlockCopy does
        //  NOT accept Color32[] ("Object must be an array of primitives" because
        //  Color32.IsPrimitive == false). That threw on EVERY capture inside
        //  ReadFaces(), so the worker never started and the 2nd window never
        //  updated (just its initial white fill).
        //
        //  The facade DOES expose:  GCHandle.Alloc(obj, Pinned),
        //  GCHandle.AddrOfPinnedObject(), GCHandle.Free(), and the 4-arg
        //  Marshal.Copy(byte[], int, IntPtr, int)  /  Marshal.Copy(IntPtr, byte[], int, int).
        //  Pinning a Color32[] and copying its raw memory is a plain memcpy that
        //  handles the struct array correctly. Both directions verified under
        //  mono against the game's exact netstandard facade.
        // =========================================================================
        static void CopyC32ToBytes(Color32[] src, int srcStart, byte[] dst, int dstStart, int count)
        {
            GCHandle h = GCHandle.Alloc(src, GCHandleType.Pinned);
            try { Marshal.Copy(h.AddrOfPinnedObject(), dst, dstStart, count * 4); }
            finally { h.Free(); }
        }
        static void CopyBytesToC32(byte[] src, int srcStart, Color32[] dst, int dstStart, int count)
        {
            GCHandle h = GCHandle.Alloc(dst, GCHandleType.Pinned);
            try { Marshal.Copy(src, srcStart, h.AddrOfPinnedObject(), count * 4); }
            finally { h.Free(); }
        }

        // Full exception detail (message + type + stack), multi-line, logged once.
        void LogErrorDetail(string tag, Exception e)
        {
            LogToFile("ERROR", tag + " :: " + e.GetType().FullName + " : " + e.Message);
            if (e.StackTrace != null)
                LogToFile("ERROR", "  stack: " + e.StackTrace.Replace("\n", "\n  "));
        }

        // =========================================================================
        //  AWAKE  -- config only (known-safe). No Unity objects, no GUI.skin.
        // =========================================================================
        void Awake()
        {
            LogInfo("[US2Dome] Initializing (URP 5-face dome capture)...");

            // PROVENANCE: print the EXACT DLL that is running, so we can always
            // tell a stale build from the current one.
            try
            {
                string asm = GetType().Assembly.Location;
                LogInfo("[US2Dome] Running DLL: " + asm);
                try { LogInfo("[US2Dome] DLL file time: " + File.GetLastWriteTimeUtc(asm).ToString("yyyy-MM-dd HH:mm:ss") + " UTC, " + (new FileInfo(asm).Length) + " bytes"); }
                catch (Exception e2) { LogInfo("[US2Dome] DLL file time: (unknown: " + e2.Message + ")"); }
            }
            catch (Exception e) { LogWarn("[US2Dome] Could not read DLL path: " + e.Message); }

            // Defensive: keep the BepInEx plugin object alive across scene reloads
            // (some games do aggressive scene cleanup right after load).
            try { UnityEngine.Object.DontDestroyOnLoad(this.gameObject); }
            catch (Exception e) { LogWarn("[US2Dome] DontDestroyOnLoad(self) failed: " + e.Message); }

            try
            {
                _cfgFile = new ConfigFile(Path.Combine(Paths.ConfigPath, "US2Dome.cfg"), true, new BepInPlugin("f3a6b8c9-1d2e-4f5a-8b9c-0d1e2f3a4b5c", "US2Dome", "1.6.6"));
                _cfgLogFile = _cfgFile.Bind("Logging", "MakeLogFile", false,
                    "Write BepInEx/US2Dome.log. Off by default so the file does not grow with every run. Turn it on (Shift+F2 menu) when you need the log, e.g. for the F3/F4 window diagnostics or to report a problem.");
                LogFileOn = _cfgLogFile.Value;
                _cfgEnabled        = _cfgFile.Bind("General", "Enabled", true, "Enable the dome renderer.");
                _cfgDomeRes        = _cfgFile.Bind("General", "DomeResolution", 2048, "Dome texture size for the in-game overlay/PNG when the second window is OFF. (With the second window on, SecondWindowResolution is the dome size.)");
                _cfgFaceRes        = _cfgFile.Bind("General", "FaceResolution", 1024, "Per-face capture resolution (power of two).");
                _cfgEveryN         = _cfgFile.Bind("General", "CaptureEveryNFrames", 1, "Capture every N frames (1 = every frame).");
                // Mini-view: a small copy of the dome on the game screen. Independent of the second window.
                _cfgOverlay        = _cfgFile.Bind("MiniView", "Show", false, "Show a small mini-view of the dome on the game screen (works with or without the second window).");
                _cfgOverlayScale   = _cfgFile.Bind("MiniView", "Size", 0.30f, "Mini-view size as a fraction of the screen height (0.05 - 1.0).");
                _cfgMvX            = _cfgFile.Bind("MiniView", "PositionX", 1.0f, "Mini-view horizontal position: 0 = left edge of the screen, 1 = right edge.");
                _cfgMvY            = _cfgFile.Bind("MiniView", "PositionY", 1.0f, "Mini-view vertical position: 0 = top of the screen, 1 = bottom.");
                _cfgFullDome       = _cfgFile.Bind("General", "FullDomeMode", false,
                    "Render the dome FULL-SCREEN (circular, black background) to project the game window directly.");
                _cfgWindow         = _cfgFile.Bind("General", "SecondWindow", true,
                    "Show the live dome in a SEPARATE OS window (recommended). Game window stays normal.");
                _cfgWindowRes      = _cfgFile.Bind("General", "SecondWindowResolution", 1024, "Second window dome size in pixels (any value). For the sharpest image set it to your projector/monitor HEIGHT (e.g. 1080 or 2160) and use fullscreen: then no rescaling happens.");
                _cfgWindowAuto = _cfgFile.Bind("General", "SecondWindowAutoSize", true,
                    "Keep the dome image the same size as the dome window's drawing area (follows resizing / fullscreen on any monitor), so the window is always drawn 1:1. " +
                    "When the sizes differ, Windows rescales the image on the CPU every frame (slow: halves the frame rate on big windows). SecondWindowResolution is then only the starting size.");
                _cfgWindowTopmost  = _cfgFile.Bind("General", "SecondWindowTopmost", true, "Keep the dome window above other windows.");

                _cfgWindowFull = _cfgFile.Bind("General", "SecondWindowFullscreen", false,
                    "Start the dome window borderless-fullscreen on the monitor it opens on. (Drag it to the projector first and press F11 instead if you prefer.) " +
                    "In the dome window: F11 or double-click toggles fullscreen, Esc leaves it.");
                _cfgMips = _cfgFile.Bind("Quality", "FaceMipmaps", true,
                    "Anti-aliasing: build mipmaps of the 5 face renders and sample them trilinearly when warping to the dome. " +
                    "Removes most stair-stepping on thin lines and flicker on tiny stars. Cheap. Works best with FaceResolution >= DomeResolution.");
                _cfgMsaa = _cfgFile.Bind("Quality", "FaceMSAA", 4,
                    "Anti-aliasing: MSAA samples for the face cameras (1 = off, 2, 4, 8). Only takes effect if MSAA is enabled in the game's URP quality settings; harmless otherwise.");
                _cfgFaceShadows = _cfgFile.Bind("Quality", "FaceShadows", true,
                    "Performance: render shadows in the 5 face cameras. Each face camera otherwise renders its own shadow maps, so this is 5x the shadow cost. " +
                    "Set false to try for a faster dome (shadows will be missing in the dome image). Restart the game after changing.");
                _cfgFacePost = _cfgFile.Bind("Quality", "FacePostProcessing", true,
                    "Performance: run URP post-processing (bloom, tonemapping...) on the 5 face cameras. Set false to try for a faster dome " +
                    "(the dome image will look different from the game window). Restart the game after changing.");
                _cfgTilt = _cfgFile.Bind("General", "DomeTiltDegrees", 35f,
                    "Tilts the whole dome view relative to the game camera, in degrees. The game camera's forward direction " +
                    "(e.g. the planet you're looking at) appears this many degrees away from the dome CENTER (zenith), toward the bottom of the dome image. " +
                    "0 = game forward at the dome center (overhead). 50 = default. 90 = game forward on the dome rim (horizon). Negative values tilt the other way (toward the top of the image). Restart the game after changing.");

                _cfgZoom = _cfgFile.Bind("General", "Zoom", 1.0f,
                    "Zoom of the dome view. 1.0 = true 180-degree dome (the whole half-sky fits the circle). 2.0 = the dome circle shows only the central 90 degrees " +
                    "(everything looks 2x bigger), 3.0 = central 60 degrees, etc. The dome stays a perfect circle; the rim just corresponds to a smaller angle. " +
                    "Allowed range 1.0 - 8.0. Note: values above 1.0 no longer match a real dome's geometry (it is a magnified view). Restart the game after changing.");

                _cfgHotkey = _cfgFile.Bind("Controls", "SettingsHotkey", "F2",
                    "Key that opens/closes the in-game settings panel (a Unity KeyCode name, e.g. F2, F9, Insert, Home). Can also be changed inside the panel.");
                ParseHotkey();
                try { InputBlocker.Install(LogInfo); } catch (Exception ie) { LogWarn("[US2Dome] InputBlocker install threw: " + ie.Message); }
                // The panel edits values live (sliders): write the file when the panel is closed / a mouse button is released, not on every change.
                _cfgFile.SaveOnConfigSet = false;
                _cfgFile.Save();

                _domeRes  = Mathf.Max(64, Mathf.NextPowerOfTwo(_cfgDomeRes.Value));
                _faceRes  = Mathf.Max(64, Mathf.NextPowerOfTwo(_cfgFaceRes.Value));
                _windowRes = Mathf.Clamp(_cfgWindowRes.Value, 256, 8192);   // v1.5.4: any size (no longer forced to a power of two)

                LogInfo("[US2Dome] Config bound OK: dome=" + _domeRes + " face=" + _faceRes +
                        " window=" + (_cfgWindow.Value ? ("on @" + _windowRes) : "off") +
                        " overlay=" + _cfgOverlay.Value + " fullDome=" + _cfgFullDome.Value +
                        " everyN=" + _cfgEveryN.Value + " tilt=" + _cfgTilt.Value + " zoom=" + _cfgZoom.Value + " mips=" + _cfgMips.Value + " msaa=" + _cfgMsaa.Value);
            }
            catch (Exception e)
            {
                LogError("[US2Dome] CONFIG BIND FAILED: " + e);
            }
        }

        // =========================================================================
        //  INIT  -- Unity objects + textures. Retriable; fully logged.
        // =========================================================================
        void EnsureInitialized()
        {
            if (_initDone || _initFailed) return;
            try
            {
                // Hidden holder GameObject.
                if (_holder == null)
                {
                    _holder = new GameObject("[US2Dome] Capture");
                    UnityEngine.Object.DontDestroyOnLoad(_holder);
                }

                // 5 face cameras (F, R, L, U, D). URP renders enabled cameras that
                // have targetTexture set to their RT BEFORE the screen camera.
                for (int i = 0; i < 5; i++)
                {
                    if (_faceCams[i] != null || _faceRTs[i] != null) continue;
                    GameObject go = new GameObject("[US2Dome] Face" + i);
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    Camera c = go.AddComponent<Camera>();
                    c.enabled = false;
                    c.clearFlags = CameraClearFlags.Skybox;
                    c.backgroundColor = Color.black;
                    c.fieldOfView = DomeGpu.FaceFov;   // slightly over 90: see DomeGpu.FaceTanHalf (seamless face borders)
                    c.aspect = 1f;
                    c.nearClipPlane = 0.1f;
                    c.farClipPlane = 1e9f;
                    c.allowHDR = false;
                    c.allowMSAA = _cfgMsaa.Value > 1;
                    c.cullingMask = ~0;
                    c.depth = 50 + i;
                    TuneFaceCamera(c, i);
                    _faceCams[i] = c;

                    // If only a BLENDED shader is available in the build, face alpha would
                    // make sky pixels vanish; an alpha-less RT format reads back alpha=1.
                    RenderTextureFormat ff = RenderTextureFormat.ARGB32;
                    if (Shader.Find("Unlit/Texture") == null && SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RGB111110Float))
                        ff = RenderTextureFormat.RGB111110Float;
                    RenderTexture rt = new RenderTexture(_faceRes, _faceRes, 24, ff);
                    rt.filterMode = _cfgMips.Value ? FilterMode.Trilinear : FilterMode.Bilinear;
                    rt.wrapMode = TextureWrapMode.Clamp;
                    rt.useMipMap = _cfgMips.Value;
                    rt.autoGenerateMips = false;          // generated explicitly by DomeGpu.Draw
                    rt.anisoLevel = 4;
                    int ms = _cfgMsaa.Value; rt.antiAliasing = (ms >= 8) ? 8 : (ms >= 4) ? 4 : (ms >= 2) ? 2 : 1;
                    if (!rt.Create())
                    {
                        LogError("[US2Dome] RenderTexture.Create failed for face " + i);
                        UnityEngine.Object.Destroy(rt);
                        continue;
                    }
                    c.targetTexture = rt;
                    _faceRTs[i] = rt;
                }

                // GPU dome warp (mesh + dome RT). With the 2nd window on, the dome RT
                // is exactly the window's DIB size so the readback maps 1:1.
                _rtRes = _cfgWindow.Value ? _windowRes : _domeRes;
                _rtResCfg = _rtRes;
                if (_gpu == null)
                {
                    _gpu = new DomeGpu(_faceRTs, _rtRes, 256, _cfgMips.Value, Mathf.Clamp(_cfgZoom.Value, 1f, 8f));
                    _gpu.OnDebug = (m) => { Dbg(m); };
                    if (!_gpu.Ready)
                    {
                        string err = _gpu.Error;
                        _gpu.Dispose(); _gpu = null;
                        throw new Exception("DomeGpu init failed: " + err);
                    }
                    LogInfo("[US2Dome] GPU remap ready (shader '" + _gpu.ShaderName + "', dome RT " + _rtRes + "px).");
                }
                _appliedZoom = Mathf.Clamp(_cfgZoom.Value, 1f, 8f);
                _appliedMips = _cfgMips.Value;
                _appliedMsaa = NormMsaa(_cfgMsaa.Value);

                // Draw the dome right after the LAST face camera finishes rendering.
                if (!_subscribed)
                {
                    RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
                    _subscribed = true;
                }

                _initDone = true;
                _initFailed = false;
                LogInfo("[US2Dome] Initialized: " + CountFaceRTs() + "/5 face RTs, dome=" +
                        _rtRes + "px, face=" + _faceRes + "px.");
            }
            catch (Exception e)
            {
                _initFailed = true;   // do NOT hammer retry every frame
                LogError("[US2Dome] INIT FAILED (will not retry this session): " + e);
            }
        }

        int CountFaceRTs()
        {
            int n = 0;
            for (int i = 0; i < 5; i++) if (_faceRTs[i] != null) n++;
            return n;
        }

        // =========================================================================
        //  UPDATE
        // =========================================================================
        void Update()
        {
            // First-frame diagnostic (always, even if init failed).
            if (!_diagDone)
            {
                _diagDone = true;
                try
                {
                    Camera mcam = FindMainCamera();
                    LogInfo("[US2Dome] Diagnostics: main camera = " +
                        (mcam != null ? (mcam.name + " (depth " + mcam.depth + ")") : "<none>") +
                        ", enabled cameras = " + CountEnabledCameras() +
                        ", init=" + (_initDone ? "done" : (_initFailed ? "FAILED" : "pending")));
                }
                catch (Exception e) { LogError("[US2Dome] Diagnostics failed: " + e); }
            }

            TickFrameDebug();
            PresentQueuedWindowFrame();
            EnsureInitialized();
            TickSettings();
            TickUiDiag();

            if (!_cfgEnabled.Value)
            {
                for (int i = 0; i < 5; i++) if (_faceCams[i] != null) _faceCams[i].enabled = false; DisableWindowCams();
                _willCapture = false;
                return;
            }

            Camera main = FindMainCamera();
            if (main == null)
            {
                for (int i = 0; i < 5; i++) if (_faceCams[i] != null) _faceCams[i].enabled = false; DisableWindowCams();
                _willCapture = false;
                if (_frame % 60 == 0) LogWarn("[US2Dome] No main camera found; skipping capture.");
                _frame++;
                return;
            }

            int n = Mathf.Max(1, _cfgEveryN.Value);
            bool capture = (_frame % n == 0);
            _frame++;

            // v1.6.3: when the dome is only consumed through the async readback (2nd window, overlay hidden),
            // rendering the 5 face cameras while every readback slot is busy is pure waste: the result
            // can never be shown. Skip those frames (saves most of the face-render cost when the readback lags).
            if (capture && _gpu != null && _gpu.Ready && SystemInfo.supportsAsyncGPUReadback && WindowOnlyOutput() && !_gpu.ReadbackSlotFree)
            {
                capture = false;
                _dbgGated++;
            }

            if (!capture)
            {
                for (int i = 0; i < 5; i++) if (_faceCams[i] != null) _faceCams[i].enabled = false; DisableWindowCams();
                _willCapture = false;
                return;
            }

            // Position the 5 face cameras to follow the main camera.
            Vector3    camPos = main.transform.position;
            double fdT0 = _fdActive ? FdNowMs() : 0.0;
            try { UpdateNameplates(main, camPos); }
            catch (Exception e) { if (_frame % 600 == 1) LogErrorDetail("[US2Dome] UpdateNameplates THREW", e); }
            double fdT1 = _fdActive ? FdNowMs() : 0.0;
            try { UpdateWindowMirror(main, camPos); }
            catch (Exception e) { if (_frame % 600 == 1) LogErrorDetail("[US2Dome] UpdateWindowMirror THREW", e); }
            if (_fdActive) { _fdNpMs += fdT1 - fdT0; _fdMirMs += FdNowMs() - fdT1; }
            // Dome tilt: rotate the whole 5-camera rig up by DomeTiltDegrees about the
            // camera's right axis, so the game's forward direction lands that many degrees
            // away from the dome center (zenith).
            Quaternion camRot = main.transform.rotation * Quaternion.Euler(-Mathf.Clamp(_cfgTilt.Value, -90f, 90f), 0f, 0f);
            CameraClearFlags cf = main.clearFlags;
            Color bc = main.backgroundColor;

            try
            {
            for (int i = 0; i < 5; i++)
            {
                if (_faceCams[i] == null || _faceRTs[i] == null) continue;
                Vector3 fwd = camRot * ToVector3(DomeMath.Fwd[i]);
                Vector3 up  = camRot * ToVector3(DomeMath.Up[i]);
                _faceCams[i].transform.position = camPos;
                _faceCams[i].transform.rotation = Quaternion.LookRotation(fwd, up);
                _faceCams[i].clearFlags = (cf == CameraClearFlags.Skybox || cf == CameraClearFlags.SolidColor) ? cf : CameraClearFlags.Skybox;
                SyncSkybox(main, _faceCams[i]);
                _faceCams[i].backgroundColor = new Color(bc.r, bc.g, bc.b, 1f);
                SyncWindowCam(i, _faceCams[i]);   // window quads on top of everything (own camera)
                _faceCams[i].enabled = true;   // URP renders to its RT this frame
            }
            }
            catch (Exception e)
            {
                // If camera placement throws (null cam/RT, bad LookRotation...),
                // _willCapture must NOT be set and we must SEE it in the log.
                for (int i = 0; i < 5; i++) if (_faceCams[i] != null) _faceCams[i].enabled = false; DisableWindowCams();
                _willCapture = false;
                LogErrorDetail("[US2Dome] FACE CAMERA PLACEMENT THREW (capture skipped)", e);
                return;
            }
            _willCapture = true;
            if (_frame == 150 || _frame == 1500) LogSkyDiagnostics(main);
            // CAPTURE DECISION (stage 1): this frame renders the 5 face cameras
            // and OnGUI reads them back. Log the first and every 60th so a live
            // run proves captures are actually firing (previously dead code).
            int cap = ++_dbgCapture;
            if (cap == 1 || cap % 60 == 0)
                Dbg("[US2Dome] CAPTURE #" + cap + " scheduled @frame=" +
                    _frame + " (5 face RTs render now; GPU dome draw after the last one)");
        }

        // True when nothing but the 2nd window reads the dome RT (so frames not headed for a readback are useless).
        bool WindowOnlyOutput()
        {
            return _domeWindow != null && _domeWindow.Ready && !_cfgFullDome.Value && !_cfgOverlay.Value;
        }

        // v1.6.3: optional per-face-camera URP savings, applied by reflection so build.sh needs no new reference.
        // Defaults keep the old look; set Quality/FaceShadows or Quality/FacePostProcessing to false to test.
        void TuneFaceCamera(Camera c, int idx)
        {
            if (_cfgFaceShadows.Value && _cfgFacePost.Value) return;
            try
            {
                Type t = Type.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData, Unity.RenderPipelines.Universal.Runtime");
                if (t == null) { if (idx == 0) LogWarn("[US2Dome] UniversalAdditionalCameraData not found; FaceShadows/FacePostProcessing ignored."); return; }
                Component d = c.gameObject.GetComponent(t);
                if (d == null) d = c.gameObject.AddComponent(t);
                string r = "";
                if (!_cfgFaceShadows.Value) r += SetBoolProp(t, d, "renderShadows", false);
                if (!_cfgFacePost.Value)    r += SetBoolProp(t, d, "renderPostProcessing", false);
                if (idx == 0) LogInfo("[US2Dome] Face camera URP tuning:" + r);
            }
            catch (Exception e) { LogWarn("[US2Dome] TuneFaceCamera failed: " + e.Message); }
        }

        static string SetBoolProp(Type t, object o, string name, bool v)
        {
            System.Reflection.PropertyInfo pi = t.GetProperty(name);
            if (pi == null || !pi.CanWrite) return " " + name + "=<missing>";
            pi.SetValue(o, v, null);
            return " " + name + "=" + v;
        }

        // The game may give its main camera a per-camera Skybox component (overrides
        // RenderSettings.skybox). The face cameras are new objects and don't have it,
        // so they'd render the global/default (black) sky. Mirror it.
        void SyncSkybox(Camera main, Camera face)
        {
            try
            {
                Skybox ms = main.GetComponent<Skybox>();
                Skybox fs = face.GetComponent<Skybox>();
                Material mat = (ms != null && ms.enabled) ? ms.material : null;
                if (mat != null)
                {
                    if (fs == null) fs = face.gameObject.AddComponent<Skybox>();
                    if (fs.material != mat) fs.material = mat;
                    fs.enabled = true;
                }
                else if (fs != null && fs.enabled) fs.enabled = false;
            }
            catch (Exception e) { if (_frame % 600 == 1) LogWarn("[US2Dome] SyncSkybox: " + e.Message); }
        }

        // One-shot scene diagnostics so a black sky can be explained from the log.
        void LogSkyDiagnostics(Camera main)
        {
            try
            {
                Skybox ms = main.GetComponent<Skybox>();
                LogInfo("[US2Dome] SKYDIAG main='" + main.name + "' clear=" + main.clearFlags + " bg=" + main.backgroundColor +
                        " cull=" + main.cullingMask + " hdr=" + main.allowHDR +
                        " skyboxComponent=" + (ms == null ? "none" : (ms.material == null ? "no material" : ms.material.name + " [" + ms.material.shader.name + "]")) +
                        " RenderSettings.skybox=" + (RenderSettings.skybox == null ? "null" : RenderSettings.skybox.name + " [" + RenderSettings.skybox.shader.name + "]"));
                Camera[] all = Camera.allCameras;
                for (int i = 0; i < all.Length; i++)
                {
                    Camera c = all[i];
                    if (c == null || IsOurs(c)) continue;
                    LogInfo("[US2Dome] SKYDIAG cam '" + c.name + "' depth=" + c.depth + " enabled=" + c.enabled + " clear=" + c.clearFlags +
                            " cull=" + c.cullingMask + " rt=" + (c.targetTexture != null) + " display=" + c.targetDisplay);
                }
            }
            catch (Exception e) { LogWarn("[US2Dome] SKYDIAG failed: " + e.Message); }
        }

        int CountEnabledCameras()
        {
            Camera[] all = Camera.allCameras;
            if (all == null) return 0;
            int n = 0;
            for (int i = 0; i < all.Length; i++) if (all[i] != null && all[i].enabled) n++;
            return n;
        }

        static Vector3 ToVector3(V3 v) { return new Vector3(v.X, v.Y, v.Z); }

        Camera FindMainCamera()
        {
            Camera c = Camera.main;
            if (c != null && c.enabled) return c;
            c = Camera.current;
            if (c != null && c.enabled) return c;

            Camera[] all = Camera.allCameras;
            if (all == null) return null;
            Camera best = null;
            float bestDepth = float.NegativeInfinity;
            for (int i = 0; i < all.Length; i++)
            {
                Camera cam = all[i];
                if (cam == null || IsOurs(cam) || !cam.enabled || cam.targetTexture != null) continue;
                if (cam.depth > bestDepth) { bestDepth = cam.depth; best = cam; }
            }
            if (best != null) return best;
            for (int i = 0; i < all.Length; i++)
            {
                Camera cam = all[i];
                if (cam == null || IsOurs(cam) || !cam.enabled) continue;
                return cam;
            }
            return null;
        }

        bool IsOurs(Camera cam)
        {
            for (int k = 0; k < 5; k++) if (_faceCams[k] == cam) return true;
            return false;
        }

        // =========================================================================
        //  ONGUI  -- capture readback + remap (after render), then display.
        // =========================================================================
        void OnGUI()
        {
            double fdGui0 = _fdActive ? FdNowMs() : 0.0;
            Event ev = Event.current;
            EventType t0 = (ev != null) ? ev.type : EventType.Layout;     // the event as it ARRIVED (the panel may mark it Used)
            Vector2 mp0 = (ev != null) ? ev.mousePosition : Vector2.zero;
            bool mouseEvt = (ev != null) && ev.isMouse;

            HandleSettingsInput();   // hotkey / key capture (US2DomeSettings.cs)

            // Mouse events on the panel must not also hit the overlay's Save PNG button underneath it.
            if (!(_panelOpen && mouseEvt && PanelContainsPoint(mp0)))
                DrawDomeGui();       // dome overlay / full-dome / second window upkeep

            UpdateMouseGate(t0, mp0);
            DrawSettingsPanel();     // in-game settings panel (on top)
            EndSliderDragOnMouseUp(t0);
            if (_cfgDirty && t0 == EventType.MouseUp) SaveCfgNow();
            if (_fdActive) _fdGuiMs += FdNowMs() - fdGui0;
            DrawFrameDebugGui();
        }

        void DrawDomeGui()
        {
            // ALIVE-BEAT: prove OnGUI() is actually invoked by this engine build.
            // If CAPTURE #N logs appear but NEVER a READBACK, OnGUI is the suspect;
            // if this line also never appears, OnGUI is not being called at all.
            int og = ++_dbgOnGui;
            if (og == 1 || og % 180 == 0)
                Dbg("[US2Dome] OnGUI alive (call #" + og + ", initDone=" + _initDone + ")");

            // ---- Second window (dome output) ----
            UpdateSecondWindow();

            if (!_cfgEnabled.Value) return;   // renderer switched off: draw nothing (no stale frame)
            if (_gpu == null || _gpu.DomeRT == null) return;

            // ---- FULL-DOME MODE ----
            if (_cfgFullDome.Value)
            {
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.blackTexture);
                float side = Mathf.Min(Screen.width, Screen.height);
                float cx = (Screen.width - side) * 0.5f;
                float cy = (Screen.height - side) * 0.5f;
                GUI.DrawTexture(new Rect(cx, cy, side, side), _gpu.DomeRT);

                float bw = 220f;
                Rect br = new Rect((Screen.width - bw) * 0.5f, Screen.height - 60f, bw, 36f);
                if (GUI.Button(br, "Save PNG", GetFullBtnStyle()))
                    SavePng(cx, cy, side);
                if (Time.time - _lastSaveTime < 4f)
                    GUI.Label(new Rect(cx, br.y - 20f, side, 18f), _lastSaveMsg, GetLabelStyle());
                return;
            }

            // ---- MINI-VIEW ----
            // A small copy of the dome on the game screen. Works with or without the second window.
            if (!_cfgOverlay.Value) return;
            float size = Screen.height * Mathf.Clamp(_cfgOverlayScale.Value, 0.05f, 1f);
            size = Mathf.Min(size, Mathf.Min(Screen.width, Screen.height));
            const float margin = 10f;
            float fx = Mathf.Clamp01(_cfgMvX.Value), fy = Mathf.Clamp01(_cfgMvY.Value);
            float x = margin + Mathf.Max(0f, Screen.width  - size - 2f * margin) * fx;
            float y = margin + Mathf.Max(0f, Screen.height - size - 2f * margin) * fy;
            // The dome texture is drawn upside-down by IMGUI, so flip it vertically (left/right stays as it is).
            GUI.DrawTextureWithTexCoords(new Rect(x, y, size, size), _gpu.DomeRT, new Rect(0f, 1f, 1f, -1f));
        }

        // ---- Lazy GUI styles (GUI.skin may be null in Awake) ----
        GUIStyle GetLabelStyle()
        {
            if (_labelStyle == null)
            {
                _labelStyle = new GUIStyle();
                _labelStyle.fontSize = 14;
                _labelStyle.normal.textColor = new Color(1f, 1f, 1f, 0.9f);
                _labelStyle.alignment = TextAnchor.UpperCenter;
            }
            return _labelStyle;
        }
        GUIStyle GetBtnStyle()
        {
            if (_btnStyle == null)
            {
                _btnStyle = new GUIStyle(GUI.skin.button);
                _btnStyle.fontSize = 12;
                _btnStyle.alignment = TextAnchor.MiddleCenter;
            }
            return _btnStyle;
        }
        GUIStyle GetFullBtnStyle()
        {
            if (_fullBtnStyle == null)
            {
                _fullBtnStyle = new GUIStyle(GUI.skin.button);
                _fullBtnStyle.fontSize = 16;
                _fullBtnStyle.alignment = TextAnchor.MiddleCenter;
                _fullBtnStyle.normal.textColor = new Color(1f, 1f, 1f, 0.7f);
                _fullBtnStyle.hover.textColor = new Color(1f, 1f, 1f, 1f);
            }
            return _fullBtnStyle;
        }

        // =========================================================================
        //  SECOND WINDOW
        // =========================================================================
        void UpdateSecondWindow()
        {
            bool want = _cfgEnabled.Value && _cfgWindow.Value && !_windowDisabledByClose && _gpu != null && (_domeWindow != null || _rtResCfg == _windowRes);

            if (!want)
            {
                if (_domeWindow != null)
                {
                    CaptureWindowPosNow();
                    // remember F11/double-click fullscreen so a re-created window comes back the same way
                    if (_domeWindow.Ready && _cfgWindowFull.Value != _domeWindow.IsFullscreen) { _cfgWindowFull.Value = _domeWindow.IsFullscreen; _cfgDirty = true; }
                    _domeWindow.Dispose(); _domeWindow = null;
                }
                return;
            }

            // Create on first need (deferred; fully logged).
            if (_domeWindow == null)
            {
                try
                {
                    if (_rtRes != _windowRes && _gpu != null && _gpu.Resize(_windowRes)) _rtRes = _windowRes;   // earlier auto-size left a different RT size
                    _domeWindow = new DomeWindow(_windowRes, _windowRes, _cfgWindowTopmost.Value, OnDomeWindowClosed);
                    _winCreatedRes = _windowRes;
                    // Route the window's own diagnostics (PushBytes early-returns,
                    // StretchBlt results, exceptions) into our thread-safe file log
                    // so they're visible in US2Dome.log, not just the BepInEx console.
                    _domeWindow.OnDebug = (string m) => LogToFile("WIN", m);
                    if (_domeWindow.Ready) RestoreWindowPos(_domeWindow);
                    if (_domeWindow.Ready && _cfgWindowFull.Value) _domeWindow.SetFullscreen(true);
                    if (_domeWindow.Ready)
                        LogInfo("[US2Dome] Second window created (" + _windowRes + "px, topmost=" +
                                (_cfgWindowTopmost.Value ? "yes" : "no") + ").");
                    else
                    {
                        LogError("[US2Dome] Second window NOT available: " + _domeWindow.Error +
                                 ". Using overlay instead.");
                        _domeWindow.Dispose();
                        _domeWindow = null;
                        _windowDisabledByClose = true;   // stop retrying
                    }
                }
                catch (Exception e)
                {
                    LogError("[US2Dome] Second window create failed: " + e);
                    if (_domeWindow != null) { _domeWindow.Dispose(); _domeWindow = null; }
                    _windowDisabledByClose = true;
                }
            }

            AutoSizeWindow();
            TrackWindowPos();

            // NOTE (v1.4.0): the per-frame window blit no longer happens here.
            // ConsumeWorkerResult() (called from the capture path in OnGUI)
            // pushes the worker's _winBytes into the window via PushBytes(), so
            // the main thread does zero per-pixel work. This method only creates
            // or disposes the window.
        }

        // v1.6.5: make the dome RT + window bitmap equal to the window's drawing square, so StretchBlt is 1:1.
        void AutoSizeWindow()
        {
            if (!_cfgWindowAuto.Value || _domeWindow == null || !_domeWindow.Ready || _gpu == null || !_gpu.Ready) return;
            if (_domeWindow.DibSide != _rtRes)
            {
                if (!_domeWindow.ResizeDib(_rtRes) && !_dibFailLogged) { _dibFailLogged = true; LogWarn("[US2Dome] window bitmap resize failed"); }
                return;
            }
            int cs = _domeWindow.ClientSide();
            if (cs < 64) return;                              // minimized / transitioning
            int side = Mathf.Clamp(cs, 256, 8192);
            if (side == _rtRes) { _autoPending = 0; return; }
            float now = Time.unscaledTime;
            if (side != _autoPending) { _autoPending = side; _autoSince = now; return; }
            if (now - _autoSince < 0.3f) return;              // wait until the size has settled (dragging a border)
            _autoPending = 0;
            if (_gpu.Resize(side))
            {
                _rtRes = side;
                _domeWindow.ResizeDib(side);
                LogInfo("[US2Dome] Dome auto-sized to the window: " + side + "px (1:1 blit)");
            }
        }

        void OnDomeWindowClosed()
        {
            _windowDisabledByClose = true;
            LogInfo("[US2Dome] Second window closed by user (set SecondWindow=true to re-enable).");
        }

        // =========================================================================
        //  CAPTURE  (v1.5.0: everything stays on the GPU; window gets an async readback)
        // =========================================================================
        // Fires after each camera finishes. When the LAST face camera is done, all
        // five face RTs are complete -> warp them into the dome RT on the GPU.
        void OnEndCameraRendering(ScriptableRenderContext ctx, Camera cam)
        {
            if (!_fdActive) { OnEndCameraRenderingInner(ctx, cam); return; }
            double fdE0 = FdNowMs();
            try { OnEndCameraRenderingInner(ctx, cam); }
            finally { _fdEndCamMs += FdNowMs() - fdE0; }
        }

        void OnEndCameraRenderingInner(ScriptableRenderContext ctx, Camera cam)
        {
            if (_gpu == null || cam == null) return;
            // A face camera has finished: paint the window quads on top of its picture (they are not drawn by the camera itself).
            for (int fi = 0; fi < 5; fi++)
                if (cam == _faceCams[fi]) { DrawWindowsIntoFace(fi); break; }
            if (cam != _faceCams[4]) return;     // the dome is drawn after the last face
            try
            {
                float t0 = Time.realtimeSinceStartup;
                double fdD0 = _fdActive ? FdNowMs() : 0.0;
                _gpu.Draw();
                if (_fdActive) _fdDrawMs += FdNowMs() - fdD0;
                _drawMsAccum += (Time.realtimeSinceStartup - t0) * 1000.0;
                int dc = ++_dbgDraw;
                if (dc == 1 || dc % 120 == 0)
                {
                    Dbg("[US2Dome] DRAW #" + dc + ": GPU dome warp issued, avg " + (_drawMsAccum / dc).ToString("F2") +
                        " ms CPU-side (readbacks delivered=" + _dbgReadback + ", skipped(busy)=" + _dbgReadbackSkip + ", face-render frames gated=" + _dbgGated + ")");
                }

                // Feed the 2nd window (async; never blocks the main thread).
                if (_domeWindow != null && _domeWindow.Ready)
                {
                    if (!_gpu.RequestReadback(OnDomeReadback)) _dbgReadbackSkip++;
                }
            }
            catch (Exception e)
            {
                LogErrorDetail("[US2Dome] OnEndCameraRendering THREW (GPU dome draw failed)", e);
            }
        }

        // Main thread, a few frames after the request. bgra = rtRes*rtRes*4, BGRA32,
        // copied straight into the (top-down) DIB buffer.
        void OnDomeReadback(byte[] bgra)
        {
            int rn = ++_dbgReadback;
            int w = _rtRes;
            int row = w * 4;
            // v1.6.3: no intermediate copy; `bgra` (DomeGpu's reused buffer) goes straight to the DIB.
            if (bgra == null || bgra.Length != w * row) return;

            if (rn == 1 || rn % 120 == 0)
            {
                int cx = ((w / 2) * w + w / 2) * 4;
                long sum = 0; int samples = 0;
                for (int i = 0; i + 2 < bgra.Length; i += 4096) { sum += bgra[i] + bgra[i + 1] + bgra[i + 2]; samples++; }
                Dbg("[US2Dome] READBACK #" + rn + " delivered: center=(B" + bgra[cx] + " G" + bgra[cx + 1] + " R" + bgra[cx + 2] +
                    ") avg(B+G+R)/3=" + (samples > 0 ? sum / (3 * samples) : 0) + " (0 everywhere = black dome: face RTs empty or shader drew nothing)");
            }

            if (_cfgSmooth == null && _cfgFile != null)
            {
                _cfgSmooth = _cfgFile.Bind("Window", "SmoothUpdates", true,
                    "Hold dome frames in a small queue and show exactly one per game frame (evens out bursts of 0/2 window updates; adds about one frame of latency). Ignored when CaptureEveryNFrames > 1.");
                try { _cfgFile.Save(); } catch { }
            }

            if (_domeWindow != null && _domeWindow.Ready)
            {
                if (_cfgSmooth != null && _cfgSmooth.Value && _cfgEveryN.Value <= 1) EnqueueWindowFrame(bgra);
                else { _wqCount = 0; _wqPrimed = false; PushToWindow(bgra); }
            }
        }

        // ---- v1.9.3: window-update smoothing (small jitter buffer) ----
        // Async readbacks can complete in bursts (0, 2, 0, 2 per frame) when the GPU is busy, which makes the dome move in lumps.
        // The queue absorbs that: frames are shown one per game frame, in order.
        ConfigEntry<bool> _cfgSmooth;
        readonly byte[][] _wq = new byte[3][];
        int  _wqHead, _wqCount;
        bool _wqPrimed;
        int  _dbgWqDrop, _dbgWqUnder;

        void EnqueueWindowFrame(byte[] bgra)
        {
            if (_wqCount >= _wq.Length) { _wqHead = (_wqHead + 1) % _wq.Length; _wqCount--; _dbgWqDrop++; }   // too far behind: drop the oldest
            int slot = (_wqHead + _wqCount) % _wq.Length;
            if (_wq[slot] == null || _wq[slot].Length != bgra.Length) _wq[slot] = new byte[bgra.Length];
            Buffer.BlockCopy(bgra, 0, _wq[slot], 0, bgra.Length);
            _wqCount++;
        }

        // Called once per game frame from Update().
        void PresentQueuedWindowFrame()
        {
            if (_wqCount == 0 && !_wqPrimed) return;
            if (!_wqPrimed) { if (_wqCount < 2) return; _wqPrimed = true; }   // wait until two frames are queued, then run one behind
            if (_wqCount == 0) { _wqPrimed = false; _dbgWqUnder++; return; }  // queue ran dry: re-prime
            byte[] f = _wq[_wqHead];
            _wqHead = (_wqHead + 1) % _wq.Length; _wqCount--;
            if (_domeWindow != null && _domeWindow.Ready && f != null && f.Length == _rtRes * _rtRes * 4) PushToWindow(f);
        }

        void PushToWindow(byte[] bgra)
        {
            int pn = ++_dbgPush;
            try
            {
                double fdP0 = _fdActive ? FdNowMs() : 0.0;
                bool pushed = _domeWindow.PushBytes(bgra);
                if (_fdActive) _fdPushMs += FdNowMs() - fdP0;
                if (pn == 1 || pn % 120 == 0)
                    Dbg("[US2Dome] PUSHBYTES #" + pn + ": " + (pushed ? "OK (blitted to 2nd window)" : "FAILED (window not updated)"));
            }
            catch (Exception e) { LogErrorDetail("[US2Dome] PushBytes THREW (2nd window blit failed)", e); }
        }

        void SavePng(float x, float y, float size)
        {
            try
            {
                if (_gpu == null || _gpu.DomeRT == null) throw new Exception("dome RT not ready");
                int sz = _gpu.DomeRT.width;
                if (_saveTex == null || _saveTex.width != sz) _saveTex = new Texture2D(sz, sz, TextureFormat.RGB24, false);
                RenderTexture prev = RenderTexture.active;
                RenderTexture.active = _gpu.DomeRT;
                _saveTex.ReadPixels(new Rect(0, 0, sz, sz), 0, 0, false);
                _saveTex.Apply(false);
                RenderTexture.active = prev;
                byte[] png = _saveTex.EncodeToPNG();
                string dir = Path.Combine(Application.dataPath, "..", "US2Dome");
                Directory.CreateDirectory(dir);
                string fname = "US2Dome_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
                string path = Path.Combine(dir, fname);
                File.WriteAllBytes(path, png);
                _lastSaveMsg = "Saved: " + path;
                _lastSaveTime = Time.time;
                LogInfo("[US2Dome] Saved dome PNG: " + path + " (" + (png.Length / 1024) + " KB)");
            }
            catch (Exception e)
            {
                _lastSaveMsg = "Save FAILED: " + e.Message;
                _lastSaveTime = Time.time;
                LogError("[US2Dome] Save PNG failed: " + e);
            }
        }

        void OnDestroy()
        {
            try
            {
                CaptureWindowPosNow();
                SaveCfgNow();
                InputBlocker.Uninstall();
                DestroyNameplates();
                DestroyWindowMirror();
                if (_subscribed) { RenderPipelineManager.endCameraRendering -= OnEndCameraRendering; _subscribed = false; }
                if (_domeWindow != null) { _domeWindow.Dispose(); _domeWindow = null; }
                if (_gpu != null) { _gpu.Dispose(); _gpu = null; }
                for (int i = 0; i < 5; i++)
                    if (_faceRTs[i] != null) { _faceRTs[i].Release(); UnityEngine.Object.Destroy(_faceRTs[i]); }
                if (_saveTex != null) UnityEngine.Object.Destroy(_saveTex);
            }
            catch (Exception e) { LogError("[US2Dome] OnDestroy cleanup failed: " + e); }
        }
    }
}
