using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace IDA.Bambu
{
    public static class Split
    {
        // 판보다 큰 닫힌 솔리드를 판에 들어가게 자름. 자른 면마다 맞춤 핀 구멍(Ø4.2, 양쪽 6mm)을 뚫을 수 있음.
        // frame: 서포트가 가장 적은 방향으로 눕힌 상태 (그 상태의 X·Y·Z로 자름)
        public static List<Brep> ToFit(Brep src, Transform frame, double limXY, double limZ, bool pins, double mm, double tol, List<string> log)
        {
            var g = src.DuplicateBrep();
            g.Transform(frame);
            var bb = g.GetBoundingBox(true);
            var size = bb.Diagonal;
            double margin = 2 * mm;
            var cuts = new List<double>[3] { new List<double>(), new List<double>(), new List<double>() };
            double[] lim = { limXY, limXY, limZ };
            double[] lo = { bb.Min.X, bb.Min.Y, bb.Min.Z };
            double[] len = { size.X, size.Y, size.Z };
            for (int ax = 0; ax < 3; ax++)
            {
                if (len[ax] <= lim[ax]) continue;
                int n = (int)Math.Ceiling(len[ax] / (lim[ax] - margin));
                for (int k = 1; k < n; k++) cuts[ax].Add(lo[ax] + len[ax] * k / n);
            }
            if (cuts.All(c => c.Count == 0)) return new List<Brep> { g };

            // 핀 구멍 자리: 자르기 전에 단면 가운데를 구해 두고, 자른 뒤 양쪽 조각에 구멍을 뚫음
            // (자르기 전에는 원기둥이 솔리드 안에 갇혀 있어서 빼기가 안 됨)
            var pinCyls = new List<Brep>();
            if (pins)
            {
                for (int ax = 0; ax < 3; ax++)
                    foreach (var pos in cuts[ax])
                    {
                        var normal = ax == 0 ? Vector3d.XAxis : ax == 1 ? Vector3d.YAxis : Vector3d.ZAxis;
                        var origin = new Point3d(ax == 0 ? pos : 0, ax == 1 ? pos : 0, ax == 2 ? pos : 0);
                        foreach (var c in PinPoints(g, new Plane(origin, normal), mm, tol))
                            pinCyls.Add(new Cylinder(new Circle(new Plane(c - normal * 6 * mm, normal), 2.1 * mm), 12 * mm).ToBrep(true, true));
                    }
            }

            // 자른 위치로 나눈 상자들과 겹치는 부분 = 조각
            var edges = new List<double>[3];
            double[] hi = { bb.Max.X, bb.Max.Y, bb.Max.Z };
            for (int ax = 0; ax < 3; ax++)
            {
                edges[ax] = new List<double> { lo[ax] - 1 };
                edges[ax].AddRange(cuts[ax]);
                edges[ax].Add(hi[ax] + 1);
            }
            var pieces = new List<Brep>();
            for (int i = 0; i + 1 < edges[0].Count; i++)
                for (int j = 0; j + 1 < edges[1].Count; j++)
                    for (int k = 0; k + 1 < edges[2].Count; k++)
                    {
                        var box = new BoundingBox(edges[0][i], edges[1][j], edges[2][k], edges[0][i + 1], edges[1][j + 1], edges[2][k + 1]);
                        var res = Brep.CreateBooleanIntersection(g, box.ToBrep(), tol);
                        if (res != null) pieces.AddRange(res.Where(r => r != null && r.IsValid));
                    }
            if (pins)
            {
                int made = 0;
                foreach (var cyl in pinCyls)
                {
                    bool any = false;
                    var cb = cyl.GetBoundingBox(true);
                    for (int i = 0; i < pieces.Count; i++)
                    {
                        if (!BoundingBox.Intersection(cb, pieces[i].GetBoundingBox(true)).IsValid) continue;
                        var diff = Brep.CreateBooleanDifference(pieces[i], cyl, tol);
                        if (diff != null && diff.Length == 1) { pieces[i] = diff[0]; any = true; }
                    }
                    if (any) made++;
                }
                log.Add(made > 0 ? $"    자른 면에 맞춤 핀 구멍 {made}쌍 (Ø4.2mm, 양쪽 6mm씩 — Ø4mm 핀이나 필라멘트 토막으로 맞춤)" : "    자른 면이 좁아 핀 구멍은 넣지 않았어요");
            }
            return pieces;
        }

        // 자른 면(단면)마다 가운데 한 점 — 벽에서 충분히 떨어진 경우만
        static IEnumerable<Point3d> PinPoints(Brep g, Plane plane, double mm, double tol)
        {
            if (!Intersection.BrepPlane(g, plane, tol, out Curve[] crvs, out _) || crvs == null) yield break;
            var loops = Curve.JoinCurves(crvs, tol * 10).Where(c => c.IsClosed && c.IsPlanar()).ToList();
            foreach (var c in loops)
            {
                var amp = AreaMassProperties.Compute(c);
                if (amp == null) continue;
                // 안쪽 구멍(다른 고리 안에 든 고리)은 건너뜀
                bool isHole = loops.Any(o => o != c && o.Contains(amp.Centroid, plane, tol) == PointContainment.Inside
                                                   && AreaMassProperties.Compute(o)?.Area > amp.Area);
                if (isHole) continue;
                var p = amp.Centroid;
                if (c.Contains(p, plane, tol) != PointContainment.Inside) continue;
                // 단면 안의 다른 구멍 고리에서도, 바깥 고리에서도 3.5mm 이상 떨어져야 함
                double clear = loops.Min(o => o.ClosestPoint(p, out double t) ? o.PointAt(t).DistanceTo(p) : 0);
                if (clear < 3.5 * mm) continue;
                yield return p;
            }
        }
    }
}
