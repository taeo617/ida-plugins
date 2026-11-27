using System;
using Eto.Drawing;
using Eto.Forms;

namespace ImagePerf.Ui
{
  /// <summary>Colours and fonts for the panel; follows Rhino's light/dark mode.</summary>
  static class Theme
  {
    public static bool Dark;
    public static Color Bg, Card, Border, Text, Sub, Faint, Accent, AccentText, Track, Hover, SegBg, SegSel;
    public static readonly Color Ok = Color.FromArgb(31, 154, 74);
    public static readonly Color Warn = Color.FromArgb(214, 132, 0);
    public static readonly Color Bad = Color.FromArgb(214, 48, 49);
    public static Font Body, BodyBold, Small, SmallBold, Title, Caption;

    public static void Init()
    {
      try { Dark = Rhino.Runtime.HostUtils.RunningInDarkMode; } catch { Dark = false; }
      if (Dark)
      {
        Bg = Rgb(0x1E, 0x1F, 0x22); Card = Rgb(0x2A, 0x2B, 0x2F); Border = Rgb(0x3A, 0x3C, 0x41);
        Text = Rgb(0xEC, 0xED, 0xEF); Sub = Rgb(0xA2, 0xA6, 0xAE); Faint = Rgb(0x6E, 0x72, 0x7A);
        Accent = Rgb(0x4C, 0x8D, 0xFF); Track = Rgb(0x48, 0x4B, 0x52); Hover = Rgb(0x33, 0x36, 0x3C);
        SegBg = Rgb(0x1F, 0x20, 0x24); SegSel = Rgb(0x3F, 0x42, 0x49);
      }
      else
      {
        Bg = Rgb(0xF3, 0xF4, 0xF6); Card = Rgb(0xFF, 0xFF, 0xFF); Border = Rgb(0xE2, 0xE4, 0xE8);
        Text = Rgb(0x1F, 0x23, 0x28); Sub = Rgb(0x66, 0x6D, 0x78); Faint = Rgb(0xA0, 0xA6, 0xAF);
        Accent = Rgb(0x25, 0x63, 0xEB); Track = Rgb(0xD5, 0xD9, 0xDF); Hover = Rgb(0xF2, 0xF6, 0xFF);
        SegBg = Rgb(0xEE, 0xF0, 0xF3); SegSel = Rgb(0xFF, 0xFF, 0xFF);
      }
      AccentText = Colors.White;
      Body = new Font(SystemFont.Default, 9);
      BodyBold = new Font(SystemFont.Bold, 9);
      Small = new Font(SystemFont.Default, 8);
      SmallBold = new Font(SystemFont.Bold, 8);
      Title = new Font(SystemFont.Bold, 12);
      Caption = new Font(SystemFont.Bold, 7.5f);
    }

    static Color Rgb(int r, int g, int b) => Color.FromArgb(r, g, b);
    public static Color Alpha(Color c, float a) => new Color(c, a);
    public static Color FromSys(System.Drawing.Color c) => Color.FromArgb(c.R, c.G, c.B, c.A);

    public static Label L(string text, Font font = null, Color? color = null, bool wrap = false) => new Label
    {
      Text = text, Font = font ?? Body, TextColor = color ?? Text,
      Wrap = wrap ? WrapMode.Word : WrapMode.None, VerticalAlignment = VerticalAlignment.Center
    };
  }

