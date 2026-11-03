// Nameplates.cs -- draws the game's planet/object name labels on the dome.
//
// WHY: the game draws its labels as TextMeshPro UI on a ScreenSpaceOverlay canvas
// ("Gui/Nameplates/Nameplate(Clone)"). Overlay UI is only drawn on top of the real game window,
// so the 5 dome face cameras never see it.
//
// HOW: every capture frame, for each active nameplate we read its text + on-screen position,
// turn that screen position into a world direction with the main camera, and place a copy of the
// text as a normal 3D TextMeshPro object (on a private layer the main camera does not draw) at
// that direction, facing the camera. The face cameras then render it like anything else, and the
// dome warp bends it correctly. All TMP/UI access is by reflection (no extra references needed).
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using BepInEx.Configuration;

namespace US2Dome
{
    public partial class US2Dome
    {
        class NpLabel
        {
            public Component tmp;      // our 3D TextMeshPro
            public string text;        // last text applied
            public float fontSize;     // last font size applied
            public float prefH;        // preferredHeight of our label for that text/size (local units)
            public int stamp;          // last frame this label was driven
            public bool active;
            public Component npComp;   // the game's UINameplate script (holds _body)
            public object bodyObj;     // the Body it currently shows (pooled nameplates get re-assigned)
            public Transform bodyT;    // the body's real 3D object (world position)
            public object sel;         // the body's SelectableComponent (has the game's own screen position of the body)
            public Vector2 lastOff;    // last trustworthy pixel offset of the nameplate from its planet (measured while the planet is in the game window)
            public bool haveOff;
        }

        ConfigEntry<bool>  _cfgNp;
        ConfigEntry<float> _cfgNpScale;
        ConfigEntry<float> _cfgNpDist;
        ConfigEntry<float> _cfgNpOffX;
        ConfigEntry<float> _cfgNpOffY;

        readonly Dictionary<Component, NpLabel> _npLabels = new Dictionary<Component, NpLabel>();
        readonly List<Component> _npTmpKeys = new List<Component>();
        Transform _npRoot;
        int _npRootCheck = -999;
        int _npLayer = -1;
        bool _npTypesDone, _npBroken, _npMatTried, _npFirstLogged;
        Type _tTmp, _tTmpBase, _tGroup, _tCanvas;
        bool _npOn = true;        // are the game's labels switched on? (inferred, see UpdateNameplates)
        // While the labels are switched OFF the game still fades in the label of the object under the mouse. Those hover labels are
        // shown on the dome too, but ONLY for the hovered object (never the "outside the game window" fallback below).
        int  _npOffFrames;
        float _npOnSince = -1f;                  // when 2+ labels first became visible while labels were off
        const float NpOnConfirmSeconds = 1.0f;   // how long that must last to count as "labels switched back on"
        Material _npMat;

        // ---- small reflection helpers ----
        static object GetP(object o, string name)
        {
            if (o == null) return null;
            try
            {
                PropertyInfo p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                return p == null ? null : p.GetValue(o, null);
            }
            catch { return null; }
        }
        static bool SetP(object o, string name, object v)
        {
            if (o == null) return false;
            try
            {
                PropertyInfo p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (p == null || !p.CanWrite) return false;
                p.SetValue(o, v, null);
                return true;
            }
            catch { return false; }
        }
        static bool SetEnumP(object o, string name, string valueName)
        {
            if (o == null) return false;
            try
            {
                PropertyInfo p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (p == null || !p.CanWrite || !p.PropertyType.IsEnum) return false;
                p.SetValue(o, Enum.Parse(p.PropertyType, valueName), null);
                return true;
            }
            catch { return false; }
        }
        static float ToF(object o)
        {
            if (o is float) return (float)o;
            if (o is double) return (float)(double)o;
            if (o is int) return (float)(int)o;
            return 0f;
        }

