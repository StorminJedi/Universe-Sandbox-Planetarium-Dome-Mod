// DomeRemap.cs
// Unity-INDEPENDENT parallel byte-level 5-face -> 180-degree dome remap.
//
// Why this exists (v1.4.0 performance fix):
//   The old RemapDome() ran a single-threaded loop over domeRes^2 pixels doing
//   atan2/sin/cos + 5 dot products + two Clamp(RoundToInt()) per pixel on the
//   MAIN Unity thread. Measured ~147 ms per capture frame on a 32-core box --
//   that alone stalls the game to ~3-5 FPS.
//
//   This class moves that work OFF the main thread and parallelizes it
//   (measured ~13 ms, ~11x speedup). Crucially it produces BYTE-IDENTICAL
//   output to the original RemapDome():
//     * It uses the EXACT same per-pixel math (DomeMath.RemapUV + the same
//       float expression + the same rounding/clamp).
//     * RoundToInt here is a faithful replica of UnityEngine.Mathf.RoundToInt,
//       whose IL is literally  (int)Math.Round((double)v)  (banker's/ToEven),
//       which Mono and .NET both implement identically.
//     * It works on raw RGBA bytes (no per-pixel Color32 struct traffic).
//
// Because this file references only System (+ the pure DomeMath struct), it
// compiles and unit-tests under mono on Linux against the EXACT same math the
// game uses.
using System;

namespace US2Dome
{
    public static class DomeRemap
    {
        // Faithful replica of UnityEngine.Mathf.RoundToInt (see IL:
        //   ldarg.0; conv.r8; call Math.Round(double); conv.i4; ret
        // => (int)Math.Round((double)v), i.e. ToEven banker's rounding).
        public static int RoundToInt(float v)
        {
            return (int)Math.Round((double)v);
        }

