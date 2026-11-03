// UiDiag.cs -- diagnostics for the game's UI (all keys are configurable under [Diagnostics] in US2Dome.cfg).
//   F3  window snapshot: lists every UI object, and from the 2nd press on, what is NEW / switched on since the previous press
//       (lines tagged [WINDIAG]). Press once with no window open, open the window, press again.
//   F4  hover probe: put the mouse over a part of a window (e.g. the mass text) and press F4 -> lists the UI objects under the mouse.
//   F5  test screenshot: saves the whole game window and the window found by the last F4 to <game>/US2Dome/WindowTest_*.png.
// Labels dump (the old F3 output, lines tagged [UIDIAG]) is now off unless [Diagnostics] IncludeLabelDump = true.
// Everything is done by reflection so no extra assembly references are needed. Read-only: changes nothing in the game.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using BepInEx.Configuration;

namespace US2Dome
{
    public partial class US2Dome
    {
        void TickUiDiag()
        {
            try
            {
                BindDiagKeys();
                if (Input.GetKeyDown(_kDump))
                {
                    try { DumpWindows(); } catch (Exception e) { LogWarn("[WINDIAG] snapshot failed: " + e); }
                    if (_cfgDiagLabels != null && _cfgDiagLabels.Value) DumpUi();
                }
                if (Input.GetKeyDown(_kHover))
                {
                    try { HoverProbe(); } catch (Exception e) { LogWarn("[WINDIAG] hover probe failed: " + e); }
                }
                if (Input.GetKeyDown(_kShot)) StartCoroutine(WindowShotRoutine());
            }
            catch (Exception e) { LogWarn("[UIDIAG] failed: " + e.Message); }
        }

        static UnityEngine.Object[] FindAll(string typeName)
        {
            Type t = Type.GetType(typeName);
            if (t == null) return null;
            return UnityEngine.Object.FindObjectsOfType(t);
        }

        static string PathOf(Transform t)
        {
            StringBuilder sb = new StringBuilder(t.name);
            int guard = 0;
            while (t.parent != null && guard++ < 12) { t = t.parent; sb.Insert(0, t.name + "/"); }
            return sb.ToString();
        }

        static object Prop(object o, string name)
        {
            if (o == null) return null;
            PropertyInfo p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            return p == null ? null : p.GetValue(o, null);
        }

        static bool IsEngineType(Type t)
        {
            string ns = t.Namespace;
            return ns != null && (ns.StartsWith("UnityEngine") || ns.StartsWith("TMPro") || ns.StartsWith("System"));
        }

        // Short, safe description of a field value; recurses one level into game classes/structs.
        string Describe(object v, Camera cam, int depth)
        {
            try
            {
                if (v == null) return "null";
                Type t = v.GetType();
                if (v is UnityEngine.Object)
                {
                    UnityEngine.Object uo = (UnityEngine.Object)v;
                    if (uo == null) return t.Name + " (destroyed)";
                    Transform tr = null;
                    if (v is Transform) tr = (Transform)v;
                    else if (v is Component) tr = ((Component)v).transform;
                    else if (v is GameObject) tr = ((GameObject)v).transform;
                    if (tr == null) return t.Name + " '" + uo.name + "'";
                    Vector3 p = tr.position;
                    string r = t.Name + " '" + PathOf(tr) + "' world=" + p.ToString("F3");
                    if (cam != null) { Vector3 sp = cam.WorldToScreenPoint(p); r += " screen=(" + sp.x.ToString("F0") + "," + sp.y.ToString("F0") + ",depth " + sp.z.ToString("F2") + ")"; }
                    return r;
                }
                if (v is Vector3) return "Vector3 " + ((Vector3)v).ToString("F4");
                if (v is Vector2) return "Vector2 " + ((Vector2)v).ToString("F4");
                if (t.IsPrimitive || v is string || t.IsEnum)
                {
                    string s = v.ToString();
                    if (s.Length > 60) s = s.Substring(0, 60);
                    return t.Name + " " + s;
                }
                if (v is System.Collections.ICollection) return t.Name + " count=" + ((System.Collections.ICollection)v).Count;
                if (IsEngineType(t)) return t.Name;
                if (depth <= 0) return t.FullName;
                StringBuilder sb = new StringBuilder(t.FullName + " {");
                FieldInfo[] fl = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < fl.Length && i < 30; i++)
                {
                    object fv = null;
                    try { fv = fl[i].GetValue(v); } catch { }
                    sb.Append(" " + fl[i].Name + "=" + Describe(fv, cam, depth - 1) + ";");
                }
                PropertyInfo[] pl = t.GetProperties(BindingFlags.Instance | BindingFlags.Public);
                for (int i = 0; i < pl.Length && i < 40; i++)
                {
                    string pn = pl[i].Name;
                    if (!pl[i].CanRead || pl[i].GetIndexParameters().Length != 0) continue;
                    if (pn.IndexOf("osition", StringComparison.Ordinal) < 0 && pn.IndexOf("Name", StringComparison.Ordinal) < 0) continue;
                    object pv = null;
                    try { pv = pl[i].GetValue(v, null); } catch { }
                    sb.Append(" prop " + pn + "=" + Describe(pv, cam, depth - 1) + ";");
                }
                sb.Append(" }");
                return sb.ToString();
            }
            catch (Exception e) { return "(describe failed: " + e.Message + ")"; }
        }