        // Field-or-property lookup that also finds PRIVATE members of base classes.
        static object GetFP(object o, string name)
        {
            if (o == null) return null;
            try
            {
                BindingFlags bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                Type t = o.GetType();
                while (t != null && t != typeof(object))
                {
                    FieldInfo f = t.GetField(name, bf);
                    if (f != null) return f.GetValue(o);
                    PropertyInfo p = t.GetProperty(name, bf);
                    if (p != null && p.CanRead && p.GetIndexParameters().Length == 0) return p.GetValue(o, null);
                    t = t.BaseType;
                }
            }
            catch { }
            return null;
        }

        // Text colour alpha x the alpha of every CanvasGroup above it (how visible the game currently draws this nameplate).
        float SrcAlphaAll(Component src)
        {
            float a = 1f;
            object co = GetP(src, "color");
            if (co is Color) a = ((Color)co).a;
            if (_tGroup != null)
            {
                Component[] gs = src.GetComponentsInParent(_tGroup);
                for (int i = 0; i < gs.Length; i++) a *= ToF(GetP(gs[i], "alpha"));
            }
            return a;
        }

        // Find the game's UINameplate (on the nameplate root, above the text) and the Body it shows.
        void ResolveBody(NpLabel nl, Component src)
        {
            try
            {
                if (nl.npComp == null)
                {
                    Transform t = src.transform;
                    for (int k = 0; k < 4 && t != null && nl.npComp == null; k++, t = t.parent)
                    {
                        Component[] cs = t.GetComponents<Component>();
                        for (int i = 0; i < cs.Length; i++)
                            if (cs[i] != null && cs[i].GetType().Name == "UINameplate") { nl.npComp = cs[i]; break; }
                    }
                }
                if (nl.npComp == null) return;
                object body = GetFP(nl.npComp, "_body");
                if (object.ReferenceEquals(body, nl.bodyObj)) return;   // unchanged
                nl.bodyObj = body;
                nl.bodyT = null; nl.sel = null;
                nl.haveOff = false;   // a pooled nameplate now shows a different body
                if (body == null) return;
                GameObject go = GetFP(GetFP(body, "GameObject"), "gameObject") as GameObject;
                if (go != null) nl.bodyT = go.transform;
                nl.sel = GetFP(body, "Selectable");
            }
            catch { }
        }

        // Label frame on the dome: "up" points toward the dome CENTRE (zenith), measured along the sphere; right = up x direction.
        static void DomeFrame(Vector3 d, Vector3 zen, Vector3 rigUp, out Vector3 up, out Vector3 right)
        {
            up = zen - d * Vector3.Dot(zen, d);
            if (up.sqrMagnitude < 1e-6f) up = rigUp - d * Vector3.Dot(rigUp, d);   // label right at the centre: use the image's own up
            if (up.sqrMagnitude < 1e-9f) up = Vector3.up;
            up.Normalize();
            right = Vector3.Cross(up, d);
        }

        void BindNameplateConfig()
        {
            if (_cfgNp != null || _cfgFile == null) return;
            _cfgNp = _cfgFile.Bind("Nameplates", "ShowLabels", true,
                "Draw the game's planet/object name labels on the dome (the game draws them as screen UI, which the dome cameras can't see, so the mod re-draws them).");
            _cfgNpScale = _cfgFile.Bind("Nameplates", "LabelSize", 3.0f,
                "Label size on the dome relative to how big the same label looks in the normal game view. A dome spreads 180 degrees over the image, so labels need to be bigger to be readable. 1 = same angular size as in the game window.");
            _cfgNpOffX = _cfgFile.Bind("Nameplates", "LabelOffsetX", 35.0f,
                "Nudge labels sideways, in pixels of the game window (normal-view scale). Measured along each label's own direction, so it turns with the label. Positive = toward the label's right, negative = left.");
            _cfgNpOffY = _cfgFile.Bind("Nameplates", "LabelOffsetY", 0.0f,
                "Nudge labels along their own up direction (toward the dome centre), in game-window pixels. Positive = toward the centre, negative = away.");
            _cfgNpDist = _cfgFile.Bind("Nameplates", "LabelDistance", 0.5f,
                "How far (in game units) from the camera the labels are placed. Only matters if labels get hidden behind a nearby planet; if the overlay shader is unavailable, raise or lower this. Minimum 0.2.");
        }

