// DomeWorker.cs
// A single background thread that runs the 5-face -> dome remap AND the
// window-resample OFF the Unity main thread (v1.4.0 performance fix).
//
// At most ONE job in flight; the caller drops a capture if the worker is
// still busy (no backlog). Uses only System.Threading (Mono/BepInEx-safe).
//
// Design (byte-identical to v1.3.0):
//   * The worker reads `src` (5*fr*fr*4 RGBA bytes, R first) and writes two
//     MAIN-THREAD-OWNED persistent buffers:
//       - `dome` = the remapped dome (outRes^2*4, RGBA)
//       - `win`  = the window DIB-ready bytes (winW*winH*4, B,G,R,255)
//     Both are computed on the worker thread via DomeRemap (parallel).
//   * The main thread NEVER does per-pixel work. It only:
//       - copies face bytes into `src` (GetRawTextureData / GetPixels32)
//       - submits the job
//       - when ready, copies `dome` into the overlay Texture2D (SetPixels32)
//         and `win` into the window DIB (raw Marshal.Copy via PushBytes).
//   * Ping-pong: the main thread keeps TWO face buffers and alternates on each
//     capture. Because at most one job is in flight, the buffer the worker is
//     reading is never the one the main thread writes next -> no race.
//   * The `dome`/`win` buffers are reused across jobs (allocated once, passed
//     in Submit). The worker writes them only while the main thread is not
//     reading (the main thread reads after TryGetResult and only re-submits on
//     a LATER capture) -> no aliasing.
using System;
using System.IO;
using System.Threading;

namespace US2Dome
{
    public sealed class DomeWorker : IDisposable
    {
        // Optional exception sink (set by US2Dome) so a remap failure is NEVER silent.
        public Action<string> OnError = null;
        // Optional per-job diagnostic sink (set by US2Dome): job start / done / ms.
        public Action<string> OnDebug = null;
        long _jobSeq = 0;

        // Diagnostic sink (set by US2Dome, routed into the locked file writer).
        // Swallows any exception so a logging failure can NEVER kill the worker.
        void DLog(string msg)
        {
            if (OnDebug == null) return;
            try { OnDebug(msg); } catch { }
        }

        private void LogErr(string msg)
        {
            if (OnError != null)
            {
                try { OnError(msg); } catch { }
            }
            // Belt-and-braces: also drop into the same file US2Dome uses (only when "Make log file" is on).
            if (!US2Dome.LogFileOn) return;
            try
            {
                string dir = System.AppDomain.CurrentDomain.BaseDirectory;
                for (int i = 0; i < 6; i++)
                {
                    if (Directory.Exists(Path.Combine(dir, "BepInEx")))
                    {
                        File.AppendAllText(Path.Combine(dir, "BepInEx", "US2Dome.log"),
                            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [ERROR] [US2Dome] " + msg + Environment.NewLine);
                        break;
                    }
                    dir = Path.GetFullPath(Path.Combine(dir, ".."));
                }
            }
            catch { }
        }

        private readonly Thread _thread;
        private readonly object _gate = new object();
        private volatile bool _quit;

        // Job inputs (set by Submit, read by Loop).
        private byte[] _src;          // face RGBA bytes (5*fr*fr*4)
        private int _faceRes;
        private int _outRes;          // dome resolution
        private int _winW, _winH;     // window (DIB) resolution
        private byte[] _dome;         // output: dome RGBA (outRes^2*4)
        private byte[] _win;          // output: window B,G,R,255 (winW*winH*4)
        private bool _hasJob;

        // Cached src-index for (faceRes, domeRes).
        private int[] _index;
        private int _indexFaceRes = -1, _indexDomeRes = -1;

        private bool _resultReady;

        public DomeWorker()
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "US2Dome-Remap" };
            _thread.Start();
        }

        public bool IsBusy { get { lock (_gate) return _hasJob; } }

