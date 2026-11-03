using System;
using System.Diagnostics;
using US2Dome;

class TimeIt
{
    static byte[] Faces(int fr)
    {
        int[] c = { 255,200,150,100,50 };
        byte[] f = new byte[5*fr*fr*4];
        for (int i=0;i<5;i++) for (int y=0;y<fr;y++) for (int x=0;x<fr;x++){
            int p=(i*fr*fr+y*fr+x)*4; f[p]=(byte)c[i]; f[p+1]=(byte)(y); f[p+2]=(byte)(x); f[p+3]=255; }
        return f;
    }
    static void Main()
    {
        int fr=512, dr=1024, wr=1024;
        byte[] faces = Faces(fr);

        // ONE-TIME: build src index
        var sw = Stopwatch.StartNew();
        int[] si = DomeRemap.BuildSrcIndex(dr, fr);
        sw.Stop();
        Console.WriteLine("BuildSrcIndex (one-time): " + sw.ElapsedMilliseconds + " ms");

        byte[] dome = new byte[dr*dr*4];
        byte[] win  = new byte[wr*wr*4];

        // PER-CAPTURE single-threaded remap (what a plain Thread / main thread would do)
        for (int warm=0; warm<3; warm++) DomeRemap.RemapBytes(faces, dome, si);
        sw = Stopwatch.StartNew();
        int iters = 20;
        for (int k=0;k<iters;k++) DomeRemap.RemapBytes(faces, dome, si);
        sw.Stop();
        Console.WriteLine("RemapBytes (per capture, avg): " + (sw.ElapsedMilliseconds/iters) + " ms  [x" + iters + "]");

        // PER-CAPTURE single-threaded window resample
        for (int warm=0; warm<3; warm++) DomeRemap.WindowResample(dome, dr, wr, wr, win);
        sw = Stopwatch.StartNew();
        for (int k=0;k<iters;k++) DomeRemap.WindowResample(dome, dr, wr, wr, win);
        sw.Stop();
        Console.WriteLine("WindowResample (per capture, avg): " + (sw.ElapsedMilliseconds/iters) + " ms  [x" + iters + "]");

        long total = 0;
        for (int k=0;k<iters;k++){ long t=0; var s2=Stopwatch.StartNew(); DomeRemap.RemapBytes(faces,dome,si); DomeRemap.WindowResample(dome,dr,wr,wr,win); s2.Stop(); t=s2.ElapsedMilliseconds; if(k==0) total=t; else total+=t; }
        Console.WriteLine("TOTAL per capture (remap+resample): ~" + (total/iters) + " ms");
        Console.WriteLine("  -> every 3 frames => amortized ~" + ((total/iters)/3.0f) + " ms/frame on ONE thread");
    }
}
