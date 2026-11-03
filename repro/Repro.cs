using System;
using System.Threading;
using US2Dome;

class Repro
{
    // ---- synthetic faces: face i is a solid color + per-face gradient so a
    //      wrong face-selection or broken index shows up clearly.
    static byte[] MakeFaces(int fr)
    {
        int[] cols = { 255, 200, 150, 100, 50 };
        byte[] f = new byte[5 * fr * fr * 4];
        for (int i = 0; i < 5; i++)
        {
            int baseI = i * fr * fr;
            for (int y = 0; y < fr; y++)
                for (int x = 0; x < fr; x++)
                {
                    int p = (baseI + y * fr + x) * 4;
                    f[p]     = (byte)cols[i];          // R
                    f[p + 1] = (byte)(y * 255 / Math.Max(1, fr - 1)); // G = v grad
                    f[p + 2] = (byte)(x * 255 / Math.Max(1, fr - 1)); // B = u grad
                    f[p + 3] = 255;
                }
        }
        return f;
    }

    // v1.3.0 reference: RemapDome() + DomeWindow.Push() DIB fill, exactly as in
    // the archived code (single-threaded, the code that WORKED).
    static byte[] V130Pipeline(byte[] faces, int fr, int dr, int winW, int winH)
    {
        byte[] dome = new byte[dr * dr * 4];
        // v1.3.0 RemapDome (Color32, R,G,B,A)
        for (int oy = 0; oy < dr; oy++)
        {
            float py = (2f * (oy + 0.5f) / dr) - 1f;
            for (int ox = 0; ox < dr; ox++)
            {
                float px = (2f * (ox + 0.5f) / dr) - 1f;
                int idx = oy * dr + ox;
                int face; float u, v;
                if (!DomeMath.RemapUV(px, py, out face, out u, out v))
                {
                    dome[idx*4]=0; dome[idx*4+1]=0; dome[idx*4+2]=0; dome[idx*4+3]=255;
                    continue;
                }
                int fx = (int)Math.Round((double)((u + 1f) * 0.5f * fr));
                int fy = (int)Math.Round((double)((v + 1f) * 0.5f * fr));
                if (fx < 0) fx = 0; if (fx > fr-1) fx = fr-1;
                if (fy < 0) fy = 0; if (fy > fr-1) fy = fr-1;
                int s = (face * fr * fr + fy * fr + fx) * 4;
                dome[idx*4]   = faces[s];
                dome[idx*4+1] = faces[s+1];
                dome[idx*4+2] = faces[s+2];
                dome[idx*4+3] = faces[s+3];
            }
        }
        // v1.3.0 DomeWindow.Push DIB fill (top-down, B,G,R,255)
        int srcSize = dr; int sMax = dr - 1;
        byte[] dib = new byte[winH * winW * 4];
        for (int wrow = 0; wrow < winH; wrow++)
        {
            int srcRow = (int)Math.Round((double)((float)(winH - 1 - wrow) * sMax / (float)(winH - 1)));
            for (int c = 0; c < winW; c++)
            {
                int srcCol = (int)Math.Round((double)((float)c * sMax / (float)(winW - 1)));
                int q = (srcRow * srcSize + srcCol) * 4;
                int p = wrow * winW * 4 + c * 4;
                dib[p]     = dome[q + 2];
                dib[p + 1] = dome[q + 1];
                dib[p + 2] = dome[q];
                dib[p + 3] = 255;
            }
        }
        return dib;
    }

