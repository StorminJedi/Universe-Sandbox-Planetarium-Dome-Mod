// US2DomeSettings.cs  (v1.6.0)
// In-game settings panel: press the hotkey (default F2, config: Controls/SettingsHotkey)
// to open/close a window on the GAME screen with every config option.
//
//   * Most options apply instantly (tilt, zoom, overlay, window on/off, fullscreen, ...).
//   * Options that need the capture pipeline rebuilt (resolutions, mipmaps, MSAA) are edited
//     freely and then applied with the "Apply" button (one rebuild for all of them).
//   * Values are saved to BepInEx/config/US2Dome.cfg when the panel is closed / a mouse
//     button is released.
//
// Pure IMGUI (OnGUI), so it works regardless of the game's input backend and never
// shows up in the dome output (the dome only contains the 5 face cameras).
using System;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;

namespace US2Dome
{
    public partial class US2Dome
    {
        // ---- config entry owned by this file (bound in Awake) ----
        ConfigEntry<string> _cfgHotkey;

        // ---- panel state ----
        bool    _panelOpen;
        bool    _panelFull;        // false = simple menu (F2), true = full menu (Shift+F2)
        Rect    _panelRect = new Rect(30f, 30f, 660f, 700f);
        Vector2 _panelScroll;
        bool    _hotkeyHeld;       // swallow key auto-repeat
        bool    _capturingKey;     // "press a key" rebinding mode
        bool    _cfgDirty;
        KeyCode _hotkey = KeyCode.F2;
        Rect    _panelScreenRect;          // panel rect in screen pixels (top-left origin), for mouse hit-testing
        bool    _inputSysLogged;
        string  _editName = "";            // number box currently being edited
        string  _editText = "";

        // ---- values the running pipeline was built with ----
        float   _appliedZoom = 1f;
        bool    _appliedMips;
        int     _appliedMsaa = 4;
        int     _winCreatedRes;
        float   _zoomEditTime = -10f;
        float   _resetArmedUntil = -10f;   // "Reset all to defaults" needs a second click within a few seconds

        static readonly string[] AnchorNames = { "TopLeft", "TopRight", "BottomLeft", "BottomRight" };

        // =========================================================================
        //  helpers
        // =========================================================================
        static int NormMsaa(int v) { return (v >= 8) ? 8 : (v >= 4) ? 4 : (v >= 2) ? 2 : 1; }

        // Parse a KeyCode name WITHOUT Enum.Parse (the game's netstandard facade strips some
        // reflection-ish overloads; ToString() on an enum is always available).
        void ParseHotkey()
        {
            string want = (_cfgHotkey != null && _cfgHotkey.Value != null) ? _cfgHotkey.Value.Trim() : "F2";
            _hotkey = KeyCode.F2;
            for (int i = 1; i < 512; i++)
            {
                KeyCode k = (KeyCode)i;
                if (string.Equals(k.ToString(), want, StringComparison.OrdinalIgnoreCase)) { _hotkey = k; return; }
            }
            LogWarn("[US2Dome] SettingsHotkey '" + want + "' is not a valid key name; using F2.");
        }

        void SaveCfgNow()
        {
            if (!_cfgDirty || _cfgFile == null) return;
            try { _cfgFile.Save(); _cfgDirty = false; }
            catch (Exception e) { LogWarn("[US2Dome] Saving config failed: " + e.Message); }
        }

        int DesiredFaceRes() { return Mathf.Max(64, Mathf.NextPowerOfTwo(_cfgFaceRes.Value)); }
        int DesiredRtRes()
        {
            return _cfgWindow.Value ? Mathf.Clamp(_cfgWindowRes.Value, 256, 8192)
                                    : Mathf.Max(64, Mathf.NextPowerOfTwo(_cfgDomeRes.Value));
        }

