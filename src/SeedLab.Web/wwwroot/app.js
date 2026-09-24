/* SeedLab - the local web UI.
 *
 * Plain HTML, CSS and JavaScript on purpose: no framework, no bundler, no CDN, nothing fetched from
 * anywhere but this server. The whole file is served from vseed's own binary.
 *
 * The map is a slippy map over a fixed tile scheme (see TileGrid.cs): the square
 * x, z in [-12288, 12288], one 256 px tile at zoom 0, halving the metres per pixel each level, so
 * zoom 3 is the game's own 12 m grid. Tiles are drawn at their true world rectangle at a fractional
 * scale, and while a tile is still being generated its parent is stretched into the slot - which is
 * why an arriving tile visibly SHARPENS rather than appearing out of nothing.
 */
'use strict';
(function () {

// ---------------------------------------------------------------------------- small helpers
const $ = (id) => document.getElementById(id);
const el = (tag, cls, text) => {
  const n = document.createElement(tag);
  if (cls) n.className = cls;
  if (text !== undefined) n.textContent = text;
  return n;
};
const clamp = (v, lo, hi) => v < lo ? lo : v > hi ? hi : v;
const nf = (v, d) => (v === null || v === undefined || !isFinite(v)) ? '—'
  : Number(v).toLocaleString('en-US', { minimumFractionDigits: d || 0, maximumFractionDigits: d || 0 });

// Coordinates are printed WITHOUT thousands separators: "9,291, 17,171" reads as four numbers,
// not as one point. Distances and counts keep their separators.
function coord(x, z, d) {
  const f = (v) => (v < 0 ? '−' : '') + Math.abs(v).toFixed(d === undefined ? 0 : d);
  return f(x) + ', ' + f(z);
}

function metres(v, d) {
  if (v === null || v === undefined || !isFinite(v)) return '—';
  return Math.abs(v) >= 1000 ? nf(v / 1000, 2) + ' km' : nf(v, d === undefined ? 0 : d) + ' m';
}

function bearing(x, z) {
  const deg = (Math.atan2(x, z) * 180 / Math.PI % 360 + 360) % 360;
  const pts = ['N','NNE','NE','ENE','E','ESE','SE','SSE','S','SSW','SW','WSW','W','WNW','NW','NNW'];
  return nf(deg, 0) + '° ' + pts[Math.round(deg / 22.5) % 16];
}

function kv(host, rows) {
  host.textContent = '';
  for (const [k, v, cls] of rows) {
    host.appendChild(el('div', 'k', k));
    const n = el('div', 'v' + (cls ? ' ' + cls : ''));
    if (v instanceof Node) n.appendChild(v); else n.textContent = v;
    host.appendChild(n);
  }
}

async function copy(text, hint) {
  try {
    await navigator.clipboard.writeText(text);
  } catch (e) {
    const t = el('textarea'); t.value = text; document.body.appendChild(t);
    t.select(); try { document.execCommand('copy'); } catch (e2) { /* nothing else to try */ }
    document.body.removeChild(t);
  }
  flash(hint || ('copied: ' + text));
}

let hintTimer = 0;
function flash(msg) {
  const h = $('mapHint');
  // hidden FIRST, then the text: the element is hidden at load, and a text change inside a
  // display:none subtree is not a change any assistive technology can observe.
  h.hidden = false;
  h.textContent = msg;
  clearTimeout(hintTimer);
  hintTimer = setTimeout(() => { h.hidden = true; }, 1600);
  // The visible hint is a map overlay that takes itself away again after 1.6 s; the announcement
  // has to outlive it, so it also goes to the permanent screen-reader-only region. This is the
  // only way a state changed by R/M/G/H/B/P/L while focus is on the canvas is announced at all.
  $('srLive').textContent = msg;
}

async function getJson(url, signal) {
  const r = await fetch(url, { signal, headers: { 'Accept': 'application/json' } });
  if (!r.ok) {
    let msg = r.status + ' ' + r.statusText;
    try { const j = await r.json(); if (j && j.error) msg = j.error; } catch (e) { /* not JSON */ }
    throw new Error(msg);
  }
  return r.json();
}

// ---------------------------------------------------------------------------- app state
const App = {
  meta: null,
  seed: null,           // int32
  seedText: '',
  report: null,
  biomeColor: {},       // name -> #rrggbb (SeedLab palette)
  biomeByIndex: {},     // Heightmap.BiomeIndex -> { name, color }
  tiles: { worldSpanM: 24576, minXZ: -12288, maxXZ: 12288, tilePixels: 256, maxZoom: 9, gameGridZoom: 3 },
  waterLevel: 30,
  waterEdge: 10500,
};

// ---------------------------------------------------------------------------- map view
const MapView = {
  canvas: null, ctx: null, dpr: 1, w: 0, h: 0,
  cx: 0, cz: 0,               // world centre, metres
  scale: 0.03,                // css pixels per metre
  minScale: 0.008, maxScale: 8,
  fitted: false,              // has the view ever been fitted against a real canvas size?
  tile: new Map(),            // tile key -> ImageBitmap
  order: [],                  // key insertion order, for eviction
  pending: new Map(),         // tile key -> AbortController
  queue: [],
  maxInflight: 8,
  dirty: true,
  tool: 'pan',
  showGrid: false,
  style: { shade: true, water: true, game: false },
  hoverPlace: null,
  pin: null,
  markers: [],
  ruler: [],
  rulerHover: null,
  cursor: null,
};

function tileKey(z, x, y) { return z + '/' + x + '/' + y; }

function styleQuery() {
  const q = [];
  if (MapView.style.game) q.push('p=game');
  if (!MapView.style.shade) q.push('shade=0');
  if (!MapView.style.water) q.push('water=0');
  return q.length ? '?' + q.join('&') : '';
}

function styleTag() { return (MapView.style.game ? 'g' : 's') + (MapView.style.shade ? 'h' : '-') + (MapView.style.water ? 'w' : '-'); }

function worldToScreenX(x) { return (x - MapView.cx) * MapView.scale + MapView.w / 2; }
function worldToScreenY(z) { return (MapView.cz - z) * MapView.scale + MapView.h / 2; }
function screenToWorldX(px) { return (px - MapView.w / 2) / MapView.scale + MapView.cx; }
function screenToWorldZ(py) { return MapView.cz - (py - MapView.h / 2) / MapView.scale; }

function zoomLevel() {
  // Which tile zoom to ask for. The ideal is the level whose metres-per-pixel matches the view, but
  // the choice is biased a quarter of a level towards the SHARPER side: a tile that is slightly
  // finer than the screen is downscaled and looks crisp, while one that is slightly coarser is
  // stretched and looks soft, and stretched is the thing this map exists to avoid.
  const m0 = App.tiles.worldSpanM / App.tiles.tilePixels;     // metres per pixel at zoom 0
  return clamp(Math.round(Math.log2(m0 * MapView.scale) + 0.25), 0, App.tiles.maxZoom);
}

function metresPerPixel(z) { return App.tiles.worldSpanM / App.tiles.tilePixels / Math.pow(2, z); }

// A canvas can measure ZERO. It happens whenever the page lays out before it is really on screen:
// loaded into a background tab, into a pane that is still animating open, into a minimised window.
// getBoundingClientRect then returns 0, the clamp below turns that into 1, and a fitWorld() against
// a 1 px canvas sets scale = 1/22260 - a view 22 km to the PIXEL, on which the whole world is far
// smaller than a dot. The map is then simply black, forever, because nothing refits it afterwards.
// That is exactly what this page did on first load. So: remember whether the view was ever fitted
// against a REAL size, and refit the moment one arrives.
function resizeCanvas() {
  const r = MapView.canvas.getBoundingClientRect();
  const real = r.width >= 2 && r.height >= 2;
  MapView.dpr = window.devicePixelRatio || 1;
  MapView.w = Math.max(1, Math.round(r.width));
  MapView.h = Math.max(1, Math.round(r.height));
  MapView.canvas.width = Math.round(MapView.w * MapView.dpr);
  MapView.canvas.height = Math.round(MapView.h * MapView.dpr);
  MapView.ctx.setTransform(MapView.dpr, 0, 0, MapView.dpr, 0, 0);
  MapView.minScale = Math.min(0.008, Math.min(MapView.w, MapView.h) / (App.tiles.worldSpanM * 4));
  // The view the user is looking at is kept across an ordinary resize - cx/cz/scale are world units,
  // so the centre stays put and the window simply shows more or less of it. Only a view that was
  // never fitted at a usable size is thrown away.
  if (real && !MapView.fitted) fitWorld();
  MapView.dirty = true;
}

function fitWorld() {
  MapView.cx = 0; MapView.cz = 0;
  const span = 2 * App.waterEdge * 1.06;
  MapView.scale = Math.min(MapView.w, MapView.h) / span;
  // Only count as fitted when there was a canvas worth fitting to.
  MapView.fitted = MapView.w >= 2 && MapView.h >= 2;
  MapView.dirty = true;
  scheduleHash();
}

function zoomBy(factor, anchorPx, anchorPy) {
  const ax = anchorPx === undefined ? MapView.w / 2 : anchorPx;
  const ay = anchorPy === undefined ? MapView.h / 2 : anchorPy;
  const wx = screenToWorldX(ax), wz = screenToWorldZ(ay);
  MapView.scale = clamp(MapView.scale * factor, MapView.minScale, MapView.maxScale);
  // Keep the anchored world point under the same screen pixel.
  MapView.cx = wx - (ax - MapView.w / 2) / MapView.scale;
  MapView.cz = wz + (ay - MapView.h / 2) / MapView.scale;
  clampCentre();
  MapView.dirty = true;
  scheduleHash();
}

function clampCentre() {
  const lim = App.tiles.maxXZ * 1.2;
  MapView.cx = clamp(MapView.cx, -lim, lim);
  MapView.cz = clamp(MapView.cz, -lim, lim);
}

// ---------------------------------------------------------------------------- tiles
function addTile(key, bmp) {
  MapView.tile.set(key, bmp);
  MapView.order.push(key);
  while (MapView.order.length > 900) {
    const k = MapView.order.shift();
    const old = MapView.tile.get(k);
    if (old && old !== bmp) { MapView.tile.delete(k); if (old.close) old.close(); }
  }
}

function clearTiles() {
  for (const [, b] of MapView.tile) { if (b && b.close) b.close(); }
  MapView.tile.clear();
  MapView.order.length = 0;
  for (const [, ac] of MapView.pending) ac.abort();
  MapView.pending.clear();
  MapView.queue.length = 0;
  MapView.dirty = true;
}

function pump() {
  while (MapView.pending.size < MapView.maxInflight && MapView.queue.length) {
    const t = MapView.queue.shift();
    if (MapView.tile.has(t.key) || MapView.pending.has(t.key)) continue;
    const ac = new AbortController();
    MapView.pending.set(t.key, ac);
    const url = '/tiles/' + App.seed + '/' + t.z + '/' + t.x + '/' + t.y + '.png' + styleQuery();
    fetch(url, { signal: ac.signal })
      .then((r) => { if (!r.ok) throw new Error('tile ' + r.status); return r.blob(); })
      .then(createImageBitmap)
      .then((bmp) => { MapView.pending.delete(t.key); addTile(t.key, bmp); MapView.dirty = true; pump(); })
      .catch(() => { MapView.pending.delete(t.key); pump(); });
  }
}

/**
 * Works out which tiles the viewport needs, cancels the ones it no longer does - which is what
 * stops the server generating terrain the user has already panned away from - and queues the rest
 * nearest-first so the middle of the screen fills in before the corners.
 */
function ensureTiles() {
  if (App.seed === null) return;
  const z = zoomLevel();
  const span = App.tiles.worldSpanM / Math.pow(2, z);
  const n = Math.pow(2, z);
  const x0 = clamp(Math.floor((screenToWorldX(0) - App.tiles.minXZ) / span), 0, n - 1);
  const x1 = clamp(Math.floor((screenToWorldX(MapView.w) - App.tiles.minXZ) / span), 0, n - 1);
  const y0 = clamp(Math.floor((App.tiles.maxXZ - screenToWorldZ(0)) / span), 0, n - 1);
  const y1 = clamp(Math.floor((App.tiles.maxXZ - screenToWorldZ(MapView.h)) / span), 0, n - 1);

  const want = new Set();
  const list = [];
  const ccx = (x0 + x1) / 2, ccy = (y0 + y1) / 2;
  for (let y = y0; y <= y1; y++) {
    for (let x = x0; x <= x1; x++) {
      const key = styleTag() + ':' + App.seed + ':' + tileKey(z, x, y);
      want.add(key);
      if (!MapView.tile.has(key) && !MapView.pending.has(key)) {
        list.push({ key, z, x, y, d: (x - ccx) * (x - ccx) + (y - ccy) * (y - ccy) });
      }
    }
  }

  for (const [key, ac] of MapView.pending) {
    if (!want.has(key)) { ac.abort(); MapView.pending.delete(key); }
  }

  MapView.queue = MapView.queue.filter((t) => want.has(t.key));
  list.sort((a, b) => a.d - b.d);
  for (const t of list) {
    if (!MapView.queue.some((q) => q.key === t.key)) MapView.queue.push(t);
  }
  pump();
  return { z, x0, x1, y0, y1, span };
}

function drawTiles(view) {
  const ctx = MapView.ctx;
  const { z, x0, x1, y0, y1, span } = view;
  ctx.imageSmoothingEnabled = true;
  ctx.imageSmoothingQuality = 'low';

  for (let y = y0; y <= y1; y++) {
    for (let x = x0; x <= x1; x++) {
      const wx0 = App.tiles.minXZ + x * span;
      const wz1 = App.tiles.maxXZ - y * span;
      const sx = Math.round(worldToScreenX(wx0));
      const sy = Math.round(worldToScreenY(wz1));
      const sw = Math.round(worldToScreenX(wx0 + span)) - sx;
      const sh = Math.round(worldToScreenY(wz1 - span)) - sy;
      if (sw <= 0 || sh <= 0) continue;

      const bmp = MapView.tile.get(styleTag() + ':' + App.seed + ':' + tileKey(z, x, y));
      if (bmp) { ctx.drawImage(bmp, sx, sy, sw, sh); continue; }

      // Not here yet: stretch the nearest ancestor that is, so panning and zooming never show a
      // hole. The moment the real tile lands it replaces this and the view sharpens.
      for (let d = 1; d <= 5 && z - d >= 0; d++) {
        const f = 1 << d;
        const pz = z - d, px = x >> d, py = y >> d;
        const p = MapView.tile.get(styleTag() + ':' + App.seed + ':' + tileKey(pz, px, py));
        if (!p) continue;
        const sub = App.tiles.tilePixels / f;
        ctx.drawImage(p, (x - px * f) * sub, (y - py * f) * sub, sub, sub, sx, sy, sw, sh);
        break;
      }
    }
  }
}

// ---------------------------------------------------------------------------- overlays
function drawOverlays() {
  const ctx = MapView.ctx;
  ctx.save();
  ctx.lineWidth = 1;
  ctx.font = '11px ui-monospace, Consolas, monospace';
  ctx.textBaseline = 'top';

  if (MapView.showGrid) drawGrid(ctx);
  drawRings(ctx);
  drawLocations(ctx);
  drawMarkers(ctx);
  drawRuler(ctx);
  drawScaleBar(ctx);
  drawNorth(ctx);
  ctx.restore();
}

function niceStep(targetPx) {
  const steps = [10, 25, 50, 100, 250, 500, 1000, 2000, 5000, 10000];
  for (const s of steps) { if (s * MapView.scale >= targetPx) return s; }
  return 10000;
}

function drawGrid(ctx) {
  const step = niceStep(70);
  ctx.strokeStyle = 'rgba(255,255,255,0.10)';
  ctx.fillStyle = 'rgba(230,226,214,0.55)';
  ctx.beginPath();
  const xa = Math.ceil(screenToWorldX(0) / step) * step;
  for (let x = xa; x <= screenToWorldX(MapView.w); x += step) {
    const px = Math.round(worldToScreenX(x)) + 0.5;
    ctx.moveTo(px, 0); ctx.lineTo(px, MapView.h);
  }
  const za = Math.floor(screenToWorldZ(0) / step) * step;
  for (let z = za; z >= screenToWorldZ(MapView.h); z -= step) {
    const py = Math.round(worldToScreenY(z)) + 0.5;
    ctx.moveTo(0, py); ctx.lineTo(MapView.w, py);
  }
  ctx.stroke();

  for (let x = xa; x <= screenToWorldX(MapView.w); x += step) {
    ctx.fillText(x.toFixed(0), Math.round(worldToScreenX(x)) + 3, 3);
  }
  for (let z = za; z >= screenToWorldZ(MapView.h); z -= step) {
    ctx.fillText(z.toFixed(0), 3, Math.round(worldToScreenY(z)) + 3);
  }
}

function drawRings(ctx) {
  const cx = worldToScreenX(0), cy = worldToScreenY(0);
  ctx.strokeStyle = 'rgba(255,255,255,0.13)';
  for (const r of [2000, 4000, 6000, 8000, 10000]) {
    ctx.beginPath(); ctx.arc(cx, cy, r * MapView.scale, 0, Math.PI * 2); ctx.stroke();
  }

  // The water edge: past it GetBiomeHeight returns the -400 constant and no terrain exists.
  ctx.strokeStyle = 'rgba(255,255,255,0.42)';
  ctx.setLineDash([6, 5]);
  ctx.beginPath(); ctx.arc(cx, cy, App.waterEdge * MapView.scale, 0, Math.PI * 2); ctx.stroke();
  ctx.setLineDash([]);

  // Pure geometry, no seed enters them: |(x, z -/+ 4000)| = 12000 + WorldAngle*100.
  ctx.setLineDash([10, 8]);
  ctx.strokeStyle = 'rgba(255,138,91,0.42)';
  ctx.beginPath(); ctx.arc(worldToScreenX(0), worldToScreenY(4000), 12000 * MapView.scale, 0, Math.PI * 2); ctx.stroke();
  ctx.strokeStyle = 'rgba(187,216,255,0.42)';
  ctx.beginPath(); ctx.arc(worldToScreenX(0), worldToScreenY(-4000), 12000 * MapView.scale, 0, Math.PI * 2); ctx.stroke();
  ctx.setLineDash([]);

  ctx.strokeStyle = 'rgba(231,226,214,0.8)';
  ctx.beginPath();
  ctx.moveTo(cx - 7, cy); ctx.lineTo(cx + 7, cy);
  ctx.moveTo(cx, cy - 7); ctx.lineTo(cx, cy + 7);
  ctx.stroke();
}

function pinPath(ctx, px, py, fill) {
  ctx.beginPath(); ctx.arc(px, py, 5, 0, Math.PI * 2);
  ctx.fillStyle = 'rgba(0,0,0,0.55)'; ctx.fill();
  ctx.beginPath(); ctx.arc(px, py, 3.2, 0, Math.PI * 2);
  ctx.fillStyle = fill; ctx.fill();
}

function drawMarkers(ctx) {
  ctx.textBaseline = 'middle';
  for (const m of MapView.markers) {
    const px = worldToScreenX(m.x), py = worldToScreenY(m.z);
    if (px < -40 || py < -40 || px > MapView.w + 40 || py > MapView.h + 40) continue;
    pinPath(ctx, px, py, '#e8c75c');
    if (m.label) {
      ctx.fillStyle = 'rgba(0,0,0,0.6)';
      const w = ctx.measureText(m.label).width;
      ctx.fillRect(px + 8, py - 8, w + 8, 16);
      ctx.fillStyle = '#e7e2d6';
      ctx.fillText(m.label, px + 12, py);
    }
  }
  if (MapView.pin) {
    const px = worldToScreenX(MapView.pin.x), py = worldToScreenY(MapView.pin.z);
    pinPath(ctx, px, py, '#74a9d8');
    ctx.strokeStyle = 'rgba(116,169,216,0.7)';
    ctx.beginPath(); ctx.arc(px, py, 9, 0, Math.PI * 2); ctx.stroke();
  }
  ctx.textBaseline = 'top';
}

function drawRuler(ctx) {
  const pts = MapView.ruler.slice();
  if (pts.length === 1 && MapView.rulerHover) pts.push(MapView.rulerHover);
  if (pts.length < 1) return;
  ctx.strokeStyle = '#e8c75c';
  ctx.setLineDash([5, 4]);
  ctx.beginPath();
  ctx.moveTo(worldToScreenX(pts[0].x), worldToScreenY(pts[0].z));
  for (let i = 1; i < pts.length; i++) ctx.lineTo(worldToScreenX(pts[i].x), worldToScreenY(pts[i].z));
  ctx.stroke();
  ctx.setLineDash([]);
  for (const p of pts) pinPath(ctx, worldToScreenX(p.x), worldToScreenY(p.z), '#e8c75c');

  if (pts.length === 2) {
    const dx = pts[1].x - pts[0].x, dz = pts[1].z - pts[0].z;
    const d = Math.hypot(dx, dz);
    const mx = (worldToScreenX(pts[0].x) + worldToScreenX(pts[1].x)) / 2;
    const my = (worldToScreenY(pts[0].z) + worldToScreenY(pts[1].z)) / 2;
    const label = metres(d, 0) + '  ' + bearing(dx, dz);
    ctx.textBaseline = 'middle';
    const w = ctx.measureText(label).width;
    ctx.fillStyle = 'rgba(0,0,0,0.7)';
    ctx.fillRect(mx - w / 2 - 6, my - 10, w + 12, 20);
    ctx.fillStyle = '#e8c75c';
    ctx.fillText(label, mx - w / 2, my);
    ctx.textBaseline = 'top';
  }
}

function drawScaleBar(ctx) {
  const target = Math.min(180, MapView.w * 0.25);
  let bar = 10;
  for (const c of [10, 25, 50, 100, 250, 500, 1000, 2000, 5000, 10000, 20000]) {
    if (c * MapView.scale <= target) bar = c;
  }
  const px = bar * MapView.scale;
  const x = 14, y = MapView.h - 46;
  ctx.strokeStyle = 'rgba(231,226,214,0.85)';
  ctx.beginPath();
  ctx.moveTo(x, y - 5); ctx.lineTo(x, y); ctx.lineTo(x + px, y); ctx.lineTo(x + px, y - 5);
  ctx.stroke();
  ctx.fillStyle = 'rgba(231,226,214,0.85)';
  ctx.fillText(bar >= 1000 ? (bar / 1000) + ' km' : bar + ' m', x, y - 20);
}

function drawNorth(ctx) {
  const x = MapView.w - 26, y = 52;
  ctx.strokeStyle = 'rgba(231,226,214,0.75)';
  ctx.fillStyle = 'rgba(231,226,214,0.75)';
  ctx.beginPath();
  ctx.moveTo(x, y - 12); ctx.lineTo(x - 5, y + 2); ctx.lineTo(x, y - 2); ctx.lineTo(x + 5, y + 2);
  ctx.closePath(); ctx.fill();
  ctx.fillText('N', x - 3, y + 5);
}

// ---------------------------------------------------------------------------- frame
function frame() {
  if (MapView.dirty) {
    MapView.dirty = false;
    const ctx = MapView.ctx;
    ctx.fillStyle = '#080d14';
    ctx.fillRect(0, 0, MapView.w, MapView.h);
    if (App.seed !== null) {
      const view = ensureTiles();
      if (view) drawTiles(view);
      drawOverlays();
    }
    updateZoomReadout();
  }
  requestAnimationFrame(frame);
}

function updateZoomReadout() {
  const z = zoomLevel();
  const mpp = metresPerPixel(z);
  const per = 1 / MapView.scale;
  $('roZoom').textContent = 'z' + z + '  tiles ' + (mpp < 1 ? mpp.toFixed(3) : nf(mpp, 2)) + ' m/px'
    + (z === App.tiles.gameGridZoom ? ' (the game’s grid)' : '')
    + '  ·  view ' + (per < 1 ? per.toFixed(2) : nf(per, 1)) + ' m/px';
}

// ---------------------------------------------------------------------------- pointer
function bindMap() {
  const c = MapView.canvas;
  let dragging = false, moved = false, lastX = 0, lastY = 0, downX = 0, downY = 0;

  c.addEventListener('pointerdown', (e) => {
    if (e.button !== 0) return;
    c.setPointerCapture(e.pointerId);
    dragging = true; moved = false;
    lastX = downX = e.offsetX; lastY = downY = e.offsetY;
    c.classList.add('is-dragging');
  });

  c.addEventListener('pointermove', (e) => {
    const px = e.offsetX, py = e.offsetY;
    MapView.cursor = { x: screenToWorldX(px), z: screenToWorldZ(py) };
    if (dragging) {
      const dx = px - lastX, dy = py - lastY;
      if (Math.abs(px - downX) + Math.abs(py - downY) > 3) moved = true;
      MapView.cx -= dx / MapView.scale;
      MapView.cz += dy / MapView.scale;
      lastX = px; lastY = py;
      clampCentre();
      MapView.dirty = true;
      scheduleHash();
    } else if (MapView.tool === 'ruler' && MapView.ruler.length === 1) {
      MapView.rulerHover = { x: MapView.cursor.x, z: MapView.cursor.z };
      MapView.dirty = true;
    }
    const over = Places.show ? placeAt(px, py) : null;
    if (over !== MapView.hoverPlace) {
      MapView.hoverPlace = over;
      c.classList.toggle('is-pointer', !!over);
      // Name first, prefab second: the reader recognises "The Elder", and the prefab is what they
      // will type into a query or grep a log for, so both belong in the one line the readout has.
      $('roPlace').textContent = over
        ? placeName(over.t)
          + (placeSub(over.t) ? ' — ' + placeSub(over.t) : '')
          + (over.t.candidateSet ? '  (1 of several candidates)' : '')
        : '';
    }
    showReadout(MapView.cursor.x, MapView.cursor.z);
  });

  const endDrag = (e) => {
    if (!dragging) return;
    dragging = false;
    c.classList.remove('is-dragging');
    try { c.releasePointerCapture(e.pointerId); } catch (err) { /* already released */ }
    if (!moved) handleClick(e.offsetX, e.offsetY);
  };
  c.addEventListener('pointerup', endDrag);
  c.addEventListener('pointercancel', () => { dragging = false; c.classList.remove('is-dragging'); });
  c.addEventListener('pointerleave', () => { MapView.cursor = null; });

  c.addEventListener('wheel', (e) => {
    e.preventDefault();
    const f = Math.pow(2, -e.deltaY * (e.deltaMode === 1 ? 0.08 : 0.0022));
    zoomBy(f, e.offsetX, e.offsetY);
  }, { passive: false });

  c.addEventListener('dblclick', (e) => { e.preventDefault(); zoomBy(2, e.offsetX, e.offsetY); });
}

function handleClick(px, py) {
  const x = screenToWorldX(px), z = screenToWorldZ(py);

  // A marker under the pointer wins over dropping a pin: the user was aiming at the thing they can
  // see, and the pin is one click away anywhere else.
  if (MapView.tool === 'pan' && Places.show) {
    const hit = placeAt(px, py);
    if (hit) { pickPlace(hit.i); return; }
  }

  if (MapView.tool === 'ruler') {
    if (MapView.ruler.length >= 2) MapView.ruler = [];
    MapView.ruler.push({ x, z });
    MapView.rulerHover = null;
    MapView.dirty = true;
    renderRuler();
    return;
  }
  if (MapView.tool === 'mark') { addMarker(x, z); return; }
  setPin(x, z);
}

// ---------------------------------------------------------------------------- readout
let readoutAc = null, readoutTimer = 0;
function showReadout(x, z) {
  $('roCoord').textContent = coord(x, z, 0);
  clearTimeout(readoutTimer);
  readoutTimer = setTimeout(async () => {
    if (App.seed === null) return;
    if (readoutAc) readoutAc.abort();
    readoutAc = new AbortController();
    try {
      const p = await getJson('/api/at?seed=' + App.seed + '&x=' + x.toFixed(2) + '&z=' + z.toFixed(2), readoutAc.signal);
      const sw = el('span'); sw.className = 'dot';
      sw.style.background = App.biomeColor[p.biome] || '#888';
      const b = $('roBiome');
      b.textContent = '';
      b.appendChild(sw);
      b.appendChild(document.createTextNode(p.outsideWaterEdge ? 'outside the world edge' : p.biome));
      $('roHeight').textContent = p.outsideWaterEdge ? ''
        : nf(p.heightM, 1) + ' m  (' + (p.underwater ? nf(-p.aboveSeaM, 1) + ' m under' : nf(p.aboveSeaM, 1) + ' m above') + ')';
    } catch (e) { /* aborted or the seed changed */ }
  }, 110);
}

// ---------------------------------------------------------------------------- pin, markers, ruler
async function setPin(x, z) {
  x = +x.toFixed(2); z = +z.toFixed(2);
  MapView.pin = { x, z, info: null };
  MapView.dirty = true;
  selectTab('Point');
  kv($('pinKv'), [['position', coord(x, z, 2)], ['', 'measuring…']]);
  $('pinActions').hidden = false;
  try {
    const p = await getJson('/api/at?seed=' + App.seed + '&x=' + x.toFixed(2) + '&z=' + z.toFixed(2));
    MapView.pin.info = p;
    const sw = el('span', 'dot'); sw.style.background = App.biomeColor[p.biome] || '#888';
    const bio = el('span'); bio.appendChild(sw); bio.appendChild(document.createTextNode(p.biome));
    kv($('pinKv'), [
      ['position', coord(p.x, p.z, 2)],
      ['biome', bio],
      ['height', nf(p.heightM, 3) + ' m'],
      ['vs sea level', (p.underwater ? nf(App.waterLevel - p.heightM, 3) + ' m below' : nf(p.aboveSeaM, 3) + ' m above') + ' the 30 m water line'],
      ['from the centre', metres(p.distanceM, 0) + ', ' + bearing(p.x, p.z)],
      ['forest factor', nf(p.forestFactor, 3) + (p.inForest ? '  in forest' : '')],
      ['river / stream', p.riverWeight > 0 ? 'yes, weight ' + nf(p.riverWeight, 3) + ', width ' + nf(p.riverWidth, 1) + ' m' : 'no'],
      ['zone', '(' + p.zoneX + ', ' + p.zoneZ + ')  64 m, centre ' + (p.zoneX * 64) + ', ' + (p.zoneZ * 64)],
      ['geometry', (p.outsideWaterEdge ? 'OUTSIDE the 10500 m water edge — the height is the −400 constant'
        : 'inside the water edge') + (p.isAshlandsGeometry ? '; inside the Ashlands ring' : '')
        + (p.isDeepNorthGeometry ? '; inside the Deep North ring' : ''), 'wrap'],
    ]);
  } catch (e) {
    kv($('pinKv'), [['position', coord(x, z, 2)], ['error', String(e.message || e)]]);
  }
}

function markerStoreKey() { return 'seedlab.markers.' + App.seed; }

function loadMarkers() {
  MapView.markers = [];
  try {
    const raw = localStorage.getItem(markerStoreKey());
    if (raw) MapView.markers = JSON.parse(raw) || [];
  } catch (e) { MapView.markers = []; }
  renderMarkers();
}

function saveMarkers() {
  try { localStorage.setItem(markerStoreKey(), JSON.stringify(MapView.markers)); } catch (e) { /* private mode */ }
}

function addMarker(x, z, label) {
  MapView.markers.push({ x: +x.toFixed(2), z: +z.toFixed(2), label: label || '' });
  saveMarkers();
  renderMarkers();
  MapView.dirty = true;
  flash('marker at ' + coord(x, z, 0));
}

function renderMarkers() {
  const host = $('markerList');
  host.textContent = '';
  $('markerCount').textContent = MapView.markers.length ? '(' + MapView.markers.length + ')' : '';
  if (!MapView.markers.length) { host.appendChild(el('div', 'empty', 'None yet.')); return; }
  MapView.markers.forEach((m, i) => {
    const row = el('div', 'marker');
    row.appendChild(el('span', 'm-co', coord(m.x, m.z, 2)));
    const name = el('input');
    name.type = 'text';
    name.className = 'm-name';
    name.value = m.label;
    name.placeholder = 'label';
    name.addEventListener('change', () => { m.label = name.value; saveMarkers(); MapView.dirty = true; });
    row.appendChild(name);
    const go = el('button', null, '⌖'); go.title = 'Centre on this marker';
    go.addEventListener('click', () => { MapView.cx = m.x; MapView.cz = m.z; MapView.dirty = true; });
    row.appendChild(go);
    const cp = el('button', null, '⧉'); cp.title = 'Copy the coordinates';
    cp.addEventListener('click', () => copy(m.x + ', ' + m.z));   // plain text, for pasting into the game
    row.appendChild(cp);
    const rm = el('button', null, '×'); rm.title = 'Remove';
    rm.addEventListener('click', () => { MapView.markers.splice(i, 1); saveMarkers(); renderMarkers(); MapView.dirty = true; });
    row.appendChild(rm);
    host.appendChild(row);
  });
}

function renderRuler() {
  const host = $('rulerKv');
  if (MapView.ruler.length < 2) {
    host.textContent = '';
    host.appendChild(el('div', 'empty', MapView.ruler.length === 1
      ? 'Click the second point.' : 'Press R, then click two points.'));
    return;
  }
  const a = MapView.ruler[0], b = MapView.ruler[1];
  const dx = b.x - a.x, dz = b.z - a.z, d = Math.hypot(dx, dz);
  kv(host, [
    ['from', coord(a.x, a.z, 1)],
    ['to', coord(b.x, b.z, 1)],
    ['distance', nf(d, 1) + ' m  (' + nf(d / 1000, 3) + ' km)'],
    ['bearing', bearing(dx, dz)],
    ['Δx, Δz', coord(dx, dz, 1)],
  ]);
}

// ---------------------------------------------------------------------------- seed panel
async function openSeed(token, opts) {
  const info = await getJson('/api/seed/resolve?q=' + encodeURIComponent(token));
  App.seed = info.seed;
  App.seedText = info.input;
  $('seedInput').value = token;
  $('chipSeed').textContent = info.seed + '  ·  ' + (info.shortestText || '');
  $('chipSeed').title = 'int32 ' + info.seed + ' — shortest typeable text "' + info.shortestText
    + '", game-style "' + info.gameStyleText + '"';

  kv($('seedTextKv'), [
    ['read as', info.readAs === 'int' ? 'an int32' : 'a seed text'],
    ['int32', String(info.seed)],
    ['shortest text', textWithCopy(info.shortestText, info.shortestText.length + ' chars, alphanumeric')],
    ['game-style', textWithCopy(info.gameStyleText, '10 chars, the 59 the game’s own generator uses')],
    ['worldGenVersion', String(App.meta.worldGenVersion)],
  ]);
  const old = $('ambiguityNote');
  if (old) old.remove();
  if (info.ambiguous) {
    const w = el('div', 'note');
    w.id = 'ambiguityNote';
    w.textContent = '“' + info.input + '” is also a typeable seed TEXT, which would be the '
      + 'different world ' + info.asText + '.';
    $('cardSeedText').appendChild(w);
  }

  clearTiles();
  $('roBiome').textContent = '';
  $('roHeight').textContent = '';
  MapView.pin = null;
  MapView.ruler = [];
  MapView.hoverPlace = null;
  $('roPlace').textContent = '';
  renderRuler();
  loadMarkers();
  resetPlaces();
  if (!opts || !opts.keepView) fitWorld();
  MapView.dirty = true;
  scheduleHash();
  try { localStorage.setItem('seedlab.lastSeed', token); } catch (e) { /* private mode */ }
  await measure();
}

function textWithCopy(text, note) {
  const n = el('span');
  n.appendChild(document.createTextNode(text));
  if (note) { const s = el('em'); s.textContent = '  ' + note; n.appendChild(s); }
  const b = el('button', 'btn btn-small', 'copy');
  b.style.marginLeft = '8px';
  b.addEventListener('click', () => copy(text));
  n.appendChild(b);
  return n;
}

let measureAc = null;
async function measure() {
  if (App.seed === null) return;
  const grid = $('gridSelect').value;
  if (measureAc) measureAc.abort();
  measureAc = new AbortController();
  kv($('gridKv'), [['', 'measuring on a ' + grid + ' m grid…']]);
  try {
    const r = await getJson('/api/seed/report?seed=' + App.seed + '&grid=' + grid + '&top=8', measureAc.signal);
    App.report = r;
    renderReport(r);
  } catch (e) {
    if (e.name !== 'AbortError') kv($('gridKv'), [['error', String(e.message || e)]]);
  }
}

function renderReport(r) {
  kv($('gridKv'), [
    ['grid', 'G' + nf(r.grid.spacingM, 0) + '  ' + r.grid.size + ' × ' + r.grid.size
      + (r.grid.isGameGrid ? '  — the grid the game itself samples' : '')],
    ['cells in world', nf(r.grid.cellsInWorld) + ' of ' + nf(r.grid.cellsTotal)],
    ['area sampled', nf(r.grid.areaSampledM2 / 1e6, 1) + ' km²  (cell ' + nf(r.grid.cellAreaM2, 0) + ' m²)'],
    ['land', nf(r.land.landM2 / 1e6, 2) + ' km²   ' + nf(100 * r.land.landCells / Math.max(1, r.grid.cellsInWorld), 2) + ' %'],
    ['water', nf(r.land.waterM2 / 1e6, 2) + ' km²   ' + nf(100 * r.land.waterCells / Math.max(1, r.grid.cellsInWorld), 2) + ' %'],
    ['time', nf(r.timing.fieldS, 2) + ' s field + ' + nf(r.timing.analysisS, 2) + ' s analysis, ' + r.timing.threads + ' threads'],
  ]);

  // biome bars
  const bars = $('biomeBars');
  bars.textContent = '';
  const rows = r.biomes.slice().sort((a, b) => b.share - a.share);
  for (const b of rows) {
    if (b.cells === 0) continue;
    const row = el('div', 'bar-row');
    row.appendChild(el('div', 'bar-name', b.name));
    const track = el('div', 'bar-track');
    const fill = el('div', 'bar-fill');
    fill.style.width = (b.share * 100).toFixed(2) + '%';
    fill.style.background = b.color;
    track.appendChild(fill);
    row.appendChild(track);
    row.appendChild(el('div', 'bar-val', nf(b.share * 100, 1) + '%'));
    bars.appendChild(row);
  }

  const tb = $('biomeTable').querySelector('tbody');
  tb.textContent = '';
  for (const b of r.biomes) {
    if (b.cells === 0) continue;
    const tr = el('tr');
    const name = el('td');
    const sw = el('span', 'dot'); sw.style.background = b.color;
    name.appendChild(sw); name.appendChild(document.createTextNode(b.name));
    tr.appendChild(name);
    tr.appendChild(el('td', 'num', nf(b.areaM2 / 1e6, 1)));
    tr.appendChild(el('td', 'num', nf(b.share * 100, 2)));
    tr.appendChild(el('td', 'num', b.nearestM === null ? '—' : metres(b.nearestM, 0)));
    tr.appendChild(el('td', 'num', b.nearestLandM === null ? '—' : metres(b.nearestLandM, 0)));
    tb.appendChild(tr);
  }
  $('biomeNote').textContent = '“Nearest” is the centre of the closest cell of that biome to '
    + '(0, 0), so it is within ' + nf(r.grid.distanceUncertaintyM, 1)
    + ' m — half a cell diagonal — of the true nearest point.';

  const i = r.islands;
  kv($('islandKv'), [
    ['islands ≥ 1 ha', nf(i.countAtLeastMin)],
    ['components (all)', nf(i.componentsAll) + '  — strongly resolution-dependent'],
    ['largest island', i.largestAreaM2 === null ? '—'
      : nf(i.largestAreaM2 / 1e6, 2) + ' km², nearest point ' + metres(i.largestNearestM, 0) + ' from the centre'],
    ['nearest land', i.nearestLandM === null ? 'none'
      : metres(i.nearestLandM, 0) + ' at ' + coord(i.nearestLandX, i.nearestLandZ, 0)
        + ', ' + bearing(i.nearestLandX, i.nearestLandZ)],
    ['spawn island', i.centreIsland === null ? 'none — the spawn area is at sea (nearest land beyond 500 m)'
      : nf(i.centreIsland.areaM2 / 1e6, 3) + ' km², ' + nf(i.centreIsland.cells) + ' cells'],
  ]);
  $('islandNote').textContent = i.rule;

  const itb = $('islandTable').querySelector('tbody');
  itb.textContent = '';
  i.top.forEach((is, n) => {
    const tr = el('tr');
    tr.appendChild(el('td', null, String(n + 1)));
    tr.appendChild(el('td', 'num', nf(is.areaM2 / 1e6, 3)));
    tr.appendChild(el('td', 'num', nf(is.cells)));
    tr.appendChild(el('td', 'num', metres(is.nearestToOriginM, 0)));
    const at = el('td', null, coord(is.nearestX, is.nearestZ, 0));
    at.style.cursor = 'pointer';
    at.title = 'Centre the map here';
    at.addEventListener('click', () => { MapView.cx = is.nearestX; MapView.cz = is.nearestZ; MapView.scale = 0.2; MapView.dirty = true; });
    tr.appendChild(at);
    itb.appendChild(tr);
  });

  const o = r.origin;
  kv($('extremesKv'), [
    ['biome at (0,0)', o.biome],
    ['height at (0,0)', nf(o.heightM, 2) + ' m  ('
      + (o.aboveWaterM >= 0 ? nf(o.aboveWaterM, 2) + ' m above' : nf(-o.aboveWaterM, 2) + ' m BELOW') + ' the water line)'],
    ['forest factor', nf(o.forestFactor, 3) + (o.inForest ? '  in forest' : '  not in forest')],
    ['highest point', nf(r.highest.heightM, 1) + ' m at ' + coord(r.highest.x, r.highest.z, 0)
      + '  (' + r.highest.biome + ')'],
    ['lowest in-world', nf(r.lowest.heightM, 1) + ' m at ' + coord(r.lowest.x, r.lowest.z, 0)],
  ]);
}

// ============================================================================ places
/* Location markers.
 *
 * The server computes a seed's locations on demand - about seven seconds for all 183 types,
 * under a second for the boss/trader prefix - so the page never waits inside a request: it asks,
 * gets "computing" with a phase, and polls until the answer is there.
 *
 * The one rule that matters for honesty: a m_unique type whose run ended with more than one
 * surviving candidate is drawn as a CANDIDATE SET - hollow, dashed, counted - and never as a
 * position. The game keeps whichever candidate's zone is generated first, which is exploration
 * order and not a function of the seed.
 */
const Places = {
  status: 'idle',     // idle | computing | ready | failed | unavailable
  set: null,          // 'core' | 'all'
  report: null,
  show: false,
  cats: { boss: true, trader: true, dungeon: true, feature: true },
  off: new Set(),     // prefabs the user switched off
  labels: 'key',
  pick: null,         // { inst, type, index }
  screen: [],         // what was last drawn, for hit testing
  poll: 0,
  colors: { boss: '#ffcf4d', trader: '#74d69a', dungeon: '#9fb8ff', feature: '#b6a8c9', pick: '#ff8f5e' },
  filter: '',
};

const CAT_ORDER = ['feature', 'dungeon', 'trader', 'boss'];
// For "boss" and "trader" the toggle word and the group heading name the SAME set - the toggle set
// for "boss" IS the eight rows under the "Bosses" heading - so they use the same word, or the two
// halves of the panel appear to disagree about what a boss is. "dungeon" and the residue do NOT
// have their own headings: the listing groups those by biome, so their toggles cut across the
// headings rather than matching one.
//
// The residue is called "Other places", not "World features" (2026-09-24). A WORLD FEATURE is now a
// specific, curated thing - the axe-head houses - that a row carries in `featureName` and the filter
// matches on, and that is the sense the user meant by "World Features may also be missing". Using
// the same two words for "everything that is not a boss, a trader or a dungeon", about 150 rows, put
// two meanings in one panel with the smaller one carrying the feature they actually asked for.
const CAT_LABEL = { boss: 'Bosses', trader: 'Traders', dungeon: 'Dungeons', feature: 'Other places' };
// The singular, written out rather than stripped. `CAT_LABEL[c].replace(/s$/, '')` used to make
// "Boss altar" out of "Boss altars"; against the plural above it would make "Bosse".
const CAT_ONE = { boss: 'Boss altar', trader: 'Trader camp', dungeon: 'Dungeon', feature: 'Place' };

/**
 * What to call a location on screen.
 *
 * <p>The server decides what a name is; this page only prefers it. <code>displayName</code> is the
 * game's own string for the place - "The Elder" for GDKing - derived from the 1.0.15 dump, and it
 * is NULL when the dump holds no name for that prefab, which is 169 of the 183 placed types (14 are
 * named: 8 boss altars, 3 traders and 3 places the game labels on the map pin). It is
 * never the prefab dressed up as a name, so <code>displayName || prefab</code> is the whole rule
 * and this is the only place the fallback is allowed to live.</p>
 *
 * <p>One row really does have a name equal to its prefab: Bonemass. That is the game's own string,
 * not a placeholder, and the server's --selftest exempts exactly that literal while failing any
 * other row that does it.</p>
 *
 * <p>What used to be here was <code>t.label</code> with a leading-'$' guard.
 * <code>Location.m_discoverLabel</code> is a localisation token on all 4 of the 186 prefabs that
 * carry one, so that test was true 183 times out of 183 and the label never named anything. The
 * token is still shown on the selected card, where it is evidence rather than a caption.</p>
 */
function placeName(t) {
  return t.displayName || t.prefab;
}

/**
 * The prefab, but only when it is telling the reader something the name did not - i.e. when the row
 * shows a name at all, and that name is not already the prefab. Empty string means "nothing to add",
 * so a caller can concatenate it without a second null test.
 */
function placeSub(t) {
  return t.displayName && t.displayName !== t.prefab ? t.prefab : '';
}

/**
 * Where a name came from, in a sentence, keyed by the server's own DisplayNameSource spelling.
 *
 * <p>The provenance is not on the wire - only the source enum is - so the wording lives here. It is
 * worth a card row because these are not all the same kind of fact: four of them read a field the
 * dumper captured, and the fifth reads a naming convention that the dump does not actually join.
 * That one says <em>unverified</em> in as many words, and it says it every time, because the card
 * is the only place a reader meets the claim.</p>
 */
function nameSourceText(t) {
  switch (t.displayNameSource) {
    case 'BossAltar':
      return 'the boss on this site’s offering bowl, through the game’s own localisation table'
           + (t.nameToken ? ' (' + t.nameToken + ')' : '');
    case 'Trader':
      return 'the trader’s own name' + (t.nameToken ? ' (' + t.nameToken + ')' : '');
    case 'TraderNpcTokenConvention':
      return 'unverified — the trader component’s name is EMPTY in this build, so the name comes from '
           + (t.nameToken || 'the $npc_ token') + ' by the convention the other two traders follow. '
           + 'Nothing in the dump joins the two; a later build could change it.';
    case 'DiscoverLabel':
      // The token is not repeated here: the "map-pin label" row below prints it beside what it
      // resolves to, which is the same fact told once.
      return 'Location.m_discoverLabel, the text the game writes on the map pin when you find it';
    case 'TeleportEnterText':
      // Several prefabs share one of these (three entrances are all "Burial Chambers"); the card's
      // own prefab line is what tells them apart, so nothing here pretends the name is unique.
      return 'the caption on this dungeon’s entrance door (Teleport.m_enterText), which the game shows '
           + 'when you walk in' + (t.nameToken ? ' (' + t.nameToken + ')' : '');
    default:
      return t.displayNameSource || 'the game data';
  }
}

/**
 * The legend swatch for a category: the marker itself, drawn by the same code that draws it on the
 * map, so the two can never disagree about what a boss looks like.
 *
 * <p>The style is set one PROPERTY at a time, never through <code>style.cssText</code>. The page's
 * own Content-Security-Policy is <code>style-src 'self'</code>, which forbids a style ATTRIBUTE -
 * and cssText writes the attribute, so it is blocked and the swatch silently disappears. Individual
 * CSSOM writes are not attributes and are allowed. Loosening the policy to 'unsafe-inline' to get a
 * swatch back would be trading a real protection for a decoration.</p>
 */
function glyphSwatch(cat, candidate, s) {
  const cv = document.createElement('canvas');
  cv.width = cv.height = 16;
  drawGlyph(cv.getContext('2d'), cat, 8, 8, s, candidate, false);
  const n = el('span', 'glyph');
  n.style.backgroundImage = 'url(' + cv.toDataURL() + ')';
  return n;
}

function readPlaceColors() {
  const cs = getComputedStyle(document.documentElement);
  for (const k of ['boss', 'trader', 'dungeon', 'feature', 'pick']) {
    const v = cs.getPropertyValue('--loc-' + k).trim();
    if (v) Places.colors[k] = v;
  }
}

function resetPlaces() {
  Places.status = App.meta && App.meta.locations && App.meta.locations.available ? 'idle' : 'unavailable';
  Places.set = null;
  Places.report = null;
  Places.pick = null;
  Places.screen = [];
  Places.off.clear();
  clearTimeout(Places.poll);
  Places.poll = 0;
  // Opening a second seed nulls the report while Places.show is still true, so without this the
  // button stays lit with nothing behind it. Same defect class as the failed and idle paths.
  setLayerPressed();
  renderPlaces();
  MapView.dirty = true;
}

async function requestPlaces(set) {
  if (App.seed === null) return;
  if (Places.status === 'unavailable') { selectTab('Places'); return; }
  Places.set = set;
  Places.status = 'computing';
  Places.pick = null;
  Places.show = true;
  setLayerPressed();
  renderPlaces();
  pollPlaces(true);
}

async function pollPlaces(start) {
  clearTimeout(Places.poll);
  const seed = App.seed;
  const set = Places.set || 'core';
  let j;
  try {
    j = await getJson('/api/locations?seed=' + seed + '&set=' + set + (start ? '&start=1' : '&start=0'));
  } catch (e) {
    // getJson throws the server's own message for a 409/501, which is the useful one.
    if (App.seed !== seed) return;
    Places.status = 'failed';
    Places.error = String(e.message || e);
    setLayerPressed();                           // clear aria-busy, or the button works forever
    renderPlaces();
    return;
  }

  if (App.seed !== seed) return;                 // the user moved on while we waited
  if (j.status === 'ready') {
    // A full run is 12,300 instances and 10,500 of them are "world features" - runestones, geysers,
    // fishing spots, abandoned huts. Drawn all at once over a fitted world they are a grey fog with
    // the map somewhere underneath, and the bosses the user came for are lost in it. So the first
    // time the full set arrives they start switched OFF, with their count on the toggle so nothing
    // is being hidden quietly, and one line saying which key brings them back.
    const firstFull = j.report.set === 'all' && (!Places.report || Places.report.set !== 'all');
    Places.status = 'ready';
    Places.report = j.report;
    Places.show = true;
    if (firstFull) {
      Places.cats.feature = false;
      const n = j.report.types.reduce((a, t) => a + (t.category === 'feature' ? t.count : 0), 0);
      if (n > 400) flash(nf(n) + ' world features are placed but hidden — press 4, or the category, to show them');
    }

    setLayerPressed();
    renderPlaces();
    MapView.dirty = true;
    return;
  }

  if (j.status === 'computing') {
    Places.status = 'computing';
    Places.phase = j.phase;
    Places.fraction = j.fraction;
    Places.elapsedS = j.elapsedS;
    renderPlaces();
    Places.poll = setTimeout(() => pollPlaces(false), 350);
    return;
  }

  Places.status = j.status === 'unavailable' ? 'unavailable' : 'idle';
  Places.error = j.error || null;
  setLayerPressed();                             // clear aria-busy, or the button works forever
  renderPlaces();
}

const PHASE_TEXT = {
  queued: 'waiting for the placement slot',
  world: 'building the 2048 × 2048 biome-point grid — the same one the game builds',
  placement: 'running the ordered placement pass',
  biomes: 'reading the biome under every instance',
  done: 'finishing',
};

function renderPlaces() {
  const un = $('locUnavailable');
  if (Places.status === 'unavailable') {
    un.hidden = false;
    $('locUnavailableNote').textContent = Places.error
      || 'This build was started without location placement, so nothing about bosses, traders or '
       + 'dungeons can be shown. Terrain, biomes and heights are unaffected.';
  } else {
    un.hidden = true;
  }

  const prog = $('locProgress');
  if (Places.status === 'computing') {
    prog.hidden = false;
    const f = Places.fraction || 0;
    $('locProgressFill').style.width = (f * 100).toFixed(0) + '%';
    $('locProgressText').textContent = (PHASE_TEXT[Places.phase] || Places.phase || 'working')
      + '  ·  ' + nf(Places.elapsedS || 0, 1) + ' s';
  } else {
    prog.hidden = true;
  }

  const r = Places.report;
  if (Places.status === 'failed') {
    kv($('locKv'), [['error', Places.error || 'the placement run failed', 'wrap']]);
  } else if (!r) {
    $('locKv').textContent = '';
    $('locKv').appendChild(el('div', 'empty', Places.status === 'computing'
      ? 'Placing…' : 'Nothing placed yet for this seed.'));
  } else {
    kv($('locKv'), [
      ['set', r.set === 'all' ? 'every location type' : 'boss altars and trader camps'],
      ['types run', nf(r.typesRun) + ' of ' + nf(r.typesTotal) + ' in the ordered list'],
      ['instances', nf(r.instances.length) + ' in ' + nf(r.types.length) + ' types'],
      ['took', nf(r.secondsWorld, 2) + ' s world + ' + nf(r.secondsPlacement, 2) + ' s placement'],
      ['game data', r.gameVersion + ', dumped ' + r.dataDumped + ' (' + r.dataStamp.slice(0, 12) + ')'],
    ]);
  }

  $('locCatCard').hidden = !r;
  $('locTypeCard').hidden = !r;
  renderPlaceCats();
  renderPlaceTypes();
  renderPlacePick();

  const list = $('locNotPredictable');
  list.textContent = '';
  const lines = r ? r.notPredictable : null;
  if (!lines) { list.appendChild(el('li', null, 'Run a placement to see the list.')); }
  else for (const s of lines) list.appendChild(el('li', null, s));

  if (r && r.notes && r.notes.length) {
    for (const n of r.notes) list.appendChild(el('li', null, n));
  }
}

function catCounts() {
  const c = { boss: 0, trader: 0, dungeon: 0, feature: 0 };
  const r = Places.report;
  if (!r) return c;
  for (const i of r.instances) c[r.types[i.t].category]++;
  return c;
}

function renderPlaceCats() {
  const host = $('locCats');
  host.textContent = '';
  if (!Places.report) return;
  const counts = catCounts();
  let n = 1;
  for (const cat of ['boss', 'trader', 'dungeon', 'feature']) {
    const b = el('button', 'cat');
    b.type = 'button';
    b.setAttribute('aria-pressed', Places.cats[cat] ? 'true' : 'false');
    b.title = 'Toggle (' + n + ')';
    n++;
    b.appendChild(glyphSwatch(cat, false, 1.1));
    b.appendChild(el('span', null, CAT_LABEL[cat]));
    b.appendChild(el('span', 'cat-n', nf(counts[cat])));
    // on/off in words, the same fourth channel the toolbar switches carry. aria-hidden because
    // Chrome folds a pseudo-element's text into the accessible name, and the button already says
    // "pressed"; without this it would announce as "Bosses 5 on".
    const w = el('span', 'cat-txt');
    w.setAttribute('aria-hidden', 'true');
    b.appendChild(w);
    b.addEventListener('click', () => toggleCat(cat));
    host.appendChild(b);
  }
}

function toggleCat(cat) {
  Places.cats[cat] = !Places.cats[cat];
  if (Places.pick && Places.report && Places.report.types[Places.pick.inst.t].category === cat && !Places.cats[cat]) {
    Places.pick = null;
    renderPlacePick();
  }
  renderPlaceCats();
  MapView.dirty = true;
}

/**
 * Everything the type list will match a typed filter against, lower-cased and joined.
 *
 * <p>The prefab alone was the old haystack, and it is why the user could not find the axe-head
 * houses: they are WoodHouse2 and WoodHouse6, five rows apart under Meadows, and nothing about
 * either spelling contains "axe". <code>featureName</code> is the clause that closes that - the
 * server puts the string "Axe-head houses" on exactly those two rows - and the display name, the
 * aliases and the group heading are what let "elder", "aesir" and "black forest" work too.</p>
 */
function placeHaystack(t) {
  return [t.prefab, t.displayName, t.groupHeading, t.featureName, t.biomeMask]
    .concat(t.aliases || [])
    .filter((s) => !!s)
    .join(' ')
    .toLowerCase();
}

/**
 * The type list: grouped, ordered and named.
 *
 * <p>The order is the server's, not this page's. Every row carries <code>groupOrder</code> (bosses
 * 0, traders 1, the biomes in map-legend order, "Several biomes" last) and <code>sortIndex</code>
 * (its place inside that group, by the exact visible string). Sorting by those two is the whole
 * comparator; the page deliberately does not compare names in JavaScript, because the C# comparer
 * is the one the CLI listing and the golden fixture are pinned to and two orderings that almost
 * agree are worse than one.</p>
 *
 * <p>A build with no name table sends <code>groupOrder</code> and <code>sortIndex</code> as -1 and
 * the headings as empty strings - the sentinels are present, never absent, so the test is
 * <code>&lt; 0</code>. Then the list falls back to exactly what it used to be: category order, then
 * prefab, and no headings at all.</p>
 *
 * <p>Headings are built while walking the already-sorted rows, so a group with nothing left in it -
 * Ocean, which has no members in this build, or any group the filter empties - simply never gets
 * one. Nothing here knows which biomes exist.</p>
 */
function renderPlaceTypes() {
  const host = $('locTypes');
  host.textContent = '';
  const r = Places.report;
  if (!r) { $('locTypeCount').textContent = ''; return; }

  const q = Places.filter.trim().toLowerCase();
  const rows = r.types
    .map((t, i) => ({ t, i }))
    .filter((x) => x.t.count > 0 && (!q || placeHaystack(x.t).includes(q)));

  const grouped = rows.every((x) => x.t.groupOrder >= 0 && x.t.sortIndex >= 0 && !!x.t.groupHeading);
  if (grouped) {
    rows.sort((a, b) => (a.t.groupOrder - b.t.groupOrder) || (a.t.sortIndex - b.t.sortIndex));
  } else {
    rows.sort((a, b) => (CAT_ORDER.indexOf(b.t.category) - CAT_ORDER.indexOf(a.t.category))
                     || a.t.prefab.localeCompare(b.t.prefab));
  }

  $('locTypeCount').textContent = '(' + rows.length + (q ? ' of ' + r.types.filter((t) => t.count > 0).length : '') + ')';

  // The heading and its group wrapper. Style is written one CSSOM property at a time and never
  // through a style ATTRIBUTE: the page's Content-Security-Policy is style-src 'self', which
  // forbids the attribute (and an inline <style> block), while individual CSSOM writes are allowed.
  // Same reason glyphSwatch sets backgroundImage rather than cssText.
  let open = null, openKey = null, openCount = null, n = 0;
  const startGroup = (t) => {
    const wrap = el('div');
    wrap.setAttribute('role', 'group');
    wrap.setAttribute('aria-label', t.groupHeading);
    const head = el('div', 'loctype-head');
    head.setAttribute('role', 'presentation');
    head.style.position = 'sticky';
    head.style.top = '0';
    head.style.zIndex = '1';
    head.style.display = 'flex';
    head.style.justifyContent = 'space-between';
    head.style.alignItems = 'baseline';
    head.style.gap = '8px';
    head.style.padding = '4px 7px';
    head.style.background = 'var(--panel-2)';
    head.style.borderBottom = '1px solid var(--line)';
    head.style.color = 'var(--ink-dim)';
    head.style.fontSize = '10.5px';
    head.style.letterSpacing = '.07em';
    head.style.textTransform = 'uppercase';
    head.appendChild(el('span', null, t.groupHeading));
    const cnt = el('span', 'lth-n', '0');
    cnt.style.fontFamily = 'var(--mono)';
    cnt.style.fontSize = '11px';
    cnt.style.letterSpacing = 'normal';
    cnt.style.color = 'var(--ink-faint)';
    head.appendChild(cnt);
    wrap.appendChild(head);
    host.appendChild(wrap);
    open = wrap; openKey = t.groupKey; openCount = cnt; n = 0;
  };

  for (const { t } of rows) {
    if (grouped) {
      if (t.groupKey !== openKey) startGroup(t);
      n++;
      openCount.textContent = String(n);
    }
    const row = el('div', 'loctype' + (Places.off.has(t.prefab) ? ' is-off' : ''));
    row.appendChild(glyphSwatch(t.category, t.candidateSet, 1.0));

    // The name cell carries both spellings in one grid column: the display name in the UI face,
    // then the prefab in the mono face. The two faces are the honesty cue - a mono-only row is one
    // the dump has no name for, and you can see which rows those are without reading a word.
    const name = el('span', 'lt-name');
    const sub = placeSub(t);
    name.appendChild(document.createTextNode(placeName(t)));
    if (sub) {
      name.style.fontFamily = 'var(--sans)';
      const p = el('span', 'lt-prefab', sub);
      p.style.fontFamily = 'var(--mono)';
      p.style.fontSize = '11px';
      p.style.color = 'var(--ink-faint)';
      p.style.marginLeft = '6px';
      name.appendChild(p);
    }
    name.title = t.prefab + ' — ' + t.biomeMask + (t.featureName ? ' — ' + t.featureName : '');
    row.appendChild(name);

    const cnt = el('span', 'lt-n', t.candidateSet ? t.count + ' cand.' : String(t.count));
    if (t.shortfall) { cnt.textContent += ' ⚠'; cnt.title = 'the generator ran out of attempts: ' + t.placed + ' of ' + t.quantity; }
    row.appendChild(cnt);
    row.addEventListener('click', () => {
      if (Places.off.has(t.prefab)) Places.off.delete(t.prefab); else Places.off.add(t.prefab);
      renderPlaceTypes();
      MapView.dirty = true;
    });
    (grouped ? open : host).appendChild(row);
  }
}

function renderPlacePick() {
  const card = $('locPickCard');
  const p = Places.pick;
  if (!p || !Places.report) { card.hidden = true; return; }
  card.hidden = false;
  const r = Places.report;
  const t = r.types[p.inst.t];
  const d = Math.hypot(p.inst.x, p.inst.z);
  const bio = App.biomeByIndex[p.inst.b];

  const sw = el('span', 'dot');
  sw.style.background = (bio && bio.color) || '#888';
  const bioNode = el('span');
  bioNode.appendChild(sw);
  bioNode.appendChild(document.createTextNode((bio && bio.name) || ('index ' + p.inst.b)));

  const rows = [];
  // Name above prefab, and the prefab always present: the name is what the reader recognises, the
  // prefab is what they will type into a query or grep a log for. Neither replaces the other.
  if (t.displayName) rows.push(['name', t.displayName]);
  rows.push(['prefab', t.prefab]);
  if (t.displayName) rows.push(['name from', nameSourceText(t), 'wrap']);
  // aliases[0] is always the prefab; aliases[1] is the display name ONLY when there is one. An
  // unnamed prefab that still carries a real alias (a resolved discoverLabel, a runestone pin) would
  // have that alias swallowed by a fixed slice(2). No row in the 1.0.15 dump has more than one alias
  // without a display name, so this was latent - but the invariant belongs to the table, not to this
  // line, and the next dump can change it. "also called" is every spelling past the ones already
  // shown.
  const also = (t.aliases || []).slice(t.displayName ? 2 : 1);
  if (also.length) rows.push(['also called', also.join(', '), 'wrap']);
  rows.push(
    ['what', CAT_ONE[t.category] || t.category],
    ['position', coord(p.inst.x, p.inst.z, 1)],
    ['biome', bioNode],
    ['from the centre', metres(d, 0) + ', ' + bearing(p.inst.x, p.inst.z)],
    ['saved y', nf(p.inst.y, 2) + ' m  — GetHeight, not the built ground'],
    ['zone', '(' + p.inst.zx + ', ' + p.inst.zz + ')'],
    ['placed', t.count + ' of m_quantity ' + t.quantity + (t.shortfall ? '  (ran out of attempts)' : '')]);
  if (t.dungeonGenerators > 0) rows.push(['dungeon', t.dungeonGenerators + ' generator' + (t.dungeonGenerators > 1 ? 's' : '')]);
  // The map-pin label is Location.m_discoverLabel and it is a localisation token on all four
  // prefabs that carry one. When the NAME was derived from it the row above already shows what it
  // resolves to, and saying so is the honest improvement: the token is the evidence. When the name
  // came from somewhere else - DN_Bossroom is named for its boss, not for $hud_pin_dnboss - nothing
  // on the wire says which of that row's other spellings this token resolves to, so the token is
  // left as a token rather than paired with a guess.
  if (t.label && t.label.charAt(0) === '$') {
    rows.push(['map-pin label', t.displayNameSource === 'DiscoverLabel'
      ? t.displayName + '  (Location.m_discoverLabel, ' + t.label + ')'
      : t.label + '  (Location.m_discoverLabel — a localisation token; see “also called”)', 'wrap']);
  }
  rows.push(['biome mask', t.biomeMask]);
  // The axe-head caveat, verbatim and in the same block as the m_unique one below, because it is
  // the same kind of statement: here is what the seed decides, and here is what it does not. It is
  // never summarised - the short version is the "~55 %" half-truth that conflates two different
  // uncertainties, and shortening it is exactly how that comes back.
  if (t.contentsNote) rows.push([t.featureName || 'contents', t.contentsNote, 'wrap']);
  if (r.namesLanguage) rows.push(['names', r.namesLanguage + ', from the ' + r.gameVersion + ' dump', 'wrap']);
  kv($('locPickKv'), rows);

  const cand = $('locCandidates');
  if (!t.candidateSet) { cand.hidden = true; return; }
  cand.hidden = false;
  const sibs = r.instances.filter((i) => i.t === p.inst.t);
  $('locCandidateNote').textContent =
    'This is a m_unique type: the game keeps exactly ONE of these ' + sibs.length + ' candidates and '
    + 'deletes the rest. Which one survives is the first whose zone a player or a peer generates — '
    + 'exploration order, not the seed. None of them is “the” position.';
  const tb = $('locCandidateTable').querySelector('tbody');
  tb.textContent = '';
  sibs.sort((a, b) => Math.hypot(a.x, a.z) - Math.hypot(b.x, b.z));
  sibs.forEach((i, n) => {
    const tr = el('tr');
    tr.appendChild(el('td', null, String(n + 1)));
    tr.appendChild(el('td', 'num', i.x.toFixed(0)));
    tr.appendChild(el('td', 'num', i.z.toFixed(0)));
    tr.appendChild(el('td', 'num', metres(Math.hypot(i.x, i.z), 0)));
    const bb = App.biomeByIndex[i.b];
    tr.appendChild(el('td', null, (bb && bb.name) || String(i.b)));
    tr.style.cursor = 'pointer';
    tr.addEventListener('click', () => { pickPlace(i); MapView.cx = i.x; MapView.cz = i.z; MapView.dirty = true; });
    tb.appendChild(tr);
  });
}

function pickPlace(inst) {
  Places.pick = { inst };
  renderPlacePick();
  selectTab('Places');
  // The Places panel is long. Clicking a marker and being shown the top of a scrolled list is the
  // same as being shown nothing, so the card is brought to the user rather than the other way round.
  $('locPickCard').scrollIntoView({ block: 'nearest' });
  MapView.dirty = true;
}

/** One marker, in canvas units. Hollow and dashed means CANDIDATE: the game has not chosen. */
function drawGlyph(ctx, cat, px, py, s, candidate, highlight) {
  const col = highlight ? Places.colors.pick : Places.colors[cat] || Places.colors.feature;
  ctx.save();
  ctx.lineWidth = candidate ? 1.3 : 1;
  ctx.strokeStyle = 'rgba(0,0,0,0.75)';
  ctx.fillStyle = col;

  const path = new Path2D();
  if (cat === 'boss') {
    const R = 6.5 * s, r2 = 2.9 * s;
    for (let i = 0; i < 10; i++) {
      const a = -Math.PI / 2 + i * Math.PI / 5;
      const rr = i % 2 ? r2 : R;
      const x = px + Math.cos(a) * rr, y = py + Math.sin(a) * rr;
      if (i === 0) path.moveTo(x, y); else path.lineTo(x, y);
    }
    path.closePath();
  } else if (cat === 'trader') {
    path.arc(px, py, 4.6 * s, 0, Math.PI * 2);
  } else if (cat === 'dungeon') {
    const a = 4.0 * s;
    path.moveTo(px, py - a); path.lineTo(px + a, py); path.lineTo(px, py + a); path.lineTo(px - a, py);
    path.closePath();
  } else {
    const a = 2.6 * s;
    path.rect(px - a, py - a, a * 2, a * 2);
  }

  if (candidate) {
    // Hollow, with a dashed ring around it: a shape you cannot mistake for a settled position.
    ctx.fillStyle = 'rgba(8,13,20,0.82)';
    ctx.fill(path);
    ctx.strokeStyle = col;
    ctx.setLineDash([2.5, 2]);
    ctx.stroke(path);
    ctx.setLineDash([]);
  } else {
    ctx.fill(path);
    ctx.stroke(path);
    if (highlight) { ctx.strokeStyle = col; ctx.lineWidth = 1.4; ctx.stroke(path); }
  }

  if (highlight) {
    ctx.strokeStyle = Places.colors.pick;
    ctx.lineWidth = 1.4;
    ctx.beginPath();
    ctx.arc(px, py, 10 * s, 0, Math.PI * 2);
    ctx.stroke();
  }

  ctx.restore();
}

function drawLocations(ctx) {
  Places.screen = [];
  const r = Places.report;
  if (!Places.show || !r) return;

  const byCat = { boss: [], trader: [], dungeon: [], feature: [] };
  const pad = 24;
  for (const i of r.instances) {
    const t = r.types[i.t];
    if (!Places.cats[t.category]) continue;
    if (Places.off.has(t.prefab)) continue;
    const px = worldToScreenX(i.x), py = worldToScreenY(i.z);
    if (px < -pad || py < -pad || px > MapView.w + pad || py > MapView.h + pad) continue;
    byCat[t.category].push({ i, t, px, py });
  }

  // A world holds about 12,300 instances and a zoomed-out view can have all of them on screen.
  // Everything stays hit-testable; only the DRAWN set is thinned, least interesting first, so the
  // frame cost is bounded and the bosses are never the ones dropped.
  let budget = 5000;
  const drawn = [];
  for (const cat of CAT_ORDER.slice().reverse()) {   // boss, trader, dungeon, feature
    const list = byCat[cat];
    const take = Math.min(list.length, budget);
    if (take <= 0) break;                            // the budget is spent; nothing below this matters
    const step = list.length > take ? list.length / take : 1;
    for (let k = 0; k < list.length && drawn.length < 5000; k += step) drawn.push(list[Math.floor(k)]);
    budget -= take;
  }

  // A marker is drawn a little smaller when the whole world is on screen: at that zoom there can
  // be several hundred of them and a full-size star is a blob, not a symbol.
  const gs = MapView.scale < 0.02 ? 0.72 : MapView.scale < 0.06 ? 0.86 : 1;

  const pick = Places.pick ? Places.pick.inst : null;
  for (const cat of CAT_ORDER) {
    for (const m of drawn) {
      if (m.t.category !== cat) continue;
      drawGlyph(ctx, cat, m.px, m.py, gs, m.t.candidateSet, pick === m.i);
    }
  }

  // Hit testing works off everything on screen, not just what was drawn. Appended with a loop and
  // not a spread: a fitted world can put ten thousand instances in one category, and spreading that
  // many arguments into push() is how an engine's argument limit gets found the hard way.
  for (const cat of CAT_ORDER) {
    for (const m of byCat[cat]) Places.screen.push(m);
  }

  drawPlaceLabels(ctx, drawn, pick);
}

/**
 * Labels, laid out so they can actually be read.
 *
 * Every marker labelled is how the first version of this looked, and at world zoom it was a wall of
 * overlapping text with the map underneath it invisible. So: a fixed budget, the most interesting
 * markers first, and a greedy overlap test that simply drops a label whose box would land on one
 * already placed. Dropping a label is safe - the marker is still there, and hovering or clicking it
 * names it.
 */
function drawPlaceLabels(ctx, drawn, pick) {
  if (Places.labels === 'none') return;

  let want = Places.labels === 'all'
    ? drawn.slice()
    : drawn.filter((m) => m.t.category === 'boss' || m.t.category === 'trader');
  if (!want.length) return;

  // Nearest to the middle of the view first, bosses and traders ahead of the rest: what the user is
  // looking at gets the label when two markers compete for the same space.
  const mx = MapView.w / 2, my = MapView.h / 2;
  const near = (m) => (m.px - mx) * (m.px - mx) + (m.py - my) * (m.py - my);
  want.sort((a, b) => (CAT_ORDER.indexOf(b.t.category) - CAT_ORDER.indexOf(a.t.category)) || near(a) - near(b));

  // Zoomed out far enough that a boss altar is a few pixels, one label per PREFAB is a legend and
  // thirty are a smear - a world holds five or six altars of each boss. So label the nearest one of
  // each kind and say how many others share the view, which keeps the count visible without
  // pretending the extra altars are not there: every one of them is still drawn.
  const spanM = MapView.w / MapView.scale;
  let counts = null;
  if (spanM > 8000) {
    counts = new Map();
    for (const m of want) counts.set(m.t.prefab, (counts.get(m.t.prefab) || 0) + 1);
    const seen = new Set();
    want = want.filter((m) => !seen.has(m.t.prefab) && seen.add(m.t.prefab));
  }

  ctx.save();
  ctx.font = '11px ui-monospace, Consolas, monospace';
  ctx.textBaseline = 'middle';

  const boxes = [];
  let placed = 0;
  for (const m of want) {
    if (placed >= 20) break;
    const n = counts ? counts.get(m.t.prefab) : 1;
    const text = placeName(m.t)
      + (n > 1 ? '  ×' + n : '')
      + (m.t.candidateSet ? ' ?' : '');
    const w = ctx.measureText(text).width + 8;
    const box = { x: m.px + 9, y: m.py - 8, w, h: 16 };
    const clash = m.i !== pick && boxes.some((b) =>
      box.x < b.x + b.w + 6 && box.x + box.w + 6 > b.x && box.y < b.y + b.h + 4 && box.y + box.h + 4 > b.y);
    if (clash) continue;
    boxes.push(box);
    placed++;
    ctx.fillStyle = 'rgba(8,13,20,0.86)';
    ctx.fillRect(box.x, box.y, box.w, box.h);
    ctx.fillStyle = m.i === pick ? Places.colors.pick
      : m.t.candidateSet ? 'rgba(231,226,214,0.78)' : Places.colors[m.t.category];
    ctx.fillText(text, box.x + 4, m.py);
  }

  ctx.restore();
}

/** The nearest visible marker to a screen point, within a comfortable click radius. */
function placeAt(px, py) {
  let best = null, bestD = 14 * 14;
  for (const m of Places.screen) {
    const dx = m.px - px, dy = m.py - py;
    const d = dx * dx + dy * dy;
    if (d < bestD) { bestD = d; best = m; }
  }
  return best;
}

// ============================================================================ search panel
// ============================================================================= search
//
// Decision 12: "everything must also be doable from the web-interface (GUI) as opposed to having
// CLI-only operations." So every flag 'vseed search' takes is a control here, every control maps onto
// the same query JSON the terminal reads, and the verdict beside the button is the server's own
// preflight - POST /api/search/preflight runs exactly what POST /api/search runs before it touches a
// seed. The page cannot show one answer while the server acts on another.

const Search = {
  meta: null,          // App.meta.search
  byKind: {},          // 'biome' -> [MetricInfo]
  bounds: { biome: {}, location: {}, group: {} },
  id: null,
  es: null,
  top: [],
  feed: [],
  queryJson: '',
  running: false,
  pf: null,            // the last preflight report
  pfSeq: 0,
  pfTimer: 0,
  runtimeTimer: 0,
  lastChangeAt: null,
  warned: new Set(),   // the run's warnings already listed, by time and text
  finalSave: null,     // the last save as the server has it NOW, for a tab that rejoined an ended run
  checking: false,     // a lost stream is being checked against GET /api/search/{id}
};

const GRID_LADDER = [
  [384, '384 m — 64×64, the fastest rung'],
  [256, '256 m — 96×96'],
  [192, '192 m — 128×128'],
  [96, '96 m — the coarsest a must-have may be screened on'],
  [48, '48 m'],
  [24, '24 m'],
  [12, '12 m — the game’s own grid'],
];

function initSearch() {
  Search.meta = App.meta.search;
  Search.byKind = { biome: [], world: [], location: [], group: [] };
  for (const m of Search.meta.metrics) (Search.byKind[m.kind] || (Search.byKind[m.kind] = [])).push(m);

  const b = Search.meta.bounds || {};
  Search.bounds = { biome: {}, location: {}, group: {}, waterEdgeM: (b.waterEdgeM || 10500) };
  for (const x of (b.biomes || [])) Search.bounds.biome[x.name] = x;
  for (const x of (b.locations || [])) Search.bounds.location[x.name] = x;
  for (const x of (b.groups || [])) Search.bounds.group[x.name] = x;

  const s = Search.meta;
  $('engineBanner').hidden = !!s.isRealEngine;
  if (!s.isRealEngine) $('engineNote').textContent = s.description;

  const grid = $('qGrid');
  grid.textContent = '';
  for (const [v, label] of GRID_LADDER) {
    const o = el('option', null, label);
    o.value = String(v);
    grid.appendChild(o);
  }
  grid.value = '96';

  renderEngineKv();

  const sel = $('presetSelect');
  sel.textContent = '';
  sel.appendChild(el('option', null, 'load a preset…'));
  sel.firstChild.value = '';
  for (const p of s.presets) {
    const o = el('option', null, p.name + (p.needsLocations ? '  (needs the location table)' : ''));
    o.value = p.name;
    sel.appendChild(o);
  }
  sel.addEventListener('change', () => { if (sel.value) applyPreset(sel.value); });

  $('qThreads').placeholder = '0 = ' + s.defaultThreads + ' of ' + s.processorCount;
  $('outNote').textContent = s.resultsDirectory
    ? 'A results file is a NAME, not a path: it is written into ' + s.resultsDirectory
      + ' and nowhere else. Leave it empty and nothing is kept on disk — the best-of table below is '
      + 'still complete. .jsonl, .csv or .json; the extension picks the format.'
    : 'This server was started without a results directory, so the page cannot keep a results file. '
      + "Export the query and run it with 'vseed search' to write one.";
  $('qOut').disabled = !s.resultsDirectory;

  // Every control that can change the verdict re-asks the server for it.
  for (const id of ['qSeeds', 'qWall', 'qOrder', 'qFrom', 'qTo', 'qRegion', 'qGrid', 'qScreen',
                    'qScreenGrid', 'qKeepMode', 'qKeep', 'qOut', 'qRotate', 'qCompress', 'qReduce',
                    'qOnLimit', 'qMaxBytes', 'qMode', 'qThreads', 'qBlock', 'qIgnoreGame']) {
    const n = $(id);
    n.addEventListener('change', schedulePreflight);
    n.addEventListener('input', schedulePreflight);
  }

  $('qKeepMode').addEventListener('change', syncKeepMode);
  syncKeepMode();

  // The panel opens on a goal that is worth asking and that FINDS something.
  //
  // "nearest Swamp <= 2,500 m" was the obvious first choice and is a bad one: GetBiome confines Swamp
  // to 2,000 m and beyond, so the nearest one sits on that floor in nearly every world - measured
  // here, 768 of 768 seeds pass at 2,200 m and 746 of 768 even at 2,050 m, which is a search that
  // looks broken because it never rejects anything. "at least a square kilometre of Swamp inside
  // 2.5 km" asks the question a player actually has and passes 6.6 % of seeds (51 of 768 measured),
  // and it is answered at T2 - the biome field alone, no heights - at about 1,200 seeds a second.
  //
  // It opens with a PREFERENCE set, because a query of nothing but must-haves is refused by decision
  // 9's rule - correctly, and the fix should be the state the page starts in rather than the first
  // error the user meets.
  $('goalList').textContent = '';
  addGoalRow({ target: 'biome:Swamp', metric: 'area_within', test: 'at_least',
               value: 1e6, radius: 2500, importance: 'must', preference: 'recommended' });
  schedulePreflight();
  pollRuntime();
}

function syncKeepMode() {
  const all = $('qKeepMode').value === 'all';
  $('qKeep').disabled = all;
  $('qRotate').disabled = !all;
  $('qCompress').disabled = !all;
  $('qReduce').disabled = !all;
}

function renderEngineKv() {
  const s = Search.meta;
  const rows = [
    ['engine', s.name],
    ['workers', s.defaultThreads + ' of ' + s.processorCount + ' logical cores at the current mode'],
  ];
  if (s.measuredSeedsPerSecond) {
    const rate = s.measuredSeedsPerSecond;
    rows.push(['measured here', nf(rate, 1) + ' seeds/s on the last run  ·  the whole space would take '
      + duration(4294967296 / rate)]);
  }
  if (s.resultsDirectory) rows.push(['results go to', s.resultsDirectory, 'wrap']);
  if (s.locationsUnavailable) {
    rows.push(['locations', 'a goal about a boss, trader, dungeon or village cannot be measured: '
      + s.locationsUnavailable, 'wrap']);
  }
  kv($('engineKv'), rows);
}

function duration(sec) {
  if (!isFinite(sec)) return '—';
  if (sec < 90) return nf(sec, 1) + ' s';
  if (sec < 5400) return nf(sec / 60, 1) + ' min';
  if (sec < 172800) return nf(sec / 3600, 1) + ' h';
  if (sec < 63072000) return nf(sec / 86400, 1) + ' days';
  return nf(sec / 31557600, 1) + ' years';
}

function bytes(n) {
  if (n == null || !isFinite(n) || n < 0) return '—';
  const u = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'];
  let i = 0;
  let v = n;
  while (v >= 1000 && i < u.length - 1) { v /= 1000; i++; }
  return (i === 0 ? v.toFixed(0) : v.toFixed(v < 10 ? 2 : 1)) + ' ' + u[i];
}

function metricOf(kind, name) {
  const list = Search.byKind[kind] || [];
  return list.find((m) => m.name === name) || list[0];
}

/** The bounds row for a goal's target, or null when nothing constrains it. */
function boundFor(kind, name) {
  if (kind === 'biome') return Search.bounds.biome[name] || null;
  if (kind === 'location') return Search.bounds.location[name] || null;
  if (kind === 'group') return Search.bounds.group[name] || null;
  return null;
}

// ---------------------------------------------------------------------------- the goal editor
function addGoalRow(preset) {
  const host = $('goalList');
  const g = Object.assign({ target: 'biome:Swamp', metric: 'nearest_distance', test: 'near',
                            value: 2500, importance: 'must', radius: 0, height: 0,
                            weight: 1, minArea: 10000, from: 'center', max: 0, pad: 0.25,
                            preference: 'none' }, preset || {});
  const colon = (g.target || '').indexOf(':');
  let kind = colon > 0 ? g.target.slice(0, colon) : 'world';
  let name = colon > 0 ? g.target.slice(colon + 1) : '';
  if (!Search.byKind[kind]) kind = 'world';

  const row = el('div', 'goal is-' + g.importance);
  const main = el('div', 'goal-main');

  const kindSel = el('select', 'g-kind');
  // "place", not "prefab": the box below now takes either spelling, so naming the kind after the
  // machine identity would be telling the user the wrong thing about what they may type.
  for (const [k, lbl] of [['biome', 'biome'], ['world', 'world'], ['group', 'group'], ['location', 'place']]) {
    const o = el('option', null, lbl); o.value = k; kindSel.appendChild(o);
  }
  kindSel.value = kind;

  const biomeSel = el('select', 'g-biome');
  for (const b of Search.meta.biomes) { const o = el('option', null, b); o.value = b; biomeSel.appendChild(o); }

  const groupSel = el('select', 'g-group');
  for (const gr of Search.meta.groups) {
    const o = el('option', null, gr.name);
    o.value = gr.name;
    o.title = gr.help;
    groupSel.appendChild(o);
  }

  // A place is a datalist, not free text: the name list already knows every spelling the oracle
  // has, so a typo can be caught here instead of by a refusal after the round trip. Both spellings
  // are in it - "GDKing" and "The Elder" - and whichever is typed is turned into the prefab before
  // it reaches the query.
  const prefabIn = el('input', 'g-prefab');
  prefabIn.type = 'text';
  prefabIn.placeholder = 'name or prefab — e.g. The Elder';
  prefabIn.size = 22;
  prefabIn.setAttribute('list', 'prefabNames');
  ensurePrefabList();

  const metricSel = el('select', 'g-metric');
  const testSel = el('select', 'g-test');
  const value = el('input', 'g-value'); value.type = 'number'; value.step = 'any';
  const unit = el('span', 'goal-unit');
  const maxIn = el('input', 'g-max'); maxIn.type = 'number'; maxIn.step = 'any'; maxIn.title = 'the high end of a between range'; maxIn.placeholder = 'max';
  const radius = el('input', 'g-radius'); radius.type = 'number'; radius.step = 'any'; radius.min = '0'; radius.max = '10500'; radius.title = 'measure within, metres'; radius.placeholder = 'within m';
  const height = el('input', 'g-height'); height.type = 'number'; height.step = 'any'; height.title = 'height, metres'; height.placeholder = 'height m';

  const imp = el('select', 'g-imp');
  for (const [v, lbl] of [['must', 'must have'], ['nice', 'nice to have']]) {
    const o = el('option', null, lbl); o.value = v; imp.appendChild(o);
  }
  imp.value = g.importance;

  const badge = el('span', 'badge g-badge', '—');
  badge.title = 'the measured cost class of this goal';

  const hint = el('p', 'note goal-hint g-hint');
  const help = el('p', 'note g-help');

  // ---- the expandable half: weight, the preference, and the goal's own parameters ----------------
  const more = el('div', 'goal-more');
  more.hidden = true;

  const weight = el('input', 'g-weight'); weight.type = 'number'; weight.step = '0.1'; weight.min = '0.1'; weight.max = '100'; weight.value = String(g.weight || 1);
  const pref = el('select', 'g-pref');
  for (const [v, lbl] of [['none', 'no extra preference'], ['recommended', 'recommended for this goal'],
                          ['closer', 'prefer closer'], ['farther', 'prefer farther'],
                          ['smaller', 'prefer smaller'], ['larger', 'prefer larger']]) {
    const o = el('option', null, lbl); o.value = v; pref.appendChild(o);
  }
  pref.value = g.preference || 'none';

  const minArea = el('input', 'g-minarea'); minArea.type = 'number'; minArea.step = '0.1'; minArea.min = '0'; minArea.value = String((g.minArea || 10000) / 1e6);
  const fromSel = el('select', 'g-from');
  for (const [v, lbl] of [['center', 'the world centre (0, 0)'], ['spawn', 'the spawn stone — needs the location table']]) {
    const o = el('option', null, lbl); o.value = v; fromSel.appendChild(o);
  }
  fromSel.value = g.from || 'center';
  const pad = el('input', 'g-pad'); pad.type = 'number'; pad.step = '0.05'; pad.min = '0'; pad.max = '2'; pad.value = String(g.pad == null ? 0.25 : g.pad);

  const lblWeight = el('label', null, 'weight');
  const lblPref = el('label', null, 'when matches are equally good');
  const lblMinArea = el('label', null, 'minimum island size (km²)');
  const lblFrom = el('label', null, 'measure from');
  const lblPad = el('label', null, 'between: score falls over');
  more.appendChild(lblPref); more.appendChild(pref);
  more.appendChild(lblWeight); more.appendChild(weight);
  more.appendChild(lblMinArea); more.appendChild(minArea);
  more.appendChild(lblFrom); more.appendChild(fromSel);
  more.appendChild(lblPad); more.appendChild(pad);
  const prefNote = el('p', 'note g-prefnote');
  more.appendChild(prefNote);

  // ---- the side buttons: expand, evidence, remove -------------------------------------------------
  const side = el('div', 'goal-side');
  const btnMore = el('button', 'goal-btn', '⌄');
  btnMore.type = 'button';
  btnMore.title = 'weight, preference and this goal’s own parameters';
  btnMore.setAttribute('aria-expanded', 'false');
  btnMore.addEventListener('click', () => {
    more.hidden = !more.hidden;
    btnMore.classList.toggle('is-on', !more.hidden);
    btnMore.setAttribute('aria-expanded', more.hidden ? 'false' : 'true');
  });

  const btnInfo = el('button', 'goal-btn', 'ⓘ');
  btnInfo.type = 'button';
  btnInfo.title = 'the evidence behind this goal’s limits';
  btnInfo.addEventListener('click', () => showEvidence(kindSel.value, targetName(), metricSel.value));

  const del = el('button', 'goal-btn goal-del', '×');
  del.type = 'button';
  del.title = 'Remove this goal';
  del.addEventListener('click', () => { row.remove(); schedulePreflight(); });

  side.appendChild(btnMore);
  side.appendChild(btnInfo);
  side.appendChild(del);

  function targetName() {
    const k = kindSel.value;
    if (k === 'biome') return biomeSel.value;
    if (k === 'group') return groupSel.value;
    // Canonicalised here as well as in collectGoals, because this is what the bounds lookup, the
    // hint line and the evidence dialog are keyed by, and all three are keyed by the prefab.
    if (k === 'location') return canonPlace(prefabIn.value);
    return metricSel.value;
  }

  function syncKind(initial) {
    const k = kindSel.value;
    biomeSel.hidden = k !== 'biome';
    groupSel.hidden = k !== 'group';
    prefabIn.hidden = k !== 'location';
    metricSel.textContent = '';
    for (const m of Search.byKind[k]) {
      const o = el('option', null, m.name.replace(/_/g, ' '));
      o.value = m.name;
      o.title = m.help;
      metricSel.appendChild(o);
    }
    if (initial && Search.byKind[k].some((m) => m.name === g.metric)) metricSel.value = g.metric;
    syncMetric(initial);
  }

  function syncMetric(initial) {
    const m = metricOf(kindSel.value, metricSel.value);
    testSel.textContent = '';
    for (const t of m.tests) { const o = el('option', null, t.replace('_', ' ')); o.value = t; testSel.appendChild(o); }
    if (initial && m.tests.indexOf(g.test) >= 0) testSel.value = g.test;
    unit.textContent = m.unitLabel;
    radius.hidden = !m.needsRadius;
    height.hidden = !m.needsHeight;
    lblMinArea.hidden = m.name !== 'island_count';
    minArea.hidden = m.name !== 'island_count';
    lblFrom.hidden = !m.name.includes('distance');
    fromSel.hidden = !m.name.includes('distance');
    if (initial) {
      value.value = g.value !== undefined && g.value !== null ? +(g.value / m.scale).toPrecision(12) : m.defaultValue;
      if (m.needsRadius) radius.value = g.radius || 2000;
      if (m.needsHeight) height.value = g.height || 300;
      if (g.max) maxIn.value = +(g.max / m.scale).toPrecision(12);
    } else {
      value.value = m.defaultValue;
      if (m.needsRadius && !radius.value) radius.value = 2000;
      if (m.needsHeight && !height.value) height.value = 300;
    }
    help.textContent = m.help + '  ·  ' + m.tier + (m.needsLocations ? '  · needs the dumped location table' : '');
    row.classList.toggle('is-unavailable', !!(m.needsLocations && Search.meta.locationsUnavailable));
    syncTest();
  }

  function syncTest() {
    const between = testSel.value === 'between';
    maxIn.hidden = !between;
    lblPad.hidden = !between;
    pad.hidden = !between;
    applyBounds();
  }

  /**
   * The bound, expressed BY the control.
   *
   * <p>The number input carries the goal's legal interval as min/max, so a browser will not submit an
   * impossible value at all; the hint underneath names the constraint that binds, in one line; the
   * evidence is behind the ⓘ. The numbers come from LocationFeasibility and BiomeGeometry on the
   * server - the same functions the engine refuses with - so "the control allows it" and "the engine
   * will not call it impossible" are the same statement.</p>
   */
  function applyBounds() {
    const m = metricOf(kindSel.value, metricSel.value);
    const k = kindSel.value;
    const b = boundFor(k, targetName());
    let lo = null;
    let hi = null;
    let line = '';

    const isDistance = m.unit === 'metres' && m.name.includes('distance');
    const isCount = m.unit === 'count' && (k === 'location' || k === 'group');

    if (isDistance && fromSel.value === 'spawn') {
      lo = 0; hi = 2 * Search.bounds.waterEdgeM;
      line = 'measured from the spawn stone, which moves with the seed, so the centre-relative ring does not apply: 0 … '
        + metres(hi, 0) + '.';
    } else if (isDistance && b) {
      lo = Math.floor(b.minDistanceM);
      hi = Math.ceil(b.maxDistanceM);
      line = b.hint;
    } else if (isDistance) {
      lo = 0; hi = Search.bounds.waterEdgeM;
      line = 'nothing outside the 10,500 m water edge is anything but Ocean.';
    } else if (isCount && b) {
      lo = 0; hi = b.maxCount;
      line = 'at most ' + b.maxCount + ' of them exist in any world — m_quantity is a proved cap '
        + '(the placement loop is while (i < attempts && placed < m_quantity)). There is no lower '
        + 'bound in the code at all.';
    } else if (m.unit === 'fraction') {
      lo = 0; hi = 100;
      line = 'a share of the measured area: 0 … 100 %.';
    } else if (isArea(m.unit)) {
      lo = 0; hi = 346.36;
      line = 'the whole 10,500 m disc is 346.36 km², so nothing measured inside it can exceed that.';
    } else {
      lo = 0; hi = null;
    }

    if (lo != null) { value.min = String(lo); maxIn.min = String(lo); } else { value.removeAttribute('min'); }
    if (hi != null) { value.max = String(hi); maxIn.max = String(hi); } else { value.removeAttribute('max'); }

    // Clamp what is already there rather than leaving a red control the user has to find.
    const v = parseFloat(value.value);
    if (isFinite(v) && lo != null && v < lo) value.value = String(lo);
    if (isFinite(v) && hi != null && v > hi) value.value = String(hi);

    const dead = b && b.placeable === false;
    hint.textContent = dead
      ? b.name + ' is in the game’s table but never placed (m_enable false, or m_quantity 0), so every world holds exactly zero.'
      : line;
    hint.classList.toggle('is-bad', !!dead);
    syncPrefNote();
  }

  function syncPrefNote() {
    const nice = imp.value === 'nice';
    pref.disabled = nice;
    if (nice) {
      prefNote.textContent = 'A nice-to-have IS a preference: it never rejects a seed, it ranks them. '
        + 'The weight above is how much it counts against the other nice-to-haves.';
      return;
    }

    if (pref.value === 'none') {
      prefNote.textContent = 'With no preference and no nice-to-have goal anywhere in the query there is '
        + 'nothing to rank by, and the run is refused rather than returning whichever N the scan reached '
        + 'first. Setting one here is one of the fixes.';
      return;
    }

    prefNote.textContent = 'The criteria language has no tie-break key: a goal either filters or scores. '
      + 'So this preference is written into the query file as a SECOND, nice-to-have goal over the same '
      + 'measurement, named "<this goal>-pref". The exported file will have one more goal in it than '
      + 'there are rows here, and that goal is this control.';
  }

  kindSel.addEventListener('change', () => { syncKind(false); schedulePreflight(); });
  metricSel.addEventListener('change', () => { syncMetric(false); schedulePreflight(); });
  testSel.addEventListener('change', () => { syncTest(); schedulePreflight(); });
  biomeSel.addEventListener('change', () => { applyBounds(); schedulePreflight(); });
  groupSel.addEventListener('change', () => { applyBounds(); schedulePreflight(); });
  prefabIn.addEventListener('input', () => { applyBounds(); schedulePreflight(); });
  fromSel.addEventListener('change', () => { applyBounds(); schedulePreflight(); });
  imp.addEventListener('change', () => {
    row.className = 'goal is-' + imp.value;
    syncPrefNote();
    schedulePreflight();
  });
  pref.addEventListener('change', () => { syncPrefNote(); schedulePreflight(); });
  for (const n of [value, maxIn, radius, height, weight, minArea, pad]) {
    n.addEventListener('input', schedulePreflight);
  }

  if (kind === 'biome' && name) biomeSel.value = name;
  if (kind === 'group' && name) groupSel.value = name;
  // A preset or a reloaded query holds the PREFAB, and that is what is shown: the box is an echo of
  // the file, and a reader must be able to diff the two. The datalist offers the name beside it.
  if (kind === 'location' && name) prefabIn.value = name;
  syncKind(true);

  main.appendChild(kindSel);
  main.appendChild(biomeSel);
  main.appendChild(groupSel);
  main.appendChild(prefabIn);
  main.appendChild(metricSel);
  main.appendChild(testSel);
  main.appendChild(value);
  main.appendChild(unit);
  main.appendChild(maxIn);
  main.appendChild(radius);
  main.appendChild(height);
  main.appendChild(imp);
  main.appendChild(badge);
  row.appendChild(main);
  row.appendChild(hint);
  row.appendChild(help);
  row.appendChild(more);
  row.appendChild(side);
  host.appendChild(row);
  schedulePreflight();
}

/**
 * Every spelling the server will accept for a place, grouped by the prefab it belongs to.
 *
 * <p><code>/api/meta</code> ships <code>locations.names</code> as one flat map from an exact
 * spelling to a prefab - 247 spellings over 232 prefabs in this build - and a prefab always maps to
 * itself, so the map is its own list of prefabs. Grouping it gives the prefab plus whatever other
 * names the game supplies for it, which is what the picker needs.</p>
 *
 * <p>It can be absent: a build with no dumped names ships an empty map. Then the picker falls back
 * to the bounds table, which is prefabs only, and everything still works - it just has no names to
 * offer.</p>
 */
function placeSpellings() {
  const names = (App.meta.locations && App.meta.locations.names) || null;
  const out = new Map();
  if (names) {
    for (const [spelling, prefab] of Object.entries(names)) {
      if (!out.has(prefab)) out.set(prefab, []);
      if (spelling !== prefab) out.get(prefab).push(spelling);
    }
  }
  for (const prefab of Object.keys(Search.bounds.location)) if (!out.has(prefab)) out.set(prefab, []);
  return out;
}

/**
 * The picker's list: one option per SPELLING, so either half of complaint 2 autocompletes.
 *
 * <p>Two options reach the same place. <code>value="GDKing"</code> is labelled with the names the
 * game has for it, and <code>value="The Elder"</code> is labelled with the prefab. Chrome matches
 * typed text against value and label both; Firefox shows the label. Either way typing "elder"
 * finds the boss and typing "GDK" finds it too, and the value that ends up in the box is turned
 * into the prefab by <code>canonPlace</code> before any query is built - so saved query files keep
 * holding prefabs and keep working unchanged.</p>
 *
 * <p>Restricted to types the placement can actually produce: the bounds table carries all 232 rows
 * of the game's table, 49 of which are never placed, and offering one of those as a completion
 * would be offering a goal that can never be met.</p>
 *
 * <p>A <code>&lt;datalist&gt;</code> cannot hold an <code>&lt;optgroup&gt;</code> - its content
 * model is options - so the list is flat. A grouped picker would have to be a custom combobox and
 * would cost the type-ahead over 183 entries, which is the wrong trade.</p>
 */
function ensurePrefabList() {
  if ($('prefabNames')) return;
  const dl = el('datalist');
  dl.id = 'prefabNames';
  for (const [prefab, alts] of placeSpellings()) {
    const b = Search.bounds.location[prefab];
    if (b && b.placeable === false) continue;
    const head = el('option');
    head.value = prefab;
    if (alts.length) head.label = alts.join(' · ');
    dl.appendChild(head);
    for (const a of alts) {
      const o = el('option');
      o.value = a;
      o.label = prefab;
      dl.appendChild(o);
    }
  }
  document.body.appendChild(dl);
}

/**
 * The same folding the server's resolver does: curly apostrophe to straight, then everything that
 * is not a letter or a digit dropped, then lower case. No diacritic folding - the server does not
 * do it either, and a page that folded further would accept spellings the server then refuses.
 */
function foldPlace(s) {
  return String(s || '').replace(/’/g, "'").replace(/[^\p{L}\p{Nd}]/gu, '').toLowerCase();
}

/** A leading "the" removed from an already-folded string. A leading "the" ONLY - not "articles". */
function dropThe(f) {
  return f.startsWith('the') && f.length > 3 ? f.slice(3) : f;
}

let _placeFold = null;

/**
 * Turn whatever the user typed into the prefab, BEFORE the goal's target string is built.
 *
 * <p>This has to happen here and not on the server, because the goal's id is derived from the raw
 * target (<code>QueryTranslator.DefaultId</code>) and that id becomes a CSV column header and a
 * JSONL field. A display name reaching that point breaks the machine-readable half of the output at
 * the id, not just at the target. Canonicalising is idempotent - a prefab resolves to itself - so
 * doing it twice is safe and doing it early is free.</p>
 *
 * <p>The passes mirror the server's, in its order: the exact spelling, then the fold, then the fold
 * with a leading "the" dropped. The article pass is applied to NAMES only, never to prefab keys:
 * "TheHole01" is a prefab and its leading "the" is part of the identity. A fold key two different
 * prefabs would claim is dropped rather than guessed at, which is what the server does with an
 * ambiguous name.</p>
 *
 * <p>Nothing matched returns the string untouched, on purpose: the server then refuses it and says
 * what it could not find, which is a better answer than this page silently inventing one.</p>
 */
function canonPlace(typed) {
  const s = String(typed || '').trim();
  if (!s) return s;
  const names = (App.meta.locations && App.meta.locations.names) || null;
  if (names && Object.prototype.hasOwnProperty.call(names, s)) return names[s];

  if (!_placeFold) {
    _placeFold = { exact: new Map(), article: new Map() };
    const add = (map, key, prefab) => {
      if (!key) return;
      const had = map.get(key);
      if (had === undefined) map.set(key, prefab);
      else if (had !== prefab) map.set(key, null);   // claimed twice: resolve it to neither
    };
    for (const [prefab, alts] of placeSpellings()) {
      add(_placeFold.exact, foldPlace(prefab), prefab);
      for (const a of alts) {
        add(_placeFold.exact, foldPlace(a), prefab);
        add(_placeFold.article, dropThe(foldPlace(a)), prefab);
      }
    }
  }

  const f = foldPlace(s);
  const hit = _placeFold.exact.get(f);
  if (hit) return hit;
  // The article is dropped from BOTH sides, which is what the server does: "Bog Witch" reaches
  // "The Bog Witch" and "The Haldor" reaches "Haldor". Dropping it on one side only would leave the
  // page disagreeing with the resolver about half the pairs.
  const art = _placeFold.article.get(dropThe(f));
  if (art) return art;
  return s;
}

function showEvidence(kind, name, metric) {
  const b = boundFor(kind, name);
  const m = metricOf(kind, metric);
  $('evidenceTitle').textContent = b ? b.name : (m ? m.name.replace(/_/g, ' ') : 'Evidence');
  $('evidenceHint').textContent = b ? b.hint : (m ? m.help : '');
  const lines = [];
  if (b) {
    lines.push('legal interval   ' + metres(b.minDistanceM, 0) + ' … ' + metres(b.maxDistanceM, 0) + ' from the centre');
    if (b.maxCount) lines.push('m_quantity       ' + b.maxCount + '  (a proved cap on a world-wide count)');
    if (b.biomes) lines.push('biome mask       ' + b.biomes);
    if (b.unique) lines.push('m_unique         yes — the instances are candidates, not placements');
    if (b.members) lines.push('members          ' + b.members.join(', '));
    lines.push('');
    lines.push(b.evidence);
  }
  if (m) {
    lines.push('');
    lines.push('metric           ' + m.name + '  (' + m.tier + ', ' + m.unitLabel + ')');
    lines.push(m.help);
  }
  if (!lines.length) lines.push('Nothing constrains this goal beyond the world itself.');
  $('evidenceBody').textContent = lines.join('\n');
  $('evidenceDialog').showModal();
}

// ---------------------------------------------------------------------------- the query
function collectGoals() {
  const out = [];
  for (const row of $('goalList').children) {
    const kind = row.querySelector('.g-kind').value;
    const metric = row.querySelector('.g-metric').value;
    const m = metricOf(kind, metric);
    let name = '';
    if (kind === 'biome') name = row.querySelector('.g-biome').value;
    else if (kind === 'group') name = row.querySelector('.g-group').value;
    // The one that matters: this target becomes the goal's id, and the id becomes a CSV column
    // header and a JSONL field. A display name must not reach it.
    else if (kind === 'location') name = canonPlace(row.querySelector('.g-prefab').value);
    else name = metric;
    const typed = parseFloat(row.querySelector('.g-value').value);
    const typedMax = parseFloat(row.querySelector('.g-max').value);
    out.push({
      target: kind + ':' + name,
      metric,
      test: row.querySelector('.g-test').value,
      value: (isFinite(typed) ? typed : 0) * m.scale,
      max: (isFinite(typedMax) ? typedMax : 0) * m.scale,
      radius: m.needsRadius ? (parseFloat(row.querySelector('.g-radius').value) || 0) : 0,
      height: m.needsHeight ? (parseFloat(row.querySelector('.g-height').value) || 0) : 0,
      minArea: (parseFloat(row.querySelector('.g-minarea').value) || 0.01) * 1e6,
      from: row.querySelector('.g-from').value,
      importance: row.querySelector('.g-imp').value,
      weight: parseFloat(row.querySelector('.g-weight').value) || 1,
      pad: parseFloat(row.querySelector('.g-pad').value),
      preference: row.querySelector('.g-pref').value,
    });
  }
  return out;
}

function applyPreset(name) {
  const p = Search.meta.presets.find((x) => x.name === name);
  if (!p) return;
  $('goalList').textContent = '';
  for (const g of p.goals) addGoalRow(g);
  if (p.gridSpacingM) {
    const opt = [...$('qGrid').options].find((o) => Math.abs(parseFloat(o.value) - p.gridSpacingM) < 1e-9);
    if (opt) $('qGrid').value = opt.value;
  }
  $('presetNote').textContent = p.description
    + (p.needsLocations ? '  This preset needs the dumped location table, so it will be refused until the dumper has run.' : '');
  schedulePreflight();
}

// The Block size box: null when empty (automatic), else the integer typed - 0 included, which the
// server refuses with its 1..65,536 message. A number box that holds something unparseable reports an
// empty value, so there is no third case.
function blockSizeBox() {
  const raw = $('qBlock').value.trim();
  if (raw === '') return null;
  const n = parseInt(raw, 10);
  return Number.isNaN(n) ? null : n;
}

function searchQuery() {
  const keepAll = $('qKeepMode').value === 'all';
  return {
    name: 'web panel',
    goals: collectGoals(),
    budgetSeeds: parseInt($('qSeeds').value, 10) || 0,
    budgetSeconds: parseFloat($('qWall').value) || 0,
    keep: parseInt($('qKeep').value, 10) || 200,
    keepAll,
    gridSpacingM: parseFloat($('qGrid').value),
    strategy: $('qStrategy').value,
    screen: $('qScreen').value,
    screenGridM: parseFloat($('qScreenGrid').value) || 0,
    regionM: parseFloat($('qRegion').value) || 0,
    threads: parseInt($('qThreads').value, 10) || 0,
    // Empty = automatic, the same rule as the terminal's (256, or smaller so every worker gets
    // blocks). It used to fall back to a fixed 64, which the query file then carried as a size the
    // user had "given". Anything typed is sent as typed, so a 0 gets the server's refusal - as
    // '--block-size 0' and 'block_size: 0' do - instead of quietly meaning "automatic" (`|| null`
    // turned 0 into null, review of 2026-09-24).
    blockSize: blockSizeBox(),
    order: $('qOrder').value,
    rangeStart: parseInt($('qFrom').value, 10) || -2147483648,
    rangeEnd: parseInt($('qTo').value, 10) || 2147483647,
    outName: $('qOut').value.trim() || null,
    rotateBytes: keepAll ? (parseInt($('qRotate').value, 10) || 0) : 0,
    compress: $('qCompress').value === '1',
    reduce: $('qReduce').value.trim() || 'none',
    onLimit: $('qOnLimit').value,
    maxBytes: parseInt($('qMaxBytes').value, 10) || 0,
    mode: $('qMode').value,
    ignoreRunningGame: $('qIgnoreGame').checked,
  };
}

// ---------------------------------------------------------------------------- the live verdict
function schedulePreflight() {
  if (Search.pfTimer) clearTimeout(Search.pfTimer);
  Search.pfTimer = setTimeout(runPreflight, 350);
  setPill('busy', 'checking…');
}

function setPill(kind, text) {
  const p = $('verdictPill');
  p.className = 'badge v-' + kind;
  p.textContent = text;
}

async function runPreflight() {
  Search.pfTimer = 0;
  const seq = ++Search.pfSeq;
  const q = searchQuery();
  if (!q.goals.length) {
    Search.pf = null;
    setPill('refused', 'no goals');
    kv($('verdictKv'), []);
    listInto($('verdictList'), ['Add a goal: a search with none has nothing to look for.']);
    $('btnRunSearch').disabled = true;
    return;
  }

  let r;
  try {
    const res = await fetch('/api/search/preflight', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(q),
    });
    r = await res.json();
    if (seq !== Search.pfSeq) return;
    if (!res.ok) {
      Search.pf = null;
      setPill('refused', 'refused');
      kv($('verdictKv'), []);
      listInto($('verdictList'), [r.error || res.statusText]);
      $('btnRunSearch').disabled = true;
      $('planCard').hidden = true;
      return;
    }
  } catch (e) {
    if (seq !== Search.pfSeq) return;
    setPill('refused', 'no answer');
    listInto($('verdictList'), ['the server did not answer: ' + String(e.message || e)]);
    return;
  }

  Search.pf = r;
  Search.queryJson = r.queryJson || '';
  renderVerdict(r);
  renderGridTable(r);
  renderGoalBadges(r);
  renderPreflightPlan(r);
}

function renderVerdict(r) {
  const refused = r.refusals.length > 0 || r.estimate.verdict === 'refuse';
  const confirm = !refused && (r.confirmations.length > 0 || r.estimate.verdict === 'confirm');
  setPill(refused ? 'refused' : confirm ? 'confirm' : 'ok',
          refused ? 'will not run' : confirm ? 'needs a yes' : 'ready');
  $('btnRunSearch').disabled = refused || Search.running;

  const est = r.estimate;
  const rows = [
    ['seeds', nf(r.seeds) + '  ·  ' + pct(r.fractionOfSpace) + ' of all 4,294,967,296 worlds'],
    ['grid', r.grid.describe],
    ['workers', r.runtime.workers + ' in ' + r.runtime.mode + ' mode'
      + (r.runtime.throttled ? '  (auto-throttled by ' + r.runtime.throttledBy + ')' : '')
      + (r.runtime.memoryLimited ? '  — memory, not the mode, chose that' : '')],
    ['time', duration(est.secondsLow) + ' – ' + duration(est.secondsHigh)
      + (est.calibrated ? '  (from measured slices)' : '  (from the cost model)')],
    ['rate', nf(est.seedsPerSecond, 1) + ' seeds/s expected'],
    ['memory', bytes(est.memoryLowBytes) + ' – ' + bytes(est.memoryHighBytes)],
  ];
  if (r.output.path) {
    rows.push(['keeps', r.output.keepAll
      ? 'every match' + (r.output.isRotating ? ', rotated into ' + bytes(r.output.rotateBytes) + ' segments' + (r.output.compress ? ' (gzipped)' : '') : ' in one file')
      : 'the best ' + nf(r.output.keep) + ' — a real cap: at most ' + bytes(r.output.keep * r.output.bytesPerRecord) + ' whatever the scan finds']);
    rows.push(['writes to', r.output.path, 'wrap']);
    rows.push(['disk', bytes(est.diskLowBytes) + ' – ' + bytes(est.diskHighBytes)
      + '  of ' + bytes(r.freeBytes) + ' free']);
  } else {
    rows.push(['keeps', 'nothing on disk — the best-of table only. Name a results file above to keep one.']);
  }
  rows.push(['checkpoint', r.checkpointPath, 'wrap']);
  kv($('verdictKv'), rows);

  const lines = [];
  for (const x of r.refusals) lines.push('REFUSED: ' + x);
  for (const x of (est.verdict === 'refuse' ? est.reasons : [])) lines.push('REFUSED: ' + x);
  for (const x of r.confirmations) lines.push('CONFIRM: ' + x);
  for (const x of (est.verdict === 'confirm' ? est.reasons : [])) lines.push('CONFIRM: ' + x);
  for (const x of r.warnings) lines.push('note: ' + x);
  for (const x of r.translationNotes) lines.push('note: ' + x);
  if (!lines.length) lines.push('Nothing to warn about: this query is a filter, it has something to rank by, and it fits.');
  listInto($('verdictList'), lines);
}

function listInto(host, lines) {
  host.textContent = '';
  for (const l of lines) host.appendChild(el('li', null, l));
}

function renderGridTable(r) {
  const tb = $('gridTable').querySelector('tbody');
  tb.textContent = '';
  // The note is the server's (GridLadder in SeedLab.Search), like every verdict in the table: the
  // page used to write its own, and it said "the sampling grid decides nothing here" beside a plan
  // block that says the grid still changes the run hash, and beside nice goals whose ranking it
  // does decide.
  $('gridNote').textContent = r.ladderNote || '';
  for (const o of (r.gridOptions || [])) {
    const tr = el('tr');
    if (o.chosen) tr.classList.add('is-chosen');
    if (!o.safeForMusts) tr.classList.add('is-unsafe');
    tr.appendChild(el('td', null, 'G' + nf(o.grid, 0) + (o.isGameGrid ? ' — the game’s own' : '') + (o.chosen ? '  ←' : '')));
    tr.appendChild(el('td', 'num', nf(o.sampledCells, 0)));
    tr.appendChild(el('td', 'num', o.millisecondsPerSeed < 10 ? nf(o.millisecondsPerSeed, 2) : nf(o.millisecondsPerSeed, 0)));
    tr.appendChild(el('td', 'num', nf(o.seedsPerSecond, o.seedsPerSecond < 10 ? 2 : 0)));
    const why = el('td', null, o.safeForMusts ? 'yes' : 'no');
    // Every rung's reason is the server's (GridLadder.Rung never sends none). The page's own fallback
    // - "a counting metric survives this grid with a margin, and every survivor is re-measured
    // exactly" - was false under screen: off, at the rungs auto-pick measures once, and for a
    // fine-only must-have at G12.
    why.title = o.why || '';
    tr.appendChild(why);
    tr.addEventListener('click', () => { $('qGrid').value = String(o.grid); schedulePreflight(); });
    tb.appendChild(tr);
  }
}

function renderGoalBadges(r) {
  const rows = [...$('goalList').children];
  const byIndex = (r.goals || []).filter((g) => !g.id.endsWith('-pref'));
  for (let i = 0; i < rows.length; i++) {
    const badge = rows[i].querySelector('.g-badge');
    const g = byIndex[i];
    if (!badge) continue;
    if (!g) { badge.textContent = '—'; badge.className = 'badge g-badge'; continue; }
    badge.textContent = g.class + (g.millisecondsPerSeed > 0
      ? '  ' + (g.millisecondsPerSeed >= 1000 ? nf(g.millisecondsPerSeed / 1000, 2) + ' s' : nf(g.millisecondsPerSeed, g.millisecondsPerSeed < 10 ? 2 : 0) + ' ms')
      : '');
    badge.className = 'badge g-badge c-' + (g.class === 'very expensive' ? 'very' : g.class);
    badge.title = g.why + '  ·  ' + g.tier + ', measured per seed on one thread';
  }

  $('costHeadline').textContent = r.costHeadline || '';
}

function renderPreflightPlan(r) {
  $('planCard').hidden = false;
  kv($('planKv'), [
    ['query', 'sha-256 ' + String(r.queryHash).slice(0, 16) + '…'],
    ['order', r.order + ', key ' + r.key + '  — same key, same sequence, every time'],
    ['blocks', nf(r.blocks) + ' of ' + r.blockSize + (r.blockSize === 1 ? ' seed' : ' seeds')],
    ['strategy', (r.strategy || 'sample')
      + (r.strategyAsked === 'auto' ? '  — auto' : '  — asked for')],
    ['highest tier', r.maxTier],
    ['region evaluated', r.regionRadiusM >= 10500 ? 'the whole world (10,500 m)'
      : 'a disc of ' + metres(r.regionRadiusM, 0) + '  — exact for these goals'],
  ]);
  const holder = $('planBlock');
  holder.textContent = (r.plan || []).join('\n');
  if (r.strategyWhy) {
    holder.textContent = r.strategyWhy + '\n\n' + holder.textContent;
  }

  const tb = $('planTable').querySelector('tbody');
  tb.textContent = '';
  for (const g of (r.goals || [])) {
    const tr = el('tr');
    const id = el('td', null, g.id);
    id.title = g.help || '';
    tr.appendChild(id);
    const c = el('td');
    const badge = el('span', 'badge c-' + (g.class === 'very expensive' ? 'very' : g.class), g.class);
    badge.title = g.why || '';
    c.appendChild(badge);
    tr.appendChild(c);
    tr.appendChild(el('td', null, g.tier));
    tr.appendChild(el('td', 'num', g.regionRadiusM >= 10500 ? 'all' : metres(g.regionRadiusM, 0)));
    const what = el('td', null, g.target + '.' + g.metric + (g.importance === 'nice' ? '  (nice)' : ''));
    if (!g.available) { what.textContent = g.unavailable || 'unavailable'; what.style.color = 'var(--bad)'; }
    else if (g.unsatisfiable) { what.textContent = g.unsatisfiable; what.style.color = 'var(--accent-hi)'; }
    tr.appendChild(what);
    tb.appendChild(tr);
  }
}

// ---------------------------------------------------------------------------- running it
async function runSearch() {
  if (Search.running) return;
  const query = searchQuery();
  if (!query.goals.length) { showSearchError('add at least one goal.'); return; }

  // The dialog is the page's half of decision 10's confirmation. The server enforces it whatever the
  // page does - a POST without 'confirmed' is refused with kind 'confirm' - so this is the courtesy,
  // not the check.
  const pf = Search.pf;
  const needs = pf && (pf.confirmations.length > 0 || pf.estimate.verdict === 'confirm');
  if (needs && !(await askToConfirm(pf))) return;
  query.confirmed = true;

  await postSearch(query);
}

async function postSearch(query) {
  stopStream();
  Search.top = [];
  Search.feed = [];
  renderResults();
  renderFeed();
  showSearchError(null);
  clearRunNotes();
  $('searchProgress').hidden = false;
  $('progressFill').style.width = '0%';
  $('progressText').textContent = 'starting…';
  $('coverageText').textContent = '';
  $('outputText').textContent = '';

  let started;
  try {
    const r = await fetch('/api/search', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(query),
    });
    started = await r.json();
    if (!r.ok) {
      if (started.kind === 'confirm' && started.preflight) {
        $('searchProgress').hidden = true;
        if (await askToConfirm(started.preflight)) {
          query.confirmed = true;
          return postSearch(query);
        }
        return;
      }
      throw new Error(started.error || r.statusText);
    }
  } catch (e) {
    $('searchProgress').hidden = true;
    showSearchError(String(e.message || e));
    return;
  }

  Search.id = started.id;
  Search.running = true;
  Search.finalSave = null;
  try { localStorage.setItem('seedlab.searchRun', Search.id); } catch (e) { /* private mode */ }
  $('btnRunSearch').disabled = true;
  $('btnCancelSearch').disabled = false;
  selectTab('Search');
  openStream();
}