        void DumpUi()
        {
            LogInfo("[UIDIAG] ===== UI dump (F3) =====");

            // 1) Canvases: how are they rendered, and by which camera?
            UnityEngine.Object[] canvases = FindAll("UnityEngine.Canvas, UnityEngine.UIModule");
            LogInfo("[UIDIAG] Canvas count: " + (canvases == null ? -1 : canvases.Length));
            if (canvases != null)
                for (int i = 0; i < canvases.Length && i < 60; i++)
                {
                    Component c = canvases[i] as Component;
                    if (c == null || !c.gameObject.activeInHierarchy) continue;
                    object wc = Prop(c, "worldCamera");
                    LogInfo("[UIDIAG] Canvas '" + PathOf(c.transform) + "' mode=" + Prop(c, "renderMode") +
                            " sortOrder=" + Prop(c, "sortingOrder") + " isRoot=" + Prop(c, "isRootCanvas") +
                            " worldCamera=" + (wc == null ? "none" : ((Camera)wc).name) +
                            " children=" + c.transform.childCount);
                }

            // 2) Text components (UGUI Text and TextMeshPro): where are they and what do they say?
            string[] textTypes = {
                "UnityEngine.UI.Text, UnityEngine.UI",
                "TMPro.TMP_Text, Unity.TextMeshPro",
                "TMPro.TextMeshPro, Unity.TextMeshPro",
                "TMPro.TextMeshProUGUI, Unity.TextMeshPro" };
            int shown = 0;
            for (int k = 0; k < textTypes.Length; k++)
            {
                UnityEngine.Object[] texts = FindAll(textTypes[k]);
                LogInfo("[UIDIAG] " + textTypes[k].Split(',')[0] + " count: " + (texts == null ? -1 : texts.Length));
                if (texts == null) continue;
                for (int i = 0; i < texts.Length && shown < 80; i++)
                {
                    Component c = texts[i] as Component;
                    if (c == null || !c.gameObject.activeInHierarchy) continue;
                    object txt = Prop(c, "text");
                    string s = txt == null ? "" : txt.ToString();
                    if (s.Length == 0) continue;
                    if (s.Length > 40) s = s.Substring(0, 40);
                    Vector3 p = c.transform.position;
                    LogInfo("[UIDIAG] Text '" + s.Replace("\n", " ") + "' path=" + PathOf(c.transform) +
                            " pos=(" + p.x.ToString("F1") + "," + p.y.ToString("F1") + "," + p.z.ToString("F1") + ")");
                    shown++;
                }
            }

            // 3) Script types that look like labels, and any script that draws with OnGUI (IMGUI).
            Dictionary<string, int> labelTypes = new Dictionary<string, int>();
            Dictionary<string, int> onGuiTypes = new Dictionary<string, int>();
            MonoBehaviour[] all = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null) continue;
                Type t = all[i].GetType();
                string n = t.FullName;
                if (n.IndexOf("label", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("marker", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("nametag", StringComparison.OrdinalIgnoreCase) >= 0)
                { int v; labelTypes.TryGetValue(n, out v); labelTypes[n] = v + 1; }
                if (n.StartsWith("US2Dome") || n.StartsWith("BepInEx")) continue;
                if (t.GetMethod("OnGUI", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null)
                { int v; onGuiTypes.TryGetValue(n, out v); onGuiTypes[n] = v + 1; }
            }
            foreach (KeyValuePair<string, int> kv in labelTypes) LogInfo("[UIDIAG] label-like script: " + kv.Key + " x" + kv.Value);
            foreach (KeyValuePair<string, int> kv in onGuiTypes) LogInfo("[UIDIAG] script with OnGUI: " + kv.Key + " x" + kv.Value);

            // 4) Cameras (which one would a screen-space canvas be attached to?)
            Camera[] cams = Camera.allCameras;
            for (int i = 0; i < cams.Length; i++)
                if (cams[i] != null && !IsOurs(cams[i]))
                    LogInfo("[UIDIAG] Camera '" + cams[i].name + "' depth=" + cams[i].depth + " cull=" + cams[i].cullingMask +
                            " rt=" + (cams[i].targetTexture != null));
            // 5) What does each nameplate point at? (so labels can be placed from the object's real position)
            try
            {
                Camera dcam = FindMainCamera();
                if (dcam != null)
                    LogInfo("[UIDIAG] MainCam pos=" + dcam.transform.position.ToString("F4") + " rot=" + dcam.transform.rotation.eulerAngles.ToString("F1") +
                            " fov=" + dcam.fieldOfView.ToString("F1") + " aspect=" + dcam.aspect.ToString("F3") + " screen=" + Screen.width + "x" + Screen.height);
                GameObject npg = GameObject.Find("Gui/Nameplates");
                Type tb = Type.GetType("TMPro.TMP_Text, Unity.TextMeshPro");
                List<string> bodyNames = new List<string>();
                int shownNp = 0;
                if (npg != null)
                    for (int c = 0; c < npg.transform.childCount && shownNp < 6; c++)
                    {
                        Transform nt = npg.transform.GetChild(c);
                        if (!nt.gameObject.activeInHierarchy) continue;
                        Component[] cs = nt.GetComponentsInChildren<Component>(true);
                        for (int i2 = 0; i2 < cs.Length; i2++)
                        {
                            if (cs[i2] == null || cs[i2].GetType().Name != "UINameplate") continue;
                            shownNp++;
                            Component tx = tb != null ? nt.GetComponentInChildren(tb) : null;
                            string label = tx != null ? (GetP(tx, "text") as string) : "?";
                            LogInfo("[UIDIAG] --- UINameplate '" + label + "' screenPos=" + (tx != null ? tx.transform.position.ToString("F0") : "?"));
                            if (!string.IsNullOrEmpty(label)) bodyNames.Add(label);
                            FieldInfo bf = cs[i2].GetType().GetField("_body", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            object body = bf != null ? bf.GetValue(cs[i2]) : null;
                            if (body == null) { LogInfo("[UIDIAG]    _body = null"); continue; }
                            // walk the whole class chain of the Body, including PRIVATE fields of base classes
                            Type bt = body.GetType();
                            int lvl = 0;
                            while (bt != null && bt != typeof(object) && !IsEngineType(bt) && lvl < 6)
                            {
                                LogInfo("[UIDIAG]    class level " + lvl + ": " + bt.FullName);
                                FieldInfo[] bfl = bt.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                                for (int f = 0; f < bfl.Length && f < 60; f++)
                                {
                                    object fv = null;
                                    try { fv = bfl[f].GetValue(body); } catch { }
                                    string d = Describe(fv, dcam, 2);
                                    if (d.Length > 700) d = d.Substring(0, 700) + "...";
                                    LogInfo("[UIDIAG]       field " + bfl[f].Name + " : " + bfl[f].FieldType.Name + " = " + d);
                                }
                                PropertyInfo[] bpl = bt.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                                for (int f = 0; f < bpl.Length && f < 60; f++)
                                {
                                    if (!bpl[f].CanRead || bpl[f].GetIndexParameters().Length != 0) continue;
                                    string pn = bpl[f].Name;
                                    bool interesting = pn.IndexOf("osition", StringComparison.OrdinalIgnoreCase) >= 0 || pn.IndexOf("ocation", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                       pn.IndexOf("ransform", StringComparison.OrdinalIgnoreCase) >= 0 || pn.IndexOf("adius", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                       pn.IndexOf("ntity", StringComparison.OrdinalIgnoreCase) >= 0 || pn.IndexOf("istance", StringComparison.OrdinalIgnoreCase) >= 0;
                                    object pv = null;
                                    if (interesting) { try { pv = bpl[f].GetValue(body, null); } catch { } }
                                    LogInfo("[UIDIAG]       prop " + pn + " : " + bpl[f].PropertyType.Name + (interesting ? (" = " + Describe(pv, dcam, 2)) : ""));
                                }
                                MethodInfo[] bml = bt.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                                StringBuilder mb = new StringBuilder();
                                for (int f = 0; f < bml.Length; f++)
                                {
                                    string mn = bml[f].Name;
                                    if (mn.StartsWith("get_") || mn.StartsWith("set_")) continue;
                                    if (mn.IndexOf("osition", StringComparison.OrdinalIgnoreCase) >= 0 || mn.IndexOf("ocation", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                        mn.IndexOf("ransform", StringComparison.OrdinalIgnoreCase) >= 0 || mn.IndexOf("ScreenPoint", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                        mn.IndexOf("World", StringComparison.OrdinalIgnoreCase) >= 0)
                                        mb.Append(bml[f].ReturnType.Name + " " + mn + "(" + bml[f].GetParameters().Length + ") ");
                                }
                                if (mb.Length > 0) LogInfo("[UIDIAG]       methods of interest: " + mb);
                                bt = bt.BaseType; lvl++;
                            }
                        }
                    }
                else LogInfo("[UIDIAG] Gui/Nameplates not found");

                // where do the planets live in the scene? list transforms named like each body
                Transform[] allT = UnityEngine.Object.FindObjectsOfType<Transform>();
                LogInfo("[UIDIAG] scene transforms: " + allT.Length);
                for (int n = 0; n < bodyNames.Count; n++)
                {
                    int hits = 0;
                    for (int i3 = 0; i3 < allT.Length && hits < 10; i3++)
                    {
                        Transform tt = allT[i3];
                        if (tt == null || tt.name.IndexOf(bodyNames[n], StringComparison.OrdinalIgnoreCase) < 0) continue;
                        string pth = PathOf(tt);
                        if (pth.StartsWith("Gui/") || pth.StartsWith("[US2Dome]")) continue;
                        hits++;
                        StringBuilder cb = new StringBuilder();
                        Component[] tc = tt.GetComponents<Component>();
                        for (int q = 0; q < tc.Length && q < 8; q++) if (tc[q] != null) cb.Append(tc[q].GetType().Name + " ");
                        LogInfo("[UIDIAG] transform for '" + bodyNames[n] + "': " + Describe(tt, dcam, 0) + " active=" + tt.gameObject.activeInHierarchy + " comps=" + cb);
                    }
                    if (hits == 0) LogInfo("[UIDIAG] no scene transform named like '" + bodyNames[n] + "'");
                }
            }
            catch (Exception e) { LogWarn("[UIDIAG] nameplate dump failed: " + e); }
            LogInfo("[UIDIAG] ===== end =====");
        }

        // =====================================================================================================================
        //  WINDOW DIAGNOSTICS (v1.8.1)
        // =====================================================================================================================
        ConfigEntry<bool> _cfgDiagLabels;
        bool _diagKeysBound;
        KeyCode _kDump = KeyCode.F3, _kHover = KeyCode.F4, _kShot = KeyCode.F5;

        class WdSnap
        {
            public Dictionary<int, bool> active = new Dictionary<int, bool>();
            public List<Transform> order = new List<Transform>();
        }
        class HoverHit
        {
            public long key;
            public Transform t;
            public bool graphic;
            public Rect r;
        }

        int _wdPress;
        Dictionary<int, bool> _wdPrev;                 // previous press: instance id -> was active in hierarchy
        HashSet<int> _wdPrevCams, _wdPrevRts;
        Dictionary<string, int> _wdPrevTypes;
        bool _hoverWinValid;
        Rect _hoverWinRect;
        string _hoverWinPath = "";
        static Type _wdTCanvas;

        static KeyCode ParseKey(string s, KeyCode def)
        {
            try { return (KeyCode)Enum.Parse(typeof(KeyCode), s, true); }
            catch { return def; }
        }

        void BindDiagKeys()
        {
            if (_diagKeysBound || _cfgFile == null) return;
            _diagKeysBound = true;
            ConfigEntry<string> a = _cfgFile.Bind("Diagnostics", "DumpKey", "F3", "Key: window snapshot. Press with no window open, open the window, press again: the log shows what is new. (Unity KeyCode name.)");
            ConfigEntry<string> b = _cfgFile.Bind("Diagnostics", "HoverKey", "F4", "Key: list the UI objects under the mouse pointer (hover over e.g. the mass text of a window, then press).");
            ConfigEntry<string> c = _cfgFile.Bind("Diagnostics", "ScreenshotKey", "F5", "Key: save a test screenshot of the whole game window and of the window found by the last hover probe, to <game>/US2Dome/.");
            _cfgDiagLabels = _cfgFile.Bind("Diagnostics", "IncludeLabelDump", false, "Also write the old label/nameplate dump ([UIDIAG]) when the snapshot key is pressed.");
            _kDump = ParseKey(a.Value, KeyCode.F3);
            _kHover = ParseKey(b.Value, KeyCode.F4);
            _kShot = ParseKey(c.Value, KeyCode.F5);
        }

        // file-only log line (the bulk of a snapshot would just flood the BepInEx console)
        void WL(string s) { LogToFile("WINDIAG", s); }

        static Type WdCanvasType()
        {
            if (_wdTCanvas == null) _wdTCanvas = Type.GetType("UnityEngine.Canvas, UnityEngine.UIModule");
            return _wdTCanvas;
        }

        // root canvases, lowest sorting order (drawn first) to highest (drawn on top)
        List<Component> RootCanvases()
        {
            List<Component> res = new List<Component>();
            Type tc = WdCanvasType();
            if (tc == null) return res;
            UnityEngine.Object[] cs = UnityEngine.Object.FindObjectsOfType(tc);
            for (int i = 0; i < cs.Length; i++)
            {
                Component c = cs[i] as Component;
                if (c == null) continue;
                object ir = Prop(c, "isRootCanvas");
                if (ir is bool && !(bool)ir) continue;
                res.Add(c);
            }
            res.Sort(delegate(Component cx, Component cy) { return ToF(Prop(cx, "sortingOrder")).CompareTo(ToF(Prop(cy, "sortingOrder"))); });
            return res;
        }

        void WdWalk(Transform t, WdSnap s, int depth)
        {
            if (t == null || depth > 40 || s.order.Count > 30000) return;
            s.active[t.GetHashCode()] = t.gameObject.activeInHierarchy;
            s.order.Add(t);
            for (int i = 0; i < t.childCount; i++) WdWalk(t.GetChild(i), s, depth + 1);
        }

        // Screen-pixel rectangle (origin bottom-left, like Input.mousePosition) of a UI element.
        static bool ScreenRectOf(Transform t, out Rect r)
        {
            r = new Rect(0f, 0f, 0f, 0f);
            RectTransform rt = t as RectTransform;
            Type tc = WdCanvasType();
            if (rt == null || tc == null) return false;
            Vector3[] c = new Vector3[4];
            rt.GetWorldCorners(c);
            Component cv = rt.GetComponentInParent(tc);
            object root = Prop(cv, "rootCanvas");
            object cvo = (root != null) ? root : (object)cv;
            object mode = Prop(cvo, "renderMode");
            string ms = (mode == null) ? "" : mode.ToString();
            Camera cam = null;
            if (ms != "ScreenSpaceOverlay")
            {
                cam = Prop(cvo, "worldCamera") as Camera;
                if (cam == null) cam = Camera.main;
            }
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                Vector3 p = c[i];
                if (cam != null) p = cam.WorldToScreenPoint(p);
                if (p.x < x0) x0 = p.x;
                if (p.y < y0) y0 = p.y;
                if (p.x > x1) x1 = p.x;
                if (p.y > y1) y1 = p.y;
            }
            r = Rect.MinMaxRect(x0, y0, x1, y1);
            return true;
        }

        static string RectStr(Rect r)
        {
            return "(" + r.xMin.ToString("F0") + "," + r.yMin.ToString("F0") + " " + r.width.ToString("F0") + "x" + r.height.ToString("F0") + ")";
        }

        static string CompList(Transform t)
        {
            StringBuilder sb = new StringBuilder();
            Component[] cs = t.GetComponents<Component>();
            for (int i = 0; i < cs.Length; i++)
            {
                if (cs[i] == null) { sb.Append("<missing> "); continue; }
                Type ty = cs[i].GetType();
                string n = ty.Name;
                if (n == "Transform" || n == "RectTransform" || n == "CanvasRenderer") continue;
                if (!IsEngineType(ty)) n = "*" + ty.FullName + "*";
                Behaviour b = cs[i] as Behaviour;
                if (b != null && !b.enabled) n += "(off)";
                sb.Append(n + " ");
            }
            return sb.ToString().TrimEnd();
        }

        static string DescTex(Texture tx)
        {
            if (tx == null) return "null";
            string s = tx.GetType().Name + " '" + tx.name + "' " + tx.width + "x" + tx.height;
            RenderTexture rt = tx as RenderTexture;
            if (rt != null) s += " fmt=" + rt.format + " created=" + rt.IsCreated();
            return s;
        }

        // The interesting fields of a game script (anything that looks like a link to a body/object, plus plain values).
        string GameFields(Component c)
        {
            StringBuilder sb = new StringBuilder("{" + c.GetType().Name + ":");
            int shown = 0, lvl = 0;
            Type ty = c.GetType();
            while (ty != null && ty != typeof(object) && !IsEngineType(ty) && lvl < 2 && shown < 8)
            {
                FieldInfo[] fl = ty.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                for (int i = 0; i < fl.Length && shown < 8; i++)
                {
                    object v = null;
                    try { v = fl[i].GetValue(c); } catch { continue; }
                    if (v == null) continue;
                    Type ft = v.GetType();
                    bool prim = ft.IsPrimitive || v is string || ft.IsEnum;
                    string fn = fl[i].Name.ToLowerInvariant();
                    bool hit = fn.IndexOf("body") >= 0 || fn.IndexOf("select") >= 0 || fn.IndexOf("target") >= 0 || fn.IndexOf("entity") >= 0 ||
                               fn.IndexOf("object") >= 0 || fn.IndexOf("planet") >= 0 || fn.IndexOf("camera") >= 0 || fn.IndexOf("texture") >= 0;
                    if (!prim && !hit) continue;
                    string d = Describe(v, null, 0);
                    if (d.Length > 140) d = d.Substring(0, 140) + "...";
                    sb.Append(" " + fl[i].Name + "=" + d);
                    shown++;
                }
                ty = ty.BaseType; lvl++;
            }
            sb.Append(" }");
            return sb.ToString();
        }

        string NodeExtra(Transform t)
        {
            StringBuilder sb = new StringBuilder();
            Component[] cs = t.GetComponents<Component>();
            for (int i = 0; i < cs.Length; i++)
            {
                Component c = cs[i];
                if (c == null) continue;
                Type ty = c.GetType();
                string n = ty.Name;
                try
                {
                    if (n == "Text" || n == "TextMeshProUGUI" || n == "TextMeshPro" || n == "TMP_Text")
                    {
                        string s = GetP(c, "text") as string;
                        if (!string.IsNullOrEmpty(s))
                        {
                            if (s.Length > 70) s = s.Substring(0, 70);
                            sb.Append("text=\"" + s.Replace("\n", "\\n") + "\" ");
                        }
                    }
                    else if (n == "RawImage") sb.Append("RAWIMAGE tex=" + DescTex(GetP(c, "texture") as Texture) + " ");
                    else if (n == "Image")
                    {
                        UnityEngine.Object sp = GetP(c, "sprite") as UnityEngine.Object;
                        if (sp != null) sb.Append("sprite=" + sp.name + " ");
                    }
                    else if (n == "CanvasGroup") sb.Append("alpha=" + ToF(GetP(c, "alpha")).ToString("F2") + " ");
                    else if (n == "Canvas") sb.Append("CANVAS mode=" + GetP(c, "renderMode") + " order=" + GetP(c, "sortingOrder") + " ");
                    else if (!IsEngineType(ty)) sb.Append(GameFields(c) + " ");
                }
                catch { }
            }
            return sb.ToString().TrimEnd();
        }

        void WdNode(Transform t, int depth, int maxDepth, ref int budget)
        {
            if (t == null || budget <= 0) return;
            budget--;
            StringBuilder sb = new StringBuilder();
            sb.Append(' ', Math.Min(depth, 14) * 2);
            sb.Append(t.name);
            if (!t.gameObject.activeSelf) sb.Append(" [inactive]");
            Rect r;
            if (ScreenRectOf(t, out r)) sb.Append(" rect=" + RectStr(r));
            string cl = CompList(t);
            if (cl.Length > 0) sb.Append(" comps=" + cl);
            string ex = NodeExtra(t);
            if (ex.Length > 0) sb.Append(" " + ex);
            WL("    " + sb);
            if (depth >= maxDepth)
            {
                if (t.childCount > 0) WL("    " + new string(' ', Math.Min(depth + 1, 14) * 2) + "... " + t.childCount + " child objects not shown");
                return;
            }
            for (int i = 0; i < t.childCount; i++) WdNode(t.GetChild(i), depth + 1, maxDepth, ref budget);
        }

        static bool IsWindowLikeType(string n)
        {
            return n.IndexOf("Window", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Popup", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   n.IndexOf("Dialog", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Inspector", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   n.IndexOf("Properties", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Panel", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ---------------------------------------------------------------------------------------------------------------
        //  F3: snapshot + diff against the previous press
        // ---------------------------------------------------------------------------------------------------------------
        void DumpWindows()
        {
            _wdPress++;
            LogInfo("[WINDIAG] snapshot #" + _wdPress + " -> see US2Dome.log");
            WL("===================== WINDOW SNAPSHOT #" + _wdPress + " =====================");

            // environment (helps decide how a window can be copied)
            try
            {
                Type tsc = Type.GetType("UnityEngine.ScreenCapture, UnityEngine.ScreenCaptureModule");
                WL("env: unity=" + Application.unityVersion + " gfx=" + SystemInfo.graphicsDeviceType + " screen=" + Screen.width + "x" + Screen.height +
                   " mode=" + Screen.fullScreenMode + " colorSpace=" + QualitySettings.activeColorSpace +
                   " pipeline=" + (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null ? UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline.GetType().Name : "none") +
                   " screenCaptureModule=" + (tsc != null) +
                   " captureIntoRT=" + (tsc != null && tsc.GetMethod("CaptureScreenshotIntoRenderTexture") != null) +
                   " captureAsTexture=" + (tsc != null && tsc.GetMethod("CaptureScreenshotAsTexture", Type.EmptyTypes) != null));
            }
            catch (Exception e) { WL("env failed: " + e.Message); }

            WdSnap now = new WdSnap();
            List<Component> roots = RootCanvases();
            for (int i = 0; i < roots.Count; i++) WdWalk(roots[i].transform, now, 0);
            int activeCount = 0;
            foreach (KeyValuePair<int, bool> kv in now.active) if (kv.Value) activeCount++;
            WL("root canvases: " + roots.Count + ", UI objects: " + now.order.Count + " (" + activeCount + " active)");
            for (int i = 0; i < roots.Count; i++)
            {
                Transform rt = roots[i].transform;
                WL("  canvas '" + PathOf(rt) + "' mode=" + Prop(roots[i], "renderMode") + " sortOrder=" + Prop(roots[i], "sortingOrder") +
                   " active=" + rt.gameObject.activeInHierarchy + " direct children=" + rt.childCount);
            }

            // script types that look like windows/panels, and how many active instances exist
            Dictionary<string, int> types = new Dictionary<string, int>();
            Dictionary<string, List<string>> typePaths = new Dictionary<string, List<string>>();
            MonoBehaviour[] mbs = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
            for (int i = 0; i < mbs.Length; i++)
            {
                if (mbs[i] == null || !mbs[i].enabled) continue;
                Type ty = mbs[i].GetType();
                if (IsEngineType(ty) || ty.FullName.StartsWith("US2Dome") || ty.FullName.StartsWith("BepInEx")) continue;
                if (!IsWindowLikeType(ty.Name)) continue;
                int c;
                types.TryGetValue(ty.FullName, out c);
                types[ty.FullName] = c + 1;
                List<string> pl;
                if (!typePaths.TryGetValue(ty.FullName, out pl)) { pl = new List<string>(); typePaths[ty.FullName] = pl; }
                if (pl.Count < 4) pl.Add(PathOf(mbs[i].transform));
            }
            foreach (KeyValuePair<string, int> kv in types)
            {
                int before = 0;
                bool known = _wdPrevTypes != null && _wdPrevTypes.TryGetValue(kv.Key, out before);
                if (_wdPrevTypes == null || !known || before != kv.Value)
                    WL("window-like script: " + kv.Key + " x" + kv.Value + (_wdPrevTypes == null ? "" : (known ? " (was " + before + ")" : " (NEW)")) +
                       "  e.g. " + string.Join(" | ", typePaths[kv.Key].ToArray()));
            }
            if (_wdPrevTypes != null)
                foreach (KeyValuePair<string, int> kv in _wdPrevTypes)
                    if (!types.ContainsKey(kv.Key)) WL("window-like script: " + kv.Key + " GONE (was " + kv.Value + ")");

            if (_wdPrev == null)
            {
                // first press: overview of the top two levels under every root canvas
                WL("--- BASELINE: top levels of the UI (press again after opening the window) ---");
                int budget = 400;
                for (int i = 0; i < roots.Count; i++)
                    WdNode(roots[i].transform, 0, 2, ref budget);
            }
            else
            {
                HashSet<int> on = new HashSet<int>();
                int offCount = 0, goneCount = 0;
                for (int i = 0; i < now.order.Count; i++)
                {
                    int id = now.order[i].GetHashCode();
                    bool isOn = now.active[id];
                    bool was;
                    bool known = _wdPrev.TryGetValue(id, out was);
                    if (isOn && (!known || !was)) on.Add(id);
                    if (!isOn && known && was) offCount++;
                }
                foreach (KeyValuePair<int, bool> kv in _wdPrev) if (kv.Value && !now.active.ContainsKey(kv.Key)) goneCount++;
                WL("--- CHANGES since snapshot #" + (_wdPress - 1) + ": " + on.Count + " objects NEW or switched on, " + offCount + " switched off, " + goneCount + " destroyed ---");
                int budget = 900, skipped = 0;
                for (int i = 0; i < now.order.Count && budget > 0; i++)
                {
                    Transform t = now.order[i];
                    int id = t.GetHashCode();
                    if (!on.Contains(id)) continue;
                    if (t.parent != null && on.Contains(t.parent.GetHashCode())) continue;   // not the top of a changed subtree
                    string pth = PathOf(t);
                    if (pth.IndexOf("/Nameplates", StringComparison.Ordinal) >= 0) { skipped++; continue; }   // label pool churn
                    WL("  CHANGED subtree: " + pth);
                    WdNode(t, 0, 12, ref budget);
                }
                if (skipped > 0) WL("  (" + skipped + " changed nameplate objects left out)");
                if (budget <= 0) WL("  (output limit reached)");
            }

            // cameras (the 3D planet preview in a window is normally its own camera drawing into a RenderTexture)
            try
            {
                Camera[] cams = Resources.FindObjectsOfTypeAll<Camera>();
                HashSet<int> camIds = new HashSet<int>();
                WL("--- cameras ---");
                for (int i = 0; i < cams.Length; i++)
                {
                    Camera cam = cams[i];
                    if (cam == null || !cam.gameObject.scene.IsValid() || IsOurs(cam) || cam.name.StartsWith("[US2Dome]")) continue;
                    int id = cam.GetHashCode();
                    camIds.Add(id);
                    bool isNew = _wdPrevCams != null && !_wdPrevCams.Contains(id);
                    string rtS = "none";
                    if (cam.targetTexture != null) rtS = cam.targetTexture.name + " " + cam.targetTexture.width + "x" + cam.targetTexture.height;
                    WL("  " + (isNew ? "NEW " : "") + "camera '" + PathOf(cam.transform) + "' enabled=" + cam.enabled + " active=" + cam.gameObject.activeInHierarchy +
                       " depth=" + cam.depth + " cull=" + cam.cullingMask + " rt=" + rtS + (cam.orthographic ? " ortho size=" + cam.orthographicSize : " fov=" + cam.fieldOfView.ToString("F1")) +
                       " pos=" + cam.transform.position.ToString("F2"));
                }
                _wdPrevCams = camIds;
            }
            catch (Exception e) { WL("cameras failed: " + e.Message); }

            try
            {
                RenderTexture[] rts = Resources.FindObjectsOfTypeAll<RenderTexture>();
                HashSet<int> rtIds = new HashSet<int>();
                int shown = 0;
                WL("--- render textures (" + rts.Length + (_wdPrevRts == null ? ", all shown" : ", only new shown") + ") ---");
                for (int i = 0; i < rts.Length; i++)
                {
                    if (rts[i] == null) continue;
                    int id = rts[i].GetHashCode();
                    rtIds.Add(id);
                    if (_wdPrevRts != null && _wdPrevRts.Contains(id)) continue;
                    if (shown++ >= 80) continue;
                    WL("  " + (_wdPrevRts != null ? "NEW " : "") + DescTex(rts[i]) + " depth=" + rts[i].depth + " aa=" + rts[i].antiAliasing);
                }
                _wdPrevRts = rtIds;
            }
            catch (Exception e) { WL("render textures failed: " + e.Message); }

            _wdPrev = now.active;
            _wdPrevTypes = types;
            WL("===================== end snapshot #" + _wdPress + " =====================");
        }

        // ---------------------------------------------------------------------------------------------------------------
        //  F4: what UI is under the mouse?
        // ---------------------------------------------------------------------------------------------------------------
        void HoverWalk(Transform t, Vector2 mp, long soKey, ref long order, List<HoverHit> hits, Type tGraphic, int depth)
        {
            if (t == null || depth > 40 || !t.gameObject.activeSelf) return;
            order++;
            Rect r;
            if (ScreenRectOf(t, out r) && r.Contains(mp))
            {
                HoverHit h = new HoverHit();
                h.key = soKey * 10000000L + order;
                h.t = t; h.r = r;
                if (tGraphic != null)
                {
                    Component g = t.GetComponent(tGraphic);
                    if (g != null)
                    {
                        object rtg = GetP(g, "raycastTarget");
                        h.graphic = !(rtg is bool) || (bool)rtg;
                    }
                }
                hits.Add(h);
            }
            for (int i = 0; i < t.childCount; i++) HoverWalk(t.GetChild(i), mp, soKey, ref order, hits, tGraphic, depth + 1);
        }

        void HoverProbe()
        {
            Vector3 m3 = Input.mousePosition;
            Vector2 mp = new Vector2(m3.x, m3.y);
            WL("===================== HOVER PROBE at mouse (" + mp.x.ToString("F0") + "," + mp.y.ToString("F0") + ") screen=" + Screen.width + "x" + Screen.height + " =====================");
            Type tGraphic = Type.GetType("UnityEngine.UI.Graphic, UnityEngine.UI");
            List<HoverHit> hits = new List<HoverHit>();
            long order = 0;
            List<Component> roots = RootCanvases();
            for (int i = 0; i < roots.Count; i++)
            {
                long so = (long)ToF(Prop(roots[i], "sortingOrder")) + 5000L;
                HoverWalk(roots[i].transform, mp, so, ref order, hits, tGraphic, 0);
            }
            hits.Sort(delegate(HoverHit ha, HoverHit hb) { return hb.key.CompareTo(ha.key); });
            WL("objects whose rectangle contains the mouse: " + hits.Count + " (topmost first; G = a clickable graphic)");
            HoverHit topG = null;
            for (int i = 0; i < hits.Count; i++)
            {
                if (hits[i].graphic && topG == null && hits[i].r.width * hits[i].r.height < 0.9f * Screen.width * Screen.height) topG = hits[i];   // skip full-screen overlays (fade/ray-block)
                if (i >= 30) continue;
                string ex = NodeExtra(hits[i].t);
                WL("  " + (hits[i].graphic ? "G " : "  ") + PathOf(hits[i].t) + " rect=" + RectStr(hits[i].r) + " comps=" + CompList(hits[i].t) + (ex.Length > 0 ? " " + ex : ""));
            }
            _hoverWinValid = false;
            if (topG == null && hits.Count > 0) topG = hits[0];
            if (topG != null)
            {
                float screenArea = Mathf.Max(1f, (float)Screen.width * (float)Screen.height);
                Transform best = null;
                Rect bestR = new Rect();
                WL("ancestors of the topmost hit '" + topG.t.name + "' (inner to outer):");
                Transform anc = topG.t;
                int guard = 0;
                while (anc != null && guard++ < 40)
                {
                    Rect ar;
                    if (ScreenRectOf(anc, out ar))
                    {
                        float frac = (ar.width * ar.height) / screenArea;
                        WL("    " + anc.name + " rect=" + RectStr(ar) + " (" + (frac * 100f).ToString("F1") + "% of screen) comps=" + CompList(anc));
                        if (frac > 0.0005f && frac < 0.6f) { best = anc; bestR = ar; }
                    }
                    anc = anc.parent;
                }
                if (best != null)
                {
                    _hoverWinValid = true;
                    _hoverWinRect = bestR;
                    _hoverWinPath = PathOf(best);
                    WL("WINDOW GUESS (largest ancestor under 60% of the screen): " + _hoverWinPath + " rect=" + RectStr(bestR) + "   <- F5 saves this region");
                    WL("  its full subtree:");
                    int budget = 300;
                    WdNode(best, 0, 10, ref budget);
                }
                else WL("no ancestor with a usable rectangle found");
            }
            WL("===================== end hover probe =====================");
            LogInfo("[WINDIAG] hover probe written to US2Dome.log" + (_hoverWinValid ? " (window guess: " + _hoverWinPath + ")" : ""));
        }

        // ---------------------------------------------------------------------------------------------------------------
        //  F5: test screenshot (does a plain screen capture contain the window, including its 3D view?)
        // ---------------------------------------------------------------------------------------------------------------
        IEnumerator WindowShotRoutine()
        {
            yield return new WaitForEndOfFrame();
            DoWindowShot();
        }

        static void ForceOpaque(Texture2D tx)
        {
            Color32[] px = tx.GetPixels32();
            for (int i = 0; i < px.Length; i++) px[i].a = 255;
            tx.SetPixels32(px);
            tx.Apply();
        }

        void DoWindowShot()
        {
            try
            {
                Type tsc = Type.GetType("UnityEngine.ScreenCapture, UnityEngine.ScreenCaptureModule");
                MethodInfo mi = (tsc == null) ? null : tsc.GetMethod("CaptureScreenshotAsTexture", Type.EmptyTypes);
                if (mi == null) { LogWarn("[WINDIAG] screenshot: ScreenCapture.CaptureScreenshotAsTexture not available in this game."); WL("screenshot: not available"); return; }
                Texture2D full = mi.Invoke(null, null) as Texture2D;
                if (full == null) { LogWarn("[WINDIAG] screenshot: capture returned null."); return; }
                string dir = Path.Combine(Path.Combine(Application.dataPath, ".."), "US2Dome");
                Directory.CreateDirectory(dir);
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                ForceOpaque(full);
                string fullPath = Path.Combine(dir, "WindowTest_" + stamp + "_full.png");
                File.WriteAllBytes(fullPath, full.EncodeToPNG());
                string msg = "[WINDIAG] screenshot saved: " + fullPath + " (" + full.width + "x" + full.height + ", screen is " + Screen.width + "x" + Screen.height + ")";
                if (_hoverWinValid)
                {
                    float sx = full.width / (float)Mathf.Max(1, Screen.width), sy = full.height / (float)Mathf.Max(1, Screen.height);
                    int x = Mathf.Clamp(Mathf.FloorToInt(_hoverWinRect.xMin * sx), 0, full.width - 1);
                    int y = Mathf.Clamp(Mathf.FloorToInt(_hoverWinRect.yMin * sy), 0, full.height - 1);
                    int w = Mathf.Clamp(Mathf.CeilToInt(_hoverWinRect.width * sx), 1, full.width - x);
                    int h = Mathf.Clamp(Mathf.CeilToInt(_hoverWinRect.height * sy), 1, full.height - y);
                    Texture2D crop = new Texture2D(w, h, TextureFormat.RGBA32, false);
                    crop.SetPixels(full.GetPixels(x, y, w, h));
                    crop.Apply();
                    string cropPath = Path.Combine(dir, "WindowTest_" + stamp + "_window.png");
                    File.WriteAllBytes(cropPath, crop.EncodeToPNG());
                    UnityEngine.Object.Destroy(crop);
                    msg += "; window region " + w + "x" + h + " at (" + x + "," + y + ") of '" + _hoverWinPath + "' saved: " + cropPath;
                }
                else msg += "; no window region (press the hover key over a window first)";
                UnityEngine.Object.Destroy(full);
                LogInfo(msg);
                WL(msg);
            }
            catch (Exception e) { LogErrorDetail("[WINDIAG] screenshot failed", e); }
        }
    }
}