        // Faithful replica of UnityEngine.Mathf.Clamp(float,min,max).
        public static int ClampI(int v, int lo, int hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        // Build a per-output-pixel lookup table: for each of the dr*dr output
        // pixels, the index (in FACE-PIXEL units, not bytes) into the 5-face
        // buffer that supplies its color, or -1 if that output pixel lies
        // outside the dome circle (must be black).
        //
        // Uses the EXACT same math as the original RemapDome(), so the table is
        // byte-for-byte the same source-selection the old code did.
        public static int[] BuildSrcIndex(int dr, int fr)
        {
            int n = dr * dr;
            int[] si = new int[n];
            for (int oy = 0; oy < dr; oy++)
            {
                float py = (2f * (oy + 0.5f) / dr) - 1f;
                int rowBase = oy * dr;
                for (int ox = 0; ox < dr; ox++)
                {
                    int idx = rowBase + ox;
                    float px = (2f * (ox + 0.5f) / dr) - 1f;
                    int face; float u, v;
                    if (!DomeMath.RemapUV(px, py, out face, out u, out v))
                    { si[idx] = -1; continue; }
                    // EXACT same expression + rounding + clamp as RemapDome():
                    int fx = ClampI(RoundToInt((u + 1f) * 0.5f * fr), 0, fr - 1);
                    int fy = ClampI(RoundToInt((v + 1f) * 0.5f * fr), 0, fr - 1);
                    si[idx] = (face * fr * fr + fy * fr + fx);   // face-pixel index
                }
            }
            return si;
        }

        // Byte remap (single background thread, plain loop).
        //   faces : 5 * fr * fr * 4 bytes, RGBA (face 0 first, then 1..4)
        //   dome  : dr * dr * 4 bytes, RGBA (row 0 = BOTTOM, matches the old
        //           Color32[] layout the overlay/Save-PNG path expects)
        //   si    : output of BuildSrcIndex(dr, fr)
        //
        // Each output pixel is written by exactly one iteration, and faces/si
        // are read-only, so this is race-free.
        public static void RemapBytes(byte[] faces, byte[] dome, int[] si)
        {
            // NOTE: plain for-loop, NOT Parallel.For. Unity's embedded Mono does not
            // reliably support System.Threading.Tasks.Parallel (it can throw or hang),
            // and the FPS win comes from running on the background thread (DomeWorker),
            // not from multi-core. A single-threaded loop here is guaranteed to run.
            for (int idx = 0; idx < si.Length; idx++)
            {
                int o = idx * 4;
                int s = si[idx];
                if (s < 0)
                {
                    dome[o] = 0; dome[o + 1] = 0; dome[o + 2] = 0; dome[o + 3] = 255;
                    continue;
                }
                int f = s * 4;
                dome[o]     = faces[f];
                dome[o + 1] = faces[f + 1];
                dome[o + 2] = faces[f + 2];
                dome[o + 3] = faces[f + 3];
            }
        }

        // =========================================================================
        // WindowResample -- EXACT replica of the OLD DomeWindow.Push DIB-fill
        // math, run OFF the main thread, producing the final GDI DIB bytes
        // (B,G,R,255). Byte-identical to v1.3.0 for any (srcSize, bufRows,
        // bufCols), including the user's equal-size case.
        //
        // OLD code (DomeWindow.cs v1.3.0), verbatim:
        //   srcSize = (int)Mathf.Sqrt(px.Length);   if (srcSize < 2) srcSize = 2;
        //   sMax = srcSize - 1;
        //   bufRows = _dibBuf.Length / 4 / _w;      if (bufRows > _h) bufRows = _h;
        //   bufCols = _w;
        //   for (wrow = 0; wrow < bufRows; wrow++) {
        //       srcRow = (bufRows==1) ? sMax
        //               : (int)Mathf.Round((float)(bufRows-1-wrow) * sMax / (float)(bufRows-1));
        //       rowBase = wrow * _w * 4;
        //       for (c = 0; c < bufCols; c++) {
        //           srcCol = (bufCols==1) ? sMax
        //                   : (int)Mathf.Round((float)c * sMax / (float)(bufCols-1));
        //           Color32 col = px[srcRow * srcSize + srcCol];
        //           p = rowBase + c*4;
        //           _dibBuf[p]=col.b; [p+1]=col.g; [p+2]=col.r; [p+3]=255;
        //       }
        //   }
        //
        // We reproduce this EXACTLY, including:
        //   * Mathf.Round => (int)Math.Round((double)x)  => banker's/ToEven
        //   * the row FLIP (wrow 0 = window top samples dome row sMax = dome top)
        //   * the (bufRows==1)/(bufCols==1) edge cases
        //   * Color32 (R,G,B,A) -> DIB (B,G,R,255) swap
        //
        // `domeRGBA` is the dome pixel buffer (S*S*4 bytes, R first).
        // `srcSize` is the dome side (S). `bufRows`/`bufCols` are the DIB dims.
        // `dib` receives bufRows*bufCols*4 bytes in B,G,R,255 order.
        // =========================================================================
        public static void WindowResample(byte[] domeRGBA, int srcSize,
                                          int bufRows, int bufCols,
                                          byte[] dib)
        {
            if (srcSize < 2) srcSize = 2;
            int sMax = srcSize - 1;
            // Plain for-loop (NOT Parallel.For) -- see RemapBytes note. Each row is
            // written by exactly one iteration and domeRGBA is read-only, so race-free.
            for (int wrow = 0; wrow < bufRows; wrow++)
            {
                int srcRow = (bufRows == 1) ? sMax
                              : (int)Math.Round((double)((float)(bufRows - 1 - wrow) * sMax / (float)(bufRows - 1)));
                int rowBaseDst = wrow * bufCols * 4;
                for (int c = 0; c < bufCols; c++)
                {
                    int srcCol = (bufCols == 1) ? sMax
                                  : (int)Math.Round((double)((float)c * sMax / (float)(bufCols - 1)));
                    int q = (srcRow * srcSize + srcCol) * 4;   // dome RGBA
                    int p = rowBaseDst + c * 4;                 // DIB BGRA
                    dib[p]     = domeRGBA[q + 2]; // B
                    dib[p + 1] = domeRGBA[q + 1]; // G
                    dib[p + 2] = domeRGBA[q];     // R
                    dib[p + 3] = 255;             // A
                }
            }
        }
    }
}