function askToConfirm(pf) {
  return new Promise((resolve) => {
    const dlg = $('confirmDialog');
    const lines = [];
    for (const x of pf.confirmations) lines.push(x);
    if (pf.estimate.verdict === 'confirm') for (const x of pf.estimate.reasons) lines.push(x);
    listInto($('confirmList'), lines);
    kv($('confirmKv'), [
      ['seeds', nf(pf.seeds) + '  ·  ' + pct(pf.fractionOfSpace) + ' of the space'],
      ['time', duration(pf.estimate.secondsLow) + ' – ' + duration(pf.estimate.secondsHigh)],
      ['disk', pf.output.path
        ? bytes(pf.estimate.diskLowBytes) + ' – ' + bytes(pf.estimate.diskHighBytes) + ' of ' + bytes(pf.freeBytes) + ' free'
        : 'nothing is written'],
      ['workers', pf.runtime.workers + ' in ' + pf.runtime.mode + ' mode'],
    ]);
    $('confirmPlan').textContent = (pf.plan || []).join('\n');

    const done = (ok) => {
      $('btnConfirmRun').removeEventListener('click', yes);
      $('btnConfirmCancel').removeEventListener('click', no);
      dlg.removeEventListener('close', onClose);
      try { dlg.close(); } catch (e) { /* already closed */ }
      resolve(ok);
    };
    const yes = () => done(true);
    const no = () => done(false);
    const onClose = () => done(false);
    $('btnConfirmRun').addEventListener('click', yes);
    $('btnConfirmCancel').addEventListener('click', no);
    dlg.addEventListener('close', onClose);
    dlg.showModal();
  });
}

