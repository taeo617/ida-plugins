using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace IDA.A4Draw
{
    // A4 도면 한 장: 레이어·치수 스타일·도곽·표제란을 만들고, 내보낼 때 쓸 정보를 객체에 적어 둠
    static class Page
    {
        public const string Root = "도면 A4";
        public const string KeyPage = "a4draw.page";     // 같은 도면 장의 객체끼리 같은 값 (Guid)
        public const string KeyFrame = "a4draw.frame";   // 용지 사각형에만: "x,y,w,h" (문서 단위, 왼쪽 아래 기준)
        public const string KeyName = "a4draw.name";     // 용지 사각형에만: 제품명

        // 레이어 이름, 선 두께(mm, 출력 기준), 색, 점선 여부
        public static readonly (string Name, double Weight, Color Color, bool Dashed)[] Layers =
        {
            ("외형선", 0.35, Color.Black, false),
            ("탄젠트선", 0.13, Color.FromArgb(120, 120, 120), false),
            ("숨은선", 0.18, Color.FromArgb(90, 90, 90), true),
            ("치수", 0.13, Color.Black, false),
            ("글자", 0.18, Color.Black, false),
            ("도곽", 0.50, Color.Black, false),
            ("용지", 0.05, Color.FromArgb(190, 190, 190), false),
        };

        public static int Layer(RhinoDoc doc, string name)
        {
            int root = doc.Layers.FindByFullPath(Root, -1);
            if (root < 0)
            {
                var r = new Layer { Name = Root, Color = Color.Black };
                root = doc.Layers.Add(r);
            }
            int idx = doc.Layers.FindByFullPath(Root + "::" + name, -1);
            if (idx >= 0) return idx;
            var spec = Layers.First(l => l.Name == name);
            var l = new Layer
            {
                Name = name,
                ParentLayerId = doc.Layers[root].Id,
                Color = spec.Color,
                PlotColor = spec.Color,
                PlotWeight = spec.Weight,
            };
            if (spec.Dashed)
            {
                int lt = doc.Linetypes.Find("Hidden");
                if (lt < 0) lt = doc.Linetypes.Find("Dashed");
                if (lt >= 0) l.LinetypeIndex = lt;
            }
            return doc.Layers.Add(l);
        }

        // 축척별 치수 스타일: 용지 위 글자 2.5mm, 값은 실제 크기(LengthFactor = 1/축척), "33mm" 식
        public static DimensionStyle DimStyle(RhinoDoc doc, double scale, double u)
        {
            string name = "IDA mm " + ScaleText(scale);
            var ds = doc.DimStyles.FindName(name);
            if (ds != null) return ds;
            var src = doc.DimStyles.FindName("IDA mm") ?? doc.DimStyles.Current;
            var d = src.Duplicate(name, Guid.Empty, Guid.Empty) as DimensionStyle ?? new DimensionStyle { Name = name };
            d.Name = name;
            d.DimensionLengthDisplay = DimensionStyle.LengthDisplay.Millmeters;
            d.LengthResolution = 0;
            d.Suffix = "mm";
            d.Prefix = "";
            d.LengthFactor = 1.0 / scale;
            d.DimensionScale = 1.0;
            d.TextHeight = 2.5 * u;
            d.ArrowLength = 2.0 * u;
            d.LeaderArrowLength = 2.0 * u;
            d.TextGap = 0.8 * u;
            d.ExtensionLineExtension = 1.5 * u;
            d.ExtensionLineOffset = 1.0 * u;
            d.CentermarkSize = 1.5 * u;
            d.ArrowType1 = DimensionStyle.ArrowType.SolidTriangle;
            d.ArrowType2 = DimensionStyle.ArrowType.SolidTriangle;
            d.DimTextLocation = DimensionStyle.TextLocation.AboveDimLine;
            d.DimRadialTextLocation = DimensionStyle.TextLocation.AboveDimLine;
            d.TextOrientation = TextOrientation.InPlane;
            d.Font = new Rhino.DocObjects.Font("Pretendard");
            int i = doc.DimStyles.Add(d, false);
            return i >= 0 ? doc.DimStyles[i] : doc.DimStyles.FindName(name);
        }

        public static string ScaleText(double s)
        {
            if (s >= 1) return $"{Math.Round(s)}:1";
            return $"1:{Math.Round(1 / s)}";
        }

        // 맞는 축척 중 가장 큰 것
        public static readonly double[] Scales = { 10, 5, 2, 1, 0.5, 0.2, 0.1, 0.05, 0.02, 0.01 };
        public static double FitScale(double wMm, double hMm, double availW, double availH)
        {
            foreach (var s in Scales)
                if (wMm * s <= availW && hMm * s <= availH) return s;
            return Scales[Scales.Length - 1];
        }

        public static ObjectAttributes Attrs(RhinoDoc doc, string layer, Guid page)
        {
            var a = doc.CreateDefaultAttributes();
            a.LayerIndex = Layer(doc, layer);
            a.ColorSource = ObjectColorSource.ColorFromLayer;
            a.PlotWeightSource = ObjectPlotWeightSource.PlotWeightFromLayer;
            a.PlotColorSource = ObjectPlotColorSource.PlotColorFromLayer;
            a.LinetypeSource = ObjectLinetypeSource.LinetypeFromLayer;
            a.SetUserString(KeyPage, page.ToString());
            return a;
        }

        // 지금 있는 A4 도면들의 오른쪽 끝 (다음 장을 놓을 X)
        public static double RightmostPage(RhinoDoc doc)
        {
            double x = double.NegativeInfinity;
            foreach (var o in doc.Objects.GetObjectList(ObjectType.Curve))
            {
                var f = o.Attributes.GetUserString(KeyFrame);
                if (string.IsNullOrEmpty(f)) continue;
                var bb = o.Geometry.GetBoundingBox(true);
                x = Math.Max(x, bb.Max.X);
            }
            return x;
        }

        public static List<RhinoObject> ObjectsOf(RhinoDoc doc, string page)
        {
            var list = new List<RhinoObject>();
            var it = new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = true, HiddenObjects = false, IncludeLights = false, IncludeGrips = false };
            foreach (var o in doc.Objects.GetObjectList(it))
                if (o.Attributes.GetUserString(KeyPage) == page) list.Add(o);
            return list;
        }
    }
}
