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

        public static OrientResult Best(Mesh mesh, double mm, double plateW, double plateD, double plateH)
        {
            var mi = MeshInfo.From(mesh);
            var scored = Candidates(mi).Select(d => (d, s: Score(mi, d, mm))).OrderBy(x => x.s.score).ToList();
            OrientResult best = null;
            foreach (var (d, s) in scored.Take(12)) // 점수 좋은 12개만 판 크기까지 따져 봄
            {
                var r = DownToBed(d);
                var pts = mi.V.Select(p => { var q = p; q.Transform(r); return q; }).ToList();
                var (ang, w, dep) = BestSpin(pts, plateW, plateD);
                var spin = Transform.Rotation(ang, Vector3d.ZAxis, Point3d.Origin);
                var x = spin * r;
                var bb = BoundingBox.Empty;
                foreach (var p in mi.V) { var q = p; q.Transform(x); bb.Union(q); }
                bool fits = ((bb.Diagonal.X <= plateW && bb.Diagonal.Y <= plateD) || (bb.Diagonal.Y <= plateW && bb.Diagonal.X <= plateD)) && bb.Diagonal.Z <= plateH;
                double score = s.score + (fits ? 0 : 100);
                if (best == null || score < best.Score)
                {
                    var move = Transform.Translation(-bb.Min.X, -bb.Min.Y, -bb.Min.Z);
                    best = new OrientResult
                    {
                        Xform = move * x, Width = bb.Diagonal.X, Depth = bb.Diagonal.Y, Height = bb.Diagonal.Z,
                        OverhangArea = s.overhang, ContactArea = s.contact, Score = score, Fits = fits,
                    };
                }
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
