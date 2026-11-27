using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace IDA.DrawPrint
{
    // rhino-ai-print 스킬의 rhino_to_ai.py(collect)를 그대로 옮긴 것.
    // 객체 → 보는 방향 결정 → 커브/면/솔리드 외곽/Make2D → AI 레이어 배정 → 일러스트 빌더용 데이터
    class Collector
    {
        const double MM2PT = 72.0 / 25.4;
        static readonly Regex CmykRe = new Regex(@"C\s*(\d{1,3})\s*M\s*(\d{1,3})\s*Y\s*(\d{1,3})\s*K\s*(\d{1,3})", RegexOptions.IgnoreCase);

        readonly RhinoDoc doc;
        readonly Dictionary<string, object> cfg;
        readonly double tol, angTol, k;
        public readonly List<string> Warn = new List<string>();
        double[] bbox;

        public Collector(RhinoDoc doc, Dictionary<string, object> cfg)
        {
            this.doc = doc; this.cfg = cfg;
            tol = doc.ModelAbsoluteTolerance;
            angTol = doc.ModelAngleToleranceRadians;
            k = RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Millimeters) * MM2PT;
        }

        // ------------------------------------------------------------ 분류
        List<Dictionary<string, object>> AiLayers => MiniJson.Arr(cfg["ai_layers"]).Select(MiniJson.Obj).ToList();

        Dictionary<string, object> Classify(string layerPath)
        {
            var segs = layerPath.Split(new[] { "::" }, StringSplitOptions.None);
            string lowLast = segs[segs.Length - 1].ToLowerInvariant(), lowAll = layerPath.ToLowerInvariant();
            var pri = new Dictionary<string, int> { { "배경색", 0 }, { "가이드", 1 }, { "도면", 2 }, { "인쇄", 3 } };
            var order = AiLayers.OrderBy(d => pri.TryGetValue(MiniJson.S(d, "name"), out var p) ? p : 9).ToList();
            foreach (var text in new[] { lowLast, lowAll })
                foreach (var d in order)
                    foreach (var kw in MiniJson.Arr(d["keywords"]).OfType<string>())
                        if (text.Contains(kw.ToLowerInvariant())) return d;
            return null;
        }

        Dictionary<string, object> LayerDef(string name) => AiLayers.FirstOrDefault(d => MiniJson.S(d, "name") == name);

        static double[] ExplicitCmyk(params string[] names)
        {
            foreach (var n in names)
            {
                if (string.IsNullOrEmpty(n)) continue;
                var m = CmykRe.Match(n);
                if (m.Success) return Enumerable.Range(1, 4).Select(i => (double)Math.Min(100, int.Parse(m.Groups[i].Value))).ToArray();
            }
            return null;
        }

        int[] DrawColor(RhinoObject obj)
        {
            var a = obj.Attributes;
            var c = a.ColorSource == ObjectColorSource.ColorFromObject ? a.ObjectColor : doc.Layers[a.LayerIndex].Color;
            return new[] { (int)c.R, (int)c.G, (int)c.B };
        }

        double? PlotWeightPt(RhinoObject obj)
        {
            var a = obj.Attributes;
            double w = a.PlotWeightSource == ObjectPlotWeightSource.PlotWeightFromObject ? a.PlotWeight : doc.Layers[a.LayerIndex].PlotWeight;
            return w > 0 ? w * MM2PT : (double?)null;
        }

        // ------------------------------------------------------------ 지오메트리
        static Curve Flat(Curve crv)
        {
            var bb = crv.GetBoundingBox(true);
            if (Math.Abs(bb.Max.Z) > 1e-6 || Math.Abs(bb.Min.Z) > 1e-6)
            {
                var p = Curve.ProjectToPlane(crv, Plane.WorldXY);
                if (p != null) return p;
            }
            return crv;
        }

        static bool Colinear(Point3d a, Point3d b, Point3d c, Point3d d, double t)
        {
            var ln = new Line(a, d);
            if (ln.Length < t) return true;
            return ln.DistanceTo(b, false) < t && ln.DistanceTo(c, false) < t;
        }

        double R(double v) => Math.Round(v * k, 4);

        Dictionary<string, object> ToPath(Curve crv)
        {
            bool closed = crv.IsClosed;
            var segs = new List<Point3d[]>();
            if (crv.TryGetPolyline(out Polyline pl))
            {
                for (int i = 0; i < pl.Count - 1; i++) segs.Add(new[] { pl[i], pl[i], pl[i + 1], pl[i + 1] });
            }
            else
            {
                var bz = BezierCurve.CreateCubicBeziers(crv, tol, angTol);
                if (bz == null || bz.Length == 0) return null;
                foreach (var b in bz)
                {
                    var p = Enumerable.Range(0, 4).Select(i => b.GetControlVertex3d(i)).ToArray();
                    if (Colinear(p[0], p[1], p[2], p[3], tol * 0.5)) { p[1] = p[0]; p[2] = p[3]; }
                    segs.Add(p);
                }
            }
            if (segs.Count == 0) return null;
            var pts = new List<object>();
            for (int i = 0; i < segs.Count; i++)
            {
                var s = segs[i];
                Point3d a = s[0], right = s[1];
                Point3d left = i == 0 ? (closed ? segs[segs.Count - 1][2] : a) : segs[i - 1][2];
                pts.Add(new List<object> { R(a.X), R(a.Y), R(left.X), R(left.Y), R(right.X), R(right.Y) });
            }
            if (!closed)
            {
                var e = segs[segs.Count - 1];
                pts.Add(new List<object> { R(e[3].X), R(e[3].Y), R(e[2].X), R(e[2].Y), R(e[3].X), R(e[3].Y) });
            }
            return new Dictionary<string, object> { { "pts", pts }, { "closed", closed } };
        }

        static double Area(Curve crv)
        {
            try { var mp = AreaMassProperties.Compute(crv); return mp?.Area ?? 0; } catch { return 0; }
        }

        static Curve Orient(Curve crv, bool ccw)
        {
            var c = crv.DuplicateCurve();
            var o = c.ClosedCurveOrientation(Plane.WorldXY);
            if ((ccw && o == CurveOrientation.Clockwise) || (!ccw && o == CurveOrientation.CounterClockwise)) c.Reverse();
            return c;
        }

        // 닫힌 커브들 → [[바깥, 구멍, 구멍…], …] (짝수 깊이 = 면, 홀수 = 구멍)
        List<List<Curve>> NestRegions(List<Curve> curves)
        {
            var items = curves.Select(c => (c: c, a: Area(c), b: c.GetBoundingBox(true))).OrderBy(t => -t.a).ToList();
            int n = items.Count;
            var parent = new int[n]; var depth = new int[n];
            for (int i = 0; i < n; i++)
            {
                var (ci, ai, bi) = items[i];
                var pt = ci.PointAtStart;
                int best = -1;
                for (int j = i - 1; j >= 0; j--)
                {
                    var (cj, aj, bj) = items[j];
                    if (aj <= ai) continue;
                    if (!(bj.Min.X - tol <= bi.Min.X && bi.Max.X <= bj.Max.X + tol && bj.Min.Y - tol <= bi.Min.Y && bi.Max.Y <= bj.Max.Y + tol)) continue;
                    if (cj.Contains(pt, Plane.WorldXY, tol) == PointContainment.Inside) { best = j; break; }
                }
                parent[i] = best;
                depth[i] = best >= 0 ? depth[best] + 1 : 0;
            }
            var regions = new SortedDictionary<int, List<Curve>>();
            for (int i = 0; i < n; i++) if (depth[i] % 2 == 0) regions[i] = new List<Curve> { Orient(items[i].c, true) };
            for (int i = 0; i < n; i++) if (depth[i] % 2 == 1 && regions.ContainsKey(parent[i])) regions[parent[i]].Add(Orient(items[i].c, false));
            return regions.Values.ToList();
        }

        // ------------------------------------------------------------ 보는 방향
        static readonly (string name, Vector3d x, Vector3d y)[] Views =
        {
            ("top", new Vector3d(1, 0, 0), new Vector3d(0, 1, 0)),
            ("front", new Vector3d(1, 0, 0), new Vector3d(0, 0, 1)),
            ("right", new Vector3d(0, 1, 0), new Vector3d(0, 0, 1)),
            ("bottom", new Vector3d(1, 0, 0), new Vector3d(0, -1, 0)),
            ("back", new Vector3d(-1, 0, 0), new Vector3d(0, 0, 1)),
            ("left", new Vector3d(0, -1, 0), new Vector3d(0, 0, 1)),
        };

        static Brep AsBrep(GeometryBase g)
        {
            switch (g)
            {
                case Extrusion ex: return ex.ToBrep();
                case Brep b: return b;
                case SubD sd: return sd.ToBrep(SubDToBrepOptions.Default);
                default: return null;
            }
        }

        static Vector3d FaceNormal(BrepFace fc)
        {
            var n = fc.NormalAt(fc.Domain(0).Mid, fc.Domain(1).Mid); // 면 방향(반전) 반영 = 바깥쪽
            n.Unitize();
            return n;
        }

        // [(면, 바깥 법선, 면적)] 평면 면만, 면적 큰 순
        List<(BrepFace f, Vector3d n, double a)> PlanarFaces(Brep brep)
        {
            var o = new List<(BrepFace, Vector3d, double)>();
            foreach (var fc in brep.Faces)
            {
                if (!fc.IsPlanar(tol)) continue;
                var mp = AreaMassProperties.Compute(fc.DuplicateFace(false));
                o.Add((fc, FaceNormal(fc), mp?.Area ?? 0));
            }
            return o.OrderBy(t => -t.Item3).ToList();
        }

        // (법선, 가중치, 방향확정) — 닫힌 평면 커브/해치 = 면적, 솔리드 = 가장 큰 평면 면 면적
        List<(Vector3d n, double w, bool signed)> Votes(GeometryBase g)
        {
            var r = new List<(Vector3d, double, bool)>();
            if (g is Curve c)
            {
                if (c.IsClosed && c.TryGetPlane(out Plane pl, tol)) r.Add((pl.Normal, Area(c), false));
                return r;
            }
            if (g is Hatch h)
            {
                double a = 0;
                foreach (var oc in h.Get3dCurves(true) ?? new Curve[0]) a += Area(oc);
                r.Add((h.Plane.Normal, a > 0 ? a : 1.0, false));
                return r;
            }
            var b = AsBrep(g);
            if (b != null)
            {
                var pf = PlanarFaces(b);
                if (pf.Count > 0) r.Add((pf[0].n, pf[0].a, true));
            }
            return r;
        }

        // 보는 사람 쪽 법선 n → 위쪽이 월드 Z인 보기 평면
        static Plane PlaneFromNormal(Vector3d n)
        {
            n.Unitize();
            var up = Math.Abs(n.Z) < 0.99 ? Vector3d.ZAxis : (n.Z > 0 ? Vector3d.YAxis : -Vector3d.YAxis);
            up = up - n * (up * n);
            up.Unitize();
            var x = Vector3d.CrossProduct(up, n);
            x.Unitize();
            return new Plane(Point3d.Origin, x, up);
        }

        (string name, Plane plane) PickView(List<RhinoObject> objs, string view)
        {
            if (string.IsNullOrEmpty(view) || view == "auto")
            {
                var clusters = new List<(Vector3d n, double w, bool s)>();
                foreach (var o in objs)
                    foreach (var (n0, w, signed) in Votes(o.Geometry))
                    {
                        var n = n0;
                        if (!n.Unitize()) continue;
                        int hit = -1;
                        for (int i = 0; i < clusters.Count; i++)
                            if (Math.Abs(clusters[i].n * n) > 0.995) { hit = i; break; }
                        if (hit >= 0)
                        {
                            var c = clusters[hit];
                            c.w += w;
                            if (signed && !c.s) { c.n = n; c.s = true; }
                            clusters[hit] = c;
                        }
                        else clusters.Add((n, w, signed));
                    }
                if (clusters.Count > 0)
                {
                    var best = clusters.OrderByDescending(c => c.w).First();
                    var n = best.n;
                    foreach (var v in Views)
                    {
                        var vn = Vector3d.CrossProduct(v.x, v.y);
                        bool ok = best.s ? vn * n > 0.995 : (Math.Abs(vn * n) > 0.995 && (v.name == "top" || v.name == "front" || v.name == "right"));
                        if (ok) return (v.name, new Plane(Point3d.Origin, v.x, v.y));
                    }
                    if (!best.s && n * new Vector3d(0, -1, 1) < 0) n = -n; // 방향 모를 때는 앞/위를 보는 쪽으로
                    return ($"face({n.X:0.00},{n.Y:0.00},{n.Z:0.00})", PlaneFromNormal(n));
                }
                var vp = doc.Views.ActiveView?.ActiveViewport;
                if (vp != null && vp.IsParallelProjection && vp.GetCameraFrame(out Plane cp))
                    return ("active", new Plane(Point3d.Origin, cp.XAxis, cp.YAxis));
                view = "top";
            }
            var vv = Views.First(t => t.name == view);
            return (vv.name, new Plane(Point3d.Origin, vv.x, vv.y));
        }

        static T ToXY<T>(T geom, Plane vplane) where T : GeometryBase
        {
            var g = (T)geom.Duplicate();
            g.Transform(Transform.ChangeBasis(Plane.WorldXY, vplane));
            return g;
        }

        double lastDepth;

        // 보는 방향을 향한 평면 면 중 가장 앞쪽 면의 경계(+매끄럽게 이어진 필렛). 없으면 null
        List<Curve> FrontFaceLoops(Brep brep, Plane vplane)
        {
            var n = vplane.ZAxis;
            var pf = PlanarFaces(brep);
            if (pf.Count == 0) return null;
            var cand = pf.Where(t => t.n * n > 0.999 && t.a >= 0.3 * pf[0].a).ToList();
            if (cand.Count == 0) return null;
            double Depth((BrepFace f, Vector3d n, double a) t) => new Vector3d(AreaMassProperties.Compute(t.f.DuplicateFace(false)).Centroid) * n;
            var best = cand.OrderByDescending(t => Math.Round(Depth(t), 3)).ThenByDescending(t => t.a).First();
            var fc = best.f;
            lastDepth = Depth(best);
            var S = new HashSet<int> { fc.FaceIndex };
            var stack = new Stack<int>(); stack.Push(fc.FaceIndex);
            while (stack.Count > 0)
            {
                int fi = stack.Pop();
                foreach (int ei in brep.Faces[fi].AdjacentEdges())
                {
                    var e = brep.Edges[ei];
                    if (!e.IsSmoothManifoldEdge(angTol * 2)) continue;
                    foreach (int fj in e.AdjacentFaces())
                    {
                        if (S.Contains(fj)) continue;
                        var f2 = brep.Faces[fj];
                        if (f2.IsPlanar(tol)) continue;
                        if (FaceNormal(f2) * n < -0.05) continue; // 뒤로 넘어가는 면 제외
                        S.Add(fj); stack.Push(fj);
                    }
                }
            }
            var outer = new List<Curve>();
            foreach (var e in brep.Edges)
                if (e.AdjacentFaces().Count(f => S.Contains(f)) == 1) outer.Add(e.DuplicateCurve());
            var loops = new List<Curve>();
            if (S.Count > 1 && outer.Count > 0)
            {
                var joined = (Curve.JoinCurves(outer, tol * 2) ?? new Curve[0]).Select(c => Flat(ToXY(c, vplane))).Where(c => c.IsClosed).OrderBy(c => -Area(c)).ToList();
                if (joined.Count > 0)
                {
                    loops.Add(joined[0]);
                    foreach (var lp in fc.Loops)
                        if (lp.LoopType == BrepLoopType.Inner) loops.Add(ToXY(lp.To3dCurve(), vplane));
                }
            }
            if (loops.Count == 0) loops = fc.Loops.Select(lp => ToXY(lp.To3dCurve(), vplane)).ToList();
            return loops.Where(c => c != null).ToList();
        }

        // kind: curve | region | outline | text | solid | skip. 커브는 보기 평면 기준 XY로 변환됨
        (string kind, List<GeometryBase> geo) ObjectCurves(RhinoObject obj, Plane vplane)
        {
            var g = obj.Geometry;
            switch (g)
            {
                case Curve c: return ("curve", new List<GeometryBase> { ToXY(c, vplane) });
                case Hatch h:
                    {
                        var all = (h.Get3dCurves(true) ?? new Curve[0]).Concat(h.Get3dCurves(false) ?? new Curve[0]);
                        return ("region", all.Select(c => (GeometryBase)ToXY(c, vplane)).ToList());
                    }
                case TextEntity _:
                case TextDot _:
                    return ("text", new List<GeometryBase>());
                case Mesh m: return ("solid", new List<GeometryBase> { m });
            }
            var b = AsBrep(g);
            if (b != null)
            {
                var n = vplane.ZAxis;
                bool flatFaces = !b.IsSolid && b.Faces.All(f => f.IsPlanar(tol));
                if (flatFaces)
                {
                    bool okAll = b.Faces.All(f => f.TryGetPlane(out Plane pl, tol) && Math.Abs(pl.Normal * n) >= 0.99);
                    if (okAll)
                    {
                        var edges = b.DuplicateNakedEdgeCurves(true, true);
                        var joined = edges != null && edges.Length > 0 ? Curve.JoinCurves(edges, tol) : new Curve[0];
                        return ("region", joined.Where(c => c.IsClosed).Select(c => (GeometryBase)ToXY(c, vplane)).ToList());
                    }
                }
                var loops = FrontFaceLoops(b, vplane);
                if (loops != null && loops.Count > 0) return ("outline", loops.Cast<GeometryBase>().ToList()); // 인쇄면 경계 = 실측
                return ("solid", new List<GeometryBase> { b });
            }
            return ("skip", null);
        }

        // [(tag, geometry)] → {tag: XY 커브들}  RhinoCommon HiddenLineDrawing = Make2D (단일 스레드: MCP 스레드에서 Rhino 종료 방지)
        Dictionary<int, List<Curve>> Make2D(List<(int tag, GeometryBase g)> solids, Plane vplane)
        {
            var outd = new Dictionary<int, List<Curve>>();
            if (solids.Count == 0) return outd;
            var geoms = solids.Select(s => (s.tag, g: ToXY(s.g, vplane))).ToList();
            var bb = BoundingBox.Empty;
            foreach (var t in geoms) bb.Union(t.g.GetBoundingBox(true));
            var vp = new ViewportInfo();
            vp.ChangeToParallelProjection(true);
            var c = bb.Center;
            vp.SetCameraLocation(new Point3d(c.X, c.Y, bb.Max.Z + bb.Diagonal.Length + 10));
            vp.SetCameraDirection(-Vector3d.ZAxis);
            vp.SetCameraUp(Vector3d.YAxis);
            vp.Extents(0.5, bb);
            var prm = new HiddenLineDrawingParameters
            {
                AbsoluteTolerance = tol,
                IncludeTangentEdges = false,
                IncludeHiddenCurves = false,
                Flatten = true,
            };
            prm.SetViewport(vp);
            foreach (var t in geoms) prm.AddGeometry(t.g, Transform.Identity, t.tag);
            var hld = HiddenLineDrawing.Compute(prm, false);
            if (hld == null) return outd;
            var raw = new Dictionary<int, List<Curve>>();
            foreach (var seg in hld.Segments)
            {
                if (seg.SegmentVisibility != HiddenLineDrawingSegment.Visibility.Visible) continue;
                var crv = seg.CurveGeometry;
                if (crv == null) continue;
                object tag;
                try { tag = seg.ParentCurve.SourceObject.Tag; } catch { continue; }
                if (!(tag is int ti)) continue;
                if (!raw.TryGetValue(ti, out var l)) raw[ti] = l = new List<Curve>();
                l.Add(crv.DuplicateCurve());
            }
            // HLD 결과 좌표를 원래 XY 위치로 (bbox 최소점 정렬) + Z 제거
            var obb = BoundingBox.Empty;
            foreach (var cs in raw.Values) foreach (var cc in cs) obb.Union(cc.GetBoundingBox(true));
            var move = Transform.Translation(new Vector3d(bb.Min.X - obb.Min.X, bb.Min.Y - obb.Min.Y, 0));
            var proj = Transform.PlanarProjection(Plane.WorldXY);
            double minLen = RhinoMath.UnitScale(UnitSystem.Millimeters, doc.ModelUnitSystem) * 1.0;
            foreach (var kv in raw)
            {
                foreach (var cc in kv.Value) { cc.Transform(proj); cc.Transform(move); }
                var res = new List<Curve>();
                foreach (var j in Curve.JoinCurves(kv.Value, tol * 2) ?? new Curve[0])
                {
                    if (!j.IsClosed && j.GetLength() < minLen) continue; // 1mm 미만 조각 제거
                    res.Add(j.Simplify(CurveSimplifyOptions.All, tol, angTol) ?? j);
                }
                outd[kv.Key] = res;
            }
            return outd;
        }

        // ------------------------------------------------------------ 수집
        class Bucket
        {
            public string Layer, Src, Mode; public int[] Rgb; public double[] Cmyk; public double? Width;
            public List<Curve> Curves = new List<Curve>();
        }
        class Meta { public string Src; public int[] Rgb; public double[] Cmyk; public double? Width; }

        readonly Dictionary<string, Bucket> buckets = new Dictionary<string, Bucket>();
        readonly List<string> bucketOrder = new List<string>();

        void Add(string ai, string mode, Curve c, Meta m)
        {
            string key = ai + "\u0001" + m.Src + "\u0001" + mode + "\u0001" + string.Join(",", (m.Cmyk ?? m.Rgb.Select(x => (double)x).ToArray()).Select(x => x.ToString())) + "\u0001" + (mode == "stroke" ? m.Width?.ToString() : "");
            if (!buckets.TryGetValue(key, out var b))
            {
                buckets[key] = b = new Bucket { Layer = ai, Src = m.Src, Mode = mode, Rgb = m.Rgb, Cmyk = m.Cmyk, Width = m.Width };
                bucketOrder.Add(key);
            }
            b.Curves.Add(c);
        }

        void Grow(double x, double y)
        {
            bbox = bbox == null ? new[] { x, y, x, y } : new[] { Math.Min(bbox[0], x), Math.Min(bbox[1], y), Math.Max(bbox[2], x), Math.Max(bbox[3], y) };
        }

        static object Nums(double[] a) => a?.Select(x => (object)x).ToList();
        static object Ints(int[] a) => a?.Select(x => (object)(double)x).ToList();

        public (List<object> items, List<object> texts, double[] bbox) Run(List<RhinoObject> objs, string view)
        {
            var (vname, vplane) = PickView(objs, string.IsNullOrEmpty(view) ? MiniJson.S(cfg, "view", "auto") : view);
            Warn.Add("보는 방향: " + vname);
            var skip = MiniJson.Arr(cfg.TryGetValue("skip_layers", out var sl) ? sl : null).OfType<string>().ToList();

            var prepared = new List<(int i, RhinoObject obj, string lp, string kind, List<GeometryBase> geo, double depth)>();
            var solids = new List<(int, GeometryBase)>();
            for (int i = 0; i < objs.Count; i++)
            {
                var obj = objs[i];
                string lp = doc.Layers[obj.Attributes.LayerIndex].FullPath;
                if (skip.Any(s => lp.ToLowerInvariant().Contains(s.ToLowerInvariant()))) continue;
                lastDepth = 0;
                var (kind, geo) = ObjectCurves(obj, vplane);
                if (kind == "skip") continue;
                if (kind == "solid") solids.Add((i, geo[0]));
                prepared.Add((i, obj, lp, kind, geo, kind == "outline" ? lastDepth : 0));
            }

            // 가림 처리: 다른 솔리드 인쇄면 외곽 안에 들어가고 그보다 뒤에 있는 면 외곽은 제외
            var hidden = new HashSet<int>();
            var outl = prepared.Where(p => p.kind == "outline").ToList();
            foreach (var p in outl)
            {
                var c0 = Flat((Curve)p.geo[0]);
                foreach (var q in outl)
                {
                    if (q.i == p.i || q.depth <= p.depth + tol) continue;
                    var cq = Flat((Curve)q.geo[0]);
                    var ts = c0.DivideByCount(12, true) ?? new double[0];
                    if (Area(cq) > Area(c0) && ts.All(t => cq.Contains(c0.PointAt(t), Plane.WorldXY, tol) != PointContainment.Outside))
                    {
                        hidden.Add(p.i);
                        Warn.Add("뒤에 가려진 면 제외: " + p.lp);
                        break;
                    }
                }
            }
            prepared = prepared.Where(p => !hidden.Contains(p.i)).ToList();

            if (solids.Count > 0)
            {
                if (MiniJson.B(cfg, "make2d", true))
                {
                    var m2d = Make2D(solids, vplane);
                    Warn.Add($"3D 객체 {solids.Count}개 Make2D 처리");
                    for (int x = 0; x < prepared.Count; x++)
                    {
                        var p = prepared[x];
                        if (p.kind != "solid") continue;
                        p.kind = "curve";
                        p.geo = (m2d.TryGetValue(p.i, out var cs) ? cs : new List<Curve>()).Cast<GeometryBase>().ToList();
                        prepared[x] = p;
                    }
                }
                else Warn.Add($"3D 객체 {solids.Count}개 건너뜀 (make2d=false)");
            }

            var texts = new List<object>();
            var pending = new List<(Curve c, Meta m)>();
            var bg = MiniJson.Nums(cfg.TryGetValue("solid_background", out var bgv) ? bgv : null);
            string unmatched = MiniJson.S(cfg, "unmatched", "auto");

            foreach (var p in prepared)
            {
                if (p.kind == "solid") continue;
                var a = p.obj.Attributes;
                var d = Classify(p.lp);
                var rgb = DrawColor(p.obj);
                var cmyk = ExplicitCmyk(a.Name ?? "", p.lp);
                var width = PlotWeightPt(p.obj);
                var segs = p.lp.Split(new[] { "::" }, StringSplitOptions.None);
                string src = segs[segs.Length - 1];

                if (p.kind == "text")
                {
                    string txt; Point3d pt; double size;
                    if (p.obj.Geometry is TextDot td) { txt = td.Text; pt = td.Point; size = MiniJson.D(cfg, "text_size_pt", 8); }
                    else
                    {
                        var te = (TextEntity)p.obj.Geometry;
                        txt = te.PlainText; pt = te.Plane.Origin;
                        size = Math.Round(te.TextHeight * k / 0.72, 2); // 캡하이트≈0.72em
                    }
                    pt.Transform(Transform.ChangeBasis(Plane.WorldXY, vplane));
                    string ai = MiniJson.S(d ?? LayerDef("가이드"), "name");
                    texts.Add(new Dictionary<string, object> {
                        { "layer", ai }, { "src", src }, { "text", txt }, { "x", Math.Round(pt.X * k, 3) }, { "y", Math.Round(pt.Y * k, 3) },
                        { "size", size }, { "rgb", Ints(rgb) }, { "cmyk", Nums(cmyk) } });
                    continue;
                }

                var crvs = p.geo.OfType<Curve>().Select(Flat).Where(c => c.GetLength() > tol * 20).ToList(); // Make2D 찌꺼기 제거
                foreach (var c in crvs)
                {
                    var meta = new Meta { Src = src, Rgb = rgb, Cmyk = cmyk, Width = width };
                    string aiName, mode;
                    if (d != null)
                    {
                        aiName = MiniJson.S(d, "name"); mode = MiniJson.S(d, "mode");
                        if (mode == "fill" && !c.IsClosed) mode = "stroke";
                    }
                    else if (p.kind == "outline")
                    {
                        aiName = "도면"; mode = "stroke"; // 솔리드 인쇄면 외곽 = 도면
                        if (bg != null && c.IsClosed) Add("배경색", "fill", c, new Meta { Src = src, Rgb = rgb, Cmyk = bg });
                    }
                    else if (unmatched == "keep") { aiName = src; mode = c.IsClosed ? "fill" : "stroke"; }
                    else if (p.kind == "region" || !c.IsClosed)
                    {
                        if (p.kind == "region") { aiName = "인쇄"; mode = "fill"; } else { aiName = "도면"; mode = "stroke"; }
                    }
                    else { pending.Add((c, meta)); continue; }
                    if (p.kind == "region") mode = "fill";
                    Add(aiName, mode, c, meta);
                }
            }

            // 미분류 닫힌 커브: 나머지를 거의 다 감싸는 가장 큰 커브 = 도면 외곽, 나머지 = 인쇄 면
            if (pending.Count > 0)
            {
                pending = pending.OrderBy(t => -Area(t.c)).ToList();
                var big = pending[0].c;
                var others = pending.Skip(1).ToList();
                int inside = others.Count(t => big.Contains(t.c.PointAtStart, Plane.WorldXY, tol) == PointContainment.Inside);
                int start = 0;
                if (others.Count >= 2 && inside >= 0.8 * others.Count)
                {
                    Add("도면", "stroke", big, pending[0].m);
                    start = 1;
                    Warn.Add("외곽선 자동 판별 → 도면");
                }
                foreach (var t in pending.Skip(start)) Add("인쇄", "fill", t.c, t.m);
            }

            var items = new List<object>();
            var keys = bucketOrder.OrderBy(x => buckets[x].Layer, StringComparer.Ordinal).ThenBy(x => buckets[x].Src, StringComparer.Ordinal).ThenBy(x => buckets[x].Mode, StringComparer.Ordinal).ToList();
            foreach (var key in keys)
            {
                var b = buckets[key];
                var shapes = new List<object>();
                if (b.Mode == "fill")
                {
                    foreach (var reg in NestRegions(b.Curves))
                    {
                        var paths = reg.Select(ToPath).Where(x => x != null).ToList();
                        if (paths.Count > 0) shapes.Add(paths.Cast<object>().ToList());
                    }
                }
                else
                {
                    foreach (var c in b.Curves)
                    {
                        var pth = ToPath(c);
                        if (pth != null) shapes.Add(new List<object> { pth });
                    }
                }
                foreach (List<object> s in shapes)
                    foreach (Dictionary<string, object> pth in s)
                        foreach (List<object> q in (List<object>)pth["pts"]) Grow((double)q[0], (double)q[1]);
                if (shapes.Count == 0) continue;
                var dd = LayerDef(b.Layer);
                object cm = b.Cmyk != null ? Nums(b.Cmyk) : (dd != null && dd.TryGetValue("fixed_cmyk", out var fc) ? fc : null);
                double w = b.Width ?? MiniJson.D(dd, "stroke_pt", 0.5);
                items.Add(new Dictionary<string, object> {
                    { "layer", b.Layer }, { "src", b.Src }, { "mode", b.Mode }, { "rgb", Ints(b.Rgb) },
                    { "cmyk", cm }, { "width", w }, { "shapes", shapes } });
            }
            foreach (Dictionary<string, object> t in texts) Grow((double)t["x"], (double)t["y"]);
            return (items, texts, bbox);
        }
    }
}
