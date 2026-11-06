// FrameDebug.cs  (v1.9.2: + per-camera and per-phase timing)
// Frame-time recorder. Press Shift+F3 in the game: it records every frame for FrameTimeSeconds (default 10 s)
// and then writes  <game>/BepInEx/US2Dome_frametimes_<date>_<time>.txt  (independent of the "Make log file" setting).
// Nothing is written to disk while recording (so the recorder does not disturb the frame times it measures).
//
// Per frame it records: the frame time, whether the dome was rendered that frame, how long the plugin's own parts took
// (dome warp issue, face-window drawing, nameplates, window mirror, OnGUI, window blit), the async readback latency,
// how many readbacks are in flight, GC activity, and (if the game build has Frame Timing Stats on) the engine's own
// main-thread / render-thread / GPU frame times.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEngine;
using BepInEx.Configuration;
using UnityEngine.Rendering;

namespace US2Dome
{
    public partial class US2Dome
    {
        // ---- config ----
        ConfigEntry<string> _cfgFdKey;
        ConfigEntry<float>  _cfgFdSeconds;
        KeyCode             _kFrame = KeyCode.F3;
        bool                _fdBound;

        // ---- state ----
        bool    _fdActive;
        double  _fdStartMs;
        float   _fdSeconds = 10f;
        bool    _fdHaveBaseline;
        string  _fdMsg = "";
        float   _fdMsgTime = -999f;

        // accumulators, filled by the instrumented code while _fdActive, drained once per frame by TickFrameDebug()
        double  _fdDrawMs;      // GPU dome warp issue (CPU side)
        double  _fdEndCamMs;    // whole endCameraRendering callback (includes the draw above, face-window drawing, readback request)
        double  _fdNpMs;        // UpdateNameplates
        double  _fdMirMs;       // UpdateWindowMirror
        double  _fdGuiMs;       // all OnGUI calls of the frame (includes the window blit)
        double  _fdPushMs;      // PushBytes (copy to DIB + StretchBlt)

        // counter baselines (to turn the plugin's running totals into per-frame numbers)
        int     _fdLastDraw, _fdLastCb, _fdLastPush, _fdLastGated, _fdLastSkip;
        int     _fdLastGc0, _fdLastGc1, _fdLastGc2;

        // ---- v1.9.2: per-camera / per-phase timing (URP begin/endCameraRendering) ----
        // Phases are measured relative to this plugin's Update() call, which marks "frame start":
        //   pre  = frame start -> first camera begins (scripts / game simulation / culling set-up)
        //   face / main / other = CPU time inside the five dome face cameras / the game's screen camera(s) / other render-texture cameras (3D view etc.)
        //   gap  = time inside the render loop that belongs to no camera
        //   post = last camera ends -> next frame start (OnGUI, present, waiting, other scripts)
        class FdCamStat { public string name; public int kind; public double total, max, begin; public int count; }
        readonly Dictionary<Camera, FdCamStat> _fdCams = new Dictionary<Camera, FdCamStat>();
        bool    _fdCamHooked;
        double  _fdFrameStartMs, _fdFirstBeginMs = -1, _fdLastEndMs = -1;
        double  _fdCamFaceMs, _fdCamMainMs, _fdCamOtherMs;
        int     _fdCamN;

        struct FdRow
        {
            public float  t;           // seconds since recording started (start of the frame this row describes)
            public float  dt;          // frame time, ms
            public byte   cap;         // 1 = the 5 face cameras + dome warp ran this frame
            public byte   gated;       // 1 = capture skipped because every readback slot was busy
            public float  draw, endCam, np, mir, gui, push;   // ms
            public float  copy;        // ms spent in DomeGpu's readback callback copying GPU data into a byte[] (sum this frame)
            public float  latMs;       // worst readback latency delivered this frame (request -> callback), ms
            public short  latFr;       // same, in frames
            public byte   cbs;         // readbacks delivered this frame
            public byte   pushes;      // window updates this frame
            public byte   skip;        // readback requests refused (slots busy)
            public byte   inflight;    // readbacks in flight at the end of the frame
            public byte   gc;          // GC collections this frame (any generation)
            public float  memMb;       // managed heap (MB)
            public float  ftMain, ftRender, ftGpu;   // Frame Timing Stats (ms), -1 = not available
            public float  pre, face, mainCam, other, gap, post;   // phases (ms), see above
            public byte   ncam;        // cameras rendered this frame
        }
        readonly List<FdRow> _fdRows = new List<FdRow>(4096);
        string _fdHeader = "";
        FrameTiming[] _fdFt = new FrameTiming[1];
        bool _fdFtOn;