    static void Main()
    {
        int fr = 512, dr = 1024, winW = 1024, winH = 1024;

        Console.WriteLine("== TEST A: Parallel.For on MAIN thread (sanity) ==");
        try
        {
            byte[] faces = MakeFaces(fr);
            int[] si = DomeRemap.BuildSrcIndex(dr, fr);
            byte[] dome = new byte[dr * dr * 4];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            DomeRemap.RemapBytes(faces, dome, si);
            sw.Stop();
            Console.WriteLine("OK, took " + sw.ElapsedMilliseconds + " ms");
            // spot-check: dome center should be face0 (R=255), G=middle, B=middle
            int c = ((dr/2)*dr + dr/2) * 4;
            Console.WriteLine("center dome byte: " + dome[c] + "," + dome[c+1] + "," + dome[c+2] + "," + dome[c+3] + " (expect 255,~127,~127,255)");
            // corner (outside circle) must be black
            int k = 0;
            Console.WriteLine("corner dome byte: " + dome[k] + "," + dome[k+1] + "," + dome[k+2] + "," + dome[k+3] + " (expect 0,0,0,255)");
        }
        catch (Exception e) { Console.WriteLine("THREW: " + e); Environment.Exit(2); }

        Console.WriteLine("\n== TEST B: full worker pipeline (background thread, EXACT shipped code) ==");
        try
        {
            var w = new DomeWorker();
            byte[] faces = MakeFaces(fr);
            byte[] dome = new byte[dr * dr * 4];
            byte[] win  = new byte[winH * winW * 4];
            // fill with 0xAA so we can detect "never written"
            for (int i = 0; i < dome.Length; i++) dome[i] = 0xAA;
            for (int i = 0; i < win.Length; i++)  win[i]  = 0xAA;

            bool ok = w.Submit(faces, fr, dr, winW, winH, dome, win);
            Console.WriteLine("Submit returned: " + ok);

            // wait up to 20s for the result (mimic the game's per-capture TryGetResult)
            bool got = false;
            var deadline = DateTime.Now.AddSeconds(20);
            while (DateTime.Now < deadline)
            {
                if (w.TryGetResult()) { got = true; break; }
                Thread.Sleep(20);
            }
            Console.WriteLine("Result ready within 20s: " + got);
            if (!got)
            {
                Console.WriteLine(">>> WORKER HUNG / NEVER COMPLETED <<<");
                // inspect worker state
                Console.WriteLine("IsBusy=" + w.IsBusy);
            }

            if (got)
            {
                // compare to v1.3.0 reference
                byte[] refDib = V130Pipeline(faces, fr, dr, winW, winH);
                int mismatch = 0;
                for (int i = 0; i < refDib.Length; i++)
                    if (refDib[i] != win[i]) { if (mismatch < 5) Console.WriteLine("win mismatch @ " + i + " ref=" + refDib[i] + " got=" + win[i]); mismatch++; }
                Console.WriteLine("window bytes mismatch count vs v1.3.0: " + mismatch + " of " + refDib.Length);

                // dome center check
                int c = ((dr/2)*dr + dr/2) * 4;
                Console.WriteLine("dome center: " + dome[c] + "," + dome[c+1] + "," + dome[c+2] + "," + dome[c+3]);
                // how many AA (never-written) bytes remain in dome?
                int unwritten = 0;
                for (int i = 0; i < dome.Length; i++) if (dome[i] == 0xAA) unwritten++;
                Console.WriteLine("dome never-written bytes: " + unwritten + " of " + dome.Length);
                unwritten = 0;
                for (int i = 0; i < win.Length; i++) if (win[i] == 0xAA) unwritten++;
                Console.WriteLine("win  never-written bytes: " + unwritten + " of " + win.Length);
            }
            w.Dispose();
        }
        catch (Exception e) { Console.WriteLine("THREW: " + e); }

        Console.WriteLine("\n== TEST C: second Submit (consecutive job) ==");
        try
        {
            var w = new DomeWorker();
            byte[] faces = MakeFaces(fr);
            byte[] dome = new byte[dr * dr * 4];
            byte[] win  = new byte[winH * winW * 4];
            w.Submit(faces, fr, dr, winW, winH, dome, win);
            bool g1 = WaitFor(w, 10);
            w.Submit(faces, fr, dr, winW, winH, dome, win);
            bool g2 = WaitFor(w, 10);
            Console.WriteLine("job1=" + g1 + " job2=" + g2);
            w.Dispose();
        }
        catch (Exception e) { Console.WriteLine("THREW: " + e); }

        Console.WriteLine("\nDONE");
    }

    static bool WaitFor(DomeWorker w, int seconds)
    {
        var d = DateTime.Now.AddSeconds(seconds);
        while (DateTime.Now < d) { if (w.TryGetResult()) return true; Thread.Sleep(10); }
        return false;
    }
}