        bool NeedsRebuild()
        {
            if (_initFailed) return true;
            if (!_initDone) return false;
            return DesiredFaceRes() != _faceRes || DesiredRtRes() != _rtResCfg ||
                   _cfgMips.Value != _appliedMips || NormMsaa(_cfgMsaa.Value) != _appliedMsaa;
        }

        string PendingText()
        {
            if (_initFailed) return "Initialization failed earlier - Apply retries it.";
            if (!NeedsRebuild()) return "";
            string t = "Pending (click Apply):";
            if (DesiredFaceRes() != _faceRes) t += " FaceResolution";
            if (DesiredRtRes() != _rtResCfg) t += _cfgWindow.Value ? " SecondWindowResolution" : " DomeResolution";
            if (_cfgMips.Value != _appliedMips) t += " FaceMipmaps";
            if (NormMsaa(_cfgMsaa.Value) != _appliedMsaa) t += " FaceMSAA";
            return t;
        }

        // Called every Update(): applies the zoom a moment after the slider stops moving
        // (rebuilding the warp meshes on every slider tick would stutter).
        void TickSettings()
        {
            if (_gpu == null || !_gpu.Ready || _cfgZoom == null) return;
            float z = Mathf.Clamp(_cfgZoom.Value, 1f, 8f);
            if (Mathf.Abs(z - _appliedZoom) > 1e-4f && Time.unscaledTime - _zoomEditTime > 0.3f)
            {
                try { _gpu.SetZoom(z); _appliedZoom = z; }
                catch (Exception e) { LogErrorDetail("[US2Dome] SetZoom THREW", e); _appliedZoom = z; }
            }
        }

        // Tear down face cameras / RTs / GPU warp and build them again from the current config.
        void ApplyRebuild()
        {
            LogInfo("[US2Dome] Applying settings (rebuilding capture pipeline)...");
            try
            {
                int newWin = Mathf.Clamp(_cfgWindowRes.Value, 256, 8192);
                // The window's bitmap is fixed-size: re-create it only if its size changes.
                if (_domeWindow != null && _winCreatedRes != newWin) { _domeWindow.Dispose(); _domeWindow = null; }

                DestroyWindowCams();
                for (int i = 0; i < 5; i++)
                {
                    if (_faceCams[i] != null)
                    {
                        _faceCams[i].enabled = false;
                        _faceCams[i].targetTexture = null;
                        UnityEngine.Object.Destroy(_faceCams[i].gameObject);
                        _faceCams[i] = null;
                    }
                    if (_faceRTs[i] != null)
                    {
                        _faceRTs[i].Release();
                        UnityEngine.Object.Destroy(_faceRTs[i]);
                        _faceRTs[i] = null;
                    }
                }
                if (_gpu != null) { _gpu.Dispose(); _gpu = null; }
                _willCapture = false;
                _winBytes = null;

                _domeRes   = Mathf.Max(64, Mathf.NextPowerOfTwo(_cfgDomeRes.Value));
                _faceRes   = Mathf.Max(64, Mathf.NextPowerOfTwo(_cfgFaceRes.Value));
                _windowRes = newWin;
                _initDone = false;
                _initFailed = false;
                EnsureInitialized();
            }
            catch (Exception e) { LogErrorDetail("[US2Dome] ApplyRebuild THREW", e); }
        }