/**
 * Rejoin the run this browser last started, if the server still has it.
 *
 * <p>A scan can run for hours and the page can be reloaded, put to sleep or closed by accident in the
 * middle of one. The server already keeps every event of a run for replay, so rejoining costs one GET
 * and gives back the plan, the best-of table and - if it is still going - the live progress and the
 * Stop button. A run the server has forgotten (it keeps four) just clears the remembered id.</p>
 */
async function rejoinSearch() {
  let id = null;
  try { id = localStorage.getItem('seedlab.searchRun'); } catch (e) { return; }
  if (!id) return;

  let state;
  try {
    state = await getJson('/api/search/' + encodeURIComponent(id));
  } catch (e) {
    try { localStorage.removeItem('seedlab.searchRun'); } catch (e2) { /* private mode */ }
    return;
  }

  Search.id = id;
  const running = state.progress && state.progress.status === 'running';
  Search.running = running;
  // The done event is frozen when the run ends; a Retry saving that worked afterwards changes the
  // server's state and not that event, so an ended run's save box is drawn from this (2026-09-24).
  Search.finalSave = running ? null : (state.save || null);
  $('btnRunSearch').disabled = running;
  $('btnCancelSearch').disabled = !running;
  $('searchProgress').hidden = false;
  showSearchError(running
    ? 'rejoined the search this tab started; it is still running on the server.'
    : 'showing the last search this tab ran. Run another to replace it.', true);
  // The server replays the run's warnings to a tab that rejoins, so the list starts empty here and
  // is filled by that replay - never from the done event as well, or each would be listed twice. (Since
  // 2026-09-24 the replay keeps every warning and the done event however long the run was; it used to
  // drop both after 4,000 events.)
  clearRunNotes();
  openStream();
}