        // Called every capture frame from Update(), BEFORE the face cameras are enabled.
        void UpdateNameplates(Camera main, Vector3 camPos)
        {
            BindNameplateConfig();
            if (_cfgNp == null) return;
            if (!_cfgNp.Value || _npBroken) { HideAllNp(); return; }

            if (!_npTypesDone)
            {
                _npTypesDone = true;
                _tTmp     = Type.GetType("TMPro.TextMeshPro, Unity.TextMeshPro");
                _tTmpBase = Type.GetType("TMPro.TMP_Text, Unity.TextMeshPro");
                _tGroup   = Type.GetType("UnityEngine.CanvasGroup, UnityEngine.UIModule");
                _tCanvas  = Type.GetType("UnityEngine.Canvas, UnityEngine.UIModule");
                if (_tTmp == null || _tTmpBase == null)
                {
                    _npBroken = true;
                    LogWarn("[US2Dome] Nameplates: TextMeshPro types not found; dome labels disabled.");
                    return;
                }
            }

            if (_npRoot == null && _frame - _npRootCheck >= 30)
            {
                _npRootCheck = _frame;
                GameObject g = GameObject.Find("Gui/Nameplates");
                _npRoot = (g != null) ? g.transform : null;
            }
            if (_npRoot == null) { HideAllNp(); return; }

            float tanHalf = Mathf.Tan(main.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float dist = Mathf.Max(0.2f, _cfgNpDist.Value);
            float sizeMul = Mathf.Max(0.1f, _cfgNpScale.Value);
            // Dome frame: the 5 face cameras are rotated by camRot (the game camera plus the tilt setting). The dome CENTRE is the
            // rig's forward axis and the image's "up" is the rig's up axis (see DomeMath).
            Quaternion camRot = main.transform.rotation * Quaternion.Euler(-Mathf.Clamp(_cfgTilt.Value, -90f, 90f), 0f, 0f);
            Vector3 zen = camRot * Vector3.forward;
            Vector3 rigUp = camRot * Vector3.up;
            float angPerPx = 2f * tanHalf / Mathf.Max(1f, (float)Screen.height);

            // The whole nameplate canvas switched off => labels are off.
            if (_tCanvas != null)
            {
                Behaviour cvb = _npRoot.GetComponent(_tCanvas) as Behaviour;
                if (cvb != null && !cvb.enabled) { HideAllNp(); return; }
            }

            // Is the game's "labels" toggle on? The game does NOT remove nameplates when it is off, it fades them to 0 -- exactly like it
            // does for planets outside ITS view. So a faded nameplate alone can't tell "toggled off" from "just off-screen". Only nameplates
            // that are INSIDE the game's view tell the truth: if any is visible, labels are on; if some are in view and all are faded
            // (for a few frames), labels are off. With nothing in view the last known state is kept (default: on).
            {
                bool anyVis = false, anyHid = false;
                int visCount = 0;
                float pmx = Screen.width * 0.15f, pmy = Screen.height * 0.15f;
                for (int i = 0; i < _npRoot.childCount; i++)
                {
                    Transform pc = _npRoot.GetChild(i);
                    if (!pc.gameObject.activeInHierarchy) continue;
                    Component ps = pc.GetComponentInChildren(_tTmpBase);
                    if (ps == null) continue;
                    string pt = GetP(ps, "text") as string;
                    if (string.IsNullOrEmpty(pt)) continue;
                    Vector3 pp = ps.transform.position;
                    if (pp.x < -pmx || pp.x > Screen.width + pmx || pp.y < -pmy || pp.y > Screen.height + pmy) continue;
                    if (SrcAlphaAll(ps) > 0.02f) { anyVis = true; visCount++; } else anyHid = true;
                }
                if (_npOn)
                {
                    if (anyVis) _npOffFrames = 0;
                    else if (anyHid) { if (++_npOffFrames >= 3) _npOn = false; }
                }
                else
                {
                    // Labels are off, but hovering an object makes its own label visible, and moving the mouse between two close objects
                    // briefly shows TWO labels (one fading in, one fading out). So "several labels visible" only counts as "labels switched
                    // back on" when it lasts (NpOnConfirmSeconds); the hover cross-fade is always shorter than that.
                    if (visCount >= 2)
                    {
                        if (_npOnSince < 0f) _npOnSince = Time.unscaledTime;
                        if (Time.unscaledTime - _npOnSince >= NpOnConfirmSeconds) { _npOn = true; _npOffFrames = 0; _npOnSince = -1f; }
                    }
                    else _npOnSince = -1f;
                }
            }
            bool hoverOnly = !_npOn;   // labels off: draw only labels the game itself currently shows (the hovered object)

            for (int i = 0; i < _npRoot.childCount; i++)
            {
                Transform ch = _npRoot.GetChild(i);
                if (!ch.gameObject.activeInHierarchy) continue;
                Component src = ch.GetComponentInChildren(_tTmpBase);
                if (src == null) continue;

                string s = GetP(src, "text") as string;
                if (string.IsNullOrEmpty(s)) continue;

                Vector3 sp = src.transform.position;   // overlay canvas: world position == screen pixels
                // Use the CENTRE of the drawn text (the object's pivot can be at the text's left edge).
                object tb = GetP(src, "textBounds");
                if (tb is Bounds) { Bounds bb = (Bounds)tb; if (bb.size.x > 0f) sp = src.transform.TransformPoint(bb.center); }

                Color col = Color.white;
                object co = GetP(src, "color");
                if (co is Color) col = (Color)co;
                float alpha = SrcAlphaAll(src);
                float npAlpha = alpha;   // the nameplate's own alpha (the game fades it out when the planet leaves ITS view)

                NpLabel lab;
                if (!_npLabels.TryGetValue(src, out lab) || lab.tmp == null)
                {
                    lab = CreateNpLabel(src, main);
                    if (lab == null) continue;
                    _npLabels[src] = lab;
                }

                ResolveBody(lab, src);

                // The game's own idea of where the planet is on ITS screen, and how visible its label should be.
                bool selOk = false;
                Vector3 bsp0 = Vector3.zero;
                float selAlpha = 0f;
                if (lab.sel != null)
                {
                    object spo0 = GetFP(lab.sel, "ScreenPosition");
                    if (spo0 is Vector3) { bsp0 = (Vector3)spo0; selOk = true; }
                    selAlpha = ToF(GetFP(lab.sel, "LabelAlpha"));
                }
                float mx = Screen.width * 0.15f, my = Screen.height * 0.15f;
                bool inView = selOk && bsp0.z > 0f &&
                              bsp0.x > -mx && bsp0.x < Screen.width + mx && bsp0.y > -my && bsp0.y < Screen.height + my &&
                              sp.x > -mx && sp.x < Screen.width + mx && sp.y > -my && sp.y < Screen.height + my;
                if (npAlpha < 0.02f)
                {
                    // The game hides labels of planets outside its own view. The dome sees more than that, so fall back to the label alpha the
                    // game keeps for the planet itself.
                    if (!hoverOnly && !inView && lab.bodyT != null && selAlpha > 0.02f) alpha = selAlpha;
                    else
                    {
                        if (_frame % 240 == 0)
                            LogInfo("[US2Dome] Nameplates: '" + s + "' hidden: nameplateAlpha=" + npAlpha.ToString("F2") + " selAlpha=" + selAlpha.ToString("F2") +
                                    " inView=" + inView + " body=" + (lab.bodyT != null) + " selOk=" + selOk);
                        continue;
                    }
                }
                else alpha = npAlpha;
                col.a = alpha;

                float fs = ToF(GetP(src, "fontSize"));
                if (fs <= 0f) fs = 24f;
                if (lab.text != s || Mathf.Abs(lab.fontSize - fs) > 0.01f)
                {
                    SetP(lab.tmp, "text", s);
                    SetP(lab.tmp, "fontSize", fs);
                    lab.text = s; lab.fontSize = fs;
                    lab.prefH = ToF(GetP(lab.tmp, "preferredHeight"));
                    if (lab.prefH <= 1e-6f) lab.prefH = fs * 0.1f;
                }
                SetP(lab.tmp, "color", col);

                // On-screen height of the source text in pixels -> same angular size on the dome (x sizeMul).
                float ph = ToF(GetP(src, "preferredHeight"));
                if (ph <= 0f) ph = fs * 1.2f;
                float pxH = ph * Mathf.Abs(src.transform.lossyScale.y);
                float worldH = 2f * dist * tanHalf * (pxH / Mathf.Max(1f, (float)Screen.height)) * sizeMul;

                // Direction from the camera to the label. Preferred: the body's REAL 3D position (works at any angle, also far outside
                // the game window). The nameplate's pixel offset from its planet (the game dodges overlapping labels) is kept.
                Vector3 dir = Vector3.forward;
                bool fromBody = false;
                Vector2 offPx = Vector2.zero;
                if (lab.bodyT != null)
                {
                    Vector3 tbody = lab.bodyT.position - camPos;
                    if (tbody.sqrMagnitude > 1e-12f) { dir = tbody.normalized; fromBody = true; }
                    if (fromBody && inView)
                    {
                        // The game's screen positions are only trustworthy while the planet is (nearly) inside the game window; far off to the
                        // side (toward/past 90 degrees) they swing wildly while anything moves, which made labels jump. So measure the offset only
                        // when both the planet and its nameplate are on screen, and otherwise keep the last good value.
                        Vector2 o = new Vector2(sp.x - bsp0.x, sp.y - bsp0.y);
                        if (o.magnitude < 800f) { lab.lastOff = o; lab.haveOff = true; }
                    }
                    if (fromBody && lab.haveOff) offPx = lab.lastOff;
                }
                if (!fromBody)
                {
                    // fallback: through the nameplate's own screen position (only sensible well inside the game window)
                    float lim = 3f * Mathf.Max((float)Screen.width, (float)Screen.height);
                    if (Mathf.Abs(sp.x - Screen.width * 0.5f) > lim || Mathf.Abs(sp.y - Screen.height * 0.5f) > lim) continue;
                    dir = main.ScreenPointToRay(new Vector3(sp.x, sp.y, 0f)).direction.normalized;
                }

                Vector3 lUp, lRight;
                DomeFrame(dir, zen, rigUp, out lUp, out lRight);
                float ox = offPx.x + _cfgNpOffX.Value, oy = offPx.y + _cfgNpOffY.Value;
                if (Mathf.Abs(ox) > 1e-4f || Mathf.Abs(oy) > 1e-4f)
                {
                    dir = (dir + (lRight * ox + lUp * oy) * angPerPx).normalized;
                    DomeFrame(dir, zen, rigUp, out lUp, out lRight);
                }

                Transform lt = lab.tmp.transform;
                lt.position = camPos + dir * dist;
                lt.rotation = Quaternion.LookRotation(dir, lUp);
                lt.localScale = Vector3.one * (worldH / lab.prefH);

                lab.stamp = _frame;
                if (!lab.active) { lab.tmp.gameObject.SetActive(true); lab.active = true; }
                if (!_npFirstLogged)
                {
                    _npFirstLogged = true;
                    LogInfo("[US2Dome] Nameplates: first label '" + s + "' fromBody=" + fromBody + " src px=(" + sp.x.ToString("F0") + "," + sp.y.ToString("F0") +
                            ") layer=" + _npLayer + " overlayShader=" + (_npMat != null) + " worldH=" + worldH.ToString("G3"));
                }
            }

            // hide labels not driven this frame; drop long-dead ones
            _npTmpKeys.Clear();
            foreach (KeyValuePair<Component, NpLabel> kv in _npLabels)
            {
                NpLabel l = kv.Value;
                if (l.tmp == null) { _npTmpKeys.Add(kv.Key); continue; }
                if (l.stamp != _frame)
                {
                    if (l.active) { l.tmp.gameObject.SetActive(false); l.active = false; }
                    if (_frame - l.stamp > 900) { UnityEngine.Object.Destroy(l.tmp.gameObject); _npTmpKeys.Add(kv.Key); }
                }
            }
            for (int i = 0; i < _npTmpKeys.Count; i++) _npLabels.Remove(_npTmpKeys[i]);
        }

        NpLabel CreateNpLabel(Component src, Camera main)
        {
            try
            {
                if (_npLayer < 0)
                {
                    _npLayer = 31;
                    for (int l = 31; l >= 8; l--)
                        if (((main.cullingMask >> l) & 1) == 0) { _npLayer = l; break; }
                }
                GameObject go = new GameObject("[US2Dome] Nameplate");
                go.layer = _npLayer;
                if (_holder != null) go.transform.SetParent(_holder.transform, false);
                Component t = go.AddComponent(_tTmp);

                SetP(t, "font", GetP(src, "font"));
                SetP(t, "richText", GetP(src, "richText"));
                SetP(t, "fontStyle", GetP(src, "fontStyle"));
                SetP(t, "enableAutoSizing", false);
                if (!SetP(t, "enableWordWrapping", false)) SetEnumP(t, "textWrappingMode", "NoWrap");
                SetEnumP(t, "alignment", "Center");

                if (!_npMatTried)
                {
                    _npMatTried = true;
                    Shader sh = Shader.Find("TextMeshPro/Distance Field Overlay");
                    if (sh == null) sh = Shader.Find("TextMeshPro/Mobile/Distance Field - Overlay");
                    Material bm = GetP(t, "fontSharedMaterial") as Material;
                    if (sh != null && bm != null) { _npMat = new Material(bm); _npMat.shader = sh; }
                    LogInfo("[US2Dome] Nameplates: overlay shader " + (sh != null ? sh.name : "NOT FOUND (labels depth-tested; adjust Nameplates/LabelDistance if hidden)") +
                            ", layer " + _npLayer);
                }
                if (_npMat != null) SetP(t, "fontSharedMaterial", _npMat);

                go.SetActive(false);
                NpLabel nl = new NpLabel();
                nl.tmp = t; nl.text = null; nl.fontSize = -1f; nl.prefH = 1f; nl.stamp = -1; nl.active = false;
                return nl;
            }
            catch (Exception e)
            {
                LogErrorDetail("[US2Dome] Nameplates: creating label failed", e);
                _npBroken = true;
                return null;
            }
        }

        void HideAllNp()
        {
            foreach (KeyValuePair<Component, NpLabel> kv in _npLabels)
            {
                NpLabel l = kv.Value;
                if (l.tmp != null && l.active) { l.tmp.gameObject.SetActive(false); l.active = false; }
            }
        }

        void DestroyNameplates()
        {
            foreach (KeyValuePair<Component, NpLabel> kv in _npLabels)
                if (kv.Value.tmp != null) UnityEngine.Object.Destroy(kv.Value.tmp.gameObject);
            _npLabels.Clear();
        }
    }
}