        // =========================================================================
        //  hotkey / key capture  (called first thing in OnGUI)
        // =========================================================================
        void HandleSettingsInput()
        {
            Event e = Event.current;
            if (e == null || _cfgHotkey == null) return;

            if (e.type == EventType.KeyUp && e.keyCode == _hotkey) { _hotkeyHeld = false; return; }
            if (e.type != EventType.KeyDown) return;

            if (_capturingKey)
            {
                if (e.keyCode == KeyCode.Escape) _capturingKey = false;
                else if (e.keyCode != KeyCode.None)
                {
                    _hotkey = e.keyCode;
                    _cfgHotkey.Value = _hotkey.ToString();
                    _cfgDirty = true;
                    _capturingKey = false;
                    _hotkeyHeld = true;      // don't toggle the panel with the key we just bound
                    SaveCfgNow();
                }
                e.Use();
                return;
            }

            if (e.keyCode == _hotkey && !_hotkeyHeld)
            {
                _hotkeyHeld = true;
                bool wantFull = e.shift;                       // hotkey = simple menu, Shift+hotkey = full menu
                if (_panelOpen && _panelFull != wantFull)
                {
                    _panelFull = wantFull;                     // the other menu is open: switch to the one asked for
                    _panelScroll = Vector2.zero;
                    GUI.FocusControl("");
                    return;
                }
                _panelOpen = !_panelOpen;
                if (_panelOpen)
                {
                    _panelFull = wantFull;
                    _panelScroll = Vector2.zero;
                    if (!_inputSysLogged) { _inputSysLogged = true; LogInfo("[US2Dome] Input systems loaded: " + InputBlocker.DescribeInputSystems() + " (panel click-blocking only works for legacy UnityEngine.Input)"); }
                }
                else { _capturingKey = false; GUI.FocusControl(""); SaveCfgNow(); }
            }
        }

        // =========================================================================
        //  mouse gate: tells InputBlocker when the game must not see the mouse
        // =========================================================================
        bool PanelContainsPoint(Vector2 p)
        {
            return _panelOpen && _panelScreenRect.Contains(p);
        }

        // Called from OnGUI BEFORE the panel window consumes the event (t0/mp0 = the event as it arrived).
        // The game's Update() runs before OnGUI, so it sees the flags from the previous pass: that is why
        // "pointer over panel" is tracked continuously (Repaint carries the mouse position every frame)
        // and not only on click.
        void UpdateMouseGate(EventType t0, Vector2 mp0)
        {
            InputBlocker.PanelOpen = _panelOpen;
            InputBlocker.CapturingKey = _capturingKey;
            if (t0 == EventType.Repaint || t0 == EventType.KeyDown || t0 == EventType.KeyUp ||
                t0 == EventType.MouseDown || t0 == EventType.MouseUp)
                InputBlocker.TextFocus = _panelOpen && GUI.GetNameOfFocusedControl().StartsWith("US2D_");
            if (!_panelOpen)
            {
                InputBlocker.MouseOver = false;
                InputBlocker.MouseCaptured = false;
                return;
            }
            bool inside = _panelScreenRect.Contains(mp0);
            if (t0 == EventType.MouseDown && inside) InputBlocker.MouseCaptured = true;
            if (t0 == EventType.MouseUp) InputBlocker.MouseCaptured = false;   // the game's Update already saw this frame's release as blocked
            if (t0 == EventType.Repaint || t0 == EventType.MouseDown || t0 == EventType.MouseUp ||
                t0 == EventType.MouseDrag || t0 == EventType.MouseMove || t0 == EventType.ScrollWheel)
                InputBlocker.MouseOver = inside;
        }

