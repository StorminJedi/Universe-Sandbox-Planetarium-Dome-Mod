// DomeMath.cs
// Unity-independent math for the 5-face -> 180-degree equidistant dome remap.
// Pure C# (System only) so it can be unit-tested with mono on Linux.
using System;

namespace US2Dome
{
    public struct V3
    {
        public float X, Y, Z;
        public V3(float x, float y, float z) { X = x; Y = y; Z = z; }
        public static V3 operator *(V3 a, V3 b) { return new V3(a.X * b.X, a.Y * b.Y, a.Z * b.Z); }
        public static V3 operator +(V3 a, V3 b) { return new V3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        public static V3 operator -(V3 a, V3 b) { return new V3(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
        public static V3 operator *(V3 a, float s) { return new V3(a.X * s, a.Y * s, a.Z * s); }
        public static float Dot(V3 a, V3 b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }
        public float Len() { return (float)Math.Sqrt(X * X + Y * Y + Z * Z); }
        public V3 Norm() { float l = Len(); if (l < 1e-9f) return new V3(0, 0, 0); return new V3(X / l, Y / l, Z / l); }
        public override string ToString() { return string.Format("({0:F4},{1:F4},{2:F4})", X, Y, Z); }
    }

    public static class DomeMath
    {
        // Face order: 0=F(+z), 1=R(+x), 2=L(-x), 3=U(+y), 4=D(-y)
        // All vectors expressed in the MAIN CAMERA base frame (+x right, +y up, +z forward).
        public static readonly V3[] Fwd =
        {
            new V3( 0, 0, 1),
            new V3( 1, 0, 0),
            new V3(-1, 0, 0),
            new V3( 0, 1, 0),
            new V3( 0,-1, 0),
        };
        public static readonly V3[] Right =
        {
            new V3( 1, 0, 0),
            new V3( 0, 0,-1),
            new V3( 0, 0, 1),
            new V3( 1, 0, 0),
            new V3( 1, 0, 0),
        };
        public static readonly V3[] Up =
        {
            new V3( 0, 1, 0),
            new V3( 0, 1, 0),
            new V3( 0, 1, 0),
            new V3( 0, 0,-1),
            new V3( 0, 0, 1),
        };

        // Convert an output-pixel coordinate (px,py) in [-1,1] (py +1 = up) into
        // (face index, u, v) where u,v are in [-1,1] on the chosen face square.
        // Returns false if the pixel is outside the dome circle (r > 1).
        public static bool RemapUV(float px, float py, out int face, out float u, out float v)
        {
            face = -1; u = 0; v = 0;
            float r = (float)Math.Sqrt(px * px + py * py);
            if (r > 1.0f) return false;
            // 180-degree equidistant: edge (r=1) is 90 deg from center.
            float theta = (float)(Math.PI / 2.0 * r);
            float az = (float)Math.Atan2(py, px);
            // Direction in the camera base frame (+x right, +y up, +z forward).
            V3 d = new V3(
                (float)(Math.Sin(theta) * Math.Cos(az)),
                (float)(Math.Sin(theta) * Math.Sin(az)),
                (float)Math.Cos(theta)
            );
            // Pick the face whose forward has the largest dot with d (front hemisphere).
            float best = -2f;
            int bi = 0;
            for (int i = 0; i < 5; i++)
            {
                float dd = V3.Dot(d, Fwd[i]);
                if (dd > best) { best = dd; bi = i; }
            }
            face = bi;
            float dz = V3.Dot(d, Fwd[bi]);
            if (dz < 1e-6f) dz = 1e-6f;
            u = V3.Dot(d, Right[bi]) / dz;
            v = V3.Dot(d, Up[bi]) / dz;
            // Clamp tiny overshoots from float error.
            if (u > 1f) u = 1f; if (u < -1f) u = -1f;
            if (v > 1f) v = 1f; if (v < -1f) v = -1f;
            return true;
        }

        // The camera rotation (as Euler XYZ degrees, relative to base) to point a
        // 90-degree-FOV square camera at each face. Matches Fwd[]/Up[] above.
        public static readonly float[] FaceEulerX = { 0, 0, 0, -90, 90 };
        public static readonly float[] FaceEulerY = { 0, 90, -90, 0, 0 };
        public static readonly float[] FaceEulerZ = { 0, 0, 0, 0, 0 };
    }
}
