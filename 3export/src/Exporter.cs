using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;

namespace IDA.Export3
{
    // rhino-export 스킬과 같은 규칙:
    //  - 라인·치수·텍스트·점·해치 제거
    //  - 1:1 크기, mm 기준, 바닥 Z=0 + 탑뷰 중심 (0,0) (평행이동만)
    //  - 3DS·FBX: Fewer polygons(MeshingParameters 0.0)로 메시화, 같은 레이어·같은 재질 메시 합치기
    //  - STEP: 메시 제외, 서피스·솔리드만
    //  - 3DS에 mm 단위(MASTER_SCALE) 기록, FBX는 포함 텍스처 빼고 '이미지 소스\파일'로 연결
    //  - 결과 폴더엔 '이미지 소스' + 파일 3개만
    public class ExportResult
    {
        public string Folder;
        public List<string> Summary = new List<string>();
    }

    public static class Exporter
    {
        public const string Suffix = "_FOUNDFOUNDED";
        public const string ImageDirName = "이미지 소스";
        static readonly string[] ImageExts = { ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp", ".tga", ".gif", ".exr", ".hdr", ".dds" };

        class Item { public GeometryBase Geo; public ObjectAttributes Attr; }

        static bool IsLineLike(GeometryBase g) =>
            g is Curve || g is AnnotationBase || g is Point || g is PointCloud || g is TextDot || g is Hatch;

        public static ExportResult Run(RhinoDoc src, IList<RhinoObject> objs, string name, string parent, Action<string> log)
        {
            var res = new ExportResult();
            string folder = Path.Combine(parent, name + Suffix);
            string img = Path.Combine(folder, ImageDirName);
            Directory.CreateDirectory(img);
            res.Folder = folder;

            // 1) 선택 객체 모으기 (블록은 풀어서), 라인류 제거
            var items = new List<Item>();
            int lines = 0, skipped = 0;
            foreach (var o in objs) Collect(o, items, ref lines, ref skipped);
            if (items.Count == 0) throw new Exception("내보낼 서피스·솔리드·메시가 없어요 (라인만 선택됐어요).");

            // 2) 원점 정렬 + mm 변환
            var bb = BoundingBox.Empty;
            foreach (var it in items) bb.Union(it.Geo.GetBoundingBox(true));
            double s = RhinoMath.UnitScale(src.ModelUnitSystem, UnitSystem.Millimeters);
            var xf = Transform.Scale(Point3d.Origin, s) * Transform.Translation(-bb.Center.X, -bb.Center.Y, -bb.Min.Z);
            double w = (bb.Max.X - bb.Min.X) * s, d = (bb.Max.Y - bb.Min.Y) * s, h = (bb.Max.Z - bb.Min.Z) * s;
            log(string.Format("크기 {0:0.##} × {1:0.##} × {2:0.##} mm, 라인 {3}개 제거", w, d, h, lines));

            var wopts = new FileWriteOptions { SuppressDialogBoxes = true, SuppressAllInput = true };
            string pFbx = Path.Combine(folder, "(fbx)" + name + Suffix + ".fbx");
            string p3ds = Path.Combine(folder, "(3ds)" + name + Suffix + ".3ds");
            string pStp = Path.Combine(folder, "(stp)" + name + Suffix + ".stp");

            // 3) FBX + 3DS (메시)
            using (var md = RhinoDoc.CreateHeadless(null))
            {
                md.ModelUnitSystem = UnitSystem.Millimeters;
                var map = new TableMap(src, md);
                var mp = new MeshingParameters(0.0);
                var groups = new Dictionary<string, List<(Mesh mesh, ObjectAttributes attr)>>();
                int pieces = 0;
                foreach (var it in items)
                {
                    Mesh m = ToMesh(it.Geo, mp);
                    if (m == null || m.Faces.Count == 0) { skipped++; continue; }
                    m.Transform(xf);
                    var key = LookKey(src, it.Attr);
                    if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<(Mesh, ObjectAttributes)>();
                    list.Add((m, it.Attr));
                    pieces++;
                }
                foreach (var g in groups.Values)
                {
                    var merged = new Mesh();
                    foreach (var (mesh, _) in g) merged.Append(mesh);
                    merged.Normals.ComputeNormals();
                    merged.Compact();
                    md.Objects.AddMesh(merged, map.Attr(g[0].attr));
                }
                log(string.Format("메시 {0}개 → {1}개로 합침", pieces, groups.Count));
                foreach (var f in map.TextureFiles())
                {
                    try { File.Copy(f, Path.Combine(img, Path.GetFileName(f)), true); } catch { /* 못 읽는 텍스처는 건너뜀 */ }
                }

                Export(md, pFbx, wopts, "FBX");
                Export(md, p3ds, wopts, "3DS");
            }
            Set3dsMillimeters(p3ds);
            var fx = FbxUnembed.Run(pFbx, ImageDirName);
            if (fx > 0) log("FBX 포함 텍스처 " + fx + "개 분리 → 이미지 소스");

            // 4) STEP (메시 제외)
            int droppedMeshes = 0, stepCount = 0;
            using (var sd = RhinoDoc.CreateHeadless(null))
            {
                sd.ModelUnitSystem = UnitSystem.Millimeters;
                var map = new TableMap(src, sd);
                foreach (var it in items)
                {
                    if (it.Geo is Mesh) { droppedMeshes++; continue; }
                    var g = it.Geo.Duplicate();
                    if (g is Surface srf) g = srf.ToBrep();
                    g.Transform(xf);
                    if (sd.Objects.Add(g, map.Attr(it.Attr)) != Guid.Empty) stepCount++;
                }
                if (stepCount > 0) Export(sd, pStp, wopts, "STEP");
                else log("STEP: 서피스·솔리드가 없어 건너뜀 (메시만 있음)");
            }

            // 5) 정리: 떨군 이미지 → 이미지 소스, 백업·.fbm 정리
            Cleanup(folder, img, name);

            res.Summary.Add(string.Format("크기 {0:0.##} × {1:0.##} × {2:0.##} mm (바닥 Z=0, 탑뷰 중앙)", w, d, h));
            foreach (var p in new[] { pStp, p3ds, pFbx })
                if (File.Exists(p)) res.Summary.Add(string.Format("{0}  {1:0.0} MB", Path.GetFileName(p), new FileInfo(p).Length / 1048576.0));
            if (lines > 0) res.Summary.Add("라인·치수·텍스트 " + lines + "개 제외");
            if (droppedMeshes > 0) res.Summary.Add("메시로 된 부품 " + droppedMeshes + "개는 STEP에 없고 3DS·FBX에만 들어갔어요");
            if (skipped > 0) res.Summary.Add("내보낼 수 없는 객체 " + skipped + "개 건너뜀");
            return res;
        }

        static void Collect(RhinoObject o, List<Item> items, ref int lines, ref int skipped)
        {
            if (o is InstanceObject io)
            {
                io.Explode(true, out RhinoObject[] parts, out ObjectAttributes[] attrs, out Transform[] xforms);
                for (int i = 0; i < parts.Length; i++)
                {
                    var g = parts[i].Geometry?.Duplicate();
                    if (g == null) { skipped++; continue; }
                    if (IsLineLike(g)) { lines++; continue; }
                    g.Transform(xforms[i]);
                    Add(g, attrs[i] ?? parts[i].Attributes, items, ref skipped);
                }
                return;
            }
            var geo = o.Geometry;
            if (geo == null) { skipped++; return; }
            if (IsLineLike(geo)) { lines++; return; }
            Add(geo.Duplicate(), o.Attributes, items, ref skipped);
        }

        static void Add(GeometryBase g, ObjectAttributes a, List<Item> items, ref int skipped)
        {
            if (g is Brep || g is Extrusion || g is Surface || g is SubD || g is Mesh) items.Add(new Item { Geo = g, Attr = a });
            else skipped++;
        }

        static Mesh ToMesh(GeometryBase g, MeshingParameters mp)
        {
            if (g is Mesh mesh) return mesh.DuplicateMesh();
            if (g is SubD sd) return Mesh.CreateFromSubD(sd, 2);
            Brep b = g as Brep ?? (g as Extrusion)?.ToBrep() ?? (g as Surface)?.ToBrep();
            if (b == null) return null;
            var parts = Mesh.CreateFromBrep(b, mp);
            if (parts == null || parts.Length == 0) return null;
            var m = new Mesh();
            foreach (var p in parts) m.Append(p);
            return m;
        }

        // 같은 레이어 + 같아 보이는 재질(이름/색/투명도/텍스처) → 같은 묶음
        static string LookKey(RhinoDoc doc, ObjectAttributes a)
        {
            string mk = "layer";
            if (a.MaterialSource == ObjectMaterialSource.MaterialFromObject && a.MaterialIndex >= 0 && a.MaterialIndex < doc.Materials.Count)
            {
                var m = doc.Materials[a.MaterialIndex];
                var t = m.GetBitmapTexture();
                mk = m.Name + "|" + m.DiffuseColor.ToArgb() + "|" + m.Transparency + "|" + (t?.FileReference?.FullPath ?? t?.FileName ?? "");
            }
            return a.LayerIndex + "|" + (int)a.MaterialSource + "|" + mk + "|" + (int)a.ColorSource + "|" + a.ObjectColor.ToArgb();
        }

        static void Export(RhinoDoc d, string path, FileWriteOptions o, string label)
        {
            if (File.Exists(path)) File.Delete(path); // 덮어쓰기 백업(*bak)이 안 생기게
            if (!d.Export(path, o.OptionsDictionary) || !File.Exists(path))
                throw new Exception(label + " 내보내기에 실패했어요: " + path);
        }

        // 3DS MASTER_SCALE(0x0100) = 단위당 인치. 1/25.4 → 1단위 = 1mm
        static void Set3dsMillimeters(string path)
        {
            var b = File.ReadAllBytes(path);
            if (b.Length < 6 || BitConverter.ToUInt16(b, 0) != 0x4D4D) return;
            uint ln = BitConverter.ToUInt32(b, 2);
            int o = 6, pos = -1;
            while (o + 6 <= b.Length && o < ln)
            {
                ushort c = BitConverter.ToUInt16(b, o);
                uint l = BitConverter.ToUInt32(b, o + 2);
                if (c == 0x3D3D) { pos = o; break; }
                if (l < 6) return;
                o += (int)l;
            }
            if (pos < 0) return;
            uint lm = BitConverter.ToUInt32(b, pos + 2);
            int ins = pos + 6;
            if (BitConverter.ToUInt16(b, ins) == 0x3D3E) ins += (int)BitConverter.ToUInt32(b, ins + 2);
            if (BitConverter.ToUInt16(b, ins) == 0x0100) return; // 이미 있음
            var chunk = new byte[10];
            BitConverter.GetBytes((ushort)0x0100).CopyTo(chunk, 0);
            BitConverter.GetBytes(10u).CopyTo(chunk, 2);
            BitConverter.GetBytes((float)(1.0 / 25.4)).CopyTo(chunk, 6);
            var outb = new byte[b.Length + 10];
            Buffer.BlockCopy(b, 0, outb, 0, ins);
            Buffer.BlockCopy(chunk, 0, outb, ins, 10);
            Buffer.BlockCopy(b, ins, outb, ins + 10, b.Length - ins);
            BitConverter.GetBytes(ln + 10).CopyTo(outb, 2);
            BitConverter.GetBytes(lm + 10).CopyTo(outb, pos + 2);
            File.WriteAllBytes(path, outb);
        }

        static void Cleanup(string folder, string img, string name)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "FOUNDFOUNDED_export", name);
            foreach (var f in Directory.GetFiles(folder))
            {
                string n = Path.GetFileName(f), lower = n.ToLowerInvariant();
                if (ImageExts.Any(e => lower.EndsWith(e))) MoveOver(f, Path.Combine(img, n));
                else if (lower.EndsWith("bak")) { Directory.CreateDirectory(tmp); MoveOver(f, Path.Combine(tmp, n)); }
            }
            foreach (var dir in Directory.GetDirectories(folder))
            {
                if (!dir.ToLowerInvariant().EndsWith(".fbm")) continue;
                foreach (var f in Directory.GetFiles(dir))
                    if (!File.Exists(Path.Combine(img, Path.GetFileName(f)))) File.Move(f, Path.Combine(img, Path.GetFileName(f)));
                try { Directory.Delete(dir, true); } catch { /* 열려 있으면 남김 */ }
            }
        }