        // =========================================================================
        //  panel
        // =========================================================================
        void DrawSettingsPanel()
        {
            if (!_panelOpen || _cfgHotkey == null) return;

            float s = Mathf.Clamp(Screen.height / 1080f, 1f, 2.5f);   // keep it readable on 4K
            float vw = Screen.width / s, vh = Screen.height / s;
            Matrix4x4 oldMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));
            try
            {
                _panelRect.width = Mathf.Min(660f, vw - 20f);
                _panelRect.height = Mathf.Min(700f, vh - 20f);
                _panelRect = GUI.Window(0x55D0E5, _panelRect, DrawSettingsWindow,
                    _panelFull ? ("US2Dome settings - ALL   (" + _hotkey + " or Shift+" + _hotkey + " closes)")
                                   : ("US2Dome settings   (" + _hotkey + " closes, Shift+" + _hotkey + " = all settings)"));
                _panelRect.x = Mathf.Clamp(_panelRect.x, 0f, Mathf.Max(0f, vw - 120f));
                _panelRect.y = Mathf.Clamp(_panelRect.y, 0f, Mathf.Max(0f, vh - 40f));
            }
            finally { GUI.matrix = oldMatrix; }
            _panelScreenRect = new Rect(_panelRect.x * s, _panelRect.y * s, _panelRect.width * s, _panelRect.height * s);
        }

        // ---- small control helpers (return true when the value changed) ----
        bool UiToggle(string label, ConfigEntry<bool> e)
        {
            bool nv = GUILayout.Toggle(e.Value, label);
            if (nv == e.Value) return false;
            e.Value = nv; _cfgDirty = true; return true;
        }

        static string Fmt(float v, int decimals) { return v.ToString("F" + decimals, CultureInfo.InvariantCulture); }

        static bool TryParseF(string t, out float v)
        {
            v = 0f;
            if (t == null) return false;
            t = t.Trim().Replace(',', '.');
            if (t.Length == 0 || t == "-" || t == "." || t == "-.") return false;
            return float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        // A small text box for typing an exact value. Applies as soon as the text is a valid number
        // (clamped to lo..hi); while it has focus it shows what you typed, otherwise the current value.
        // Enter / Esc (handled in DrawSettingsWindow) or touching the slider leaves the box.
        bool UiNumField(string name, float current, int decimals, float lo, float hi, out float value)
        {
            value = current;
            bool focused = GUI.GetNameOfFocusedControl() == name;
            string shown;
            if (focused)
            {
                if (_editName != name) { _editName = name; _editText = Fmt(current, decimals); }
                shown = _editText;
            }
            else
            {
                if (_editName == name) _editName = "";
                shown = Fmt(current, decimals);
            }
            GUI.SetNextControlName(name);
            string nt = GUILayout.TextField(shown, 9, GUILayout.Width(64f));
            if (focused && nt != _editText)
            {
                _editText = nt;
                float p;
                if (TryParseF(nt, out p))
                {
                    p = Mathf.Clamp(p, lo, hi);
                    p = (float)Math.Round(p, decimals);
                    if (Math.Abs(p - current) > 1e-6f) { value = p; return true; }
                }
            }
            return false;
        }

        // v1.6.6: our own horizontal slider. The built-in GUILayout.HorizontalSlider dropped the drag after the first
        // change (the drag is owned by IMGUI's hot control, which something reset). Here the drag state is OUR field
        // (_sliderDrag, keyed by label), absolute mouse->value mapping, ended by any MouseUp (see EndSliderDragOnMouseUp).
        string _sliderDrag = null;
        bool   _blurPending;
        Texture2D _white;

        // Called from OnGUI AFTER the panel has been drawn: a drag can never outlive the mouse button, and a number
        // box that was kept in sync during the drag is released now.
        void EndSliderDragOnMouseUp(EventType t0)
        {
            if (t0 != EventType.MouseUp) return;
            _sliderDrag = null;
            if (_blurPending) { _blurPending = false; GUI.FocusControl(""); }
        }

        float UiSliderBar(string key, float v, float min, float max)
        {
            int id = GUIUtility.GetControlID(FocusType.Passive);
            Rect r = GUILayoutUtility.GetRect(80f, 20f, GUILayout.ExpandWidth(true));
            Event ev = Event.current;
            const float tw = 14f;
            float span = Mathf.Max(1f, r.width - tw);
            float range = Mathf.Max(1e-6f, max - min);
            float nv = v;

            if (ev.type == EventType.MouseDown && ev.button == 0 && r.Contains(ev.mousePosition))
            {
                _sliderDrag = key;
                GUIUtility.hotControl = id;
                ev.Use();
                nv = min + Mathf.Clamp01((ev.mousePosition.x - r.x - tw * 0.5f) / span) * range;
            }
            else if (_sliderDrag == key && ev.type == EventType.MouseDrag)
            {
                ev.Use();
                nv = min + Mathf.Clamp01((ev.mousePosition.x - r.x - tw * 0.5f) / span) * range;
            }
            else if (_sliderDrag == key && ev.type == EventType.MouseUp)
            {
                _sliderDrag = null;
                if (GUIUtility.hotControl == id) GUIUtility.hotControl = 0;
                ev.Use();
            }

            if (ev.type == EventType.Repaint)
            {
                if (_white == null) _white = Texture2D.whiteTexture;
                float t = Mathf.Clamp01((Mathf.Clamp(nv, min, max) - min) / range);
                Color oc = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, 0.28f);
                GUI.DrawTexture(new Rect(r.x, r.center.y - 2f, r.width, 4f), _white);
                bool active = (_sliderDrag == key);
                GUI.color = active ? new Color(1f, 0.85f, 0.35f, 1f) : new Color(0.85f, 0.88f, 0.95f, 1f);
                GUI.DrawTexture(new Rect(r.x + t * span, r.center.y - 8f, tw, 16f), _white);
                GUI.color = oc;
            }
            return nv;
        }

        bool UiSlider(string label, ConfigEntry<float> e, float min, float max, int decimals)
        {
            bool changed = false;
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(200f));
            float cur = Mathf.Clamp(e.Value, min, max);
            float raw = UiSliderBar(label, cur, min, max);
            if (Math.Abs(raw - cur) > 1e-6f)          // the slider itself was dragged
            {
                float nv = (float)Math.Round(raw, decimals);
                if (Math.Abs(nv - e.Value) > 1e-6f) { e.Value = nv; _cfgDirty = true; changed = true; }
                if (_editName == "US2D_" + label) _editText = Fmt(e.Value, decimals);   // keep a focused number box in sync
                _blurPending = true;                                                    // leave the box on mouse-up, NOT mid-drag
            }
            float typed;
            if (UiNumField("US2D_" + label, e.Value, decimals, min, max, out typed))
            { e.Value = typed; _cfgDirty = true; changed = true; }
            GUILayout.EndHorizontal();
            return changed;
        }

        // Slider range min..max in steps; typed values may go up to hardMax.
        bool UiIntSlider(string label, ConfigEntry<int> e, int min, int max, int step, int hardMax)
        {
            bool changed = false;
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(200f));
            float cur = Mathf.Clamp(e.Value, min, max);
            float raw = UiSliderBar(label, cur, min, max);
            if (Math.Abs(raw - cur) > 0.5f)
            {
                int nv = (int)(Math.Round(raw / step) * step);
                if (nv < min) nv = min;
                if (nv > max) nv = max;
                if (nv != e.Value) { e.Value = nv; _cfgDirty = true; changed = true; }
                if (_editName == "US2D_" + label) _editText = Fmt(e.Value, 0);
                _blurPending = true;
            }
            float typed;
            if (UiNumField("US2D_" + label, e.Value, 0, min, hardMax, out typed))
            { e.Value = (int)Math.Round(typed); _cfgDirty = true; changed = true; }
            GUILayout.EndHorizontal();
            return changed;
        }

        bool UiChoice(string label, ConfigEntry<int> e, int[] options)
        {
            bool changed = false;
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(200f));
            for (int i = 0; i < options.Length; i++)
            {
                bool on = (e.Value == options[i]);
                bool nv = GUILayout.Toggle(on, options[i].ToString(), GUI.skin.button, GUILayout.MinWidth(46f));
                if (nv && !on) { e.Value = options[i]; _cfgDirty = true; changed = true; }
            }
            GUILayout.EndHorizontal();
            return changed;
        }

        void DrawSettingsWindow(int id)
        {
            Event ke = Event.current;
            if (ke != null && ke.type == EventType.KeyDown &&
                (ke.keyCode == KeyCode.Return || ke.keyCode == KeyCode.KeypadEnter || ke.keyCode == KeyCode.Escape) &&
                GUI.GetNameOfFocusedControl().StartsWith("US2D_"))
            {
                GUI.FocusControl("");
                ke.Use();
            }

            _panelScroll = GUILayout.BeginScrollView(_panelScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar);

            bool full = _panelFull;

            // ---------------- General ----------------
            GUILayout.Label("<b>Main switches</b>", RichLabel());
            UiToggle("Enable the dome renderer", _cfgEnabled);

            bool fsNow = (_domeWindow != null && _domeWindow.Ready) ? _domeWindow.IsFullscreen : _cfgWindowFull.Value;
            bool fsNew = GUILayout.Toggle(fsNow, "Dome window fullscreen  (F11 / double-click in the dome window also works)");
            if (fsNew != fsNow)
            {
                _cfgWindowFull.Value = fsNew; _cfgDirty = true;
                if (_domeWindow != null && _domeWindow.Ready) _domeWindow.SetFullscreen(fsNew);
            }

            if (UiToggle("Second window (separate dome window)", _cfgWindow))
            {
                _windowDisabledByClose = false;
                if (NeedsRebuild()) ApplyRebuild();    // the dome RT size follows the window setting
            }
            if (full)
            {
                if (UiToggle("Second window always on top", _cfgWindowTopmost))
                    if (_domeWindow != null && _domeWindow.Ready) _domeWindow.SetTopmost(_cfgWindowTopmost.Value);
            }
            if (_windowDisabledByClose && _cfgWindow.Value && GUILayout.Button("Dome window was closed - click to reopen it"))
                _windowDisabledByClose = false;

            // ---------------- View ----------------
            GUILayout.Space(6f);
            GUILayout.Label("<b>View</b>", RichLabel());
            UiSlider("Tilt (degrees)", _cfgTilt, -90f, 90f, 1);
            if (full)
            {
                if (UiSlider("Zoom", _cfgZoom, 1f, 8f, 2)) _zoomEditTime = Time.unscaledTime;
                UiChoice("Capture every N frames", _cfgEveryN, new int[] { 1, 2, 3, 4, 6, 8 });
            }

            // ---------------- Planet labels on the dome ----------------
            GUILayout.Space(6f);
            GUILayout.Label("<b>Planet labels on the dome</b>", RichLabel());
            BindNameplateConfig();
            if (_cfgNp != null)
            {
                UiToggle("Show planet/object labels on the dome", _cfgNp);
                UiSlider("Label size", _cfgNpScale, 0.5f, 10f, 2);
                UiSlider("Label offset X (+ = right)", _cfgNpOffX, -200f, 200f, 1);
                UiSlider("Label offset Y (+ = up)", _cfgNpOffY, -200f, 200f, 1);
                UiSlider("Label distance", _cfgNpDist, 0.2f, 10f, 2);
            }

            BindWindowMirrorConfig();
            if (_cfgWm != null)
            {
                // ---------------- Game window on the dome ----------------
                GUILayout.Space(6f);
                GUILayout.Label("<b>Game window on the dome</b> (the planet properties window)", RichLabel());
                UiToggle("Show the window on the dome", _cfgWm);
                UiSlider("Angle from dome centre", _cfgWmAng, 0f, 89f, 1);
                UiSlider("Direction around the dome (0 top, 90 right, 180 bottom)", _cfgWmAz, -180f, 180f, 1);
                UiSlider("Size (degrees, longest side)", _cfgWmSize, 5f, 120f, 1);
                UiToggle("Flip vertically (use if it shows upside-down)", _cfgWmFlip);

                // ---------------- 3D object view ----------------
                GUILayout.Space(6f);
                GUILayout.Label("<b>3D object view</b> (the picture at the top of the properties window)", RichLabel());
                int ovMode = !_cfgOv.Value ? 0 : (_cfgOvShow.Value ? 1 : 2);
                int ovNew = GUILayout.SelectionGrid(ovMode, new string[] { "Keep in properties window", "Own window on the dome", "Remove it" }, 3);
                if (ovNew != ovMode)
                {
                    _cfgOv.Value = (ovNew != 0);
                    _cfgOvShow.Value = (ovNew != 2);
                    _cfgDirty = true;
                }
                if (_cfgOv.Value && _cfgOvShow.Value)
                {
                    UiSlider("3D view: angle from dome centre", _cfgOvAng, 0f, 89f, 1);
                    UiSlider("3D view: direction around the dome (0 top, 90 right, 180 bottom)", _cfgOvAz, -180f, 180f, 1);
                    UiSlider("3D view: size (degrees, longest side)", _cfgOvSize, 5f, 120f, 1);
                    if (full) UiToggle("3D view: use the game's own picture (off = copy from the screen)", _cfgOvDirect);
                    UiToggle("3D view: flip vertically (if upside-down)", _cfgOvFlip);
                }
            }

            // ---------------- Data Views ----------------
            if (_cfgDv != null)
            {
                GUILayout.Space(6f);
                GUILayout.Label("<b>Data Views window on the dome</b> (graphs, docked on the left)", RichLabel());
                UiToggle("Show the Data Views window on the dome", _cfgDv);
                UiSlider("Data Views: angle from dome centre", _cfgDvAng, 0f, 89f, 1);
                UiSlider("Data Views: direction around the dome (0 top, 90 right, 180 bottom)", _cfgDvAz, -180f, 180f, 1);
                UiSlider("Data Views: size (degrees, longest side)", _cfgDvSize, 5f, 120f, 1);
                UiToggle("Data Views: flip vertically (if upside-down)", _cfgDvFlip);
            }

            if (full)
            {
            // ---------------- In-game display ----------------
            GUILayout.Space(6f);
            GUILayout.Label("<b>In-game preview</b>", RichLabel());
            UiToggle("Full-dome mode (fill the game screen)", _cfgFullDome);
            UiToggle("Show overlay preview", _cfgOverlay);
            UiToggle("Hide overlay while the second window is open", _cfgHideOverlayWhenWin);
            UiToggle("Show 'Save PNG' button", _cfgShowSaveButton);
            UiSlider("Overlay size (screen height)", _cfgOverlayScale, 0.05f, 1f, 2);
            int sel = 3;
            for (int i = 0; i < AnchorNames.Length; i++) if (AnchorNames[i] == _cfgOverlayAnchor.Value) sel = i;
            int nsel = GUILayout.SelectionGrid(sel, AnchorNames, 4);
            if (nsel != sel) { _cfgOverlayAnchor.Value = AnchorNames[nsel]; _cfgDirty = true; }

            // ---------------- Quality / resolution (need Apply) ----------------
            GUILayout.Space(6f);
            GUILayout.Label("<b>Quality & resolution</b>  (click Apply after changing)", RichLabel());
            UiChoice("Dome resolution", _cfgDomeRes, new int[] { 512, 1024, 2048, 4096 });
            UiChoice("Face resolution", _cfgFaceRes, new int[] { 256, 512, 1024, 2048 });

            UiToggle("Dome follows window size (keeps the blit 1:1)", _cfgWindowAuto);
            UiIntSlider("Second window resolution", _cfgWindowRes, 256, 4096, 16, 8192);
            GUILayout.BeginHorizontal();
            GUILayout.Label("   presets", GUILayout.Width(200f));
            int[] presets = new int[] { 720, 1080, 1440, 2160 };
            for (int i = 0; i < presets.Length; i++)
                if (GUILayout.Button(presets[i].ToString(), GUILayout.MinWidth(56f)))
                { _cfgWindowRes.Value = presets[i]; _cfgDirty = true; }
            GUILayout.EndHorizontal();

            UiToggle("Face mipmaps (anti-aliasing)", _cfgMips);
            UiChoice("Face MSAA", _cfgMsaa, new int[] { 1, 2, 4, 8 });

            // ---------------- Controls ----------------
            GUILayout.Space(6f);
            GUILayout.Label("<b>Controls</b>", RichLabel());
            GUILayout.BeginHorizontal();
            GUILayout.Label("Settings hotkey", GUILayout.Width(200f));
            if (GUILayout.Button(_capturingKey ? "Press a key...  (Esc cancels)" : (_hotkey.ToString() + "   (click to change)")))
                _capturingKey = true;
            GUILayout.EndHorizontal();
            if (_cfgLogFile != null && UiToggle("Make log file (BepInEx/US2Dome.log) - off by default", _cfgLogFile))
                LogFileOn = _cfgLogFile.Value;

            }

            GUILayout.EndScrollView();

            // ---------------- footer ----------------
            Color oldColor = GUI.color;
            GUI.color = new Color(1f, 0.85f, 0.35f, 1f);
            GUILayout.Label(PendingText());          // always drawn (keeps the IMGUI layout stable)
            GUI.color = oldColor;

            GUILayout.BeginHorizontal();
            if (_panelFull)
            {
                bool oldEnabled = GUI.enabled;
                GUI.enabled = NeedsRebuild();
                if (GUILayout.Button("Apply")) ApplyRebuild();
                GUI.enabled = oldEnabled;
                bool armed = Time.unscaledTime < _resetArmedUntil;
                if (GUILayout.Button(armed ? "Click again to confirm reset" : "Reset all to defaults"))
                {
                    if (armed) { _resetArmedUntil = -10f; ResetAllToDefaults(); }
                    else _resetArmedUntil = Time.unscaledTime + 4f;
                }
            }
            if (GUILayout.Button("Close"))
            {
                _panelOpen = false; _capturingKey = false; GUI.FocusControl(""); SaveCfgNow();
            }
            GUILayout.EndHorizontal();

            GUI.DragWindow(new Rect(0f, 0f, 10000f, 22f));
        }

        // Put EVERY option (all sections of US2Dome.cfg, incl. the nameplate ones and the hotkey) back to its default value.
        void ResetAllToDefaults()
        {
            if (_cfgFile == null) return;
            bool winBefore = _cfgWindow != null && _cfgWindow.Value;
            try
            {
                ConfigDefinition[] keys = new ConfigDefinition[_cfgFile.Keys.Count];
                _cfgFile.Keys.CopyTo(keys, 0);
                for (int i = 0; i < keys.Length; i++)
                {
                    ConfigEntryBase eb = _cfgFile[keys[i]];
                    if (eb != null) eb.BoxedValue = eb.DefaultValue;
                }
            }
            catch (Exception e) { LogErrorDetail("[US2Dome] Reset to defaults THREW", e); }

            _cfgDirty = true;
            ParseHotkey();
            if (_cfgLogFile != null) LogFileOn = _cfgLogFile.Value;
            _capturingKey = false;
            _editName = ""; _editText = "";
            GUI.FocusControl("");
            _zoomEditTime = Time.unscaledTime;            // TickSettings applies the default zoom right away
            if (_domeWindow != null && _domeWindow.Ready)
            {
                _domeWindow.SetFullscreen(_cfgWindowFull.Value);
                _domeWindow.SetTopmost(_cfgWindowTopmost.Value);
            }
            if (_cfgWindow.Value != winBefore)
            {
                _windowDisabledByClose = false;
                if (NeedsRebuild()) ApplyRebuild();
            }
            SaveCfgNow();
            LogInfo("[US2Dome] All settings reset to defaults.");
        }

        GUIStyle _richLabel;
        GUIStyle RichLabel()
        {
            if (_richLabel == null)
            {
                _richLabel = new GUIStyle(GUI.skin.label);
                _richLabel.richText = true;
            }
            return _richLabel;
        }
    }
}
