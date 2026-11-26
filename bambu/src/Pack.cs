using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Rhino.Geometry;

namespace IDA.Bambu
{
    public class PackItem
    {
        public double W, D;     // 바닥 자국 (X·Y)
        public bool Rotated;    // 90° 돌려서 놓음
        public int Plate = -1;  // 0부터
        public double X, Y;     // 판 안에서 놓인 위치 (왼쪽 아래)
    }

    public static class Pack
    {
        class Shelf { public double Y, H, X; }

        // 선반(줄) 방식 배치: 큰 것부터, 들어가는 첫 판·첫 줄에. 판이 모자라면 새 판.
        public static int Run(List<PackItem> items, double plateW, double plateD, double gap, double margin)
        {
            double uw = plateW - 2 * margin, ud = plateD - 2 * margin;
            var plates = new List<List<Shelf>>();
            foreach (var it in items.OrderByDescending(i => Math.Max(i.W, i.D)).ThenByDescending(i => i.W * i.D))
            {
                // 긴 변을 X로 (안 들어가면 반대로)
                double w = Math.Max(it.W, it.D), d = Math.Min(it.W, it.D);
                bool rot = it.D > it.W;
                if (w > uw) { (w, d) = (d, w); rot = !rot; }
                bool placed = false;
                for (int p = 0; p < plates.Count && !placed; p++) placed = TryPlace(plates[p], it, w, d, rot, p, uw, ud, gap, margin);
                if (!placed)
                {
                    plates.Add(new List<Shelf>());
                    placed = TryPlace(plates[plates.Count - 1], it, w, d, rot, plates.Count - 1, uw, ud, gap, margin);
                    if (!placed) { it.Plate = plates.Count - 1; it.X = margin; it.Y = margin; it.Rotated = rot; } // 판보다 큼 (나누지 않은 경우)
                }
            }
            return plates.Count;
        }

        static bool TryPlace(List<Shelf> shelves, PackItem it, double w, double d, bool rot, int plate, double uw, double ud, double gap, double margin)
        {
            foreach (var s in shelves)
                if (d <= s.H + 1e-9 && s.X + w <= uw + 1e-9)
                {
                    it.Plate = plate; it.X = margin + s.X; it.Y = margin + s.Y; it.Rotated = rot;
                    s.X += w + gap; return true;
                }
            double y = shelves.Count == 0 ? 0 : shelves.Last().Y + shelves.Last().H + gap;
            if (y + d > ud + 1e-9 || w > uw + 1e-9) return false;
            var ns = new Shelf { Y = y, H = d, X = w + gap };
            shelves.Add(ns);
            it.Plate = plate; it.X = margin; it.Y = margin + y; it.Rotated = rot;
            return true;
        }
    }

    public static class Stl
    {
        // 바이너리 STL (mm). scale: 문서 단위 → mm
        public static void Write(string path, Mesh mesh, double scale, string name)
        {
            var m = mesh.DuplicateMesh();
            m.Faces.ConvertQuadsToTriangles();
            using (var bw = new BinaryWriter(File.Create(path)))
            {
                var header = new byte[80];
                var txt = System.Text.Encoding.ASCII.GetBytes(("bambu " + name).PadRight(80).Substring(0, 80));
                Array.Copy(txt, header, 80);
                bw.Write(header);
                bw.Write((uint)m.Faces.Count);
                for (int i = 0; i < m.Faces.Count; i++)
                {
                    var f = m.Faces[i];
                    Point3d a = m.Vertices[f.A], b = m.Vertices[f.B], c = m.Vertices[f.C];
                    var n = Vector3d.CrossProduct(b - a, c - a); n.Unitize();
                    bw.Write((float)n.X); bw.Write((float)n.Y); bw.Write((float)n.Z);
                    foreach (var p in new[] { a, b, c }) { bw.Write((float)(p.X * scale)); bw.Write((float)(p.Y * scale)); bw.Write((float)(p.Z * scale)); }
                    bw.Write((ushort)0);
                }
            }
        }
    }
}