        static void MoveOver(string from, string to)
        {
            if (File.Exists(to)) File.Delete(to);
            File.Move(from, to);
        }
    }

    // 원본 문서의 레이어·재질을 새 문서로 옮겨 적기
    class TableMap
    {
        readonly RhinoDoc src, dst;
        readonly Dictionary<int, int> layers = new Dictionary<int, int>();
        readonly Dictionary<int, int> mats = new Dictionary<int, int>();
        readonly HashSet<string> textures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public TableMap(RhinoDoc src, RhinoDoc dst) { this.src = src; this.dst = dst; }

        public IEnumerable<string> TextureFiles() => textures.Where(File.Exists);

        public ObjectAttributes Attr(ObjectAttributes a)
        {
            var n = a.Duplicate();
            n.LayerIndex = Layer(a.LayerIndex);
            n.MaterialIndex = a.MaterialIndex >= 0 ? Mat(a.MaterialIndex) : -1;
            return n;
        }

        int Layer(int i)
        {
            if (i < 0 || i >= src.Layers.Count) return dst.Layers.CurrentLayerIndex;
            if (layers.TryGetValue(i, out var j)) return j;
            var l = src.Layers[i];
            string nm = (l.FullPath ?? l.Name ?? "Layer").Replace("::", "_");
            int k = dst.Layers.FindByFullPath(nm, -1);
            if (k < 0)
            {
                var nl = new Layer { Name = nm, Color = l.Color };
                if (l.RenderMaterialIndex >= 0) nl.RenderMaterialIndex = Mat(l.RenderMaterialIndex);
                k = dst.Layers.Add(nl);
                if (k < 0) k = dst.Layers.CurrentLayerIndex;
            }
            return layers[i] = k;
        }

        int Mat(int i)
        {
            if (i < 0 || i >= src.Materials.Count) return -1;
            if (mats.TryGetValue(i, out var j)) return j;
            var m = new Material();
            m.CopyFrom(src.Materials[i]);
            foreach (var t in src.Materials[i].GetTextures() ?? new Texture[0])
            {
                var fp = t?.FileReference?.FullPath;
                if (string.IsNullOrEmpty(fp)) fp = t?.FileName;
                if (!string.IsNullOrEmpty(fp)) textures.Add(fp);
            }
            return mats[i] = dst.Materials.Add(m);
        }
    }
}