  /// <summary>Base for painted controls: anti-aliasing, dimming when disabled, pointer cursor.</summary>
  abstract class Painted : Drawable
  {
    protected bool Hovering;
    protected Painted() { Cursor = Cursors.Pointer; }
    protected float A => Enabled ? 1f : 0.4f;
    protected override void OnMouseEnter(MouseEventArgs e) { Hovering = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(MouseEventArgs e) { Hovering = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
      e.Graphics.AntiAlias = true;
      var s = ClientSize;
      e.Graphics.FillRectangle(BackgroundColor, 0, 0, s.Width, s.Height);
      PaintBody(e.Graphics, s.Width - 0.5f, s.Height - 0.5f);
    }
    protected abstract void PaintBody(Graphics g, float w, float h);

    protected static void Round(Graphics g, Color fill, float x, float y, float w, float h, float r)
      => g.FillPath(fill, GraphicsPath.GetRoundRect(new RectangleF(x, y, w, h), r));
    protected static void RoundStroke(Graphics g, Color c, float x, float y, float w, float h, float r)
      => g.DrawPath(new Pen(c, 1), GraphicsPath.GetRoundRect(new RectangleF(x + 0.5f, y + 0.5f, w - 1, h - 1), r));
    protected static void Centered(Graphics g, Font f, Color c, string t, float x, float y, float w, float h)
    {
      var sz = g.MeasureString(f, t);
      g.DrawText(f, c, x + (w - sz.Width) / 2, y + (h - sz.Height) / 2, t);
    }
  }

  /// <summary>Segmented control (pill group). Click a segment to select it.</summary>
  class Segmented : Painted
  {
    readonly string[] _items;
    int _sel;
    public event Action<int> Changed;
    public Segmented(int width, int height, params string[] items)
    {
      _items = items; Size = new Size(width, height);
    }
    public int SelectedIndex { get => _sel; set { if (_sel == value) return; _sel = value; Invalidate(); } }
    protected override void OnMouseDown(MouseEventArgs e)
    {
      if (!Enabled) return;
      int i = (int)(e.Location.X / (ClientSize.Width / (float)_items.Length));
      i = Math.Max(0, Math.Min(_items.Length - 1, i));
      e.Handled = true;
      if (i == _sel) return;
      _sel = i; Invalidate(); Changed?.Invoke(i);
    }
    protected override void PaintBody(Graphics g, float w, float h)
    {
      Round(g, Theme.Alpha(Theme.SegBg, A), 0, 0, w, h, 7);
      float cw = (w - 4) / _items.Length;
      for (int i = 0; i < _items.Length; i++)
      {
        float x = 2 + i * cw;
        bool sel = i == _sel;
        if (sel)
        {
          Round(g, Theme.Alpha(Theme.SegSel, A), x, 2, cw, h - 4, 5.5f);
          RoundStroke(g, Theme.Alpha(Theme.Border, A), x, 2, cw, h - 4, 5.5f);
        }
        var c = sel ? Theme.Text : Theme.Sub;
        Centered(g, sel ? Theme.SmallBold : Theme.Small, Theme.Alpha(c, A), _items[i], x, 2, cw, h - 4);
      }
    }
  }

  /// <summary>iOS-style on/off switch.</summary>
  class Toggle : Painted
  {
    bool _on;
    public event Action<bool> Changed;
    public Toggle() { Size = new Size(34, 20); }
    public bool Checked { get => _on; set { if (_on == value) return; _on = value; Invalidate(); } }
    public void Flip() { if (!Enabled) return; _on = !_on; Invalidate(); Changed?.Invoke(_on); }
    protected override void OnMouseDown(MouseEventArgs e) { Flip(); e.Handled = true; }
    protected override void PaintBody(Graphics g, float w, float h)
    {
      float th = 18, ty = (h - th) / 2;
      Round(g, Theme.Alpha(_on ? Theme.Accent : Theme.Track, A), 0, ty, w, th, th / 2);
      float k = th - 4, kx = _on ? w - k - 2 : 2;
      g.FillEllipse(Theme.Alpha(Colors.White, A), kx, ty + 2, k, k);
    }
  }

