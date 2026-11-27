using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;

namespace IDA.Bambu
{
    // 메쉬 삼각형 정보 (방향 점수 계산용)
    public class MeshInfo
    {
        public Point3d[] V;      // 꼭짓점
        public Vector3d[] N;     // 삼각형 법선
        public Point3d[] C;      // 삼각형 중심
        public double[] A;       // 삼각형 넓이
        public double Total;
        public double Diag;

        public static MeshInfo From(Mesh src)
        {
            var m = src.DuplicateMesh();
            m.Faces.ConvertQuadsToTriangles();
            m.Normals.ComputeNormals();
            m.FaceNormals.ComputeFaceNormals();
            var info = new MeshInfo();
            info.V = m.Vertices.Select(p => new Point3d(p)).ToArray();
            int n = m.Faces.Count;
            info.N = new Vector3d[n]; info.C = new Point3d[n]; info.A = new double[n];
            for (int i = 0; i < n; i++)
            {
                var f = m.Faces[i];
                Point3d a = info.V[f.A], b = info.V[f.B], c = info.V[f.C];
                var cr = Vector3d.CrossProduct(b - a, c - a);
                double area = cr.Length / 2;
                info.A[i] = area;
                info.C[i] = new Point3d((a.X + b.X + c.X) / 3, (a.Y + b.Y + c.Y) / 3, (a.Z + b.Z + c.Z) / 3);
                info.N[i] = area > 0 ? cr / (2 * area) : Vector3d.Zero;
                info.Total += area;
            }
            info.Diag = m.GetBoundingBox(true).Diagonal.Length;
            return info;
        }
    }

    public class OrientResult
    {
        public Transform Xform;        // 원래 위치 → 판 위 (Z=0 바닥, XY 최소 = 원점)
        public double Width, Depth, Height;
        public double OverhangArea;    // 45°보다 누운 아랫면 (서포트 필요)
        public double ContactArea;     // 바닥에 닿는 면
        public double Score;
        public bool Fits;
        public double BaseWidth;       // 바닥에 닿는 자국의 가장 좁은 폭
        public int Stability;          // 0 안정 · 1 브림 권장 · 2 불안정(모서리·점으로 섬)
    }

    // 한 객체의 방향 후보 중: 판에 들어가는 최선(Fit) / 판 크기 무시한 최선(Free = 나눠서 눕힐 때)
    public class OrientSet
    {
        public OrientResult Fit, Free;
    }

    public static class Orient
    {
        const double Cos45 = 0.70710678;

        // 바닥으로 갈 방향 후보: 넓은 평면(법선을 넓이로 묶음) + 축·대각선
        public static List<Vector3d> Candidates(MeshInfo mi)
        {
            var bins = new Dictionary<(int, int, int), (Vector3d n, double a)>();
            for (int i = 0; i < mi.N.Length; i++)
            {
                var v = mi.N[i]; if (v.IsZero) continue;
                var key = ((int)Math.Round(v.X * 12), (int)Math.Round(v.Y * 12), (int)Math.Round(v.Z * 12));
                bins.TryGetValue(key, out var e);
                bins[key] = (e.n + v * mi.A[i], e.a + mi.A[i]);
            }
            var list = bins.Values.OrderByDescending(b => b.a).Take(30)
                .Select(b => { var d = b.n; d.Unitize(); return d; }).ToList();
            for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++) for (int z = -1; z <= 1; z++)
            {
                if (x == 0 && y == 0 && z == 0) continue;
                var d = new Vector3d(x, y, z); d.Unitize(); list.Add(d);
            }
            var outList = new List<Vector3d>();
            foreach (var d in list)
                if (!outList.Any(o => o * d > Math.Cos(Math.PI / 36))) outList.Add(d); // 5° 안은 같은 후보
            return outList;
        }

        // down 방향이 바닥(-Z)으로 가도록 돌리는 변환
        public static Transform DownToBed(Vector3d down)
        {
            var target = -Vector3d.ZAxis;
            if (down * target < -0.9999) return Transform.Rotation(Math.PI, Vector3d.XAxis, Point3d.Origin);
            if (down * target > 0.9999) return Transform.Identity;
            return Transform.Rotation(down, target, Point3d.Origin);
        }