        // Submit a job. Returns false if a job is already in flight (drop it).
        //
        //   src     = face RGBA bytes (5*faceRes*faceRes*4, R first)
        //   faceRes = face resolution (fr)
        //   outRes  = dome resolution (dr)
        //   winW/H  = window DIB resolution (target for the resample)
        //   dome    = buffer to receive the dome (must be outRes^2*4 bytes)
        //   win     = buffer to receive the window bytes (must be winW*winH*4)
        public bool Submit(byte[] src, int faceRes, int outRes,
                           int winW, int winH, byte[] dome, byte[] win)
        {
            lock (_gate)
            {
                if (_hasJob) return false;
                _src = src;
                _faceRes = faceRes;
                _outRes = outRes;
                _winW = winW;
                _winH = winH;
                _dome = dome;
                _win = win;
                _resultReady = false;
                _hasJob = true;
                Monitor.Pulse(_gate);
            }
            return true;
        }

        // Fetch the completed result (if any). Returns true if a result is
        // ready; the caller then reads the SAME buffers it passed to Submit
        // (they are the worker's output buffers, now filled).
        public bool TryGetResult()
        {
            lock (_gate)
            {
                bool r = _resultReady;
                if (r) _resultReady = false;   // consume once
                return r;
            }
        }

        private int[] GetIndex(int faceRes, int domeRes)
        {
            if (_index == null || _indexFaceRes != faceRes || _indexDomeRes != domeRes)
            {
                _index = DomeRemap.BuildSrcIndex(domeRes, faceRes);
                _indexFaceRes = faceRes;
                _indexDomeRes = domeRes;
            }
            return _index;
        }

        private void Loop()
        {
            while (true)
            {
                byte[] srcCopy, dome, win; int outRes, faceRes, winW, winH;
                lock (_gate)
                {
                    while (!_hasJob && !_quit) Monitor.Wait(_gate);
                    if (_quit) return;
                    // SNAPSHOT the source face bytes so the main thread may reuse
                    // the face buffer immediately after Submit() returns. The
                    // clone runs on THIS worker thread (off the Unity thread),
                    // and _hasJob is only cleared AFTER the clone completes, so
                    // Submit() (and thus the next ReadFaces() that overwrites the
                    // face buffer) can never run while we are still reading it.
                    srcCopy = (byte[])_src.Clone();
                    dome = _dome;
                    win  = _win;
                    outRes = _outRes;
                    faceRes = _faceRes;
                    winW = _winW;
                    winH = _winH;
                    _hasJob = false;
                }

                // Compute OUTSIDE the lock, off the Unity thread (plain loops now).
                bool ok = true;
                long job = Interlocked.Increment(ref _jobSeq);
                DLog("WORKER job #" + job + " START (dome=" + outRes + " face=" + faceRes
                     + " win=" + winW + "x" + winH + " srcBytes=" + srcCopy.Length + ")");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    int[] idx = GetIndex(faceRes, outRes);
                    // 1) Remap faces -> dome.
                    DomeRemap.RemapBytes(srcCopy, dome, idx);
                    // 2) Resample dome -> window DIB-ready bytes (B,G,R,255).
                    DomeRemap.WindowResample(dome, outRes, winH, winW, win);
                }
                catch (Exception e)
                {
                    ok = false;
                    sw.Stop();
                    DLog("WORKER job #" + job + " FAILED after " + sw.ElapsedMilliseconds + " ms: " + e.GetType().Name + ": " + e.Message);
                    LogErr("Worker remap threw (job DROPPED, will retry next capture): " + e);
                }

                if (ok)
                {
                    sw.Stop();
                    lock (_gate)
                    {
                        _resultReady = true;
                    }
                    DLog("WORKER job #" + job + " DONE in " + sw.ElapsedMilliseconds + " ms (result ready)");
                }
            }
        }

        public void Dispose()
        {
            _quit = true;
            lock (_gate) Monitor.PulseAll(_gate);
        }
    }
}