  /// <summary>Flat slider with an accent fill and a round knob.</summary>
  class Slide : Painted
  {
    int _v;
    bool _drag;
    public int Min, Max;
    public event Action<int> Changed;
    public Slide(int min, int max, int width) { Min = min; Max = max; Size = new Size(width, 22); }
    public int Value { get => _v; set { value = Math.Max(Min, Math.Min(Max, value)); if (_v == value) return; _v = value; Invalidate(); } }
    void SetFrom(float x)
    {
      float w = ClientSize.Width - 16;
      int v = (int)Math.Round(Min + (Max - Min) * Math.Max(0, Math.Min(1, (x - 8) / w)));
      if (v == _v) return;
      _v = v; Invalidate(); Changed?.Invoke(v);
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
      if (!Enabled) return;
      _drag = true; try { typeof(Control).GetMethod("CaptureMouse")?.Invoke(this, null); } catch { }
      SetFrom(e.Location.X); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
      if (_drag && e.Buttons.HasFlag(MouseButtons.Primary)) { SetFrom(e.Location.X); e.Handled = true; }
      base.OnMouseMove(e);
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
      _drag = false; try { typeof(Control).GetMethod("ReleaseMouseCapture")?.Invoke(this, null); } catch { }
      base.OnMouseUp(e);
    }
    protected override void PaintBody(Graphics g, float w, float h)
    {
      float x0 = 8, x1 = w - 8, cy = h / 2;
      float t = Max > Min ? (_v - Min) / (float)(Max - Min) : 0;
      float kx = x0 + (x1 - x0) * t;
      Round(g, Theme.Alpha(Theme.Track, A), x0, cy - 2, x1 - x0, 4, 2);
      Round(g, Theme.Alpha(Theme.Accent, A), x0, cy - 2, kx - x0, 4, 2);
      float r = Hovering || _drag ? 7.5f : 7f;
      g.FillEllipse(Theme.Alpha(Colors.White, A), kx - r, cy - r, 2 * r, 2 * r);
      g.DrawEllipse(new Pen(Theme.Alpha(Theme.Accent, A), 1.6f), kx - r + 0.8f, cy - r + 0.8f, 2 * r - 1.6f, 2 * r - 1.6f);
    }
  }

  /// <summary>Rounded button; primary = filled accent.</summary>
  class FlatButton : Painted
  {
    public string Text;
    public bool Primary;
    public event Action Click;
    public FlatButton(string text, int width, int height = 30, bool primary = false)
    {
      Text = text; Primary = primary; Size = new Size(width, height);
    }
    public void PerformClick() { if (Enabled) Click?.Invoke(); }
    protected override void OnMouseUp(MouseEventArgs e)
    {
      var s = ClientSize;
      if (Enabled && e.Location.X >= 0 && e.Location.Y >= 0 && e.Location.X <= s.Width && e.Location.Y <= s.Height) Click?.Invoke();
      e.Handled = true;
    }
    protected override void PaintBody(Graphics g, float w, float h)
    {
      if (Primary)
      {
        var c = Hovering ? Blend(Theme.Accent, Colors.Black, 0.12f) : Theme.Accent;
        Round(g, Theme.Alpha(c, A), 0, 0, w, h, 7);
        Centered(g, Theme.BodyBold, Theme.Alpha(Theme.AccentText, A), Text, 0, 0, w, h);
      }
      else
      {
        Round(g, Theme.Alpha(Hovering ? Theme.Hover : Theme.Card, A), 0, 0, w, h, 7);
        RoundStroke(g, Theme.Alpha(Theme.Border, A), 0, 0, w, h, 7);
        Centered(g, Theme.BodyBold, Theme.Alpha(Theme.Text, A), Text, 0, 0, w, h);
      }
    }
    static Color Blend(Color a, Color b, float t) => new Color(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);
  }