        // tolMm: 문서 단위로 1mm
        public static (double score, double overhang, double contact, double height) Score(MeshInfo mi, Vector3d down, double mm)
        {
            var up = -down;
            double zmin = double.MaxValue, zmax = double.MinValue;
            foreach (var p in mi.V) { double z = p.X * up.X + p.Y * up.Y + p.Z * up.Z; if (z < zmin) zmin = z; if (z > zmax) zmax = z; }
            double over = 0, contact = 0;
            for (int i = 0; i < mi.N.Length; i++)
            {
                double nz = mi.N[i] * up;
                if (nz >= -Cos45) continue;
                double cz = mi.C[i].X * up.X + mi.C[i].Y * up.Y + mi.C[i].Z * up.Z - zmin;
                if (nz < -0.985 && cz < 0.2 * mm) contact += mi.A[i];
                else if (cz > 0.4 * mm) over += mi.A[i];
            }
            double h = zmax - zmin;
            double t = Math.Max(mi.Total, 1e-9);
            double score = 10 * over / t + 1.0 * h / Math.Max(mi.Diag, 1e-9) - 3 * contact / t;
            if (contact / t < 0.003) score += 1.5; // 점·선으로만 닿으면 쓰러지거나 떨어짐
            return (score, over, contact, h);
        }

        // 바닥에 눕힌 뒤 Z축으로 돌려 바닥 자국이 가장 작은 각도 (판에 들어가는 쪽 우선)
        public static (double angle, double w, double d) BestSpin(IList<Point3d> pts, double plateW, double plateD)
        {
            var sample = pts.Count > 4000 ? pts.Where((p, i) => i % (pts.Count / 4000 + 1) == 0).ToList() : pts.ToList();
            double best = double.MaxValue, bestA = 0, bw = 0, bd = 0; bool bestFit = false;
            for (int k = 0; k < 36; k++)
            {
                double a = k * Math.PI / 72; // 0~87.5°, 2.5° 간격
                double c = Math.Cos(a), s = Math.Sin(a);
                double x0 = double.MaxValue, x1 = double.MinValue, y0 = double.MaxValue, y1 = double.MinValue;
                foreach (var p in sample)
                {
                    double x = p.X * c - p.Y * s, y = p.X * s + p.Y * c;
                    if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y;
                }
                double w = x1 - x0, d = y1 - y0;
                bool fit = (w <= plateW && d <= plateD) || (d <= plateW && w <= plateD);
                double area = w * d;
                if ((fit && !bestFit) || (fit == bestFit && area < best - 1e-9)) { best = area; bestA = a; bw = w; bd = d; bestFit = fit; }
            }
            return (bestA, bw, bd);
        }

        // 판에 들어가는 방향 중 최선 (없으면 판 크기 무시한 최선, Fits=false)
        public static OrientResult Best(Mesh mesh, double mm, double plateW, double plateD, double plateH)
        {
            var a = Analyze(mesh, mm, plateW, plateD, plateH);
            return a.Fit ?? a.Free;
        }

