using System;
using System.IO;
using Rhino;

namespace ImagePerf
{
  public enum Layout { Hex = 0, Square = 1, Even = 2 }
  public enum HoleShape { Circle = 0, Hexagon = 1, Slot = 2 }
  public enum ToneMode { Halftone = 0, Threshold = 1 }
  public enum Mapping { Projection = 0, UV = 1 }
  public enum FitMode { Contain = 0, Cover = 1, Stretch = 2 }
  public enum CutMode { Auto = 0, Trim = 1, Boolean = 2, Curves = 3 }

  /// <summary>All settings of one run. Saved in the plug-in settings so the next run starts the same way.</summary>
  public class PerfOptions
  {
    public string ImagePath = "";
    public ToneMode Tone = ToneMode.Halftone;
    public bool Invert;
    public double Cutoff = 0.06;      // halftone: darkness below this → no hole
    public double Threshold = 0.5;    // threshold mode: darkness at/above this → hole
    public double Gamma = 1.0;        // > 1 = fewer mid-tone holes, < 1 = more

    public Layout Layout = Layout.Hex;
    public double Spacing = 4.0;      // centre to centre, mm (model units)
    public double MinSize = 0.8;
    public double MaxSize = 3.4;
    public double Margin = 3.0;       // hole edge to face edge
    public double MinWall = 0.6;      // smallest bridge left between two holes

    public HoleShape Shape = HoleShape.Circle;
    public double SlotRatio = 2.5;    // slot length / width
    public double Angle = 0;          // degrees, slot / hexagon rotation in the tangent plane

    public Mapping Mapping = Mapping.Projection;
    public FitMode Fit = FitMode.Contain;
    public int Rotate;                // quarter turns, counter-clockwise
    public bool FlipX;

    public CutMode Cut = CutMode.Auto;
    public bool KeepOriginal;

    public PerfOptions Clone() => (PerfOptions)MemberwiseClone();

    public bool HasImage => !string.IsNullOrWhiteSpace(ImagePath) && File.Exists(ImagePath);

    /// <summary>Spacing/size sanity: max size is limited so holes never touch.</summary>
    public double EffectiveMax => Math.Max(0.01, Math.Min(MaxSize, MaxPossible));
    public double MaxPossible => Shape == HoleShape.Slot ? Spacing - MinWall : Spacing - MinWall;
    public double EffectiveMin => Math.Max(0.0, Math.Min(MinSize, EffectiveMax));

    // ───────────── persistence ─────────────
    static PersistentSettings S => ImagePerfPlugin.Instance?.Settings;

    public static PerfOptions Load()
    {
      var o = new PerfOptions();
      var s = S;
      if (s == null) return o;
      o.ImagePath = s.GetString("ImagePath", o.ImagePath);
      o.Tone = (ToneMode)s.GetInteger("Tone", (int)o.Tone);
      o.Invert = s.GetBool("Invert", o.Invert);
      o.Cutoff = s.GetDouble("Cutoff", o.Cutoff);
      o.Threshold = s.GetDouble("Threshold", o.Threshold);
      o.Gamma = s.GetDouble("Gamma", o.Gamma);
      o.Layout = (Layout)s.GetInteger("Layout", (int)o.Layout);
      o.Spacing = s.GetDouble("Spacing", o.Spacing);
      o.MinSize = s.GetDouble("MinSize", o.MinSize);
      o.MaxSize = s.GetDouble("MaxSize", o.MaxSize);
      o.Margin = s.GetDouble("Margin", o.Margin);
      o.MinWall = s.GetDouble("MinWall", o.MinWall);
      o.Shape = (HoleShape)s.GetInteger("Shape", (int)o.Shape);
      o.SlotRatio = s.GetDouble("SlotRatio", o.SlotRatio);
      o.Angle = s.GetDouble("Angle", o.Angle);
      o.Mapping = (Mapping)s.GetInteger("Mapping", (int)o.Mapping);
      o.Fit = (FitMode)s.GetInteger("Fit", (int)o.Fit);
      o.Rotate = s.GetInteger("Rotate", o.Rotate);
      o.FlipX = s.GetBool("FlipX", o.FlipX);
      o.Cut = (CutMode)s.GetInteger("Cut", (int)o.Cut);
      o.KeepOriginal = s.GetBool("KeepOriginal", o.KeepOriginal);
      return o;
    }

    public void Save()
    {
      var s = S;
      if (s == null) return;
      s.SetString("ImagePath", ImagePath ?? "");
      s.SetInteger("Tone", (int)Tone);
      s.SetBool("Invert", Invert);
      s.SetDouble("Cutoff", Cutoff);
      s.SetDouble("Threshold", Threshold);
      s.SetDouble("Gamma", Gamma);
      s.SetInteger("Layout", (int)Layout);
      s.SetDouble("Spacing", Spacing);
      s.SetDouble("MinSize", MinSize);
      s.SetDouble("MaxSize", MaxSize);
      s.SetDouble("Margin", Margin);
      s.SetDouble("MinWall", MinWall);
      s.SetInteger("Shape", (int)Shape);
      s.SetDouble("SlotRatio", SlotRatio);
      s.SetDouble("Angle", Angle);
      s.SetInteger("Mapping", (int)Mapping);
      s.SetInteger("Fit", (int)Fit);
      s.SetInteger("Rotate", Rotate);
      s.SetBool("FlipX", FlipX);
      s.SetInteger("Cut", (int)Cut);
      s.SetBool("KeepOriginal", KeepOriginal);
    }
  }
}