function openStream() {
  const es = new EventSource('/api/search/' + Search.id + '/stream');
  Search.es = es;

  es.addEventListener('started', (ev) => {
    const d = JSON.parse(ev.data);
    Search.queryJson = d.queryJson || '';
    Search.started = d;
    if (d.preflight) {
      Search.pf = d.preflight;
      renderPreflightPlan(d.preflight);
      renderGridTable(d.preflight);
    }
    renderPlan(d);
  });

  es.addEventListener('progress', (ev) => renderProgress(JSON.parse(ev.data), false));

  es.addEventListener('stage', (ev) => {
    const d = JSON.parse(ev.data);
    const box = $('funnelBox');
    box.hidden = false;
    box.textContent = 'Stage ' + d.stage + ' of ' + d.of + ': measuring '
      + (d.measuring || []).join(', ') + '; deferring ' + (d.deferring || []).join(', ')
      + ' until stage 2.  ' + nf(d.seeds) + ' seeds.';
  });

  es.addEventListener('funnelGate', (ev) => {
    const d = JSON.parse(ev.data);
    const box = $('funnelBox');
    box.hidden = false;
    box.textContent = (d.lines || []).join('\n');
  });

  es.addEventListener('result', (ev) => {
    Search.feed.unshift(JSON.parse(ev.data));
    if (Search.feed.length > 12) Search.feed.length = 12;
    renderFeed();
  });

  // The authoritative table. It is the engine's own (score desc, seed asc) best-of list over the
  // records it emitted, not something the page re-sorted - so it matches 'vseed search' exactly.
  es.addEventListener('top', (ev) => {
    Search.top = JSON.parse(ev.data).results || [];
    renderResults();
  });

  // A warning the run gave and went on from - a checkpoint another program held, saved again later.
  // Each one is ADDED to the list: one replacing the other is how a warning used to vanish. A stream
  // that reconnects is replayed the run from its start, so each is listed once, by time and text.
  es.addEventListener('warning', (ev) => {
    const w = JSON.parse(ev.data);
    addRunWarning(w.message, w.atUtc);
  });

  es.addEventListener('done', (ev) => {
    const d = JSON.parse(ev.data);
    // For a tab that rejoined an ended run, the save as the server has it now wins over the event.
    const fin = Search.finalSave;
    const shown = fin
      ? Object.assign({}, d, { checkpointError: fin.checkpointError, checkpointPath: fin.checkpointPath,
        resumeCommand: fin.resumeCommand })
      : d;
    renderProgress(shown, true);
    stopStream();
    if (d.status === 'failed') showSearchError(d.message);
    if (shown.checkpointError) showSaveError(false, shown.checkpointError.message);
    else if (d.checkpointError && shown.checkpointPath) {
      showSaveError(true, shown.checkpointPath + ' was saved again after the run ended, so a resume continues from where it stopped.');
    }
  });

  // The browser reconnects a stream that closed without a done event, for as long as the tab is open.
  // That is right while the run goes on (a dropped connection), and wrong once the server has no such
  // run (it was restarted, or has since run newer searches) or the run has ended: then the stream is
  // closed and the run's last state drawn from GET /api/search/{id} (review of 2026-09-24).
  es.onerror = () => { checkLostStream(es); };
}