        // 모든 후보를 판 크기·안정성까지 따져서 Fit / Free 두 개를 돌려줌
        public static OrientSet Analyze(Mesh mesh, double mm, double plateW, double plateD, double plateH)
        {
            var mi = MeshInfo.From(mesh);
            var all = new List<OrientResult>();
            foreach (var d in Candidates(mi))
            {
                var s = Score(mi, d, mm);
                var r = DownToBed(d);
                var pts = mi.V.Select(p => { var q = p; q.Transform(r); return q; }).ToList();
                var (ang, w, dep) = BestSpin(pts, plateW, plateD);
                var spin = Transform.Rotation(ang, Vector3d.ZAxis, Point3d.Origin);
                var x = spin * r;
                var placed = new List<Point3d>(mi.V.Length);
                var bb = BoundingBox.Empty;
                foreach (var p in mi.V) { var q = p; q.Transform(x); bb.Union(q); placed.Add(q); }
                bool fits = ((bb.Diagonal.X <= plateW && bb.Diagonal.Y <= plateD) || (bb.Diagonal.Y <= plateW && bb.Diagonal.X <= plateD)) && bb.Diagonal.Z <= plateH;

                // 바닥 자국(바닥에서 0.3mm 안의 점)의 가장 좁은 폭으로 쓰러질지 판단
                double h = bb.Diagonal.Z;
                var foot = placed.Where(q => q.Z - bb.Min.Z < 0.3 * mm).ToList();
                double bw = MinWidth(foot);
                int stab = Stab(s.contact / Math.Max(mi.Total, 1e-9), bw, h, mm);
                double score = s.score
                             + (stab == 2 ? 3.0 : stab == 1 ? 0.3 : 0)   // 쓰러질 자세는 크게 감점
                             + Math.Max(0, h / mm - 150) / 110.0;          // 150mm 넘게 높으면 시간·흔들림 감점

                var move = Transform.Translation(-bb.Min.X, -bb.Min.Y, -bb.Min.Z);
                all.Add(new OrientResult
                {
                    Xform = move * x, Width = bb.Diagonal.X, Depth = bb.Diagonal.Y, Height = h,
                    OverhangArea = s.overhang, ContactArea = s.contact, Score = score, Fits = fits,
                    BaseWidth = bw, Stability = stab,
                });
            }
            var sorted = all.OrderBy(o => o.Score).ToList();
            return new OrientSet { Free = sorted.FirstOrDefault(), Fit = sorted.FirstOrDefault(o => o.Fits) };
        }

        // 낮은 건 안 쓰러짐. 바닥 폭 ≥ 높이/4 안정, ≥ 높이/6 브림 권장, 그보다 좁거나 모서리·점으로만 닿으면 불안정
        static int Stab(double contactRatio, double baseW, double h, double mm)
        {
            if (h <= 15 * mm) return 0;
            if (contactRatio < 0.003 || baseW < 1 * mm) return 2;
            if (baseW >= h / 4) return 0;
            if (baseW >= h / 6) return 1;
            return 2;
        }

        // 점들을 XY에서 돌려 보며 가장 좁은 폭 (0~177.5°, 2.5° 간격)
        static double MinWidth(List<Point3d> pts)
        {
            if (pts.Count < 2) return 0;
            var sample = pts.Count > 3000 ? pts.Where((p, i) => i % (pts.Count / 3000 + 1) == 0).ToList() : pts;
            double best = double.MaxValue;
            for (int k = 0; k < 72; k++)
            {
                double a = k * Math.PI / 72, c = Math.Cos(a), s = Math.Sin(a);
                double lo = double.MaxValue, hi = double.MinValue;
                foreach (var p in sample) { double v = p.X * c - p.Y * s; if (v < lo) lo = v; if (v > hi) hi = v; }
                best = Math.Min(best, hi - lo);
            }
            return best;
        }

        // 놓인 상태(바닥 Z=0)에서 서포트가 필요한 아랫면만 모은 메쉬
        public static Mesh OverhangFaces(Mesh placed, double mm)
        {
            var m = placed.DuplicateMesh();
            m.Faces.ConvertQuadsToTriangles();
            var outM = new Mesh();
            for (int i = 0; i < m.Faces.Count; i++)
            {
                var f = m.Faces[i];
                Point3d a = m.Vertices[f.A], b = m.Vertices[f.B], c = m.Vertices[f.C];
                var n = Vector3d.CrossProduct(b - a, c - a);
                if (!n.Unitize() || n.Z >= -Cos45) continue;
                if ((a.Z + b.Z + c.Z) / 3 < 0.4 * mm) continue;
                int k = outM.Vertices.Count;
                outM.Vertices.Add(a); outM.Vertices.Add(b); outM.Vertices.Add(c);
                outM.Faces.AddFace(k, k + 1, k + 2);
            }
            if (outM.Faces.Count == 0) return null;
            outM.Normals.ComputeNormals();
            outM.Compact();
            return outM;
        }
    }
}
