using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace NPatch
{
  /// <summary>
  /// Boundary selection shared by NPatch and NPatchFill.
  /// Click edges one by one (click again to drop one), or double-click any edge of the hole to
  /// select the whole loop around it, even across separate surfaces.
  /// </summary>
  static class BoundaryPicker
  {
    [DllImport("user32.dll")] static extern uint GetDoubleClickTime();

    static long DoubleClickMs
    {
      get { try { return Math.Max(200, GetDoubleClickTime()); } catch { return 500; } }
    }

    /// <summary>A naked edge of a document object.</summary>
    class EdgeRef
    {
      public Guid Id;
      public RhinoObject Obj;
      public Brep Brep;
      public int Index;
      public Curve Curve;
      public string Key => Id + ":" + Index;
    }

    public static bool Pick(RhinoDoc doc, string prompt, out List<BoundaryInput> inputs, out Guid sourceId, out Brep sourceBrep, out Result result)
    {
      inputs = new List<BoundaryInput>();
      sourceId = Guid.Empty; sourceBrep = null; result = Result.Success;
      double tol = doc.ModelAbsoluteTolerance;

      var edges = new List<EdgeRef>();
      var curves = new List<(Guid id, Curve c)>();

      // 1. Pre-selected edges/curves, if any.
      var pre = new GetObject();
      pre.GeometryFilter = ObjectType.EdgeFilter | ObjectType.Curve;
      pre.GeometryAttributeFilter = GeometryAttributeFilter.BoundaryEdge | GeometryAttributeFilter.WireCurve;
      pre.SubObjectSelect = true;
      pre.EnablePreSelect(true, true);
      pre.EnablePostSelect(false);
      if (pre.GetMultiple(1, 0) == GetResult.Object)
      {
        for (int i = 0; i < pre.ObjectCount; i++) AddRef(doc, pre.Object(i), edges, curves);
      }
      else
      {
        // 2. Interactive picking with double-click = whole loop.
        var go = new GetObject();
        go.GeometryFilter = ObjectType.EdgeFilter | ObjectType.Curve;
        go.GeometryAttributeFilter = GeometryAttributeFilter.BoundaryEdge | GeometryAttributeFilter.WireCurve;
        go.SubObjectSelect = true;
        go.AlreadySelectedObjectSelect = true;
        go.DeselectAllBeforePostSelect = false;
        go.EnablePreSelect(false, true);
        go.AcceptNothing(true);
        var sw = Stopwatch.StartNew();
        string lastKey = null; long lastT = -100000;
        while (true)
        {
          int n = edges.Count + curves.Count;
          go.SetCommandPrompt(n == 0
            ? prompt + "  (엣지를 더블클릭하면 구멍 둘레 전체가 선택돼요)"
            : $"{n}개 선택됨. 계속 클릭하거나 Enter로 완료 (더블클릭 = 구멍 둘레 전체, 다시 클릭 = 빼기)");
          var r = go.Get();
          if (r == GetResult.Nothing) break;
          if (r != GetResult.Object) { Unhighlight(doc, edges, curves); result = go.CommandResult(); return false; }

          var o = go.Object(0);
          long now = sw.ElapsedMilliseconds;
          var single = new List<EdgeRef>(); var singleC = new List<(Guid, Curve)>();
          AddRef(doc, o, single, singleC);
          string key = single.Count > 0 ? single[0].Key : singleC.Count > 0 ? singleC[0].Item1.ToString() : null;
          bool dbl = key != null && key == lastKey && now - lastT <= DoubleClickMs;

          if (dbl && single.Count > 0)
          {
            var loop = LoopFrom(doc, single[0], tol);
            if (loop != null)
            {
              Unhighlight(doc, edges, curves);
              edges = loop;
              curves.Clear();
              RhinoApp.WriteLine($"구멍 둘레 엣지 {loop.Count}개를 선택했어요.");
            }
            else
            {
              if (!edges.Any(e => e.Key == key)) edges.Add(single[0]);
              RhinoApp.WriteLine("이 엣지에서 닫힌 구멍 둘레를 찾지 못했어요. 엣지를 하나씩 골라 주세요.");
            }
            lastKey = null; // a third click starts over
          }
          else
          {
            // Toggle: clicking an already chosen edge removes it.
            if (single.Count > 0)
            {
              int i = edges.FindIndex(e => e.Key == key);
              if (i >= 0) { SetHighlight(doc, edges[i], false); edges.RemoveAt(i); }
              else edges.Add(single[0]);
            }
            else if (singleC.Count > 0)
            {
              int i = curves.FindIndex(c => c.id == singleC[0].Item1);
              if (i >= 0) { doc.Objects.Select(curves[i].id, false); curves.RemoveAt(i); }
              else curves.Add(singleC[0]);
            }
            lastKey = key; lastT = now;
          }
          foreach (var e in edges) SetHighlight(doc, e, true);
          foreach (var c in curves) doc.Objects.Select(c.id, true);
          doc.Views.Redraw();
        }
      }

      // 3. Build the inputs.
      foreach (var e in edges)
      {
        var be = e.Brep.Edges[e.Index];
        var faces = be.AdjacentFaces();
        inputs.Add(new BoundaryInput
        {
          Curve = be.DuplicateCurve(),
          AdjacentFace = faces.Length > 0 ? e.Brep.Faces[faces[0]] : null,
          SourceObjectId = e.Id
        });
      }
      foreach (var (_, c) in curves)
      {
        // A single closed curve is split at kinks so each side gets its own label.
        var pieces = c.IsClosed && c.DuplicateSegments() is Curve[] segs && segs.Length > 1 ? segs : new[] { c.DuplicateCurve() };
        foreach (var pc in pieces) inputs.Add(new BoundaryInput { Curve = pc });
      }
      var ids = edges.Select(e => e.Id).Distinct().ToList();
      if (curves.Count == 0 && ids.Count == 1) { sourceId = ids[0]; sourceBrep = edges[0].Brep; }

      Unhighlight(doc, edges, curves);
      doc.Objects.UnselectAll();
      doc.Views.Redraw();
      if (inputs.Count == 0) { result = Result.Nothing; return false; }
      return true;
    }

    /// <summary>The hole loop through a given edge, as (object id, edge index) pairs; null if none. For tests/scripts.</summary>
    public static List<(Guid id, int edge)> FindLoop(RhinoDoc doc, Guid objectId, int edgeIndex)
    {
      var obj = doc.Objects.FindId(objectId);
      var b = BrepOf(obj);
      if (b == null || edgeIndex < 0 || edgeIndex >= b.Edges.Count) return null;
      var start = new EdgeRef { Id = objectId, Obj = obj, Brep = b, Index = edgeIndex, Curve = b.Edges[edgeIndex].DuplicateCurve() };
      return LoopFrom(doc, start, doc.ModelAbsoluteTolerance)?.Select(e => (e.Id, e.Index)).ToList();
    }

    static Brep BrepOf(RhinoObject o) =>
      o?.Geometry as Brep ?? (o?.Geometry as Surface)?.ToBrep() ?? (o?.Geometry as Extrusion)?.ToBrep();

    static void AddRef(RhinoDoc doc, ObjRef o, List<EdgeRef> edges, List<(Guid, Curve)> curves)
    {
      var be = o.Edge();
      if (be != null)
      {
        var obj = o.Object();
        edges.Add(new EdgeRef { Id = o.ObjectId, Obj = obj, Brep = be.Brep, Index = be.EdgeIndex, Curve = be.DuplicateCurve() });
      }
      else if (o.Curve() is Curve c) curves.Add((o.ObjectId, c.DuplicateCurve()));
    }

    static void SetHighlight(RhinoDoc doc, EdgeRef e, bool on)
    {
      var obj = e.Obj ?? doc.Objects.FindId(e.Id);
      obj?.SelectSubObject(new ComponentIndex(ComponentIndexType.BrepEdge, e.Index), on, true, true);
    }

    static void Unhighlight(RhinoDoc doc, List<EdgeRef> edges, List<(Guid id, Curve c)> curves)
    {
      foreach (var e in edges) SetHighlight(doc, e, false);
      foreach (var c in curves) doc.Objects.Select(c.id, false);
    }

    /// <summary>
    /// Walks naked edges head-to-tail from <paramref name="start"/> until the loop closes.
    /// Naked edges that coincide with another naked edge are unjoined seams between surfaces,
    /// not part of the hole, so they are skipped.
    /// </summary>
    static List<EdgeRef> LoopFrom(RhinoDoc doc, EdgeRef start, double tol)
    {
      double jt = Math.Max(tol * 10, 1e-6);

      // Candidate naked edges from visible objects near the start edge.
      var startBox = start.Brep.GetBoundingBox(false);
      startBox.Inflate(startBox.Diagonal.Length * 2);
      var cands = new List<EdgeRef>();
      var settings = new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = false, HiddenObjects = false, ObjectTypeFilter = ObjectType.Brep | ObjectType.Surface | ObjectType.Extrusion };
      foreach (var obj in doc.Objects.GetObjectList(settings))
      {
        var b = obj.Id == start.Id ? start.Brep : BrepOf(obj);
        if (b == null || !BoundingBox.Intersection(b.GetBoundingBox(false), startBox).IsValid) continue;
        foreach (var e in b.Edges)
          if (e.Valence == EdgeAdjacency.Naked)
            cands.Add(new EdgeRef { Id = obj.Id, Obj = obj, Brep = b, Index = e.EdgeIndex, Curve = e.DuplicateCurve() });
      }

      // Drop seams: an edge whose midpoint lies on another naked edge (and vice versa).
      bool OnCurve(Curve c, Point3d p) => c.ClosestPoint(p, out double t) && c.PointAt(t).DistanceTo(p) <= jt;
      var seams = new HashSet<string>();
      for (int i = 0; i < cands.Count; i++)
        for (int j = i + 1; j < cands.Count; j++)
        {
          var a = cands[i]; var b = cands[j];
          if (!BoundingBox.Intersection(a.Curve.GetBoundingBox(false), b.Curve.GetBoundingBox(false)).IsValid) continue;
          if (OnCurve(b.Curve, a.Curve.PointAtNormalizedLength(0.5)) || OnCurve(a.Curve, b.Curve.PointAtNormalizedLength(0.5)))
          { seams.Add(a.Key); seams.Add(b.Key); }
        }
      if (seams.Contains(start.Key)) return null; // user double-clicked a seam, not a hole edge
      cands.RemoveAll(c => seams.Contains(c.Key));

      var loop = new List<EdgeRef> { start };
      var used = new HashSet<string> { start.Key };
      Point3d first = start.Curve.PointAtStart, end = start.Curve.PointAtEnd;
      Vector3d dirIn = start.Curve.TangentAtEnd;
      for (int guard = 0; guard < 500; guard++)
      {
        if (end.DistanceTo(first) <= jt && loop.Count > 1) return loop;
        EdgeRef best = null; bool bestFlip = false; double bestScore = double.MaxValue;
        foreach (var c in cands)
        {
          if (used.Contains(c.Key)) continue;
          foreach (bool flip in new[] { false, true })
          {
            var p = flip ? c.Curve.PointAtEnd : c.Curve.PointAtStart;
            if (p.DistanceTo(end) > jt) continue;
            var t = flip ? -c.Curve.TangentAtEnd : c.Curve.TangentAtStart;
            double turn = Vector3d.VectorAngle(dirIn, t); // prefer going straight on at junctions
            if (turn < bestScore) { bestScore = turn; best = c; bestFlip = flip; }
          }
        }
        if (best == null)
        {
          // Closed single edge (e.g. a circular hole).
          if (loop.Count == 1 && start.Curve.IsClosed) return loop;
          return null;
        }
        loop.Add(best); used.Add(best.Key);
        end = bestFlip ? best.Curve.PointAtStart : best.Curve.PointAtEnd;
        dirIn = bestFlip ? -best.Curve.TangentAtStart : best.Curve.TangentAtEnd;
      }
      return null;
    }
  }
}
