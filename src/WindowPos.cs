// WindowPos.cs -- remembers where the dome window was (which monitor, position, size, fullscreen)
// and puts it back the next time the game starts.
using System;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;

namespace US2Dome
{
    public partial class US2Dome
    {
        ConfigEntry<bool>   _cfgRememberPos;
        ConfigEntry<string> _cfgWinRect;
        string _winRectSeen = "";
        float  _winRectSince;

        void BindWinPosConfig()
        {
            if (_cfgWinRect != null || _cfgFile == null) return;
            _cfgRememberPos = _cfgFile.Bind("General", "RememberWindowPosition", true,
                "Remember the dome window's monitor, position, size and fullscreen state and restore them on the next start.");
            _cfgWinRect = _cfgFile.Bind("General", "SecondWindowRect", "",
                "Saved automatically: x,y,width,height of the dome window (outer rectangle, in Windows screen pixels). Delete the value to reset to the default position.");
        }

        // Called right after the dome window is created (before fullscreen is applied, so fullscreen lands on the saved monitor).
        void RestoreWindowPos(DomeWindow w)
        {
            try
            {
                BindWinPosConfig();
                if (_cfgRememberPos == null || !_cfgRememberPos.Value) return;
                string s = _cfgWinRect.Value;
                _winRectSeen = s ?? "";
                if (string.IsNullOrEmpty(s)) return;
                string[] p = s.Split(',');
                int x, y, ww, hh;
                if (p.Length != 4 ||
                    !int.TryParse(p[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out x) ||
                    !int.TryParse(p[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out y) ||
                    !int.TryParse(p[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ww) ||
                    !int.TryParse(p[3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out hh))
                { LogWarn("[US2Dome] SecondWindowRect is not 'x,y,w,h': " + s); return; }
                bool ok = w.SetNormalRect(x, y, ww, hh);
                LogInfo("[US2Dome] Dome window position restore " + (ok ? "OK: " : "skipped: ") + s);
            }
            catch (Exception e) { LogWarn("[US2Dome] RestoreWindowPos failed: " + e.Message); }
        }

        // Called every frame while the window exists. Saves once the window has been still for a second
        // (so dragging doesn't write the file continuously).
        void TrackWindowPos()
        {
            try
            {
                if (_domeWindow == null || !_domeWindow.Ready) return;
                BindWinPosConfig();
                if (_cfgRememberPos == null || !_cfgRememberPos.Value) return;
                int x, y, w, h;
                if (!_domeWindow.GetNormalRect(out x, out y, out w, out h)) return;
                string cur = x + "," + y + "," + w + "," + h + (_domeWindow.IsFullscreen ? "F" : "");
                string saved = _cfgWinRect.Value + (_cfgWindowFull.Value ? "F" : "");
                if (cur == saved) { _winRectSeen = cur; return; }
                if (cur != _winRectSeen) { _winRectSeen = cur; _winRectSince = Time.unscaledTime; return; }
                if (Time.unscaledTime - _winRectSince < 1f) return;
                _cfgWinRect.Value = x + "," + y + "," + w + "," + h;
                _cfgWindowFull.Value = _domeWindow.IsFullscreen;
                _cfgDirty = true;
                SaveCfgNow();
            }
            catch (Exception e) { LogWarn("[US2Dome] TrackWindowPos failed: " + e.Message); }
        }

        // Immediate save (used just before the window is disposed).
        void CaptureWindowPosNow()
        {
            try
            {
                if (_domeWindow == null || !_domeWindow.Ready) return;
                BindWinPosConfig();
                if (_cfgRememberPos == null || !_cfgRememberPos.Value) return;
                int x, y, w, h;
                if (!_domeWindow.GetNormalRect(out x, out y, out w, out h)) return;
                _cfgWinRect.Value = x + "," + y + "," + w + "," + h;
                _cfgDirty = true;
            }
            catch { }
        }
    }
}
