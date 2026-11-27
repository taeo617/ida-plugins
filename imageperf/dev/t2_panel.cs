// Panel + live preview check: builds the real session/conduit/dialog on the plate, captures the viewport
// with the preview on, and renders only the panel window (WPF) to a PNG.
#r "PresentationFramework"
#r "PresentationCore"
#r "WindowsBase"
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using Rhino;
using Rhino.Geometry;
using Rhino.Display;

var log = new StringBuilder();
var dev = @"C:\Users\User\Documents\Rhino Agent\ida-plugins\imageperf\dev";
var outDir = Path.Combine(dev, "out");
var doc = RhinoDoc.ActiveDoc;
try
{
  var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "ImagePerf");
  if (asm == null) { Rhino.PlugIns.PlugIn.LoadPlugIn(new Guid("64449869-33b7-4be6-95be-b056357cb42a")); asm = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "ImagePerf"); }
  Type T(string n) => asm.GetType("ImagePerf." + n);

  var plateObj = doc.Objects.FindByLayer("앞판")[0];
  var target = Activator.CreateInstance(T("PerfTarget"));
  T("PerfTarget").GetField("Id").SetValue(target, plateObj.Id);
  var brep = ((Brep)plateObj.Geometry).DuplicateBrep();
  T("PerfTarget").GetField("Brep").SetValue(target, brep);
  // top face (normal +Z)
  int top = Enumerable.Range(0, brep.Faces.Count).OrderByDescending(i => { var f = brep.Faces[i]; return f.NormalAt(f.Domain(0).Mid, f.Domain(1).Mid).Z; }).First();
  ((List<int>)T("PerfTarget").GetField("Faces").GetValue(target)).Add(top);

  var listType = typeof(List<>).MakeGenericType(T("PerfTarget"));
  var targets = Activator.CreateInstance(listType);
  listType.GetMethod("Add").Invoke(targets, new[] { target });

  var opt = Activator.CreateInstance(T("PerfOptions"));
  void Set(string f, object v) => T("PerfOptions").GetField(f).SetValue(opt, v);
  Set("Spacing", 3.0); Set("MinSize", 0.5); Set("MaxSize", 2.5); Set("Margin", 3.0); Set("MinWall", 0.5);
  Set("Invert", false); Set("FlipX", false); Set("Rotate", 0);
  Set("Shape", Enum.Parse(T("HoleShape"), "Circle")); Set("Layout", Enum.Parse(T("Layout"), "Hex"));

  var session = Activator.CreateInstance(T("PerfSession"), targets, opt, doc.ModelAbsoluteTolerance);
  var err = T("PerfSession").GetMethod("LoadImage").Invoke(session, new object[] { @"C:\Users\User\Documents\Rhino Agent\IDA-logo.png" });
  log.AppendLine("load image err: " + (err ?? "none"));
  var res = T("PerfSession").GetMethod("ComputeNow").Invoke(session, null);
  var holes = (System.Collections.IList)T("PerfResult").GetField("Holes").GetValue(res);
  log.AppendLine($"holes: {holes.Count}  ms={T("PerfResult").GetField("Ms").GetValue(res)}");

  var conduit = (DisplayConduit)Activator.CreateInstance(T("PerfConduit"), session);
  conduit.Enabled = true;
  var view = doc.Views.Find("Top", false);
  view.ActiveViewport.ZoomBoundingBox(brep.GetBoundingBox(true));
  view.Redraw();
  var vc = new ViewCapture { Width = 1400, Height = 950, ScaleScreenItems = false, DrawAxes = false, DrawGrid = false, DrawGridAxes = false };
  object bmp = vc.CaptureToBitmap(view);
  bmp.GetType().GetMethod("Save", new[] { typeof(string) }).Invoke(bmp, new object[] { Path.Combine(outDir, "t5_preview_top.png") });

  // Panel: construct the real dialog, show its WPF window off to the side, render just that window.
  var dlg = Activator.CreateInstance(T("PerfDialog"), session);
  // Move the panel content into a modeless form (a modal dialog would block this script).
  var ed = (Eto.Forms.Dialog<bool>)dlg;
  var panel = ed.Content; ed.Content = null;
  var form = new Eto.Forms.Form { Content = panel, BackgroundColor = ed.BackgroundColor, Padding = ed.Padding, Location = new Eto.Drawing.Point(-4000, 0), ShowInTaskbar = false };
  form.Show();
  var win = (System.Windows.Window)form.ControlObject;
  for (int i = 0; i < 20; i++)
  {
    // Let WPF run its layout/render passes (we are on the UI thread).
    var frame = new System.Windows.Threading.DispatcherFrame();
    System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
      new Action(() => frame.Continue = false));
    System.Windows.Threading.Dispatcher.PushFrame(frame);
  }
  win.UpdateLayout();
  log.AppendLine($"window {win.ActualWidth}x{win.ActualHeight} content={win.Content?.GetType().Name}");
  var content = (System.Windows.FrameworkElement)win.Content;
  content.UpdateLayout();
  int w = (int)Math.Ceiling(content.ActualWidth), h = (int)Math.Ceiling(content.ActualHeight);
  var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w * 2, h * 2, 192, 192, System.Windows.Media.PixelFormats.Pbgra32);
  rtb.Render(content);
  var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
  enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
  using (var fs = File.Create(Path.Combine(outDir, "t5_panel.png"))) enc.Save(fs);
  log.AppendLine($"panel {w}x{h}");
  form.Close();
  conduit.Enabled = false;
  doc.Views.Redraw();
}
catch (Exception e) { log.AppendLine("ERR " + e); }
File.WriteAllText(Path.Combine(outDir, "log.txt"), log.ToString());
