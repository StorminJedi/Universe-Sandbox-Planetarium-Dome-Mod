// InputBlocker.cs  (v1.6.1)
// Keeps the GAME from seeing mouse clicks / drags / scroll (and, while a key is being
// rebound, key presses) that are meant for the settings panel.
//
// IMGUI (OnGUI) already swallows those events for other IMGUI code, but the game reads the
// mouse through UnityEngine.Input (directly, or via uGUI/EventSystem which sits on top of it),
// and IMGUI cannot stop that. So we Harmony-patch the relevant UnityEngine.Input getters with
// prefixes that return "nothing pressed / zero movement" while the panel wants the mouse.
//
// This only covers the legacy UnityEngine.Input API. If the game reads its mouse through the
// new Input System package instead, these patches have no effect (see the log line
// "Input systems loaded" written when the panel is first opened).
using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace US2Dome
{
    public static class InputBlocker
    {
        // Set from the plugin's OnGUI (main thread). Read by the patched getters.
        public static volatile bool PanelOpen;
        public static volatile bool MouseOver;       // pointer is over the panel
        public static volatile bool MouseCaptured;   // a mouse button went down on the panel and is still held
        public static volatile bool CapturingKey;    // "press a key" rebinding is active
        public static volatile bool TextFocus;       // a number box in the panel has keyboard focus

        static bool BlockMouse { get { return PanelOpen && (MouseOver || MouseCaptured); } }
        static bool BlockKeys  { get { return PanelOpen && (CapturingKey || TextFocus); } }

        static Harmony _harmony;
        public static int Patched;
        public static int Failed;

        // ------------------------------------------------------------------
        //  prefixes  (return false => skip the original and use __result)
        //  Original arguments are accessed positionally (__0) so we don't depend on Unity's parameter names.
        // ------------------------------------------------------------------
        static bool PreMouseBool(ref bool __result)
        {
            if (BlockMouse) { __result = false; return false; }
            return true;
        }

        static bool PreKeyCode(KeyCode __0, ref bool __result)
        {
            if (BlockKeys || (BlockMouse && __0 >= KeyCode.Mouse0 && __0 <= KeyCode.Mouse6))
            { __result = false; return false; }
            return true;
        }

        static bool PreKeyName(string __0, ref bool __result)
        {
            if (BlockKeys || (BlockMouse && __0 != null && __0.StartsWith("mouse", StringComparison.OrdinalIgnoreCase)))
            { __result = false; return false; }
            return true;
        }

        static bool PreAnyKey(ref bool __result)
        {
            if (BlockMouse || BlockKeys) { __result = false; return false; }
            return true;
        }

        static bool IsMouseAxis(string n)
        {
            return n != null && n.IndexOf("mouse", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool PreAxis(string __0, ref float __result)
        {
            if (BlockMouse && IsMouseAxis(__0)) { __result = 0f; return false; }
            return true;
        }

        static bool PreButtonName(string __0, ref bool __result)
        {
            // default Input Manager mouse buttons: "Fire1".."Fire3"
            if (BlockMouse && __0 != null &&
                (__0.StartsWith("fire", StringComparison.OrdinalIgnoreCase) || IsMouseAxis(__0)))
            { __result = false; return false; }
            return true;
        }

        static bool PreScroll(ref Vector2 __result)
        {
            if (BlockMouse) { __result = Vector2.zero; return false; }
            return true;
        }

        static bool PreInputString(ref string __result)
        {
            if (BlockKeys) { __result = ""; return false; }
            return true;
        }

        // ------------------------------------------------------------------
        //  install / uninstall
        // ------------------------------------------------------------------
        static void PatchOne(Action<string> log, string method, Type[] sig, string prefixName)
        {
            try
            {
                BindingFlags bf = BindingFlags.Public | BindingFlags.Static;
                MethodBase orig = (sig == null)
                    ? (MethodBase)typeof(Input).GetMethod(method, bf)
                    : (MethodBase)typeof(Input).GetMethod(method, bf, null, sig, null);
                if (orig == null) { Failed++; log("[US2Dome] InputBlocker: Input." + method + " not found (skipped)"); return; }
                MethodInfo pre = typeof(InputBlocker).GetMethod(prefixName, BindingFlags.NonPublic | BindingFlags.Static);
                if (pre == null) { Failed++; log("[US2Dome] InputBlocker: prefix " + prefixName + " missing"); return; }
                _harmony.Patch(orig, new HarmonyMethod(pre));
                Patched++;
            }
            catch (Exception e)
            {
                Failed++;
                log("[US2Dome] InputBlocker: patching Input." + method + " failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        public static void Install(Action<string> log)
        {
            if (_harmony != null) return;
            try
            {
                _harmony = new Harmony("com.us2dome.inputblocker");
                Type[] i1 = new Type[] { typeof(int) };
                Type[] k1 = new Type[] { typeof(KeyCode) };
                Type[] s1 = new Type[] { typeof(string) };

                PatchOne(log, "GetMouseButton",     i1, "PreMouseBool");
                PatchOne(log, "GetMouseButtonDown", i1, "PreMouseBool");
                PatchOne(log, "GetMouseButtonUp",   i1, "PreMouseBool");

                PatchOne(log, "GetKey",     k1, "PreKeyCode");
                PatchOne(log, "GetKeyDown", k1, "PreKeyCode");
                PatchOne(log, "GetKeyUp",   k1, "PreKeyCode");
                PatchOne(log, "GetKey",     s1, "PreKeyName");
                PatchOne(log, "GetKeyDown", s1, "PreKeyName");
                PatchOne(log, "GetKeyUp",   s1, "PreKeyName");

                PatchOne(log, "get_anyKey",     null, "PreAnyKey");
                PatchOne(log, "get_anyKeyDown", null, "PreAnyKey");

                PatchOne(log, "GetAxis",    s1, "PreAxis");
                PatchOne(log, "GetAxisRaw", s1, "PreAxis");
                PatchOne(log, "GetButton",     s1, "PreButtonName");
                PatchOne(log, "GetButtonDown", s1, "PreButtonName");
                PatchOne(log, "GetButtonUp",   s1, "PreButtonName");

                PatchOne(log, "get_mouseScrollDelta", null, "PreScroll");
                PatchOne(log, "get_inputString",      null, "PreInputString");

                log("[US2Dome] InputBlocker: " + Patched + " Input patches applied, " + Failed + " failed.");
            }
            catch (Exception e)
            {
                log("[US2Dome] InputBlocker install failed (panel clicks may reach the game): " + e);
            }
        }

        public static void Uninstall()
        {
            PanelOpen = false; MouseOver = false; MouseCaptured = false; CapturingKey = false; TextFocus = false;
            try { if (_harmony != null) { _harmony.UnpatchSelf(); _harmony = null; } }
            catch { }
        }

        // For the log: is the new Input System package in use by the game?
        public static string DescribeInputSystems()
        {
            try
            {
                string found = "";
                Assembly[] all = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < all.Length; i++)
                {
                    string n = all[i].GetName().Name;
                    if (n.StartsWith("Unity.InputSystem", StringComparison.OrdinalIgnoreCase) ||
                        n.StartsWith("Rewired", StringComparison.OrdinalIgnoreCase) ||
                        n.StartsWith("InControl", StringComparison.OrdinalIgnoreCase))
                        found += (found.Length > 0 ? ", " : "") + n;
                }
                return found.Length > 0 ? found : "none (legacy UnityEngine.Input only)";
            }
            catch (Exception e) { return "unknown (" + e.Message + ")"; }
        }
    }
}
