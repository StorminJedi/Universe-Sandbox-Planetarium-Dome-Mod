// DomeWindow.cs
// Windows-only: creates a SECOND top-level OS window and blits the dome
// (CPU pixels) into it each frame via GDI. Gives the user a separate window
// showing the planetarium dome while the main game window stays normal.
//
// Everything is defensive: if ANY Win32 call fails we log it and report
// "unsupported", so the mod never crashes the game. This code only runs on
// Windows (the BepInEx target). Painting is done by direct GetDC+StretchBlt
// from the update loop (does not depend on WM_PAINT being pumped), so it is
// robust even if Unity's message pumping is lazy.
using System;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace US2Dome
{
    public class DomeWindow : IDisposable
    {
        // ---- Win32 constants ----
        const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
        const uint WS_EX_TOPMOST       = 0x00000008;
        const uint WS_EX_TOOLWINDOW   = 0x00000080;
        const int  WM_DESTROY         = 0x0002;
        const int  WM_CLOSE           = 0x0010;
        const int  SRCCOPY            = 0x00CC0020;
        // fullscreen toggle (v1.5.3)
        const int  GWL_STYLE          = -16;
        const long WS_POPUP_VISIBLE   = 0x90000000L;   // WS_POPUP | WS_VISIBLE
        const uint SWP_FRAMECHANGED   = 0x0020;
        const uint SWP_SHOWWINDOW     = 0x0040;
        const uint SWP_NOSIZE         = 0x0001;
        const uint SWP_NOMOVE         = 0x0002;
        const uint SWP_NOACTIVATE     = 0x0010;
        const int  WM_SETCURSOR       = 0x0020;
        const int  WM_KEYDOWN         = 0x0100;
        const int  WM_LBUTTONDBLCLK   = 0x0203;
        const int  VK_ESCAPE          = 0x1B;
        const int  VK_F11             = 0x7A;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO
        {
            public uint cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int  biWidth;
            public int  biHeight;   // negative => top-down
            public ushort biPlanes;
            public ushort biBitCount;
            public uint  biCompression;
            public uint  biSizeImage;
            public int   biXPelsPerMeter;
            public int   biYPelsPerMeter;
            public uint  biClrUsed;
            public uint  biClrImportant;
        }

        // ---- P/Invoke ----
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern uint RegisterClassExW(ref WNDCLASSEX wc);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateWindowExW(uint exStyle, string cls, string title, uint style,
            int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr lp);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int n);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
        [DllImport("user32.dll")] static extern void PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool AdjustWindowRectEx(ref RECT r, uint style, bool menu, uint exStyle);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtrW(IntPtr h, int idx);
        [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtrW(IntPtr h, int idx, IntPtr val);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint flags);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfoW(IntPtr mon, ref MONITORINFO mi);
        [DllImport("user32.dll")] static extern IntPtr SetCursor(IntPtr c);
        [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll")] static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr old);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
        [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr dc, int x, int y, int w, int h,
            IntPtr src, int sx, int sy, int sw, int sh, int rop);
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bi,
            uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string name);

        const uint DIB_RGB_COLORS = 0;

        delegate IntPtr WNDPROC(IntPtr h, uint m, IntPtr w, IntPtr l);
        // Keep the delegate alive for the process lifetime (GC would dangle the WNDPROC).
        static readonly WNDPROC _wndProcDelegate;
        static readonly IntPtr _wndProcPtr;
        static DomeWindow _instance;

        // ---- Instance state ----
        IntPtr _hwnd = IntPtr.Zero;
        IntPtr _hbmDib = IntPtr.Zero;
        IntPtr _dibBits = IntPtr.Zero;
        // 1x1 opaque-black DIB: used to paint the letterbox bars so the dome
        // can be blitted into a centered SQUARE (staying circular at any
        // window size/aspect ratio, including fullscreen).
        IntPtr _hbmBlack = IntPtr.Zero;
        IntPtr _memDC = IntPtr.Zero;
        IntPtr _oldObj = IntPtr.Zero;
        int    _w, _h;
        byte[] _dibBuf;
        bool   _ready;
        string _err = "";
        bool   _destroying;
        Action _onClose;
        bool   _topmost;
        bool   _fullscreen;
        RECT   _restoreRect;
        IntPtr _restoreStyle = IntPtr.Zero;

        // Diagnostic log hook (wired to US2Dome's thread-safe file logger).
        // Lets us see PushBytes early-return reasons + StretchBlt results, which
        // are otherwise invisible (they used to go to Debug.LogException only).
        public Action<string> OnDebug;
        int _pushCount = 0;
        int _pushOk = 0;
        void DLog(string msg)
        {
            if (OnDebug != null) OnDebug(msg);
        }

        static DomeWindow()
        {
            _wndProcDelegate = new WNDPROC(Proc);
            _wndProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
        }

        public bool Ready { get { return _ready; } }
        public bool IsFullscreen { get { return _fullscreen; } }

        // Borderless fullscreen on whichever monitor the window is currently on.
        // Toggle with F11 / double-click while the dome window has focus; Esc exits.
        public void SetFullscreen(bool on)
        {
            if (!_ready || _hwnd == IntPtr.Zero || _fullscreen == on) return;
            try
            {
                if (on)
                {
                    GetWindowRect(_hwnd, out _restoreRect);
                    _restoreStyle = GetWindowLongPtrW(_hwnd, GWL_STYLE);
                    MONITORINFO mi = new MONITORINFO();
                    mi.cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFO));
                    IntPtr mon = MonitorFromWindow(_hwnd, 2);   // MONITOR_DEFAULTTONEAREST
                    if (mon == IntPtr.Zero || !GetMonitorInfoW(mon, ref mi)) { DLog("[US2Dome] Fullscreen: GetMonitorInfo failed"); return; }
                    SetWindowLongPtrW(_hwnd, GWL_STYLE, new IntPtr(WS_POPUP_VISIBLE));
                    SetWindowPos(_hwnd, new IntPtr(-1), mi.rcMonitor.left, mi.rcMonitor.top,
                        mi.rcMonitor.right - mi.rcMonitor.left, mi.rcMonitor.bottom - mi.rcMonitor.top,
                        SWP_FRAMECHANGED | SWP_SHOWWINDOW);
                    _fullscreen = true;
                    DLog("[US2Dome] Dome window -> fullscreen " + (mi.rcMonitor.right - mi.rcMonitor.left) + "x" + (mi.rcMonitor.bottom - mi.rcMonitor.top));
                }
                else
                {
                    SetWindowLongPtrW(_hwnd, GWL_STYLE, _restoreStyle);
                    SetWindowPos(_hwnd, new IntPtr(_topmost ? -1 : -2), _restoreRect.left, _restoreRect.top,
                        _restoreRect.right - _restoreRect.left, _restoreRect.bottom - _restoreRect.top,
                        SWP_FRAMECHANGED | SWP_SHOWWINDOW);
                    _fullscreen = false;
                    DLog("[US2Dome] Dome window -> windowed");
                }
            }
            catch (Exception e) { DLog("[US2Dome] SetFullscreen THREW: " + e.Message); }
        }
        public void ToggleFullscreen() { SetFullscreen(!_fullscreen); }

        // v1.6.0: change "always on top" while the window is open.
        public void SetTopmost(bool on)
        {
            _topmost = on;
            if (!_ready || _hwnd == IntPtr.Zero || _fullscreen) return;   // fullscreen is always topmost; restored on exit
            try { SetWindowPos(_hwnd, new IntPtr(on ? -1 : -2), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE); }
            catch (Exception e) { DLog("[US2Dome] SetTopmost THREW: " + e.Message); }
        }
        [DllImport("user32.dll")] static extern IntPtr MonitorFromRect(ref RECT r, uint flags);

        // v1.6.8: outer rectangle of the window in its NORMAL (non-fullscreen) state, in virtual-screen pixels
        // (this is what gets remembered so the window comes back on the same monitor).
        public bool GetNormalRect(out int x, out int y, out int w, out int h)
        {
            x = 0; y = 0; w = 0; h = 0;
            if (!_ready || _hwnd == IntPtr.Zero) return false;
            try
            {
                RECT r;
                if (_fullscreen) r = _restoreRect;
                else if (!GetWindowRect(_hwnd, out r)) return false;
                if (r.left <= -30000 || r.top <= -30000) return false;   // minimized
                x = r.left; y = r.top; w = r.right - r.left; h = r.bottom - r.top;
                return w > 0 && h > 0;
            }
            catch { return false; }
        }

        // Move/resize to a remembered rectangle, but only if it still touches a connected monitor
        // (so an unplugged projector can't leave the window off-screen).
        public bool SetNormalRect(int x, int y, int w, int h)
        {
            if (!_ready || _hwnd == IntPtr.Zero || _fullscreen) return false;
            if (w < 200 || h < 200) return false;
            try
            {
                RECT r = new RECT(); r.left = x; r.top = y; r.right = x + w; r.bottom = y + h;
                if (MonitorFromRect(ref r, 0) == IntPtr.Zero)   // MONITOR_DEFAULTTONULL
                { DLog("[US2Dome] Saved window position is off every connected monitor; using the default position."); return false; }
                SetWindowPos(_hwnd, new IntPtr(_topmost ? -1 : -2), x, y, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
                return true;
            }
            catch (Exception e) { DLog("[US2Dome] SetNormalRect THREW: " + e.Message); return false; }
        }

        public string Error { get { return _err; } }

        // v1.6.5: size of the dome bitmap, and of the square the dome is drawn into (min of the client size).
        public int DibSide { get { return _w; } }
        public int ClientSide()
        {
            if (!_ready || _hwnd == IntPtr.Zero) return 0;
            RECT r; if (!GetClientRect(_hwnd, out r)) return 0;
            return Math.Min(r.right - r.left, r.bottom - r.top);
        }

        // Re-create the DIB at side x side so the blit is always 1:1 (GDI HALFTONE stretching is slow software work).
        public bool ResizeDib(int side)
        {
            if (!_ready || _hwnd == IntPtr.Zero || _memDC == IntPtr.Zero || side < 16) return false;
            if (side == _w && side == _h) return true;
            try
            {
                BITMAPINFOHEADER bi = new BITMAPINFOHEADER();
                bi.biSize = 40; bi.biWidth = side; bi.biHeight = -side; bi.biPlanes = 1; bi.biBitCount = 32; bi.biCompression = 0;
                IntPtr sdc = GetDC(IntPtr.Zero);
                IntPtr bits;
                IntPtr nb = CreateDIBSection(sdc, ref bi, DIB_RGB_COLORS, out bits, IntPtr.Zero, 0);
                ReleaseDC(IntPtr.Zero, sdc);
                if (nb == IntPtr.Zero || bits == IntPtr.Zero) { DLog("[US2Dome] ResizeDib(" + side + ") CreateDIBSection failed"); return false; }
                SelectObject(_memDC, nb);
                if (_hbmDib != IntPtr.Zero) DeleteObject(_hbmDib);
                _hbmDib = nb; _dibBits = bits; _w = side; _h = side;
                _dibBuf = new byte[4];   // only the legacy CPU Push() path used it
                DLog("[US2Dome] Window bitmap resized to " + side + "px");
                return true;
            }
            catch (Exception e) { DLog("[US2Dome] ResizeDib THREW: " + e.Message); return false; }
        }

        public DomeWindow(int width, int height, bool topmost, Action onClose)
        {
            _w = width; _h = height;
            _onClose = onClose;
            _topmost = topmost;

            if (System.Environment.OSVersion.Platform != PlatformID.Win32NT)
            { _err = "non-Windows platform"; return; }

            try
            {
                WNDCLASSEX wc = new WNDCLASSEX();
                wc.cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEX));
                wc.lpfnWndProc = _wndProcPtr;
                wc.hInstance = GetModuleHandleW(null);
                wc.style = 0x0008;            // CS_DBLCLKS (double-click toggles fullscreen)
                wc.hbrBackground = IntPtr.Zero;
                wc.lpszClassName = "US2DomeWindowClass";
                uint atom = RegisterClassExW(ref wc);
                if (atom == 0)
                {
                    int rerr = Marshal.GetLastWin32Error();
                    if (rerr != 1410)   // ERROR_CLASS_ALREADY_EXISTS is fine: the window is being re-created (v1.6.0)
                    { _err = "RegisterClassExW failed (err " + rerr + ")"; return; }
                }

                uint ex = topmost ? WS_EX_TOPMOST : 0;
                // v1.6.3: width/height are the DOME size, but CreateWindowEx takes the OUTER size. With the borders
                // and title bar the client area ended up smaller than the DIB, so every frame took the slow
                // HALFTONE StretchBlt path. Ask for an outer size whose CLIENT area is exactly width x height
                // (1:1 blit, no resampling at the default window size).
                int outerW = width, outerH = height;
                try
                {
                    RECT rc = new RECT(); rc.left = 0; rc.top = 0; rc.right = width; rc.bottom = height;
                    if (AdjustWindowRectEx(ref rc, WS_OVERLAPPEDWINDOW, false, ex)) { outerW = rc.right - rc.left; outerH = rc.bottom - rc.top; }
                }
                catch { }
                _hwnd = CreateWindowExW(ex, "US2DomeWindowClass", "US2Dome - 180\u00b0 Planetarium",
                    WS_OVERLAPPEDWINDOW, -1, -1, outerW, outerH,
                    IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
                if (_hwnd == IntPtr.Zero)
                { _err = "CreateWindowExW failed (err " + Marshal.GetLastWin32Error() + ")"; return; }

                // Create a top-down 32bpp DIB (the source bitmap we blit from).
                BITMAPINFOHEADER bi = new BITMAPINFOHEADER();
                bi.biSize = 40;
                bi.biWidth = _w;
                bi.biHeight = -_h;      // top-down
                bi.biPlanes = 1;
                bi.biBitCount = 32;
                bi.biCompression = 0;   // BI_RGB
                IntPtr screenDC = GetDC(IntPtr.Zero);
                _hbmDib = CreateDIBSection(screenDC, ref bi, DIB_RGB_COLORS, out _dibBits, IntPtr.Zero, 0);
                ReleaseDC(IntPtr.Zero, screenDC);
                if (_hbmDib == IntPtr.Zero || _dibBits == IntPtr.Zero)
                { _err = "CreateDIBSection failed (err " + Marshal.GetLastWin32Error() + ")"; return; }
                _dibBuf = new byte[_w * _h * 4];

                // A memory DC that always has the DIB selected, for StretchBlt.
                IntPtr srcDC = GetDC(_hwnd);
                _memDC = CreateCompatibleDC(srcDC);
                ReleaseDC(_hwnd, srcDC);
                if (_memDC == IntPtr.Zero) { _err = "CreateCompatibleDC failed"; return; }
                _oldObj = SelectObject(_memDC, _hbmDib);

                // 1x1 opaque-black DIB for painting the bars.
                BITMAPINFOHEADER bk = new BITMAPINFOHEADER();
                bk.biSize = 40;
                bk.biWidth = 1;
                bk.biHeight = -1;      // top-down
                bk.biPlanes = 1;
                bk.biBitCount = 32;
                bk.biCompression = 0;
                IntPtr sc2 = GetDC(IntPtr.Zero);
                IntPtr bitsB;
                _hbmBlack = CreateDIBSection(sc2, ref bk, DIB_RGB_COLORS, out bitsB, IntPtr.Zero, 0);
                ReleaseDC(IntPtr.Zero, sc2);
                if (_hbmBlack != IntPtr.Zero && bitsB != IntPtr.Zero)
                {
                    // 32bpp DIB byte order in memory is B,G,R,A (little-endian):
                    // 0xFF000000 -> B=0 G=0 R=0 A=255 = opaque BLACK.
                    Marshal.WriteInt32(bitsB, unchecked((int)0xFF000000));
                    SelectObject(_memDC, _hbmBlack);
                    SelectObject(_memDC, _hbmDib);          // restore the dome DIB
                }

                _instance = this;
                ShowWindow(_hwnd, 5);   // SW_SHOW
                _ready = true;
            }
            catch (Exception e)
            { _err = "exception: " + e.Message; _ready = false; }
        }

        // =========================================================================
        // v1.4.0 FAST PATH: PushBytes
        // ---------------------------------------------------------------------
        // The per-pixel resample (dome -> DIB) now runs on the DomeWorker thread
        // (see DomeRemap.WindowResample, proven byte-identical to Push above).
        // This method only does the CHEAP part on the Unity main thread:
        //   * Marshal.Copy the ready-to-go DIB bytes (B,G,R,255) into the DIB,
        //   * black out the letterbox bars,
        //   * StretchBlt the centered square.
        // No per-pixel math here at all.
        //
        // `bgra` = _winBytes from the worker: bufRows*bufCols*4 bytes, B,G,R,255
        //   where bufRows = _h and bufCols = _w (we submit winW=winH=_windowRes).
        // =========================================================================
        public bool PushBytes(byte[] bgra)
        {
            int pc = Interlocked.Increment(ref _pushCount);
            // Every silent early-return is now logged (previously invisible).
            if (!_ready)        { if (pc==1||pc%30==0) DLog("[US2Dome] PushBytes #" + pc + ": SKIP !_ready, err='" + _err + "'"); return false; }
            if (_dibBits == IntPtr.Zero) { if (pc==1||pc%30==0) DLog("[US2Dome] PushBytes #" + pc + ": SKIP _dibBits==null (DIB creation failed)"); return false; }
            if (bgra == null)   { if (pc==1||pc%30==0) DLog("[US2Dome] PushBytes #" + pc + ": SKIP bgra==null"); return false; }
            int bufRows = _h;
            int bufCols = _w;
            int n = bufRows * bufCols * 4;
            if (bgra.Length < n) { DLog("[US2Dome] PushBytes #" + pc + ": SKIP bgra.Length=" + bgra.Length + " < needed " + n + " (worker resample size mismatch!)"); return false; }

            bool result = false;
            try
            {
                Marshal.Copy(bgra, 0, _dibBits, n);   // one bulk copy, no per-pixel loop

                IntPtr dc = GetDC(_hwnd);
                if (dc == IntPtr.Zero) { DLog("[US2Dome] PushBytes #" + pc + ": FAIL GetDC(_hwnd) returned NULL (hWnd=" + _hwnd + ")"); return false; }

                RECT r;
                GetClientRect(_hwnd, out r);
                int cw = r.right - r.left; if (cw < 1) cw = 1;
                int ch = r.bottom - r.top; if (ch < 1) ch = 1;

                // v1.5.1 FLICKER FIX: never blank the dome area. The old code painted
                // the WHOLE client black and then drew the dome, so every frame the
                // screen briefly showed black. Now only the letterbox BARS are painted
                // black (four thin rects), and the dome square is overwritten in place.
                int side = Math.Min(cw, ch);
                int ox = (cw - side) / 2;
                int oy = (ch - side) / 2;
                bool blackOk = true;
                if (_hbmBlack != IntPtr.Zero)
                {
                    SelectObject(_memDC, _hbmBlack);
                    if (ox > 0)
                    {
                        blackOk &= StretchBlt(dc, 0, 0, ox, ch, _memDC, 0, 0, 1, 1, SRCCOPY);
                        blackOk &= StretchBlt(dc, ox + side, 0, cw - ox - side, ch, _memDC, 0, 0, 1, 1, SRCCOPY);
                    }
                    if (oy > 0)
                    {
                        blackOk &= StretchBlt(dc, 0, 0, cw, oy, _memDC, 0, 0, 1, 1, SRCCOPY);
                        blackOk &= StretchBlt(dc, 0, oy + side, cw, ch - oy - side, _memDC, 0, 0, 1, 1, SRCCOPY);
                    }
                    SelectObject(_memDC, _hbmDib);
                }

                // 2) Blit the dome into a centered square (1:1, no stretch).
                // v1.5.4: when the window is not exactly the dome size, GDI would rescale with
                // nearest-neighbour (jaggies + flickering stars). HALFTONE = proper filtered resize.
                if (side != _w || side != _h)
                {
                    SetStretchBltMode(dc, 4);              // HALFTONE
                    SetBrushOrgEx(dc, 0, 0, IntPtr.Zero);  // required after HALFTONE
                }
                bool domeOk = StretchBlt(dc, ox, oy, side, side, _memDC, 0, 0, _w, _h, SRCCOPY);
                ReleaseDC(_hwnd, dc);
                result = true;
                if (pc == 1 || pc % 30 == 0)
                {
                    int ok = Interlocked.Increment(ref _pushOk);
                    DLog("[US2Dome] PushBytes #" + pc + " (" + ok + " total OK): client=" + cw + "x" + ch +
                        " side=" + side + " blackBlt=" + blackOk + " domeBlt=" + domeOk +
                        " (StretchBlt=false means GDI failed to draw)");
                }
            }
            catch (Exception e)
            {
                // Log the FULL exception (was Debug.LogException -> only in
                // BepInEx's LogOutput, not our US2Dome.log file).
                DLog("[US2Dome] PushBytes #" + pc + " THREW: " + e.GetType().FullName + " : " + e.Message);
                if (e.StackTrace != null)
                    DLog("[US2Dome] PushBytes #" + pc + "   stack: " + e.StackTrace.Replace("\n", "\n  "));
                return false;
            }
            return result;
        }

        // Push new dome pixels (Color32[], length _w*_h) and blit to the window.
        //
        // Orientation: the dome is stored bottom-up (row 0 = BOTTOM of the
        // dome; the in-game overlay relies on that). A GDI DIB is top-down
        // (row 0 = TOP of the window), so we copy the dome rows in reverse
        // order: window top row <- dome top row. This makes the second window
        // match the correctly-oriented main overlay.
        //
        // Aspect: we blit into a centered SQUARE of side min(cw, ch) and paint
        // the rest black, so the dome stays a CIRCLE at any window size or
        // aspect ratio (including fullscreen). No stretching.
        public void Push(Color32[] px)
        {
            if (!_ready || _dibBits == IntPtr.Zero || px == null || px.Length == 0) return;
            try
            {
                int srcSize = (int)Mathf.Sqrt(px.Length);   // source dome is square S x S
                if (srcSize < 2) srcSize = 2;
                int sMax = srcSize - 1;
                int bufRows = _dibBuf.Length / 4 / _w;      // DIB rows available
                if (bufRows > _h) bufRows = _h;
                int bufCols = _w;

                // Fill the DIB (top-down) by resampling the square source dome.
                // wrow = 0 is the WINDOW TOP -> sample the DOME TOP (row sMax).
                for (int wrow = 0; wrow < bufRows; wrow++)
                {
                    int srcRow = (bufRows == 1) ? sMax
                                  : (int)Mathf.Round((float)(bufRows - 1 - wrow) * sMax / (float)(bufRows - 1));
                    int rowBase = wrow * _w * 4;
                    for (int c = 0; c < bufCols; c++)
                    {
                        int srcCol = (bufCols == 1) ? sMax
                                      : (int)Mathf.Round((float)c * sMax / (float)(bufCols - 1));
                        Color32 col = px[srcRow * srcSize + srcCol];
                        int p = rowBase + c * 4;
                        _dibBuf[p]     = col.b;
                        _dibBuf[p + 1] = col.g;
                        _dibBuf[p + 2] = col.r;
                        _dibBuf[p + 3] = 255; // opaque (32bpp GDI: 0 = transparent!)
                    }
                }
                Marshal.Copy(_dibBuf, 0, _dibBits, bufRows * bufCols * 4);

                IntPtr dc = GetDC(_hwnd);
                if (dc == IntPtr.Zero) return;

                RECT r;
                GetClientRect(_hwnd, out r);
                int cw = r.right - r.left; if (cw < 1) cw = 1;
                int ch = r.bottom - r.top; if (ch < 1) ch = 1;

                // 1) Black out the whole client area (the letterbox bars).
                if (_hbmBlack != IntPtr.Zero)
                {
                    SelectObject(_memDC, _hbmBlack);
                    StretchBlt(dc, 0, 0, cw, ch, _memDC, 0, 0, 1, 1, SRCCOPY);
                    SelectObject(_memDC, _hbmDib);
                }

                // 2) Blit the dome into a centered square (1:1, no stretch).
                int side = Math.Min(cw, ch);
                int ox = (cw - side) / 2;
                int oy = (ch - side) / 2;
                StretchBlt(dc, ox, oy, side, side, _memDC, 0, 0, _w, _h, SRCCOPY);
                ReleaseDC(_hwnd, dc);
            }
            catch (Exception e)
            { Debug.LogException(e); }
        }

        // WNDPROC (must be static for the delegate). Only handles close/destroy;
        // everything else falls through to DefWindowProcW.
        static IntPtr Proc(IntPtr h, uint m, IntPtr w, IntPtr l)
        {
            switch (m)
            {
                case (uint)WM_KEYDOWN:
                {
                    int vk = (int)w.ToInt64();
                    if (_instance != null)
                    {
                        if (vk == VK_F11) _instance.ToggleFullscreen();
                        else if (vk == VK_ESCAPE && _instance._fullscreen) _instance.SetFullscreen(false);
                    }
                    return IntPtr.Zero;
                }
                case (uint)WM_LBUTTONDBLCLK:
                    if (_instance != null) _instance.ToggleFullscreen();
                    return IntPtr.Zero;
                case (uint)WM_SETCURSOR:
                    // hide the mouse cursor over the dome while fullscreen (projector)
                    if (_instance != null && _instance._fullscreen) { SetCursor(IntPtr.Zero); return new IntPtr(1); }
                    return DefWindowProcW(h, m, w, l);
                case (uint)WM_CLOSE:
                    return DefWindowProcW(h, m, w, l);  // triggers DestroyWindow -> WM_DESTROY
                case (uint)WM_DESTROY:
                {
                    if (_instance != null)
                    {
                        DomeWindow self = _instance;
                        _instance = null;
                        self._ready = false;
                        self._destroying = true;
                        Action cb = self._onClose;
                        self._onClose = null;
                        if (cb != null)
                        {
                            try { cb(); }
                            catch (Exception e) { Debug.LogException(e); }
                        }
                    }
                    return IntPtr.Zero;
                }
                default:
                    return DefWindowProcW(h, m, w, l);
            }
        }

        public void Dispose()
        {
            if (_destroying) return;   // window is closing itself; avoid re-entrancy
            try
            {
                if (_hwnd != IntPtr.Zero)
                {
                    // v1.6.0: a programmatic Dispose is NOT "the user closed the window", so drop the
                    // close callback, and destroy SYNCHRONOUSLY (we are on the creating thread) so a
                    // later re-create can never be torn down by this window's late WM_DESTROY.
                    _onClose = null;
                    _destroying = true;
                    IntPtr hh = _hwnd;
                    _hwnd = IntPtr.Zero;
                    DestroyWindow(hh);
                }
            }
            catch (Exception e) { Debug.LogException(e); }
            finally
            {
                _ready = false;
                CleanupGdi();
            }
        }

        void CleanupGdi()
        {
            if (_memDC != IntPtr.Zero)
            {
                SelectObject(_memDC, _oldObj);
                DeleteDC(_memDC);
                _memDC = IntPtr.Zero;
            }
            if (_hbmDib != IntPtr.Zero) { DeleteObject(_hbmDib); _hbmDib = IntPtr.Zero; }
            if (_hbmBlack != IntPtr.Zero) { DeleteObject(_hbmBlack); _hbmBlack = IntPtr.Zero; }
        }
    }
}