async function checkLostStream(es) {
  if (Search.checking || Search.es !== es) return;
  Search.checking = true;
  try {
    const r = await fetch('/api/search/' + encodeURIComponent(Search.id), { headers: { 'Accept': 'application/json' } });
    if (Search.es !== es) return;
    if (r.status === 404) {
      stopStream();
      showSearchError('this server no longer knows that search (it was restarted, or has run newer searches since). '
        + 'A checkpoint it saved is still on disk: run the query again with --resume, or press Find seeds.', true);
      return;
    }
    if (!r.ok) return;
    const state = await r.json();
    const p = state.progress || {};
    if (p.status && p.status !== 'running') {
      stopStream();
      renderProgress(Object.assign({}, p, state.save || {}), true);
      if (state.save && state.save.checkpointError) showSaveError(false, state.save.checkpointError.message);
    }
  } catch (e) {
    // The server itself is gone; the browser keeps trying, and a restarted server answers 404 above.
  } finally {
    Search.checking = false;
  }
}

function renderPlan(d) {
  $('planCard').hidden = false;
  const rows = [
    ['range', nf(d.from) + ' … ' + nf(d.to) + '  (' + nf(d.rangeSeeds) + ' seeds)'],
    // Singulars spelled out: the automatic size makes "1 block" and "blocks of 1" routine.
    ['this run visits', nf(d.limit) + (d.limit === 1 ? ' seed in ' : ' seeds in ') + nf(d.blocks)
      + (d.blocks === 1 ? ' block of ' : ' blocks of ') + d.blockSize + (d.blockSize === 1 ? ' seed' : '')],
    ['share of the space', pct(d.fractionOfSpace) + ' of all 4,294,967,296 worlds'],
    ['order', d.order + ', key ' + d.key + '  — same key, same sequence, every time'],
    ['grid', 'G' + nf(d.grid, 0) + (d.gridIsGameGrid ? '  — the grid the game itself samples' : '')],
    ['region evaluated', d.regionRadiusM >= 10500 ? 'the whole world (10,500 m)'
      : 'a disc of ' + metres(d.regionRadiusM, 0) + '  — exact for these goals'],
    ['highest tier', d.maxTier],
    ['workers', String(d.threads)],
    ['keeps', d.keepAll ? 'every match' : 'the best ' + nf(d.keep)],
    ['query', 'sha-256 ' + String(d.queryHash).slice(0, 16) + '…'],
  ];
  if (d.wallSeconds > 0) rows.push(['time budget', duration(d.wallSeconds)]);
  if (d.outPath) rows.push(['results file', d.outPath, 'wrap']);
  rows.push(['checkpoint', d.checkpoint, 'wrap']);
  if (d.cleanedOrphans && d.cleanedOrphans.length) {
    rows.push(['cleaned up', d.cleanedOrphans.length + ' orphaned temp file(s) a killed run had left', 'wrap']);
  }
  kv($('planKv'), rows);

  if (d.warnings && d.warnings.length) showSearchError(d.warnings.join('  '), true);
  for (const u of (d.unsatisfiable || [])) {
    if (u.importance === 'nice') showSearchError('goal ' + u.id + ' can never be satisfied: ' + u.reason, true);
  }
}

