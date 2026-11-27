// Rhino -> Illustrator 인쇄 데이터 빌더 (FOUNDFOUNDED 규칙)
// DATA_PATH 가 정의되어 있지 않으면 JSON 파일 선택 창을 띄운다 (파일 > 스크립트 > 기타 스크립트로 직접 실행 가능).
(function () {
  var MM = 72 / 25.4;
  function readText(p) { var f = new File(p); f.encoding = 'UTF-8'; f.open('r'); var s = f.read(); f.close(); return s; }
  var path = (typeof DATA_PATH !== 'undefined') ? DATA_PATH : null;
  if (!path) { var pick = File.openDialog('Rhino 내보내기 JSON 선택', '*.json'); if (!pick) return 'cancel'; path = pick.fsName; }
  var D = eval('(' + readText(path) + ')');
  var C = D.config;

  // ---------- 색상
  var cache = {};
  function cmyk(c, m, y, k) { var o = new CMYKColor(); o.cyan = c; o.magenta = m; o.yellow = y; o.black = k; return o; }
  function toCMYK(rgb, explicit, isFill) {
    if (explicit) return cmyk(explicit[0], explicit[1], explicit[2], explicit[3]);
    var key = rgb.join(',') + (isFill ? 'f' : 's');
    if (cache[key]) return cache[key];
    var v;
    if (rgb[0] < 8 && rgb[1] < 8 && rgb[2] < 8) v = isFill ? C.black_fill : C.black_stroke;
    else if (rgb[0] > 250 && rgb[1] > 250 && rgb[2] > 250) v = [0, 0, 0, 0];
    else {
      try { v = app.convertSampleColor(ImageColorSpace.RGB, rgb, ImageColorSpace.CMYK, ColorConvertPurpose.defaultpurpose); }
      catch (e) { var r = rgb[0] / 255, g = rgb[1] / 255, b = rgb[2] / 255, k = 1 - Math.max(r, g, b);
        v = k >= 1 ? [0, 0, 0, 100] : [(1 - r - k) / (1 - k) * 100, (1 - g - k) / (1 - k) * 100, (1 - b - k) / (1 - k) * 100, k * 100]; }
    }
    var out = cmyk(Math.round(v[0]), Math.round(v[1]), Math.round(v[2]), Math.round(v[3]));
    cache[key] = out; return out;
  }
  function rgbc(a) { var o = new RGBColor(); o.red = a[0]; o.green = a[1]; o.blue = a[2]; return o; }

  // ---------- 글꼴: 팀 기본 Pretendard (없으면 일러스트 기본 글꼴 그대로)
  var FONT = null;
  var fontNames = [C.font || 'Pretendard-Regular', 'Pretendard-Regular', 'PretendardVariable-Regular', 'Pretendard'];
  for (var fi = 0; fi < fontNames.length && !FONT; fi++) { try { FONT = app.textFonts.getByName(fontNames[fi]); } catch (e) {} }
  function setFont(tf) { if (FONT) try { tf.textRange.characterAttributes.textFont = FONT; } catch (e) {} }
  // 격리 모드 해제: 막 켜진 일러스트가 격리 모드로 시작하면 레이어 패널에 '격리 모드'만 보이고 그 상태로 저장될 수 있음
  function exitIsolation() { for (var q = 0; q < 3; q++) { try { app.executeMenuCommand('exitisolation'); } catch (e) {} } }

  // ---------- 문서
  app.coordinateSystem = CoordinateSystem.DOCUMENTCOORDINATESYSTEM;
  var preset = new DocumentPreset();
  preset.colorMode = DocumentColorSpace.CMYK;
  preset.units = RulerUnits.Millimeters;
  preset.width = 210 * MM; preset.height = 297 * MM;
  preset.title = D.project;
  var doc = app.documents.addDocument('Print', preset);
  exitIsolation();

  // ---------- 레이어 (위 -> 아래 순서: config 순서, 미분류 레이어는 맨 위)
  var used = {}, i, j;
  for (i = 0; i < D.items.length; i++) used[D.items[i].layer] = 1;
  for (i = 0; i < D.texts.length; i++) used[D.texts[i].layer] = 1;
  var order = [], known = {};
  for (i = 0; i < C.ai_layers.length; i++) { known[C.ai_layers[i].name] = C.ai_layers[i]; }
  for (var n in used) if (!known[n]) order.push({ name: n, layer_color: [255, 79, 255] });
  for (i = 0; i < C.ai_layers.length; i++) if (C.ai_layers[i].always || used[C.ai_layers[i].name]) order.push(C.ai_layers[i]);
  var L = {};
  for (i = order.length - 1; i >= 0; i--) {
    var lay = (i === order.length - 1) ? doc.layers[0] : doc.layers.add();
    lay.name = order[i].name; lay.color = rgbc(order[i].layer_color);
    L[order[i].name] = lay;
  }

  // ---------- 그룹 (AI 레이어 안에서 Rhino 레이어 이름별)
  var G = {};
  function group(layer, src) {
    var k = layer + '::' + src;
    if (!G[k]) { G[k] = L[layer].groupItems.add(); G[k].name = src; }
    return G[k];
  }
  function addPath(container, p) {
    var pi = container.pathItems.add();
    for (var q = 0; q < p.pts.length; q++) {
      var a = p.pts[q], pp = pi.pathPoints.add();
      pp.anchor = [a[0], a[1]]; pp.leftDirection = [a[2], a[3]]; pp.rightDirection = [a[4], a[5]];
      pp.pointType = PointType.CORNER;
    }
    pi.closed = p.closed;
    return pi;
  }
  function styleFill(pi, col) { pi.filled = true; pi.fillColor = col; pi.stroked = false; }
  function styleStroke(pi, col, w) { pi.filled = false; pi.stroked = true; pi.strokeColor = col; pi.strokeWidth = w; }

  var count = {}, outlines = [];
  for (i = 0; i < D.items.length; i++) {
    var it = D.items[i], grp = group(it.layer, it.src);
    var col = toCMYK(it.rgb, it.cmyk, it.mode === 'fill');
    for (j = 0; j < it.shapes.length; j++) {
      var s = it.shapes[j];
      if (it.mode === 'fill' && s.length > 1) {
        var cp = grp.compoundPathItems.add();
        for (var h = s.length - 1; h >= 0; h--) styleFill(addPath(cp, s[h]), col);
      } else if (it.mode === 'fill') {
        styleFill(addPath(grp, s[0]), col);
      } else {
        var sp = addPath(grp, s[0]);
        styleStroke(sp, col, it.width);
        if (it.layer === '도면' && s[0].closed) outlines.push(sp);
      }
      count[it.layer] = (count[it.layer] || 0) + 1;
    }
  }
  for (i = 0; i < D.texts.length; i++) {
    var t = D.texts[i], tg = group(t.layer, t.src);
    var tf = tg.textFrames.pointText([t.x, t.y]);
    tf.contents = t.text;
    setFont(tf);
    tf.textRange.characterAttributes.size = t.size;
    tf.textRange.characterAttributes.fillColor = toCMYK(t.rgb, t.cmyk, true);
    count[t.layer] = (count[t.layer] || 0) + 1;
  }

  // ---------- 치수 가이드 (도면 외곽선마다 가로/세로)
  if (C.auto_dimension && outlines.length && L['가이드']) {
    var dc = C.dim_cmyk || [71, 63, 60, 14], dcol = cmyk(dc[0], dc[1], dc[2], dc[3]);
    var off = (C.dim_offset_mm || 6) * MM, ext = 1.5 * MM, fs = C.text_size_pt || 8;
    var dg = L['가이드'].groupItems.add(); dg.name = '치수';
    function seg(x1, y1, x2, y2) { var p = dg.pathItems.add(); p.setEntirePath([[x1, y1], [x2, y2]]); p.filled = false; p.stroked = true; p.strokeColor = dcol; p.strokeWidth = 0.3; return p; }
    function label(txt, x, y, center) {
      var t = dg.textFrames.pointText([x, y]); t.contents = txt;
      setFont(t);
      t.textRange.characterAttributes.size = fs; t.textRange.characterAttributes.fillColor = dcol;
      if (center) t.textRange.paragraphAttributes.justification = Justification.CENTER;
      return t;
    }
    function fmt(v) { var mm = Math.round(v / MM * 10) / 10; return String(mm) + (C.dim_suffix || ''); }
    var big = [];  // 너무 작은 외곽(10mm 미만)은 생략, 다른 외곽 안에 들어간 것도 생략
    for (i = 0; i < outlines.length; i++) {
      var g = outlines[i].geometricBounds; // [l,t,r,b]
      if (g[2] - g[0] < 10 * MM && g[1] - g[3] < 10 * MM) continue;
      var inside = false;
      for (j = 0; j < outlines.length; j++) { if (i === j) continue; var o = outlines[j].geometricBounds;
        if (o[0] <= g[0] && o[1] >= g[1] && o[2] >= g[2] && o[3] <= g[3] && (o[2] - o[0]) * (o[1] - o[3]) > (g[2] - g[0]) * (g[1] - g[3])) { inside = true; break; } }
      if (!inside) big.push(g);
    }
    for (i = 0; i < big.length; i++) {
      var l = big[i][0], t = big[i][1], r = big[i][2], bt = big[i][3];
      // 가로: 위쪽
      var yy = t + off;
      seg(l, t + ext, l, yy + ext); seg(r, t + ext, r, yy + ext); seg(l, yy, r, yy);
      label(fmt(r - l), (l + r) / 2, yy + 1.5 * MM, true);
      // 세로: 오른쪽
      var xx = r + off;
      seg(r + ext, t, xx + ext, t); seg(r + ext, bt, xx + ext, bt); seg(xx, t, xx, bt);
      label(fmt(t - bt), xx + 2 * MM, (t + bt) / 2 - fs * 0.35, false);
      count['가이드'] = (count['가이드'] || 0) + 1;
    }
  }

  // ---------- 아트보드
  var b = D.bbox, w = b[2] - b[0], h = b[3] - b[1], cx = (b[0] + b[2]) / 2, cy = (b[1] + b[3]) / 2;
  var mg = (C.artboard_margin_mm || 0) * MM, A4w = 210 * MM, A4h = 297 * MM, W, H, mode = C.artboard;
  if (mode instanceof Array) { W = mode[0] * MM; H = mode[1] * MM; }
  else if (mode === 'fit') { W = w + mg * 2; H = h + mg * 2; }
  else {
    var fitsP = (w + mg * 2 <= A4w && h + mg * 2 <= A4h), fitsL = (w + mg * 2 <= A4h && h + mg * 2 <= A4w);
    if (mode === 'a4' || fitsP) { W = A4w; H = A4h; if (!fitsP && fitsL) { W = A4h; H = A4w; } }
    else if (fitsL) { W = A4h; H = A4w; }
    else { W = w + mg * 2; H = h + mg * 2; }
  }
  W = Math.round(W / MM * 10) / 10 * MM; H = Math.round(H / MM * 10) / 10 * MM;
  doc.artboards[0].artboardRect = [cx - W / 2, cy + H / 2, cx + W / 2, cy - H / 2];
  doc.artboards[0].name = D.project;

  // 빈 그룹 정리
  for (var g in G) { if (G[g].pageItems.length === 0) G[g].remove(); }

  // ---------- 저장 전 정리: 격리 모드 해제, 선택 해제, 레이어는 모두 보이기·잠금 해제·인쇄 가능, 맨 위 레이어를 현재 레이어로
  app.activeDocument = doc;
  exitIsolation();
  try { doc.selection = null; } catch (e) {}
  for (i = 0; i < doc.layers.length; i++) { try { doc.layers[i].visible = true; doc.layers[i].locked = false; doc.layers[i].printable = true; } catch (e) {} }
  try { doc.activeLayer = doc.layers[0]; } catch (e) {}

  // ---------- 저장
  var msg = [];
  for (var c in count) msg.push(c + ' ' + count[c]);
  var lnames = []; for (i = 0; i < doc.layers.length; i++) lnames.push(doc.layers[i].name);
  var result = 'OK | ' + msg.join(', ') + ' | 아트보드 ' + Math.round(W / MM) + 'x' + Math.round(H / MM) + 'mm' +
    ' | 레이어 ' + lnames.join('·') + ' | 글꼴 ' + (FONT ? FONT.name : '기본');
  if (D.save_path) {
    var opt = new IllustratorSaveOptions(); opt.pdfCompatible = true; opt.embedICCProfile = true;
    var sf = new File(D.save_path);
    if (!sf.parent.exists) sf.parent.create();
    doc.saveAs(sf, opt);
    result += (new File(D.save_path).exists ? ' | 저장: ' : ' | 저장 실패: ') + D.save_path;
  }
  exitIsolation();
  try { app.executeMenuCommand('fitall'); } catch (e) {}
  return result;
})();