  /// <summary>Small coloured status pill (e.g. "✓ 모두 OK").</summary>
  class Pill : Painted
  {
    string _text = ""; Color _color = Colors.Gray;
    public Pill() { Cursor = Cursors.Default; Size = new Size(10, 22); }
    public void Set(string text, Color color)
    {
      _text = text; _color = color;
      using (var bmp = new Bitmap(1, 1, PixelFormat.Format32bppRgba))
      using (var g = new Graphics(bmp)) Width = (int)Math.Ceiling(g.MeasureString(Theme.SmallBold, text).Width) + 22;
      Invalidate();
    }
    protected override void PaintBody(Graphics g, float w, float h)
    {
      Round(g, Theme.Alpha(_color, Theme.Dark ? 0.22f : 0.12f), 0, 0, w, h, h / 2);
      Centered(g, Theme.SmallBold, _color, _text, 0, 0, w, h);
    }
  }

  /// <summary>Coloured dot (edge status, legend).</summary>
  class Dot : Drawable
  {
    Color _c;
    public Dot(Color c, int size = 8) { _c = c; Size = new Size(size, size); }
    public Color Color { get => _c; set { _c = value; Invalidate(); } }
    protected override void OnPaint(PaintEventArgs e)
    {
      var s = ClientSize;
      e.Graphics.AntiAlias = true;
      e.Graphics.FillRectangle(BackgroundColor, 0, 0, s.Width, s.Height);
      e.Graphics.FillEllipse(_c, 0, 0, s.Width - 0.5f, s.Height - 0.5f);
    }
  }

  /// <summary>Colour swatch button.</summary>
  class Swatch : Painted
  {
    Color _c;
    public event Action Click;
    public Swatch(Color c) { _c = c; Size = new Size(44, 22); }
    public Color Color { get => _c; set { _c = value; Invalidate(); } }
    protected override void OnMouseUp(MouseEventArgs e) { if (Enabled) Click?.Invoke(); e.Handled = true; }
    protected override void PaintBody(Graphics g, float w, float h)
    {
      Round(g, Theme.Alpha(Theme.Card, A), 0, 0, w, h, 6);
      RoundStroke(g, Theme.Alpha(Hovering ? Theme.Accent : Theme.Border, A), 0, 0, w, h, 6);
      Round(g, Theme.Alpha(_c, A), 4, 4, w - 8, h - 8, 3.5f);
    }
  }

  /// <summary>White rounded card that hosts content.</summary>
  class Card : Drawable
  {
    public Card(Control content, int padX = 14, int padY = 12)
    {
      Padding = new Padding(padX, padY);
      Content = content;
      BackgroundColor = Theme.Bg;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
      // ClientSize is rounded up at fractional display scales, so inset by a pixel to keep the right/bottom border visible.
      var g = e.Graphics; var s = ClientSize;
      float w = s.Width - 1, h = s.Height - 1;
      g.AntiAlias = true;
      g.FillRectangle(Theme.Bg, 0, 0, s.Width, s.Height);
      g.FillPath(Theme.Card, GraphicsPath.GetRoundRect(new RectangleF(0, 0, w, h), 10));
      g.DrawPath(new Pen(Theme.Border, 1), GraphicsPath.GetRoundRect(new RectangleF(0.5f, 0.5f, w - 1, h - 1), 10));
    }
  }

  /// <summary>A row that tints on hover and reports enter/leave (used for edge rows).</summary>
  class HoverRow : Drawable
  {
    bool _hover;
    public event Action<bool> HoverChanged;
    public HoverRow(Control content) { Content = content; Padding = new Padding(8, 4); }
    protected override void OnMouseEnter(MouseEventArgs e) { _hover = true; Invalidate(); HoverChanged?.Invoke(true); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(MouseEventArgs e) { _hover = false; Invalidate(); HoverChanged?.Invoke(false); base.OnMouseLeave(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
      var g = e.Graphics; var s = ClientSize;
      g.AntiAlias = true;
      g.FillRectangle(Theme.Card, 0, 0, s.Width, s.Height);
      if (_hover) g.FillPath(Theme.Hover, GraphicsPath.GetRoundRect(new RectangleF(0, 0, s.Width, s.Height), 6));
    }
  }
}