function pct(f) {
  if (!isFinite(f) || f <= 0) return '0 %';
  const p = f * 100;
  if (p >= 1) return nf(p, 2) + ' %';
  if (p >= 0.0001) return p.toPrecision(3) + ' %';
  return p.toExponential(2) + ' %';
}

// MetricCatalog's Unit enum serialises as its member name lower-cased, so Unit.SquareMetres arrives
// as "squaremetres". This used to test for "square_metres", which never matched: every area figure -
// a goal's threshold in the plan table, a measured value in a result's tooltip - fell through to the
// count branch and was printed as a raw m² integer. Both spellings are accepted now so the fix cannot
// be undone by a rename at either end.
function isArea(unit) { return unit === 'squaremetres' || unit === 'square_metres'; }

function fmtUnit(v, unit) {
  if (unit === 'metres') return metres(v, 0);
  if (isArea(unit)) return nf(v / 1e6, 2) + ' km²';
  if (unit === 'fraction') return nf(v * 100, 2) + ' %';
  return nf(v, 0);
}

function renderProgress(p, final) {
  const limit = p.limit || 0;
  const scanned = p.scanned || 0;
  const frac = limit ? clamp(scanned / limit, 0, 1) : (final ? 1 : 0);
  $('progressFill').style.width = (frac * 100).toFixed(1) + '%';
  // A final rate the server says is not the machine's - fewer workers had blocks than there were
  // workers, or it is a funnel's cheap first stage - carries the server's own label and is not
  // extrapolated to the whole space or kept as "measured here" (the terminal prints the same label).
  const machineRate = !final || p.rateIsMachine !== false;
  $('progressText').textContent = (final ? p.status + ' · ' : '')
    + nf(scanned) + ' of ' + nf(limit) + ' seeds  ·  ' + nf(p.passed) + ' passed  ·  '
    + nf(p.seedsPerSecond, 1) + ' seeds/s' + (final && p.rateNote ? ' ' + p.rateNote : '')
    + '  ·  ' + duration(p.elapsedS)
    + (!final && p.etaSeconds != null ? '  ·  ' + duration(p.etaSeconds) + ' left' : '')
    + (final && p.message ? '  ·  ' + p.message : '');

  const covered = p.fractionCovered != null ? p.fractionCovered : (scanned / 4294967296);
  let line = 'covered ' + pct(covered) + ' of the 4,294,967,296-world space';
  if (machineRate && p.seedsPerSecond > 0) line += '  ·  the whole space at this rate: ' + duration(4294967296 / p.seedsPerSecond);
  if (p.suppressedResults > 0) line += '  ·  ' + nf(p.suppressedResults) + ' finds not shown in the ticker (the table is complete)';
  $('coverageText').textContent = line;

  // Decision 10's output half of the live line: kept, bytes written, the projected final size (an
  // approximation, and labelled one) and what is left on the volume.
  const out = [];
  if (p.kept != null) out.push(nf(p.kept) + ' kept');
  if (p.resultBytes != null && p.resultBytes >= 0) {
    // A bounded run rewrites its file from the kept heap at each flush, so 0 B on disk mid-block is
    // the truth rather than a missing number, and saying which it is costs one word.
    out.push(bytes(p.resultBytes) + (p.boundedRewrites && !final ? ' on disk (rewritten at each flush)' : ' written'));
  }
  if (p.projectedBytes != null) out.push('~' + bytes(p.projectedBytes) + ' projected (approx.)');
  if (p.freeBytes != null && p.freeBytes >= 0) out.push(bytes(p.freeBytes) + ' free');
  if (final && p.resultLine) out.push(p.resultLine);
  if (final && p.resultsPath) out.push('→ ' + p.resultsPath);
  if (final && p.resumeCommand) out.push('resume: ' + p.resumeCommand);
  if (final && p.repairedBytes > 0) out.push('a torn tail of ' + bytes(p.repairedBytes) + ' was truncated back to the checkpoint');
  $('outputText').textContent = out.join('  ·  ');

  if (final) {
    if (machineRate && p.seedsPerSecond > 0) Search.meta.measuredSeedsPerSecond = p.seedsPerSecond;
    renderEngineKv();
    const tiered = Search.top.length && Search.top[0].landKm2 == null;
    // A funnel stopped in stage 1 placed nothing: its scanned count is stage 1's, not a set of results.
    $('resultNote').textContent = (p.stage === 1
      ? 'The run stopped during stage 1, so stage 2 never placed a seed and there are no results — the line above says why.'
      : p.complete
      ? 'The whole requested range was evaluated.'
      : 'The run stopped before the range was exhausted, so these are the best of ' + nf(scanned)
        + ' seeds, not of the range.')
      + (tiered ? '  The land and largest-island columns are empty because no goal in this query '
        + 'needed heights, so the run never measured them — that is the tiering working, not a gap.' : '');
  }
}