        internal static double FdNowMs() { return Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency; }

        void BindFrameDebug()
        {
            if (_fdBound || _cfgFile == null) return;
            _fdBound = true;
            _cfgFdKey = _cfgFile.Bind("Diagnostics", "FrameTimeKey", "F3",
                "Key (held together with Shift): record frame times for FrameTimeSeconds, then write BepInEx/US2Dome_frametimes_*.txt. (Unity KeyCode name.)");
            _cfgFdSeconds = _cfgFile.Bind("Diagnostics", "FrameTimeSeconds", 10f, "How many seconds the frame-time recorder runs (1 - 60).");
            _kFrame = ParseKey(_cfgFdKey.Value, KeyCode.F3);
            try { _cfgFile.Save(); } catch { }
        }

        // True when Shift+<frame key> is pressed this frame (UiDiag uses this so Shift+F3 does not also fire the F3 window snapshot).
        bool FrameKeyChord()
        {
            return Input.GetKeyDown(_kFrame) && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift));
        }

        // Called at the very top of Update(). Records the frame that just ended and starts/stops the recording.
        void TickFrameDebug()
        {
            try
            {
                BindFrameDebug();

                if (!_fdActive)
                {
                    if (FrameKeyChord()) FdStart();
                    return;
                }

                double now = FdNowMs();
                double elapsed = (now - _fdStartMs) / 1000.0;

                if (_fdHaveBaseline && _fdRows.Count < 40000)
                    _fdRows.Add(FdMakeRow((float)elapsed, now));
                _fdHaveBaseline = true;
                FdResetFrameCounters();
                _fdFrameStartMs = now;

                if (elapsed >= _fdSeconds) FdFinish();
            }
            catch (Exception e)
            {
                _fdActive = false;
                FdUnhookCams();
                LogWarn("[US2Dome] FrameDebug tick failed: " + e.Message);
            }
        }

        void FdResetFrameCounters()
        {
            _fdDrawMs = _fdEndCamMs = _fdNpMs = _fdMirMs = _fdGuiMs = _fdPushMs = 0;
            _fdFirstBeginMs = -1; _fdLastEndMs = -1; _fdCamFaceMs = _fdCamMainMs = _fdCamOtherMs = 0; _fdCamN = 0;
            _fdLastDraw = _dbgDraw; _fdLastCb = _dbgReadback; _fdLastPush = _dbgPush; _fdLastGated = _dbgGated; _fdLastSkip = _dbgReadbackSkip;
            _fdLastGc0 = GC.CollectionCount(0); _fdLastGc1 = GC.CollectionCount(1); _fdLastGc2 = GC.CollectionCount(2);
            if (_gpu != null) { _gpu.DbgCopyMs = 0; _gpu.DbgLatMsMax = 0; _gpu.DbgLatFramesMax = 0; }
        }

        static byte Clamp8(int v) { return (byte)(v < 0 ? 0 : (v > 255 ? 255 : v)); }

        FdRow FdMakeRow(float elapsed, double nowMs)
        {
            FdRow r = new FdRow();
            r.t = elapsed;
            r.dt = Time.unscaledDeltaTime * 1000f;
            r.cap = (byte)(_willCapture ? 1 : 0);
            r.gated = Clamp8(_dbgGated - _fdLastGated);
            r.draw = (float)_fdDrawMs; r.endCam = (float)_fdEndCamMs; r.np = (float)_fdNpMs; r.mir = (float)_fdMirMs;
            r.gui = (float)_fdGuiMs; r.push = (float)_fdPushMs;
            r.cbs = Clamp8(_dbgReadback - _fdLastCb);
            r.pushes = Clamp8(_dbgPush - _fdLastPush);
            r.skip = Clamp8(_dbgReadbackSkip - _fdLastSkip);
            int gc = (GC.CollectionCount(0) - _fdLastGc0) + (GC.CollectionCount(1) - _fdLastGc1) + (GC.CollectionCount(2) - _fdLastGc2);
            r.gc = Clamp8(gc);
            r.memMb = GC.GetTotalMemory(false) / 1048576f;
            if (_gpu != null)
            {
                r.copy = (float)_gpu.DbgCopyMs;
                r.latMs = (float)_gpu.DbgLatMsMax;
                r.latFr = (short)Math.Min(_gpu.DbgLatFramesMax, 32000);
                r.inflight = Clamp8(_gpu.InFlight);
            }
            if (_fdFirstBeginMs >= 0 && _fdLastEndMs >= _fdFirstBeginMs)
            {
                double camSum = _fdCamFaceMs + _fdCamMainMs + _fdCamOtherMs;
                r.pre = (float)(_fdFirstBeginMs - _fdFrameStartMs);
                r.face = (float)_fdCamFaceMs; r.mainCam = (float)_fdCamMainMs; r.other = (float)_fdCamOtherMs;
                r.gap = (float)Math.Max(0.0, (_fdLastEndMs - _fdFirstBeginMs) - camSum);
                r.post = (float)(nowMs - _fdLastEndMs);
            }
            else { r.pre = (float)(nowMs - _fdFrameStartMs); }   // no camera rendered this frame
            r.ncam = Clamp8(_fdCamN);
            r.ftMain = r.ftRender = r.ftGpu = -1f;
            if (_fdFtOn)
            {
                try
                {
                    FrameTimingManager.CaptureFrameTimings();
                    uint n = FrameTimingManager.GetLatestTimings(1, _fdFt);
                    if (n > 0)
                    {
                        r.ftMain = (float)_fdFt[0].cpuMainThreadFrameTime;
                        r.ftRender = (float)_fdFt[0].cpuRenderThreadFrameTime;
                        r.ftGpu = (float)_fdFt[0].gpuFrameTime;
                    }
                }
                catch { _fdFtOn = false; }
            }
            return r;
        }

        void FdStart()
        {
            _fdRows.Clear();
            _fdSeconds = Mathf.Clamp(_cfgFdSeconds != null ? _cfgFdSeconds.Value : 10f, 1f, 60f);
            _fdHaveBaseline = false;
            _fdFtOn = false;
            try { _fdFtOn = FrameTimingManager.IsFeatureEnabled(); } catch { _fdFtOn = false; }
            _fdHeader = FdBuildHeader();
            FdResetFrameCounters();
            _fdStartMs = FdNowMs();
            _fdFrameStartMs = _fdStartMs;
            _fdCams.Clear();
            FdHookCams();
            _fdActive = true;
            LogInfo("[US2Dome] Frame-time recording started (" + _fdSeconds.ToString("F0") + " s).");
        }

        void FdFinish()
        {
            _fdActive = false;
            FdUnhookCams();
            try
            {
                string path = FdWriteReport();
                _fdMsg = "Frame times saved: " + path;
                LogInfo("[US2Dome] Frame-time report saved: " + path);
            }
            catch (Exception e)
            {
                _fdMsg = "Frame-time save FAILED: " + e.Message;
                LogError("[US2Dome] Frame-time report failed: " + e);
            }
            _fdMsgTime = Time.unscaledTime;
        }

        void FdHookCams()
        {
            if (_fdCamHooked) return;
            RenderPipelineManager.beginCameraRendering += FdOnBeginCam;
            RenderPipelineManager.endCameraRendering += FdOnEndCam;   // subscribed after the dome's own handler, so it runs after it
            _fdCamHooked = true;
        }

        void FdUnhookCams()
        {
            if (!_fdCamHooked) return;
            RenderPipelineManager.beginCameraRendering -= FdOnBeginCam;
            RenderPipelineManager.endCameraRendering -= FdOnEndCam;
            _fdCamHooked = false;
        }

        FdCamStat FdGetCamStat(Camera cam)
        {
            FdCamStat s;
            if (!_fdCams.TryGetValue(cam, out s))
            {
                s = new FdCamStat();
                string tgt = cam.targetTexture != null ? (" -> RT " + cam.targetTexture.width + "x" + cam.targetTexture.height) : " -> screen";
                s.name = cam.name + (IsOurs(cam) ? " [dome face]" : "") + tgt;
                _fdCams[cam] = s;
            }
            return s;
        }

        void FdOnBeginCam(ScriptableRenderContext ctx, Camera cam)
        {
            if (!_fdActive || cam == null) return;
            double now = FdNowMs();
            if (_fdFirstBeginMs < 0) _fdFirstBeginMs = now;
            FdGetCamStat(cam).begin = now;
        }

        void FdOnEndCam(ScriptableRenderContext ctx, Camera cam)
        {
            if (!_fdActive || cam == null) return;
            double now = FdNowMs();
            FdCamStat s = FdGetCamStat(cam);
            double d = s.begin > 0 ? now - s.begin : 0.0;
            s.begin = 0;
            s.total += d; s.count++; if (d > s.max) s.max = d;
            _fdLastEndMs = now; _fdCamN++;
            if (IsOurs(cam)) { s.kind = 0; _fdCamFaceMs += d; }
            else if (cam.targetTexture == null) { s.kind = 1; _fdCamMainMs += d; }
            else { s.kind = 2; _fdCamOtherMs += d; }
        }

        // Small on-screen note (game window only) while recording and for a few seconds after.
        void DrawFrameDebugGui()
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            string txt = null;
            if (_fdActive)
            {
                double left = _fdSeconds - (FdNowMs() - _fdStartMs) / 1000.0;
                txt = "US2Dome: recording frame times... " + Math.Max(0, left).ToString("F0") + " s left";
            }
            else if (Time.unscaledTime - _fdMsgTime < 8f) txt = _fdMsg;
            if (string.IsNullOrEmpty(txt)) return;
            float w = Mathf.Min(Screen.width - 20f, 760f);
            GUI.Box(new Rect((Screen.width - w) * 0.5f, 8f, w, 26f), txt);
        }

        string FdBuildHeader()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("US2Dome frame-time report   plugin v1.6.6 / frame-debug 1.9.2   " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("recording: " + _fdSeconds.ToString("F0") + " s");
            try
            {
                sb.AppendLine("GPU: " + SystemInfo.graphicsDeviceName + " | " + SystemInfo.graphicsDeviceType + " | VRAM " + SystemInfo.graphicsMemorySize + " MB | driver " + SystemInfo.graphicsDeviceVersion);
                sb.AppendLine("CPU: " + SystemInfo.processorType + " | " + SystemInfo.processorCount + " threads | RAM " + SystemInfo.systemMemorySize + " MB");
                sb.AppendLine("Unity " + Application.unityVersion + " | async readback supported: " + SystemInfo.supportsAsyncGPUReadback +
                              " | render threading: " + SystemInfo.graphicsMultiThreaded);
                sb.AppendLine("Game window: " + Screen.width + "x" + Screen.height + " fullscreen=" + Screen.fullScreen + " refresh=" + Screen.currentResolution.refreshRateRatio.value.ToString("F1") + " Hz" +
                              " | vSyncCount=" + QualitySettings.vSyncCount + " targetFrameRate=" + Application.targetFrameRate);
                sb.AppendLine("Dome: enabled=" + _cfgEnabled.Value + " rt=" + _rtRes + "px face=" + _faceRes + "px mips=" + _cfgMips.Value + " msaa=" + _cfgMsaa.Value +
                              " faceShadows=" + _cfgFaceShadows.Value + " facePost=" + _cfgFacePost.Value + " everyN=" + _cfgEveryN.Value + " zoom=" + _cfgZoom.Value);
                sb.AppendLine("Outputs: secondWindow=" + _cfgWindow.Value + " windowReady=" + (_domeWindow != null && _domeWindow.Ready) +
                              (_domeWindow != null && _domeWindow.Ready ? (" dib=" + _domeWindow.DibSide + " client=" + _domeWindow.ClientSide() + " fullscreen=" + _domeWindow.IsFullscreen) : "") +
                              " autoSize=" + _cfgWindowAuto.Value + " topmost=" + _cfgWindowTopmost.Value + " miniView=" + _cfgOverlay.Value + " fullDomeMode=" + _cfgFullDome.Value +
                              " windowOnlyOutput=" + WindowOnlyOutput() + " smoothUpdates=" + (_cfgSmooth != null && _cfgSmooth.Value && _cfgEveryN.Value <= 1) + " (queue drops so far " + _dbgWqDrop + ", underruns " + _dbgWqUnder + ")");
                sb.AppendLine("Frame Timing Stats (engine main/render/GPU times): " + (_fdFtOn ? "available" : "NOT available in this build (columns show -1)"));
            }
            catch (Exception e) { sb.AppendLine("(header incomplete: " + e.Message + ")"); }
            return sb.ToString();
        }

        static float Pct(float[] sorted, double p)
        {
            if (sorted.Length == 0) return 0f;
            int i = (int)Math.Ceiling(p / 100.0 * sorted.Length) - 1;
            if (i < 0) i = 0; if (i >= sorted.Length) i = sorted.Length - 1;
            return sorted[i];
        }

        string FdWriteReport()
        {
            int n = _fdRows.Count;
            StringBuilder sb = new StringBuilder(n * 120 + 8192);
            sb.Append(_fdHeader);
            sb.AppendLine("frames recorded: " + n);
            if (n < 5) { sb.AppendLine("Too few frames recorded."); return FdSave(sb); }

            float[] dts = new float[n];
            double total = 0;
            for (int i = 0; i < n; i++) { dts[i] = _fdRows[i].dt; total += dts[i]; }
            float[] sorted = (float[])dts.Clone();
            Array.Sort(sorted);
            float med = Pct(sorted, 50);
            sb.AppendLine();
            sb.AppendLine("=== SUMMARY ===");
            sb.AppendLine("avg fps " + (n * 1000.0 / total).ToString("F1") + " | frame ms: min " + sorted[0].ToString("F2") + "  p50 " + med.ToString("F2") +
                          "  p95 " + Pct(sorted, 95).ToString("F2") + "  p99 " + Pct(sorted, 99).ToString("F2") + "  max " + sorted[n - 1].ToString("F2"));
            sb.AppendLine("fps: worst " + (1000f / Math.Max(0.001f, sorted[n - 1])).ToString("F1") + " | 1% low " + (1000f / Math.Max(0.001f, Pct(sorted, 99))).ToString("F1") +
                          " | best " + (1000f / Math.Max(0.001f, sorted[0])).ToString("F1"));
            int spikes = 0, gcFrames = 0, gatedFrames = 0, capFrames = 0, pushFrames = 0; int maxNoPush = 0, run = 0;
            double sDraw = 0, sEnd = 0, sNp = 0, sMir = 0, sGui = 0, sPush = 0, sCopy = 0, sLat = 0; int latN = 0; float maxLat = 0; int maxLatFr = 0;
            double dtPush = 0, dtNoPush = 0; int nPush = 0, nNoPush = 0, maxInfl = 0;
            double sFtM = 0, sFtR = 0, sFtG = 0; int ftN = 0;
            List<float> pushTimes = new List<float>();
            for (int i = 0; i < n; i++)
            {
                FdRow r = _fdRows[i];
                if (r.dt > med * 1.5f) spikes++;
                if (r.gc > 0) gcFrames++;
                if (r.gated > 0) gatedFrames++;
                if (r.cap > 0) capFrames++;
                if (r.pushes > 0) { pushFrames++; run = 0; pushTimes.Add(r.t); dtPush += r.dt; nPush++; } else { run++; if (run > maxNoPush) maxNoPush = run; dtNoPush += r.dt; nNoPush++; }
                sDraw += r.draw; sEnd += r.endCam; sNp += r.np; sMir += r.mir; sGui += r.gui; sPush += r.push; sCopy += r.copy;
                if (r.cbs > 0) { sLat += r.latMs; latN++; if (r.latMs > maxLat) maxLat = r.latMs; if (r.latFr > maxLatFr) maxLatFr = r.latFr; }
                if (r.inflight > maxInfl) maxInfl = r.inflight;
                if (r.ftGpu >= 0) { sFtM += r.ftMain; sFtR += r.ftRender; sFtG += r.ftGpu; ftN++; }
            }
            sb.AppendLine("frames slower than 1.5x median: " + spikes + " (" + (100.0 * spikes / n).ToString("F1") + "%) | frames with a GC: " + gcFrames +
                          " | frames with dome rendered: " + capFrames + " | frames skipped because readback slots were busy: " + gatedFrames);
            sb.AppendLine("window updates (dome frames shown): " + pushFrames + " = " + (pushFrames / total * 1000.0).ToString("F1") + " /s | longest stretch of frames with NO window update: " + maxNoPush + " frames");
            if (pushTimes.Count > 2)
            {
                List<float> iv = new List<float>();
                for (int i = 1; i < pushTimes.Count; i++) iv.Add((pushTimes[i] - pushTimes[i - 1]) * 1000f);
                float[] ivs = iv.ToArray(); Array.Sort(ivs);
                sb.AppendLine("time between window updates (ms): p50 " + Pct(ivs, 50).ToString("F1") + "  p95 " + Pct(ivs, 95).ToString("F1") + "  max " + ivs[ivs.Length - 1].ToString("F1"));
            }
            sb.AppendLine("avg frame ms: with window update " + (nPush > 0 ? (dtPush / nPush).ToString("F2") : "-") + " | without " + (nNoPush > 0 ? (dtNoPush / nNoPush).ToString("F2") : "-"));
            sb.AppendLine("plugin cost, avg ms per frame: dome-warp issue " + (sDraw / n).ToString("F3") + " | whole endCameraRendering " + (sEnd / n).ToString("F3") +
                          " | nameplates " + (sNp / n).ToString("F3") + " | window mirror " + (sMir / n).ToString("F3") + " | OnGUI total " + (sGui / n).ToString("F3") +
                          " | window blit (PushBytes) " + (sPush / n).ToString("F3") + " | readback copy " + (sCopy / n).ToString("F3"));
            sb.AppendLine("readback latency: avg " + (latN > 0 ? (sLat / latN).ToString("F1") : "-") + " ms, worst " + maxLat.ToString("F1") + " ms / " + maxLatFr + " frames | max in flight " + maxInfl + " of " + DomeGpu.MaxInFlight);
            if (ftN > 0)
                sb.AppendLine("engine frame timing, avg ms: main thread " + (sFtM / ftN).ToString("F2") + " | render thread " + (sFtR / ftN).ToString("F2") + " | GPU " + (sFtG / ftN).ToString("F2"));

            // phases + per camera (v1.9.2)
            double sPre = 0, sFace = 0, sMainC = 0, sOther = 0, sGap = 0, sPost = 0, sNcam = 0;
            for (int i = 0; i < n; i++)
            {
                FdRow r = _fdRows[i];
                sPre += r.pre; sFace += r.face; sMainC += r.mainCam; sOther += r.other; sGap += r.gap; sPost += r.post; sNcam += r.ncam;
            }
            sb.AppendLine();
            sb.AppendLine("=== WHERE THE FRAME GOES (avg ms per frame; phases are measured from this plugin's Update() call) ===");
            sb.AppendLine("pre-render (scripts/simulation before the first camera) " + (sPre / n).ToString("F2") +
                          " | dome face cameras " + (sFace / n).ToString("F2") + " | game screen camera(s) " + (sMainC / n).ToString("F2") +
                          " | other render-texture cameras " + (sOther / n).ToString("F2") + " | render-loop gaps " + (sGap / n).ToString("F2") +
                          " | post-render (OnGUI/present/wait) " + (sPost / n).ToString("F2") + "   (cameras per frame " + (sNcam / n).ToString("F1") + ")");
            sb.AppendLine("Note: a camera's time includes the dome plugin's end-of-camera work for it; with 'multithreaded rendering' off, GPU waits also land inside camera or post time.");
            sb.AppendLine();
            sb.AppendLine("camera                                             renders/frame   avg ms/render   avg ms/frame   worst ms");
            List<FdCamStat> cl = new List<FdCamStat>(_fdCams.Values);
            cl.Sort(delegate (FdCamStat a, FdCamStat b) { return b.total.CompareTo(a.total); });
            for (int i = 0; i < cl.Count && i < 24; i++)
            {
                FdCamStat c = cl[i];
                if (c.count == 0) continue;
                sb.AppendLine(c.name.PadRight(52) + (c.count / (double)n).ToString("F2").PadLeft(8) + (c.total / c.count).ToString("F2").PadLeft(16) +
                              (c.total / n).ToString("F2").PadLeft(15) + c.max.ToString("F2").PadLeft(11));
            }

            // half-second bins: shows the rubber-band shape directly
            sb.AppendLine();
            sb.AppendLine("=== 0.5 s BINS (the rubber-band shape) ===");
            sb.AppendLine("  t(s)  frames  avgFPS  worst-ms  windowUpd  domeFrames  gated  gc");
            int bins = (int)Math.Ceiling(_fdRows[n - 1].t / 0.5f) + 1;
            for (int b = 0; b < bins; b++)
            {
                int c = 0, pu = 0, cp = 0, gt = 0, g = 0; float worst = 0;
                for (int i = 0; i < n; i++)
                {
                    FdRow r = _fdRows[i];
                    if ((int)(r.t / 0.5f) != b) continue;
                    c++; pu += r.pushes; cp += r.cap; gt += r.gated; g += r.gc; if (r.dt > worst) worst = r.dt;
                }
                if (c == 0) continue;
                sb.AppendLine((b * 0.5f).ToString("F1").PadLeft(6) + c.ToString().PadLeft(8) + (c / 0.5f).ToString("F1").PadLeft(8) + worst.ToString("F1").PadLeft(10) +
                              pu.ToString().PadLeft(11) + cp.ToString().PadLeft(12) + gt.ToString().PadLeft(7) + g.ToString().PadLeft(5));
            }

            sb.AppendLine();
            sb.AppendLine("=== PER FRAME (all ms unless noted) ===");
            sb.AppendLine("t(s)    dt     cap gate  draw   endCam np     mir    gui    push   copy   latMs  latFr cbs push skip infl gc  heapMB ftMain ftRend ftGPU pre    face   main   other  gap    post   ncam");
            for (int i = 0; i < n; i++)
            {
                FdRow r = _fdRows[i];
                sb.Append(r.t.ToString("F3").PadRight(8)).Append(r.dt.ToString("F2").PadRight(7))
                  .Append(r.cap.ToString().PadRight(4)).Append(r.gated.ToString().PadRight(5))
                  .Append(r.draw.ToString("F2").PadRight(7)).Append(r.endCam.ToString("F2").PadRight(7))
                  .Append(r.np.ToString("F2").PadRight(7)).Append(r.mir.ToString("F2").PadRight(7))
                  .Append(r.gui.ToString("F2").PadRight(7)).Append(r.push.ToString("F2").PadRight(7)).Append(r.copy.ToString("F2").PadRight(7))
                  .Append(r.latMs.ToString("F1").PadRight(7)).Append(r.latFr.ToString().PadRight(6))
                  .Append(r.cbs.ToString().PadRight(4)).Append(r.pushes.ToString().PadRight(5)).Append(r.skip.ToString().PadRight(5)).Append(r.inflight.ToString().PadRight(5))
                  .Append(r.gc.ToString().PadRight(4)).Append(r.memMb.ToString("F0").PadRight(7))
                  .Append(r.ftMain.ToString("F1").PadRight(7)).Append(r.ftRender.ToString("F1").PadRight(7)).Append(r.ftGpu.ToString("F1").PadRight(7))
                  .Append(r.pre.ToString("F1").PadRight(7)).Append(r.face.ToString("F1").PadRight(7)).Append(r.mainCam.ToString("F1").PadRight(7))
                  .Append(r.other.ToString("F1").PadRight(7)).Append(r.gap.ToString("F1").PadRight(7)).Append(r.post.ToString("F1").PadRight(7)).Append(r.ncam.ToString()).AppendLine();
            }
            return FdSave(sb);
        }

        string FdSave(StringBuilder sb)
        {
            string dir = Path.Combine(Application.dataPath, "..", "BepInEx");
            if (!Directory.Exists(dir)) dir = Application.dataPath;
            string path = Path.GetFullPath(Path.Combine(dir, "US2Dome_frametimes_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt"));
            File.WriteAllText(path, sb.ToString());
            return path;
        }
    }
}
