using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace IDA.Export3
{
    // 바이너리 FBX: 포함된 텍스처(Video 노드)를 빼고, 텍스처 경로를 '이미지 소스\<파일>'로 바꿉니다.
    // → FBX를 열어도 *.fbm 폴더가 안 생기고, 텍스처는 옆의 '이미지 소스'에서 찾음. (rhino-export 스킬 4단계와 같음)
    static class FbxUnembed
    {
        class Node { public byte[] Name; public List<byte[]> Props = new List<byte[]>(); public List<Node> Kids = new List<Node>(); public bool HasKids; }

        public static int Run(string path, string subdir)
        {
            byte[] b = File.ReadAllBytes(path);
            if (b.Length < 27 || Encoding.ASCII.GetString(b, 0, 18) != "Kaydara FBX Binary") return 0; // ASCII FBX면 그대로
            uint ver = BitConverter.ToUInt32(b, 23);
            bool big = ver >= 7500;
            int hs = big ? 24 : 12, nullrec = hs + 1;

            ulong U(int o) => big ? BitConverter.ToUInt64(b, o) : BitConverter.ToUInt32(b, o);

            int ReadProp(int o, List<byte[]> into)
            {
                char t = (char)b[o]; int s = o; o += 1;
                switch (t)
                {
                    case 'Y': o += 2; break;
                    case 'C': o += 1; break;
                    case 'I': case 'F': o += 4; break;
                    case 'D': case 'L': o += 8; break;
                    case 'f': case 'd': case 'l': case 'i': case 'b': o += 12 + (int)BitConverter.ToUInt32(b, o + 8); break;
                    case 'S': case 'R': o += 4 + (int)BitConverter.ToUInt32(b, o); break;
                    default: throw new Exception("FBX 속성 형식을 읽지 못했어요: " + t);
                }
                into.Add(Slice(b, s, o - s));
                return o;
            }

            Node Parse(ref int o)
            {
                ulong end = U(o), np = U(o + (big ? 8 : 4));
                o += hs;
                if (end == 0) { o += 1; return null; }
                int nl = b[o];
                var n = new Node { Name = Slice(b, o + 1, nl) };
                o += 1 + nl;
                for (ulong i = 0; i < np; i++) o = ReadProp(o, n.Props);
                if ((ulong)o < end)
                {
                    while (true)
                    {
                        var k = Parse(ref o);
                        if (k == null) break;
                        n.Kids.Add(k);
                    }
                }
                n.HasKids = n.Kids.Count > 0 || (ulong)o < end;
                o = (int)end;
                return n;
            }

            int pos = 27;
            var top = new List<Node>();
            while (true)
            {
                var n = Parse(ref pos);
                if (n == null) break;
                top.Add(n);
            }
            byte[] footerId = Slice(b, pos, 16);

            byte[] SProp(string s) { var e = Encoding.UTF8.GetBytes(s); var r = new byte[5 + e.Length]; r[0] = (byte)'S'; BitConverter.GetBytes((uint)e.Length).CopyTo(r, 1); e.CopyTo(r, 5); return r; }
            string Str(byte[] p) => Encoding.UTF8.GetString(p, 5, p.Length - 5);
            byte[] Rebase(byte[] p)
            {
                var baseName = Str(p).Replace('/', '\\').Split('\\').Last();
                return baseName.Length > 0 ? SProp(subdir + "\\" + baseName) : p;
            }
            bool Is(byte[] name, string s) => Encoding.ASCII.GetString(name) == s;

            void Fix(Node node, string parent)
            {
                string nm = Encoding.ASCII.GetString(node.Name);
                if ((nm == "FileName" || nm == "Filename" || nm == "RelativeFilename") && (parent == "Video" || parent == "Texture"))
                    node.Props = node.Props.Select(p => p[0] == 'S' ? Rebase(p) : p).ToList();
                if (nm == "P" && parent == "Properties70" && node.Props.Count > 0 && node.Props[0][0] == 'S')
                {
                    var key = Str(node.Props[0]);
                    var last = node.Props[node.Props.Count - 1];
                    if ((key == "Path" || key == "RelPath") && last[0] == 'S') node.Props[node.Props.Count - 1] = Rebase(last);
                }
                foreach (var k in node.Kids) Fix(k, nm);
            }
            foreach (var n in top) Fix(n, "");

            var vids = new HashSet<string>();
            foreach (var n in top.Where(n => Is(n.Name, "Objects")))
            {
                foreach (var k in n.Kids.Where(k => Is(k.Name, "Video") && k.Props.Count > 0)) vids.Add(Convert.ToBase64String(k.Props[0]));
                n.Kids = n.Kids.Where(k => !Is(k.Name, "Video")).ToList();
            }
            if (vids.Count > 0)
            {
                foreach (var n in top)
                {
                    if (Is(n.Name, "Connections"))
                        n.Kids = n.Kids.Where(k => !(k.Props.Count >= 3 && (vids.Contains(Convert.ToBase64String(k.Props[1])) || vids.Contains(Convert.ToBase64String(k.Props[2]))))).ToList();
                    if (Is(n.Name, "Definitions"))
                    {
                        n.Kids = n.Kids.Where(k => !(Is(k.Name, "ObjectType") && k.Props.Count > 0 && k.Props[0][0] == 'S' && Str(k.Props[0]) == "Video")).ToList();
                        foreach (var k in n.Kids.Where(k => Is(k.Name, "Count") && k.Props.Count > 0 && k.Props[0][0] == 'I'))
                        {
                            int c = BitConverter.ToInt32(k.Props[0], 1) - vids.Count;
                            var p = new byte[5]; p[0] = (byte)'I'; BitConverter.GetBytes(c).CopyTo(p, 1); k.Props[0] = p;
                        }
                    }
                }
            }

            var outb = new List<byte>(b.Length);
            outb.AddRange(Slice(b, 0, 27));
            void PutU(int at, ulong v) { var bytes = big ? BitConverter.GetBytes(v) : BitConverter.GetBytes((uint)v); for (int i = 0; i < bytes.Length; i++) outb[at + i] = bytes[i]; }
            void Write(Node node)
            {
                int start = outb.Count;
                outb.AddRange(new byte[hs]);
                outb.Add((byte)node.Name.Length); outb.AddRange(node.Name);
                int p0 = outb.Count;
                foreach (var p in node.Props) outb.AddRange(p);
                int plen = outb.Count - p0;
                foreach (var k in node.Kids) Write(k);
                if (node.HasKids) outb.AddRange(new byte[nullrec]);
                PutU(start, (ulong)outb.Count);
                PutU(start + (big ? 8 : 4), (ulong)node.Props.Count);
                PutU(start + (big ? 16 : 8), (ulong)plen);
            }
            foreach (var n in top) Write(n);
            outb.AddRange(new byte[nullrec]);
            outb.AddRange(footerId);
            outb.AddRange(new byte[4]);
            int pad = ((outb.Count + 15) & ~15) - outb.Count;
            outb.AddRange(new byte[pad == 0 ? 16 : pad]);
            outb.AddRange(BitConverter.GetBytes(ver));
            outb.AddRange(new byte[120]);
            outb.AddRange(new byte[] { 0xf8, 0x5a, 0x8c, 0x6a, 0xde, 0xf5, 0xd9, 0x7e, 0xec, 0xe9, 0x0c, 0xe3, 0x75, 0x8f, 0x29, 0x0b });

            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, outb.ToArray());
            File.Delete(path);
            File.Move(tmp, path);
            return vids.Count;
        }

        static byte[] Slice(byte[] b, int o, int n) { var r = new byte[n]; Buffer.BlockCopy(b, o, r, 0, n); return r; }
    }
}