function stopStream() {
  if (Search.es) { Search.es.close(); Search.es = null; }
  Search.running = false;
  $('btnRunSearch').disabled = false;
  $('btnCancelSearch').disabled = true;
}

async function cancelSearch() {
  if (!Search.id) return;
  $('btnCancelSearch').disabled = true;
  $('progressText').textContent = 'stopping at the next block boundary…';
  try { await fetch('/api/search/' + Search.id + '/cancel', { method: 'POST' }); } catch (e) { /* gone */ }
}

function clearRunNotes() {
  const list = $('searchWarnings');
  list.textContent = '';
  list.hidden = true;
  Search.warned = new Set();
  $('saveError').hidden = true;
  $('saveError').classList.remove('is-saved');
  $('saveRetryNote').textContent = '';
  $('btnRetrySave').disabled = false;
  $('btnRetrySave').hidden = false;
}

function addRunWarning(msg, atUtc) {
  if (!msg) return;
  const key = (atUtc || '') + '|' + msg;
  if (Search.warned.has(key)) return;
  Search.warned.add(key);
  const list = $('searchWarnings');
  list.appendChild(el('li', null, msg));
  list.hidden = false;
}

/**
 * The last checkpoint of a run that stopped early: not saved (with the reason, the file and what a
 * resume will do - the server's own sentence) and the Retry saving button, or saved after a retry.
 */
function showSaveError(saved, msg) {
  const box = $('saveError');
  box.hidden = false;
  box.classList.toggle('is-saved', !!saved);
  $('saveErrorText').textContent = msg || '';
  $('btnRetrySave').hidden = !!saved;
  $('btnRetrySave').disabled = false;
}

/**
 * "Retry saving": the same save, once more, on the server. Close the program that holds the file
 * first; the answer says whether it worked, and the button stays until it has.
 */
async function retrySave() {
  if (!Search.id) return;
  $('btnRetrySave').disabled = true;
  $('saveRetryNote').textContent = 'saving…';
  let body;
  try {
    const r = await fetch('/api/search/' + encodeURIComponent(Search.id) + '/retry-save', { method: 'POST' });
    if (r.status === 404) {
      // It answered: it does not have this run any more (restarted, or it has run newer searches).
      $('saveRetryNote').textContent = 'this server no longer knows that search (it was restarted, or has run newer '
        + 'searches since), so it cannot save it again. The checkpoint described above is still on disk: run the '
        + 'query again with --resume, or press Find seeds.';
      $('btnRetrySave').disabled = true;
      return;
    }
    body = await r.json();
    if (!r.ok && !body.message) throw new Error(body.error || r.statusText);
  } catch (e) {
    $('saveRetryNote').textContent = 'the server did not answer: ' + String(e.message || e);
    $('btnRetrySave').disabled = false;
    return;
  }

  $('saveRetryNote').textContent = '';
  if (body.saved) {
    showSaveError(true, String(body.message || '').replace(/^saved:\s*/, ''));
    // The run's own line had no resume command when nothing on disk could be resumed; now there is one.
    if (body.resumeCommand) $('saveRetryNote').textContent = 'resume: ' + body.resumeCommand;
  } else if (body.running) {
    $('saveRetryNote').textContent = body.message;
    $('btnRetrySave').disabled = false;
  } else {
    showSaveError(false, body.message);
    $('saveRetryNote').textContent = 'still not saved - tried again at ' + new Date().toLocaleTimeString();
  }
}

function showSearchError(msg, info) {
  const n = $('searchError');
  n.hidden = !msg;
  n.textContent = msg || '';
  n.classList.toggle('is-info', !!info);
}

function renderResults() {
  const tb = $('resultTable').querySelector('tbody');
  tb.textContent = '';
  $('resultCount').textContent = Search.top.length ? '(' + Search.top.length + ')' : '';
  $('resultEmpty').hidden = Search.top.length > 0;
  for (const r of Search.top) {
    const tr = el('tr');
    tr.appendChild(el('td', 'num', nf(r.score, 3)));
    const s = el('td', null, r.text);
    s.title = goalSummary(r);
    tr.appendChild(s);
    tr.appendChild(el('td', 'num', String(r.seed)));
    tr.appendChild(el('td', 'num', r.landKm2 == null ? '—' : nf(r.landKm2, 0)));
    tr.appendChild(el('td', 'num', r.largestIslandKm2 == null ? '—' : nf(r.largestIslandKm2, 1)));
    tr.addEventListener('click', () => {
      for (const o of tb.children) o.classList.remove('is-active');
      tr.classList.add('is-active');
      openSeed(String(r.seed)).catch((e) => flash(String(e.message || e)));
    });
    tb.appendChild(tr);
  }
}

function goalSummary(r) {
  const parts = ['int32 ' + r.seed, 'tier ' + r.tier];
  for (const g of r.goals) {
    const v = g.value == null ? (g.bounded ? '> ' + fmtUnit(g.boundRadius, g.unit) : '—') : fmtUnit(g.value, g.unit);
    parts.push(g.id + ' = ' + v + (g.pass ? '' : '  (failed)'));
  }
  return parts.join('\n');
}

function renderFeed() {
  $('feedCard').hidden = Search.feed.length === 0;
  const host = $('resultFeed');
  host.textContent = '';
  for (const r of Search.feed) {
    const row = el('div', 'feed-row');
    row.appendChild(el('span', 'f-score', nf(r.score, 3)));
    const s = el('span', 'f-seed', r.text);
    s.title = goalSummary(r);
    s.addEventListener('click', () => openSeed(String(r.seed)).catch(() => {}));
    row.appendChild(s);
    row.appendChild(el('span', null, r.landKm2 == null ? '' : nf(r.landKm2, 0) + ' km² land'));
    host.appendChild(row);
  }
}

/**
 * The auto-throttle banner.
 *
 * <p>Decision 7: no change of speed is silent. The server records every change the throttle makes and
 * this polls for them at the throttle's own interval. It says what it can honestly say - a running
 * scan keeps the workers it started with, because SearchRun claims its threads once - so the banner
 * names the change AND what it does and does not do to the run in flight.</p>
 */
async function pollRuntime() {
  if (Search.runtimeTimer) clearTimeout(Search.runtimeTimer);
  let r;
  try {
    r = await getJson('/api/runtime');
  } catch (e) {
    Search.runtimeTimer = setTimeout(pollRuntime, 30000);
    return;
  }

  Search.runtime = r;
  Search.meta.defaultThreads = r.defaultThreads;
  renderEngineKv();
  kv($('workerKv'), (r.workerLines || []).map((l) => {
    const i = l.indexOf('  ');
    return i > 0 ? [l.slice(0, i).trim(), l.slice(i).trim()] : ['', l];
  }));

  const last = (r.changes || [])[r.changes.length - 1];
  const banner = $('throttleBanner');
  if (last) {
    banner.hidden = false;
    $('throttleNote').textContent = last.line
      + (Search.running
        ? '  This search keeps the ' + (Search.started ? Search.started.threads : '') + ' workers it started with — a run claims its threads once — and the next one starts in ' + last.to + ' mode.'
        : '  The next search starts in ' + last.to + ' mode.');
  } else if (r.throttled) {
    banner.hidden = false;
    $('throttleNote').textContent = r.throttledBy + ' is running, so searches are in background mode ('
      + r.modeDescription + '). Tick "run at full speed even while Valheim is open" to override it.';
  } else {
    banner.hidden = true;
  }

  Search.runtimeTimer = setTimeout(pollRuntime, Math.max(5000, (r.pollIntervalS || 15) * 1000));
}

// ---------------------------------------------------------------------------- tabs, toolbar, keys
function selectTab(name) {
  for (const n of ['Seed', 'Places', 'Search', 'Point', 'Help']) {
    const on = n === name;
    $('tabBtn' + n).classList.toggle('is-active', on);
    $('tabBtn' + n).setAttribute('aria-selected', on ? 'true' : 'false');
    $('panel' + n).classList.toggle('is-active', on);
    $('panel' + n).hidden = !on;
  }
}

/* The three modes are a radio group: aria-checked, not aria-pressed, because "pressed" can say
   "Mark is on" but cannot say "one of three" - and that turning Ruler on turns Pan off is exactly
   the fact neither a screen reader nor a sighted user could get out of the old colour. The markup
   carries no aria-pressed on these three, deliberately: the CSS keys both attributes and a leftover
   one would paint two states at once. */
const TOOL_SAID = { pan: 'pan and inspect', ruler: 'ruler: click two points', mark: 'mark: click to drop a marker' };

function setTool(t) {
  const changed = MapView.tool !== t;
  MapView.tool = t;
  for (const [id, name] of [['toolPan', 'pan'], ['toolRuler', 'ruler'], ['toolMark', 'mark']]) {
    const on = name === t;
    const b = $(id);
    b.setAttribute('aria-checked', on ? 'true' : 'false');
    // Roving tabindex: a radio group is ONE tab stop, and Tab lands on the mode that is on.
    b.tabIndex = on ? 0 : -1;
  }
  MapView.canvas.classList.toggle('is-crosshair', t !== 'pan');
  if (t !== 'ruler') { MapView.rulerHover = null; }
  // R, M and Esc change the mode while focus is on the canvas, where no button announces anything.
  if (changed) flash(TOOL_SAID[t]);
  MapView.dirty = true;
}

function setLayerPressed() {
  // Places is the only control that has to compute before it can be on, and aria-pressed cannot
  // express that: it reports false during the first run and true during a core -> all upgrade, both
  // of them correct about the markers on screen and both of them silent about the work. So the busy
  // state is read from Places.status itself - it cannot be inferred from aria-pressed, and it
  // cannot be inferred from !!Places.report either. Never claim a state that is not true yet.
  $('layPlaces').setAttribute('aria-pressed', Places.show && !!Places.report ? 'true' : 'false');
  $('layPlaces').setAttribute('aria-busy', Places.status === 'computing' ? 'true' : 'false');
}

function togglePlaces() {
  if (!Places.report && Places.status !== 'computing') {
    // Nothing to show yet: asking for the fast set is what the user meant by pressing this.
    requestPlaces('core');
    return;
  }

  if (!Places.report) {
    // The first run, still computing. There is nothing to show or hide, the button truthfully reads
    // "working", and pollPlaces sets Places.show itself when the report lands - so flipping the flag
    // here changed nothing while announcing "places shown" over a button that said OFF. The button
    // and the toast must not contradict each other.
    flash('still placing - the markers appear when it finishes');
    return;
  }

  Places.show = !Places.show;
  setLayerPressed();
  MapView.dirty = true;
  flash(Places.show ? 'places shown' : 'places hidden');
}

// What each layer is called out loud, for the live region. G/H/B/P are pressed with focus on the
// canvas, where the button that changed is nowhere near the eye or the cursor.
const LAYER_SAID = { grid: 'coordinate grid', shade: 'hillshade', water: 'water depth', game: 'in-game palette' };

function toggleLayer(which) {
  if (which === 'places') { togglePlaces(); return; }
  if (which === 'grid') {
    MapView.showGrid = !MapView.showGrid;
    $('layGrid').setAttribute('aria-pressed', MapView.showGrid);
    flash(LAYER_SAID.grid + (MapView.showGrid ? ' on' : ' off'));
    MapView.dirty = true;
    return;
  }
  if (which === 'shade') MapView.style.shade = !MapView.style.shade;
  if (which === 'water') MapView.style.water = !MapView.style.water;
  if (which === 'game') MapView.style.game = !MapView.style.game;
  $('layShade').setAttribute('aria-pressed', MapView.style.shade);
  $('layWater').setAttribute('aria-pressed', MapView.style.water);
  $('layGame').setAttribute('aria-pressed', MapView.style.game);
  // MapView.style's keys are exactly shade / water / game, so this reaches all three branches.
  flash(LAYER_SAID[which] + (MapView.style[which] ? ' on' : ' off'));
  // Different style, different tiles. The old ones stay in the browser's HTTP cache, so flipping
  // back is instant; only the ones that are actually new are generated.
  for (const [, ac] of MapView.pending) ac.abort();
  MapView.pending.clear();
  MapView.queue.length = 0;
  MapView.dirty = true;
}

function bindKeys() {
  window.addEventListener('keydown', (e) => {
    const t = e.target;
    const typing = t && (t.tagName === 'INPUT' || t.tagName === 'SELECT' || t.tagName === 'TEXTAREA');

    // Alt+1..5 reach the panels from anywhere, including from inside a field - they are the one
    // shortcut a user presses while their hands are already in the goal editor.
    if (e.altKey && !e.ctrlKey && !e.metaKey && e.key >= '1' && e.key <= '5') {
      selectTab(['Seed', 'Places', 'Search', 'Point', 'Help'][+e.key - 1]);
      e.preventDefault();
      return;
    }

    if (typing) return;
    if (e.ctrlKey || e.metaKey || e.altKey) return;

    const step = e.shiftKey ? 240 : 60;
    switch (e.key) {
      case 'ArrowLeft': MapView.cx -= step / MapView.scale; break;
      case 'ArrowRight': MapView.cx += step / MapView.scale; break;
      case 'ArrowUp': MapView.cz += step / MapView.scale; break;
      case 'ArrowDown': MapView.cz -= step / MapView.scale; break;
      case '+': case '=': zoomBy(2); break;
      case '-': case '_': zoomBy(0.5); break;
      case '0': fitWorld(); break;
      case 'r': case 'R': setTool(MapView.tool === 'ruler' ? 'pan' : 'ruler'); MapView.ruler = []; renderRuler(); break;
      case 'm': case 'M':
        if (MapView.cursor) addMarker(MapView.cursor.x, MapView.cursor.z);
        else setTool(MapView.tool === 'mark' ? 'pan' : 'mark');
        break;
      case 'g': case 'G': toggleLayer('grid'); break;
      case 'h': case 'H': toggleLayer('shade'); break;
      case 'b': case 'B': toggleLayer('water'); break;
      case 'p': case 'P': toggleLayer('game'); break;
      case 'l': case 'L': togglePlaces(); break;
      case 'f': requestPlaces('core'); break;
      case 'F': requestPlaces('all'); break;
      case '1': toggleCat('boss'); break;
      case '2': toggleCat('trader'); break;
      case '3': toggleCat('dungeon'); break;
      case '4': toggleCat('feature'); break;
      case 's': case 'S': selectTab('Search'); runSearch(); break;
      case '.': cancelSearch(); break;
      case '/': selectTab('Seed'); $('seedInput').focus(); $('seedInput').select(); break;
      case 'c': case 'C':
        copy(MapView.cx.toFixed(1) + ', ' + MapView.cz.toFixed(1), 'centre copied');
        break;
      case 'Escape':
        if (Places.pick) { Places.pick = null; renderPlacePick(); MapView.dirty = true; }
        setTool('pan'); MapView.ruler = []; renderRuler();
        break;
      case '?': selectTab('Help'); break;
      default: return;
    }
    clampCentre();
    MapView.dirty = true;
    scheduleHash();
    e.preventDefault();
  });
}

// ---------------------------------------------------------------------------- sidebar splitter
/* The map and the panels should not have to fight for the window. The sidebar is draggable and the
   width is remembered per browser; double-clicking the splitter snaps it back. */
function bindSplitter() {
  const sp = $('splitter');
  if (!sp) return;
  let w0 = 0, x0 = 0, dragging = false;

  try {
    const saved = parseInt(localStorage.getItem('seedlab.sidebar'), 10);
    if (saved >= 280 && saved <= 900) document.documentElement.style.setProperty('--sidebar-w', saved + 'px');
  } catch (e) { /* private mode */ }

  sp.addEventListener('pointerdown', (e) => {
    dragging = true;
    sp.setPointerCapture(e.pointerId);
    sp.classList.add('is-dragging');
    w0 = $('sidebar').getBoundingClientRect().width;
    x0 = e.clientX;
    e.preventDefault();
  });
  sp.addEventListener('pointermove', (e) => {
    if (!dragging) return;
    const w = clamp(w0 + (e.clientX - x0), 280, Math.min(900, window.innerWidth - 320));
    document.documentElement.style.setProperty('--sidebar-w', Math.round(w) + 'px');
  });
  const end = (e) => {
    if (!dragging) return;
    dragging = false;
    sp.classList.remove('is-dragging');
    try { sp.releasePointerCapture(e.pointerId); } catch (err) { /* already released */ }
    try { localStorage.setItem('seedlab.sidebar', String(Math.round($('sidebar').getBoundingClientRect().width))); } catch (err) { /* private mode */ }
    resizeCanvas();
  };
  sp.addEventListener('pointerup', end);
  sp.addEventListener('pointercancel', end);
  sp.addEventListener('dblclick', () => {
    document.documentElement.style.setProperty('--sidebar-w', '384px');
    try { localStorage.removeItem('seedlab.sidebar'); } catch (e) { /* private mode */ }
    resizeCanvas();
  });
}

// ---------------------------------------------------------------------------- url hash
let hashTimer = 0;
function scheduleHash() {
  clearTimeout(hashTimer);
  hashTimer = setTimeout(() => {
    if (App.seed === null) return;
    const h = '#seed=' + App.seed + '&x=' + MapView.cx.toFixed(0) + '&z=' + MapView.cz.toFixed(0)
      + '&s=' + MapView.scale.toPrecision(4);
    if (location.hash !== h) history.replaceState(null, '', h);
  }, 400);
}

function readHash() {
  const h = location.hash.replace(/^#/, '');
  if (!h) return null;
  const p = {};
  for (const part of h.split('&')) {
    const i = part.indexOf('=');
    if (i > 0) p[part.slice(0, i)] = decodeURIComponent(part.slice(i + 1));
  }
  return p.seed ? p : null;
}

// ---------------------------------------------------------------------------- stats
async function refreshStats() {
  try {
    const s = await getJson('/api/stats');
    kv($('statsKv'), [
      ['tiles rendered', nf(s.tiles.rendered) + '  in ' + nf(s.tiles.renderSeconds, 2) + ' s'
        + (s.tiles.rendered ? '  (' + nf(1000 * s.tiles.renderSeconds / s.tiles.rendered, 1) + ' ms each)' : '')],
      ['tile cache', nf(s.tiles.cacheEntries) + ' tiles, ' + nf(s.tiles.cacheBytes / 1048576, 1) + ' of '
        + nf(s.tiles.cacheCapacityBytes / 1048576, 0) + ' MiB'],
      ['cache hits', nf(s.tiles.hits) + ' hit / ' + nf(s.tiles.misses) + ' generated / ' + nf(s.tiles.evictions) + ' evicted'],
      ['generators built', nf(s.worldsConstructed)],
      ['uptime', nf(s.uptimeS, 0) + ' s'],
    ]);
  } catch (e) { /* the server is gone; the page still works */ }
}

// ---------------------------------------------------------------------------- boot
async function boot() {
  MapView.canvas = $('map');
  MapView.ctx = MapView.canvas.getContext('2d', { alpha: false });

  App.meta = await getJson('/api/meta');
  App.tiles = App.meta.tiles;
  App.waterLevel = App.meta.world.waterLevelM;
  App.waterEdge = App.meta.world.waterEdgeM;
  for (const b of App.meta.palette.biomes) {
    App.biomeColor[b.name] = b.seedlab;
    App.biomeByIndex[b.index] = { name: b.name, color: b.seedlab };
  }
  readPlaceColors();
  $('dnSwatch').style.background = App.biomeColor['Deep North'] || '#e8f0ff';
  $('chipEngine').textContent = 'vseed ' + App.meta.engine + '  ·  Valheim ' + App.meta.game
    + '  ·  worldGen ' + App.meta.worldGenVersion;

  initSearch();

  resizeCanvas();
  bindMap();
  bindKeys();
  bindSplitter();
  requestAnimationFrame(frame);

  window.addEventListener('resize', resizeCanvas);
  // 'resize' only fires for the WINDOW. The canvas also changes size when a pane or splitter around
  // it moves, and - the case that actually bit - when it goes from zero to its real size after the
  // page has already booted. A ResizeObserver sees all of those; the window listener stays for the
  // device-pixel-ratio change that a monitor switch brings, which the observer does not report.
  if (window.ResizeObserver) new ResizeObserver(resizeCanvas).observe(MapView.canvas);
  $('seedForm').addEventListener('submit', (e) => {
    e.preventDefault();
    const v = $('seedInput').value.trim();
    if (v) openSeed(v).catch((err) => flash(String(err.message || err)));
  });
  $('btnRandom').addEventListener('click', () => {
    const a = new Uint32Array(1);
    crypto.getRandomValues(a);
    openSeed(String(a[0] | 0)).catch((err) => flash(String(err.message || err)));
  });
  $('btnRemeasure').addEventListener('click', measure);
  $('gridSelect').addEventListener('change', measure);
  $('btnResolve').addEventListener('click', resolveTool);
  $('toolInput').addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); resolveTool(); } });

  for (const [id, name] of [['tabBtnSeed', 'Seed'], ['tabBtnPlaces', 'Places'], ['tabBtnSearch', 'Search'],
                            ['tabBtnPoint', 'Point'], ['tabBtnHelp', 'Help']]) {
    $(id).addEventListener('click', () => { selectTab(name); if (name === 'Help') refreshStats(); });
  }
  for (const [id, t] of [['toolPan', 'pan'], ['toolRuler', 'ruler'], ['toolMark', 'mark']]) {
    $(id).addEventListener('click', () => setTool(t));
  }

  /* A radio group is navigated with the arrow keys, and the event is consumed HERE.
     stopPropagation is load-bearing: bindKeys listens on window in the bubble phase and only skips
     INPUT/SELECT/TEXTAREA, so without it the same press would also pan the map. The asymmetry is
     deliberate - arrows keep panning the map everywhere except inside this one group.
     setTool runs before .focus() so the roving tabindex is already 0 on the button we focus. */
  $('toolModes').addEventListener('keydown', (e) => {
    const ids = ['toolPan', 'toolRuler', 'toolMark'];
    const order = ['pan', 'ruler', 'mark'];
    const i = order.indexOf(MapView.tool);
    let j;
    if (e.key === 'ArrowRight' || e.key === 'ArrowDown') j = (i + 1) % order.length;
    else if (e.key === 'ArrowLeft' || e.key === 'ArrowUp') j = (i + order.length - 1) % order.length;
    else if (e.key === 'Home') j = 0;
    else if (e.key === 'End') j = order.length - 1;
    else return;
    setTool(order[j]);
    $(ids[j]).focus();
    e.preventDefault();
    e.stopPropagation();
  });
  $('layGrid').addEventListener('click', () => toggleLayer('grid'));
  $('layShade').addEventListener('click', () => toggleLayer('shade'));
  $('layWater').addEventListener('click', () => toggleLayer('water'));
  $('layGame').addEventListener('click', () => toggleLayer('game'));
  $('layPlaces').addEventListener('click', () => toggleLayer('places'));
  $('btnZoomIn').addEventListener('click', () => zoomBy(2));
  $('btnZoomOut').addEventListener('click', () => zoomBy(0.5));
  $('btnZoomFit').addEventListener('click', fitWorld);
  $('btnAddGoal').addEventListener('click', () => addGoalRow());
  $('btnRunSearch').addEventListener('click', runSearch);
  $('btnCancelSearch').addEventListener('click', cancelSearch);
  $('btnRetrySave').addEventListener('click', retrySave);
  $('btnCopyQuery').addEventListener('click', () => copy(Search.queryJson, 'query file copied — save it and run: vseed search that.json'));
  $('btnDownloadQuery').addEventListener('click', downloadQuery);
  $('btnEvidenceClose').addEventListener('click', () => { try { $('evidenceDialog').close(); } catch (e) { /* already closed */ } });

  $('btnLocCore').addEventListener('click', () => requestPlaces('core'));
  $('btnLocAll').addEventListener('click', () => requestPlaces('all'));
  $('btnLocClear').addEventListener('click', () => { Places.show = false; setLayerPressed(); MapView.dirty = true; });
  $('locLabels').addEventListener('change', () => { Places.labels = $('locLabels').value; MapView.dirty = true; });
  $('locFilter').addEventListener('input', () => { Places.filter = $('locFilter').value; renderPlaceTypes(); });
  $('btnLocAllOn').addEventListener('click', () => { Places.off.clear(); renderPlaceTypes(); MapView.dirty = true; });
  $('btnLocAllOff').addEventListener('click', () => {
    if (Places.report) for (const t of Places.report.types) { if (t.count > 0) Places.off.add(t.prefab); }
    renderPlaceTypes();
    MapView.dirty = true;
  });
  $('btnLocCopy').addEventListener('click', () => { if (Places.pick) copy(Places.pick.inst.x.toFixed(0) + ', ' + Places.pick.inst.z.toFixed(0)); });
  $('btnLocCentre').addEventListener('click', () => {
    if (!Places.pick) return;
    MapView.cx = Places.pick.inst.x;
    MapView.cz = Places.pick.inst.z;
    MapView.scale = Math.max(MapView.scale, 0.35);
    MapView.dirty = true;
    scheduleHash();
  });
  $('btnLocMark').addEventListener('click', () => {
    if (!Places.pick) return;
    const t = Places.report.types[Places.pick.inst.t];
    addMarker(Places.pick.inst.x, Places.pick.inst.z, placeName(t) + (t.candidateSet ? ' (candidate)' : ''));
  });
  $('btnCopyPin').addEventListener('click', () => { if (MapView.pin) copy(MapView.pin.x.toFixed(2) + ', ' + MapView.pin.z.toFixed(2)); });
  $('btnSavePin').addEventListener('click', () => { if (MapView.pin) addMarker(MapView.pin.x, MapView.pin.z); });
  $('btnCentrePin').addEventListener('click', () => { if (MapView.pin) { MapView.cx = MapView.pin.x; MapView.cz = MapView.pin.z; MapView.dirty = true; } });

  const h = readHash();
  let start = h ? h.seed : null;
  if (!start) { try { start = localStorage.getItem('seedlab.lastSeed'); } catch (e) { start = null; } }
  if (!start) start = 'MWd8eV6svz';
  await openSeed(start, { keepView: !!(h && h.x) });
  if (h && h.x) {
    MapView.cx = parseFloat(h.x); MapView.cz = parseFloat(h.z);
    MapView.scale = clamp(parseFloat(h.s) || MapView.scale, MapView.minScale, MapView.maxScale);
    MapView.dirty = true;
  }

  rejoinSearch();

  // Open on something worth looking at. The whole world fitted to the window IS the interesting
  // view - it is the shape of the continent - but an empty one is just a coastline, so the fast
  // boss-and-trader placement is started straight away and the altars appear a moment later. It is
  // under a second, it is the cheapest prefix there is, and it is the question a player opening a
  // seed actually has.
  if (App.meta.locations && App.meta.locations.available) requestPlaces('core');
}

async function resolveTool() {
  const v = $('toolInput').value.trim();
  if (!v) return;
  try {
    const r = await getJson('/api/seed/resolve?q=' + encodeURIComponent(v));
    const open = el('button', 'btn btn-small', 'open this world');
    open.addEventListener('click', () => openSeed(v));
    kv($('toolKv'), [
      ['read as', r.readAs === 'int' ? 'an int32' : 'a seed text'],
      ['int32', String(r.seed)],
      ['as a TEXT it would be', String(r.asText) + (r.ambiguous ? '  ← a different world' : '')],
      ['shortest text', textWithCopy(r.shortestText, '')],
      ['game-style text', textWithCopy(r.gameStyleText, '')],
      ['lanes', 'even ' + r.laneEven + ', odd ' + r.laneOdd + ', combiner ' + r.laneCombiner],
      ['', open],
    ]);
  } catch (e) {
    kv($('toolKv'), [['error', String(e.message || e)]]);
  }
}

function downloadQuery() {
  const text = Search.queryJson || (Search.pf && Search.pf.queryJson) || '';
  if (!text) { flash('nothing to export yet — the verdict has not come back.'); return; }
  const blob = new Blob([text], { type: 'application/json' });
  const url = URL.createObjectURL(blob);
  const a = el('a');
  a.href = url;
  a.download = 'seedlab-query.json';
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 5000);
  flash('query file exported — run it with: vseed search seedlab-query.json');
}

boot().catch((e) => {
  document.body.appendChild(el('pre', null, 'SeedLab could not start: ' + (e.message || e)));
});

})();
