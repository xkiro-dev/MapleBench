/**
 * The map canvas: draws a whole map the way the client composes it, and turns
 * pointer gestures into direct manipulation — everything visible is hoverable,
 * selectable, draggable and editable in place.
 *
 * Draw order, from the measured model (docs/map-data-model.md):
 *   1. backgrounds with front=0, in list order, type 0-7 deciding rx/ry
 *      parallax and cx/cy tiling
 *   2. the layers, in the order the image lists them — NOT 0..7; 749080500.img
 *      ships a layer 8 and the layer list comes from the document. Within a
 *      layer objects draw by their z, then tiles draw by art z and numeric WZ
 *      child order. zM groups platforms/footholds; it is not a draw key.
 *   3. backgrounds with front=1
 *   4. overlays: VR bound, rect zones, footholds, ladders, portals, life,
 *      reactors
 *
 * INTERACTION MODEL (the HaCreator vocabulary, exceeded):
 *   - Navigation: space-hold = grab-pan, middle/right-drag = pan, wheel =
 *     zoom at the cursor, +/- zoom, 0 fits the map, 1 = 100%. Arrows nudge
 *     the selection (Shift = 10px) or pan the view when nothing is selected.
 *   - Hover: a spatial grid over every visible kind answers "what is under
 *     the cursor" in O(bucket) — never a linear scan of 13k tiles — with a
 *     pixel-alpha test on art so hover tracks the drawn pixels, not the
 *     bounding box. Footholds hover as LINES with their whole chain lit.
 *   - Selection: click selects, shift/ctrl-click toggles, drag on empty is a
 *     live marquee (kind-filtered by the overlay toggles and layer
 *     visibility), Ctrl+A selects everything in filter, Esc clears. A
 *     selection of N drags together and lands as ONE undo entry.
 *   - Editing in place: foothold vertices drag (linked coincident endpoints
 *     follow — the invariant the data actually keeps), double-click a segment
 *     inserts a vertex, double-click a free chain end extends the chain,
 *     ladders drag whole or by endpoint, rect zones resize by corner/edge
 *     handles, tiles double-click into a variant picker.
 *
 * Performance: static art is baked into 1024-px world chunks PER LAYER;
 * dragged entries leave the bake and draw live, so a 20-tile drag re-blits
 * chunks + 20 sprites per frame instead of rebaking. The ceiling is 13,279
 * tiles in one map (867145100).
 *
 * Animation, parallax and payload rules are unchanged from the measured
 * phases; see git history for the evidence trail.
 */

const CHUNK = 1024;
const MAX_CHUNKS = 256;
const GRID = 256;              // spatial index bucket, world px
const ALPHA_HIT = 16;          // opacity above this counts as "on the art"
const ALPHA_CACHE_MAX = 48;    // decoded-pixel LRU entries
const ALPHA_MAX_PIXELS = 1 << 21; // art bigger than 2M px hits by box, not pixel
const DRAG_THRESHOLD = 3;      // screen px before a press becomes a drag

const RECT_COLORS = {
  tooltip: { fill: 'rgba(41, 145, 255, 0.18)', stroke: '#62b5ff', badge: '#1268a6' },
  area: { fill: 'rgba(151, 100, 255, 0.16)', stroke: '#b28cff', badge: '#6846a8' },
  buffzone: { fill: 'rgba(255, 166, 61, 0.16)', stroke: '#ffad4f', badge: '#a45d12' },
  swimarea: { fill: 'rgba(55, 205, 198, 0.16)', stroke: '#53d7d0', badge: '#167d78' },
  default: { fill: 'rgba(74, 190, 170, 0.14)', stroke: '#63cfba', badge: '#267d6e' },
};

const rectColors = (kind) => RECT_COLORS[String(kind || '').toLowerCase()] ?? RECT_COLORS.default;

export class MapView {
  /**
   * @param canvas the <canvas> to own
   * @param handlers {
   *   onSelect(selArray), onStatus(text), onViewChanged(),
   *   onMoveMany(items, dx, dy), onMoveFoothold(entry, vertex),
   *   onMoveLadder(entry), onRectResize(entry),
   *   onInsertVertex(entry, x, y), onExtendFoothold(entry, vertex, x, y),
   *   onPlace(x, y), onPlaceLadder(x, y1, y2), onFootholdChain(layer, points),
   *   onTilePicker(sel, screenX, screenY), onHover(info|null)
   * }
   */
  constructor(canvas, handlers) {
    this.canvas = canvas;
    this.ctx = canvas.getContext('2d');
    this.handlers = handlers;
    this.doc = null;

    this.cam = { x: 0, y: 0, scale: 1 };
    this.images = new Map();        // art key -> { img, ok, missing, frames? }
    this.portalIcons = null;        // pt -> { img, w, h, ox, oy }
    this.chunks = new Map();        // "layer|cx,cy" -> canvas | 'empty'
    this.staticItems = [];          // draw-ordered tiles + objs (order = index)
    this.layerOrder = [];
    this.layerItems = new Map();
    this.spineItems = [];
    this.layerVisible = new Map();
    this.overlays = {
      back: true, foothold: true, ladder: true, portal: true,
      life: true, reactor: true, rect: true, vr: true,
    };

    this.anim = { playing: false, start: 0 };
    this.frameCache = new Map();

    // Selection is a LIST — records { kind, entry, item? }. kind is one of
    // tile, obj, back, foothold, ladder, portal, life, reactor, rect.
    this.sel = [];
    this.hover = null;              // same record shape, plus vertex/handle
    this.gesture = null;            // the active pointer gesture
    this.spaceHeld = false;
    this.liveSet = new Set();       // entries excluded from the bake (dragging)
    this.index = new Map();         // spatial grid: "cx,cy" -> [records]
    this.alphaCache = new Map();    // art key -> { w, h, data } LRU
    this.needsDraw = false;
    this.destroyed = false;

    // Authoring modes: armed placement ghost, foothold chain draw, chain
    // extension from a free end, two-click ladders.
    this.placing = null;
    this.placingImg = null;
    this.fhDraw = null;             // { layer, points }
    this.fhExtend = null;           // { entry, vertex } — rubber band from the free end
    this.ladderStart = null;
    this.mouse = null;              // last world position
    this.mouseScreen = null;

    this.resize = this.resize.bind(this);
    window.addEventListener('resize', this.resize);
    this.observer = new ResizeObserver(() => this.resize());
    this.observer.observe(canvas.parentElement ?? canvas);

    canvas.addEventListener('pointerdown', (e) => this.pointerDown(e));
    canvas.addEventListener('pointermove', (e) => this.pointerMove(e));
    canvas.addEventListener('pointerup', (e) => this.pointerUp(e));
    canvas.addEventListener('pointercancel', () => { if (this.gesture) this.abortGesture(); });
    canvas.addEventListener('dblclick', (e) => this.doubleClick(e));
    canvas.addEventListener('wheel', (e) => this.wheel(e), { passive: false });
    canvas.addEventListener('contextmenu', (e) => e.preventDefault());

    this.resize();
    this.loop();
  }

  destroy() {
    this.destroyed = true;
    window.removeEventListener('resize', this.resize);
    this.observer.disconnect();
    this.images.clear();
    this.chunks.clear();
    this.alphaCache.clear();
  }

  /* ============================================================ document */

  setDoc(doc) {
    this.doc = doc;
    this.layerVisible.clear();
    for (const layer of doc?.layers ?? []) this.layerVisible.set(layer.index, true);
    this.rebuildStatic();
    // The chunk cache holds the PREVIOUS document's baked art; drop it so a
    // link stub does not draw the old map's leftovers.
    this.dropChunks();
    this.sel = [];
    this.hover = null;
    this.gesture = null;
    this.liveSet.clear();
    this.ladderStart = null;
    this.fhExtend = null;
    this.loadArt();
    if (this.anim.playing) this.loadFrameArt();
    this.fitToBounds();
    this.invalidate();
  }

  setPortalIcons(list) {
    this.portalIcons = new Map();
    for (const row of list ?? []) {
      const entry = { w: row.w, h: row.h, ox: row.ox, oy: row.oy, img: null };
      if (row.path) {
        const img = new Image();
        img.onload = () => { entry.img = img; this.invalidate(); };
        img.src = `/api/canvas?path=${encodeURIComponent(row.path)}&v=me`;
      }
      this.portalIcons.set(row.pt, entry);
    }
    this.invalidate();
  }

  setLayerVisible(index, on) {
    this.layerVisible.set(index, on);
    if (!on) {
      // Selection never keeps what the eye cannot see.
      this.sel = this.sel.filter((s) => !(s.item && s.item.layer === index));
      this.handlers.onSelect?.(this.sel);
    }
    this.invalidate();
  }

  setOverlay(name, on) {
    this.overlays[name] = on;
    if (!on) {
      const gone = { foothold: 'foothold', ladder: 'ladder', portal: 'portal', life: 'life', reactor: 'reactor', rect: 'rect' }[name];
      if (gone) {
        this.sel = this.sel.filter((s) => s.kind !== gone);
        this.handlers.onSelect?.(this.sel);
      }
    }
    this.invalidate();
  }

  animatedArtCount() {
    let count = 0;
    for (const meta of Object.values(this.doc?.art ?? {})) {
      if (meta.frames?.length > 1) count++;
    }
    return count;
  }

  setPlaying(on) {
    if (this.anim.playing === !!on) return;
    this.anim.playing = !!on;
    this.anim.start = performance.now();
    if (on) this.loadFrameArt();
    this.dropChunks();
    this.invalidate();
  }

  loadFrameArt() {
    for (const [key, meta] of Object.entries(this.doc?.art ?? {})) {
      if (!meta.frames || meta.frames.length < 2) continue;
      const slot = this.images.get(key);
      if (!slot || slot.frames) continue;
      slot.frames = meta.frames.map((frame, i) => {
        if (i === 0) return slot;
        const entry = { img: null, ok: false, missing: false };
        const img = new Image();
        img.onload = () => { entry.img = img; entry.ok = true; this.invalidate(); };
        img.onerror = () => { entry.missing = true; };
        img.src = `/api/canvas?path=${encodeURIComponent(frame.path)}&v=me`;
        return entry;
      });
    }
  }

  currentFrame(key, meta) {
    if (!this.anim.playing || !meta.frames || meta.totalMs <= 0) return 0;
    let index = this.frameCache.get(key);
    if (index !== undefined) return index;
    let t = (performance.now() - this.anim.start) % meta.totalMs;
    index = 0;
    for (let i = 0; i < meta.frames.length; i++) {
      t -= meta.frames[i].delay;
      if (t < 0) { index = i; break; }
    }
    this.frameCache.set(key, index);
    return index;
  }

  /** Local model changed (a drag landed, an undo re-fetched): rebuild. */
  refreshStatic() {
    this.rebuildStatic();
    this.dropChunks();
    this.invalidate();
  }

  select(sel) {
    this.sel = Array.isArray(sel) ? sel : sel ? [sel] : [];
    this.invalidate();
  }

  /* ==================================================== placement + drawing */

  setPlacing(placing) {
    this.placing = placing;
    this.placingImg = null;
    this.ladderStart = null;
    this.fhExtend = null;
    if (placing?.art?.thumbPath) {
      const img = new Image();
      img.onload = () => { this.placingImg = img; this.invalidate(); };
      img.src = `/api/mapedit/thumb?path=${encodeURIComponent(placing.art.thumbPath)}`;
    }
    this.updateCursor();
    this.invalidate();
  }

  snapPoint(wx, wy) {
    const s = this.placing?.getSnap?.();
    if (!s || !(s.pw > 0) || !(s.ph > 0)) return [Math.round(wx), Math.round(wy)];
    return [
      Math.round((wx - s.ax) / s.pw) * s.pw + s.ax,
      Math.round((wy - s.ay) / s.ph) * s.ph + s.ay,
    ];
  }

  beginFootholdDraw(layer) {
    this.fhDraw = { layer, points: [] };
    this.fhExtend = null;
    this.updateCursor();
    this.invalidate();
  }

  cancelFootholdDraw() {
    if (this.fhDraw) {
      this.fhDraw = null;
      this.updateCursor();
      this.invalidate();
    }
  }

  finishFootholdDraw() {
    const draw = this.fhDraw;
    this.fhDraw = null;
    this.updateCursor();
    this.invalidate();
    if (draw && draw.points.length >= 2) {
      this.handlers.onFootholdChain?.(draw.layer, draw.points);
    }
  }

  /** Arms chain extension from a segment's free end; every click adds a
   *  linked segment until Esc/Enter. */
  beginExtend(entry, vertex) {
    this.fhExtend = { entry, vertex };
    this.placing = null;
    this.fhDraw = null;
    this.updateCursor();
    this.handlers.onModeChanged?.();
    this.invalidate();
  }

  cancelExtend() {
    if (this.fhExtend) {
      this.fhExtend = null;
      this.updateCursor();
      this.handlers.onModeChanged?.();
      this.invalidate();
    }
  }

  /** Esc backs out of the current gesture/mode cleanly, innermost first.
   *  Returns true when something was cancelled. */
  cancelMode() {
    if (this.gesture) { this.abortGesture(); return true; }
    if (this.fhExtend) { this.cancelExtend(); return true; }
    if (this.fhDraw) { this.cancelFootholdDraw(); return true; }
    if (this.ladderStart) { this.ladderStart = null; this.invalidate(); return true; }
    return false;
  }

  /** Cancels the running gesture and puts every entry it moved back where it
   *  started — Esc mid-drag means "as if I never grabbed it", not "commit
   *  wherever it happens to be". */
  abortGesture() {
    const g = this.gesture;
    this.gesture = null;
    if (g && g.moved) {
      if (g.kind === 'drag-sel') {
        for (const start of g.starts) this.restoreStart(start);
        this._chainCache = null;
      } else if (g.kind === 'fh-vertex') {
        for (const end of g.linked) {
          if (end.vertex === 1) { end.fh.x1 = g.startX; end.fh.y1 = g.startY; }
          else { end.fh.x2 = g.startX; end.fh.y2 = g.startY; }
        }
        this._chainCache = null;
      } else if (g.kind === 'ladder-end') {
        g.entry.x = g.startX;
        if (g.vertex === 1) g.entry.y1 = g.startY; else g.entry.y2 = g.startY;
      } else if (g.kind === 'rect-resize') {
        g.entry.x1 = g.start.x0; g.entry.y1 = g.start.y0;
        g.entry.x2 = g.start.x1; g.entry.y2 = g.start.y1;
      }
    }
    if (g && g.kind === 'marquee') {
      this.sel = g.base;
      this.handlers.onSelect?.(this.sel);
    }
    this.liveSet.clear();
    this.dropChunks();
    this.updateCursor();
    this.invalidate();
  }

  /** Puts one captured start position back (the inverse of captureStart). */
  restoreStart(start) {
    const s = start.sel;
    const e = s.entry;
    if (s.kind === 'foothold') {
      for (const end of start.linked) {
        if (end.vertex === 1) { end.fh.x1 = end.x; end.fh.y1 = end.y; }
        else { end.fh.x2 = end.x; end.fh.y2 = end.y; }
      }
    } else if (s.kind === 'ladder') {
      e.x = start.x; e.y1 = start.y1; e.y2 = start.y2;
    } else if (s.kind === 'rect') {
      e.x1 = start.x1; e.y1 = start.y1; e.x2 = start.x2; e.y2 = start.y2;
    } else if (s.kind === 'life') {
      e.x = start.x; e.cy = start.y; e.y = start.y;
    } else {
      e.x = start.x; e.y = start.y;
      if (s.item) s.item.rect = this.itemRect(s.item);
    }
  }

  fitToBounds() {
    const b = this.doc?.bounds;
    if (!b) {
      const pts = [...(this.doc?.portals ?? []), ...(this.doc?.life ?? [])];
      if (pts.length) {
        this.cam.x = pts.reduce((s, p) => s + p.x, 0) / pts.length;
        this.cam.y = pts.reduce((s, p) => s + p.y, 0) / pts.length;
      } else {
        this.cam.x = 0; this.cam.y = 0;
      }
      this.cam.scale = 1;
      this.viewChanged();
      return;
    }
    this.cam.x = (b.left + b.right) / 2;
    this.cam.y = (b.top + b.bottom) / 2;
    const w = this.canvas.clientWidth || 800;
    const h = this.canvas.clientHeight || 600;
    const fit = Math.min(w / Math.max(1, b.right - b.left), h / Math.max(1, b.bottom - b.top));
    this.cam.scale = Math.min(1, Math.max(0.1, fit * 0.95));
    this.viewChanged();
  }

  /** Zoom keeping a screen point fixed; anchor defaults to the canvas centre. */
  zoomBy(factor, sx, sy) {
    const ax = sx ?? this.canvas.clientWidth / 2;
    const ay = sy ?? this.canvas.clientHeight / 2;
    const [wx, wy] = this.toWorld(ax, ay);
    this.cam.scale = Math.min(8, Math.max(0.05, this.cam.scale * factor));
    const [nwx, nwy] = this.toWorld(ax, ay);
    this.cam.x += wx - nwx;
    this.cam.y += wy - nwy;
    this.viewChanged();
    this.invalidate();
  }

  zoomTo(scale) {
    const ax = this.canvas.clientWidth / 2;
    const ay = this.canvas.clientHeight / 2;
    const [wx, wy] = this.toWorld(ax, ay);
    this.cam.scale = Math.min(8, Math.max(0.05, scale));
    const [nwx, nwy] = this.toWorld(ax, ay);
    this.cam.x += wx - nwx;
    this.cam.y += wy - nwy;
    this.viewChanged();
    this.invalidate();
  }

  panBy(dx, dy) {
    this.cam.x += dx / this.cam.scale;
    this.cam.y += dy / this.cam.scale;
    this.viewChanged();
    this.invalidate();
  }

  /** Minimap click-to-jump: put a world point at the centre of the view. */
  centerOn(wx, wy) {
    this.cam.x = wx;
    this.cam.y = wy;
    this.viewChanged();
    this.invalidate();
  }

  /** The world rectangle the canvas currently shows, for the minimap. */
  viewportWorld() {
    const [x0, y0] = this.toWorld(0, 0);
    const [x1, y1] = this.toWorld(this.canvas.clientWidth, this.canvas.clientHeight);
    return { x0, y0, x1, y1 };
  }

  setSpaceHeld(on) {
    if (this.spaceHeld === !!on) return;
    this.spaceHeld = !!on;
    if (this.spaceHeld && this.hover) {
      // Modal pan: hit-testing is suspended, so nothing reads as hoverable.
      this.hover = null;
      this.handlers.onHover?.(null);
      this.invalidate();
    }
    this.updateCursor();
  }

  viewChanged() {
    this.handlers.onViewChanged?.();
  }

  /* ============================================================ art */

  loadArt() {
    const art = this.doc?.art ?? {};
    for (const [key, meta] of Object.entries(art)) {
      if (this.images.has(key)) continue;
      const slot = { img: null, ok: false, missing: meta.missing || !meta.path };
      this.images.set(key, slot);
      if (slot.missing) continue;
      const img = new Image();
      img.onload = () => { slot.img = img; slot.ok = true; this.dropChunks(); this.invalidate(); };
      img.onerror = () => { slot.missing = true; };
      img.src = `/api/canvas?path=${encodeURIComponent(meta.path)}&v=me`;
    }
  }

  /* ============================================================ static list */

  rebuildStatic() {
    this.staticItems = [];
    this.layerOrder = [];
    this.layerItems = new Map();
    this.spineItems = [];
    if (!this.doc) { this.index = new Map(); return; }

    for (const layer of this.doc.layers) {
      const mag = Number(layer.tsMag ?? layer.tSMag ?? 1) || 1;
      const items = [];

      // Equal-z objects keep the WZ list order. Their zM is the platform they
      // belong to, not a visual tie-breaker; sorting by it disconnects composed
      // scenery such as Ellinia's signs, bridges and foliage.
      const objs = [...layer.objs].sort((a, b) => a.z - b.z);
      for (const o of objs) {
        const meta = o.art ? this.doc.art[o.art] : null;
        items.push({
          kind: 'obj', layer: layer.index, entry: o, mag: 1, z: o.z,
          animated: !!(meta?.frames?.length > 1) && !o.spine,
        });
        if (o.spine) this.spineItems.push({ x: o.x, y: o.y, layer: layer.index });
      }

      const tiles = [...layer.tiles].sort((a, b) => {
        const za = this.artZ(a.art); const zb = this.artZ(b.art);
        return (za - zb) || ((a.z ?? 0) - (b.z ?? 0));
      });
      for (const t of tiles) {
        items.push({ kind: 'tile', layer: layer.index, entry: t, mag, z: this.artZ(t.art) });
      }

      this.layerOrder.push(layer.index);
      this.layerItems.set(layer.index, items);
      this.staticItems.push(...items);
    }

    for (let i = 0; i < this.staticItems.length; i++) {
      const item = this.staticItems[i];
      item.rect = this.itemRect(item);
      item.order = i;
    }

    this.rebuildIndex();
  }

  artZ(key) {
    const meta = key ? this.doc.art[key] : null;
    return meta ? meta.z : 0;
  }

  itemRect(item) {
    const e = item.entry;
    const meta = e.art ? this.doc.art[e.art] : null;
    if (!meta || meta.missing) return { x: e.x - 16, y: e.y - 16, w: 32, h: 32, missing: true };
    const mag = item.mag || 1;
    const w = meta.w * mag; const h = meta.h * mag;
    const ox = meta.ox * mag; const oy = meta.oy * mag;
    const flipped = (e.f ?? 0) === 1;
    return { x: flipped ? e.x - (w - ox) : e.x - ox, y: e.y - oy, w, h, missing: false };
  }

  /** A life spawn uses cy as its feet/foothold anchor. It shares the same
   * origin, flip and animation model as map art, but remains an editable life
   * overlay rather than becoming part of a map layer. */
  lifeDrawItem(life) {
    const meta = life.art ? this.doc?.art?.[life.art] : null;
    if (!meta || meta.missing) return null;
    const entry = { ...life, y: life.cy || life.y };
    const item = { kind: 'life', entry, mag: 1, animated: !!(meta.frames?.length > 1) };
    item.rect = this.itemRect(item);
    return item;
  }

  lifeRect(life) {
    return this.lifeDrawItem(life)?.rect
      ?? { x: life.x - 14, y: (life.cy || life.y) - 14, w: 28, h: 28, missing: true };
  }

  /* ==================================================== spatial index */

  /**
   * A bucket grid over everything hoverable. Rebuilt whole on any model
   * change — one pass over ~n items, cheap next to a single chunk bake —
   * and read on every mousemove, where it turns "what is under the cursor
   * among 13k things" into a couple of array scans.
   */
  rebuildIndex() {
    this.index = new Map();
    if (!this.doc) return;
    const add = (x0, y0, x1, y1, record) => {
      const c0x = Math.floor(x0 / GRID); const c1x = Math.floor(x1 / GRID);
      const c0y = Math.floor(y0 / GRID); const c1y = Math.floor(y1 / GRID);
      for (let cy = c0y; cy <= c1y; cy++) {
        for (let cx = c0x; cx <= c1x; cx++) {
          const key = `${cx},${cy}`;
          let cell = this.index.get(key);
          if (!cell) { cell = []; this.index.set(key, cell); }
          cell.push(record);
        }
      }
    };

    for (const item of this.staticItems) {
      const r = item.rect;
      add(r.x, r.y, r.x + r.w, r.y + r.h, { type: 'static', item });
    }
    for (const fh of this.doc.footholds) {
      add(Math.min(fh.x1, fh.x2) - 8, Math.min(fh.y1, fh.y2) - 8,
        Math.max(fh.x1, fh.x2) + 8, Math.max(fh.y1, fh.y2) + 8, { type: 'fh', fh });
    }
    for (const l of this.doc.ladders) {
      add(l.x - 10, Math.min(l.y1, l.y2) - 10, l.x + 10, Math.max(l.y1, l.y2) + 10, { type: 'ladder', l });
    }
    for (const p of this.doc.portals) {
      add(p.x - 30, p.y - 40, p.x + 30, p.y + 20, { type: 'portal', p });
    }
    for (const l of this.doc.life) {
      const r = this.lifeRect(l);
      add(r.x, r.y, r.x + r.w, r.y + r.h + 22, { type: 'life', l });
    }
    for (const r of this.doc.reactors) {
      add(r.x - 16, r.y - 16, r.x + 16, r.y + 16, { type: 'reactor', r });
    }
    for (const r of this.doc.rects ?? []) {
      const x0 = Math.min(r.x1, r.x2); const x1 = Math.max(r.x1, r.x2);
      const y0 = Math.min(r.y1, r.y2); const y1 = Math.max(r.y1, r.y2);
      add(x0 - 8, y0 - 8, x1 + 8, y1 + 8, { type: 'rect', r });
    }
  }

  cellAt(wx, wy) {
    return this.index.get(`${Math.floor(wx / GRID)},${Math.floor(wy / GRID)}`) ?? [];
  }

  /** Candidates within a world radius — cells overlapping the square around
   *  the point, deduped. Zoomed far out, the pick tolerance outgrows the
   *  bucket padding; this keeps hits exact at every zoom. */
  cellsAround(wx, wy, radius) {
    const c0x = Math.floor((wx - radius) / GRID); const c1x = Math.floor((wx + radius) / GRID);
    const c0y = Math.floor((wy - radius) / GRID); const c1y = Math.floor((wy + radius) / GRID);
    if (c0x === c1x && c0y === c1y) return this.cellAt(wx, wy);
    const seen = new Set();
    const out = [];
    for (let cy = c0y; cy <= c1y; cy++) {
      for (let cx = c0x; cx <= c1x; cx++) {
        for (const rec of this.index.get(`${cx},${cy}`) ?? []) {
          if (!seen.has(rec)) { seen.add(rec); out.push(rec); }
        }
      }
    }
    return out;
  }

  /* ============================================================ chunks */

  dropChunks() { this.chunks.clear(); }

  chunkFor(layer, cx, cy) {
    const key = `${layer}|${cx},${cy}`;
    let chunk = this.chunks.get(key);
    if (chunk) return chunk === 'empty' ? null : chunk;

    if (this.chunks.size >= MAX_CHUNKS) this.chunks.clear();

    const left = cx * CHUNK; const top = cy * CHUNK;
    const items = this.layerItems.get(layer) ?? [];
    let surface = null; let g = null;
    for (const item of items) {
      if (this.liveSet.has(item.entry)) continue;        // drawn live while dragging
      if (this.anim.playing && item.animated) continue;  // drawn live while playing
      const r = item.rect;
      if (r.x + r.w < left || r.x > left + CHUNK || r.y + r.h < top || r.y > top + CHUNK) continue;
      if (!surface) {
        surface = document.createElement('canvas');
        surface.width = CHUNK; surface.height = CHUNK;
        g = surface.getContext('2d');
        g.imageSmoothingEnabled = false;
        g.translate(-left, -top);
      }
      this.drawItem(g, item);
    }
    this.chunks.set(key, surface ?? 'empty');
    return surface;
  }

  drawItem(g, item) {
    const e = item.entry;
    const r = item.rect;
    const meta = e.art ? this.doc.art[e.art] : null;
    const slot = e.art ? this.images.get(e.art) : null;
    if (!slot || slot.missing || !slot.ok) {
      g.strokeStyle = slot?.missing ? '#e0409a' : '#666';
      g.strokeRect(r.x + 0.5, r.y + 0.5, r.w - 1, r.h - 1);
      return;
    }

    let img = slot.img;
    let fx = 0; let fy = 0; let fw = r.w; let fh = r.h;
    if (meta?.frames?.length > 1) {
      const index = this.currentFrame(e.art, meta);
      const frame = meta.frames[index];
      const frameSlot = slot.frames ? slot.frames[index] : (index === 0 ? slot : null);
      if (!frameSlot?.ok) {
        if (index === 0) return;
        img = slot.img;
        fx = meta.frames[0].dx * item.mag; fy = meta.frames[0].dy * item.mag;
        fw = meta.frames[0].w * item.mag; fh = meta.frames[0].h * item.mag;
      } else {
        img = frameSlot.img;
        fx = frame.dx * item.mag; fy = frame.dy * item.mag;
        fw = frame.w * item.mag; fh = frame.h * item.mag;
      }
    }

    if ((e.f ?? 0) === 1) {
      g.save();
      g.translate(r.x + r.w, r.y);
      g.scale(-1, 1);
      g.drawImage(img, fx, fy, fw, fh);
      g.restore();
    } else {
      g.drawImage(img, r.x + fx, r.y + fy, fw, fh);
    }
  }

  /* ============================================================ frame loop */

  invalidate() { this.needsDraw = true; }

  loop() {
    if (this.destroyed) return;
    if (this.needsDraw || this.anim.playing) {
      this.needsDraw = false;
      try { this.draw(); } catch (err) { console.error('map draw failed', err); }
    }
    requestAnimationFrame(() => this.loop());
  }

  resize() {
    const host = this.canvas.parentElement;
    if (!host) return;
    const dpr = window.devicePixelRatio || 1;
    const w = host.clientWidth; const h = host.clientHeight;
    if (w === 0 || h === 0) return;
    this.canvas.width = Math.round(w * dpr);
    this.canvas.height = Math.round(h * dpr);
    this.canvas.style.width = `${w}px`;
    this.canvas.style.height = `${h}px`;
    this.dpr = dpr;
    this.invalidate();
  }

  toScreen(wx, wy) {
    const { x, y, scale } = this.cam;
    return [
      (wx - x) * scale + this.canvas.clientWidth / 2,
      (wy - y) * scale + this.canvas.clientHeight / 2,
    ];
  }

  toWorld(sx, sy) {
    const { x, y, scale } = this.cam;
    return [
      (sx - this.canvas.clientWidth / 2) / scale + x,
      (sy - this.canvas.clientHeight / 2) / scale + y,
    ];
  }

  draw() {
    const ctx = this.ctx;
    const w = this.canvas.clientWidth; const h = this.canvas.clientHeight;
    ctx.setTransform(this.dpr || 1, 0, 0, this.dpr || 1, 0, 0);
    ctx.clearRect(0, 0, w, h);
    ctx.fillStyle = '#1b2330';
    ctx.fillRect(0, 0, w, h);
    if (!this.doc) return;

    this.frameCache.clear();
    this._backBadges = [];

    if (this.overlays.back) this.drawBacks(ctx, false);
    this.drawStatic(ctx);
    if (this.liveSet.size) {
      // Dragged art, live, over its layer-mates.
      ctx.save();
      this.applyCamera(ctx);
      for (const item of this.staticItems) {
        if (this.liveSet.has(item.entry)) this.drawItem(ctx, item);
      }
      ctx.restore();
    }
    if (this.overlays.back) this.drawBacks(ctx, true);

    this.drawSpineBadges(ctx);
    this.drawOverlays(ctx);
    this.drawHover(ctx);
    this.drawSelection(ctx);
    this.drawMarquee(ctx);
    this.drawPlacementGhost(ctx);
    this.drawFootholdDraft(ctx);
    this.drawExtendDraft(ctx);
    this.drawLadderDraft(ctx);
  }

  drawLadderDraft(ctx) {
    if (!this.ladderStart || !this.mouse) return;
    const [ax, ay] = this.toScreen(this.ladderStart.x, this.ladderStart.y);
    const [, by] = this.toScreen(this.ladderStart.x, Math.round(this.mouse[1]));
    ctx.save();
    ctx.strokeStyle = '#5ec8e6';
    ctx.lineWidth = 3;
    ctx.setLineDash([6, 4]);
    ctx.beginPath();
    ctx.moveTo(ax, ay); ctx.lineTo(ax, by);
    ctx.stroke();
    ctx.setLineDash([]);
    ctx.fillStyle = '#5ec8e6';
    ctx.fillRect(ax - 3, ay - 3, 6, 6);
    ctx.restore();
  }

  drawPlacementGhost(ctx) {
    if (!this.placing || !this.mouse) return;
    const [gx, gy] = this.snapPoint(this.mouse[0], this.mouse[1]);
    const [sx, sy] = this.toScreen(gx, gy);
    const s = this.cam.scale;
    ctx.save();
    ctx.globalAlpha = 0.6;
    const art = this.placing.art;
    if (art && this.placingImg) {
      ctx.drawImage(this.placingImg,
        sx - art.ox * s, sy - art.oy * s,
        this.placingImg.width * s, this.placingImg.height * s);
    } else {
      ctx.strokeStyle = '#7fd0ff';
      ctx.setLineDash([4, 3]);
      if (this.placing.kind === 'life') {
        ctx.beginPath();
        ctx.moveTo(sx, sy - 9); ctx.lineTo(sx + 8, sy); ctx.lineTo(sx, sy + 9); ctx.lineTo(sx - 8, sy);
        ctx.closePath(); ctx.stroke();
      } else {
        ctx.strokeRect(sx - 10, sy - 10, 20, 20);
      }
    }
    ctx.globalAlpha = 1;
    ctx.fillStyle = '#7fd0ff';
    ctx.fillRect(sx - 2, sy - 2, 4, 4);
    ctx.restore();
  }

  drawFootholdDraft(ctx) {
    if (!this.fhDraw) return;
    const points = this.fhDraw.points;
    ctx.save();
    ctx.strokeStyle = '#ffe066';
    ctx.fillStyle = '#ffe066';
    ctx.lineWidth = 2;
    ctx.beginPath();
    points.forEach((p, i) => {
      const [sx, sy] = this.toScreen(p.x, p.y);
      if (i === 0) ctx.moveTo(sx, sy); else ctx.lineTo(sx, sy);
    });
    if (points.length && this.mouse) {
      const [sx, sy] = this.toScreen(Math.round(this.mouse[0]), Math.round(this.mouse[1]));
      ctx.lineTo(sx, sy);
    }
    ctx.stroke();
    for (const p of points) {
      const [sx, sy] = this.toScreen(p.x, p.y);
      ctx.fillRect(sx - 3, sy - 3, 6, 6);
    }
    ctx.restore();
  }

  /** The chain-extension rubber band, from the free end to the cursor. */
  drawExtendDraft(ctx) {
    if (!this.fhExtend || !this.mouse) return;
    const fh = this.fhExtend.entry;
    const ax = this.fhExtend.vertex === 2 ? fh.x2 : fh.x1;
    const ay = this.fhExtend.vertex === 2 ? fh.y2 : fh.y1;
    const [sx, sy] = this.toScreen(ax, ay);
    const [mx, my] = this.toScreen(Math.round(this.mouse[0]), Math.round(this.mouse[1]));
    ctx.save();
    ctx.strokeStyle = '#ffe066';
    ctx.lineWidth = 2;
    ctx.setLineDash([6, 4]);
    ctx.beginPath();
    ctx.moveTo(sx, sy); ctx.lineTo(mx, my);
    ctx.stroke();
    ctx.setLineDash([]);
    ctx.fillStyle = '#ffe066';
    ctx.fillRect(sx - 4, sy - 4, 8, 8);
    ctx.restore();
  }

  applyCamera(ctx) {
    const w = this.canvas.clientWidth; const h = this.canvas.clientHeight;
    ctx.translate(w / 2, h / 2);
    ctx.scale(this.cam.scale, this.cam.scale);
    ctx.translate(-this.cam.x, -this.cam.y);
  }

  drawStatic(ctx) {
    const w = this.canvas.clientWidth; const h = this.canvas.clientHeight;
    const [left, top] = this.toWorld(0, 0);
    const [right, bottom] = this.toWorld(w, h);

    ctx.save();
    this.applyCamera(ctx);
    ctx.imageSmoothingEnabled = this.cam.scale < 1;

    const c0x = Math.floor(left / CHUNK); const c1x = Math.floor(right / CHUNK);
    const c0y = Math.floor(top / CHUNK); const c1y = Math.floor(bottom / CHUNK);

    for (const layer of this.layerOrder) {
      if (!this.layerVisible.get(layer)) continue;
      for (let cy = c0y; cy <= c1y; cy++) {
        for (let cx = c0x; cx <= c1x; cx++) {
          const chunk = this.chunkFor(layer, cx, cy);
          if (chunk) ctx.drawImage(chunk, cx * CHUNK, cy * CHUNK);
        }
      }
      if (this.anim.playing) {
        for (const item of this.layerItems.get(layer) ?? []) {
          if (!item.animated || this.liveSet.has(item.entry)) continue;
          const r = item.rect;
          if (r.x + r.w < left || r.x > right || r.y + r.h < top || r.y > bottom) continue;
          this.drawItem(ctx, item);
        }
      }
    }
    ctx.restore();
  }

  drawSpineBadges(ctx) {
    const badges = [];
    for (const s of this.spineItems) {
      if (this.layerVisible.get(s.layer)) badges.push(this.toScreen(s.x, s.y));
    }
    badges.push(...(this._backBadges ?? []));
    if (!badges.length) return;
    ctx.save();
    ctx.font = '10px system-ui, sans-serif';
    ctx.textAlign = 'center';
    for (const [sx, sy] of badges) {
      const label = 'spine';
      const width = ctx.measureText(label).width + 8;
      ctx.fillStyle = 'rgba(20, 26, 36, 0.85)';
      ctx.fillRect(sx - width / 2, sy - 8, width, 14);
      ctx.strokeStyle = '#8a6fe8';
      ctx.strokeRect(sx - width / 2 + 0.5, sy - 7.5, width - 1, 13);
      ctx.fillStyle = '#b9a6ff';
      ctx.fillText(label, sx, sy + 3);
    }
    ctx.restore();
  }

  /** World rect of a non-tiling background at the current camera, or null.
   *  The same parallax math drawBacks uses — hover/selection track exactly
   *  what is drawn. */
  backRect(back) {
    const meta = back.art ? this.doc.art[back.art] : null;
    if (!meta || meta.missing) return null;
    const type = Number(back.type) || 0;
    if (type !== 0) return null;   // tiling/scrolling backs cover everything
    const [wl, wt] = this.toWorld(0, 0);
    const bx = back.x - (back.rx * wl) / 100 - (meta.ox ?? 0);
    const by = back.y - (back.ry * wt) / 100 - (meta.oy ?? 0);
    return { x: bx, y: by, w: meta.w, h: meta.h };
  }

  drawBacks(ctx, front) {
    const w = this.canvas.clientWidth; const h = this.canvas.clientHeight;
    const [wl, wt] = this.toWorld(0, 0);
    const [wr, wb] = this.toWorld(w, h);

    for (const back of this.doc.backs) {
      if ((back.front === 1) !== front) continue;
      const meta = back.art ? this.doc.art[back.art] : null;
      const slot = back.art ? this.images.get(back.art) : null;

      const type = Number(back.type) || 0;
      const tileX = type === 1 || type === 3 || type === 4 || type === 6 || type === 7;
      const tileY = type === 2 || type === 3 || type === 5 || type === 6 || type === 7;
      const moveX = type === 4 || type === 6;
      const moveY = type === 5 || type === 7;

      const elapsed = this.anim.playing ? (performance.now() - this.anim.start) / 1000 : 0;
      const bx = moveX
        ? wl + back.x + elapsed * back.rx * 5 - (meta?.ox ?? 0)
        : back.x - (back.rx * wl) / 100 - (meta?.ox ?? 0);
      const by = moveY
        ? wt + back.y + elapsed * back.ry * 5 - (meta?.oy ?? 0)
        : back.y - (back.ry * wt) / 100 - (meta?.oy ?? 0);

      if (back.spine) {
        this._backBadges?.push(this.toScreen(bx + (meta?.ox ?? 0), by + (meta?.oy ?? 0)));
      }
      if (!meta || meta.missing || !slot?.ok) continue;

      const cw = (back.cx > 0 ? back.cx : meta.w);
      const ch = (back.cy > 0 ? back.cy : meta.h);

      let x0 = bx; let x1 = bx + 1;
      if (tileX) {
        x0 = bx + Math.floor((wl - bx) / cw - 1) * cw;
        x1 = wr + cw;
      }
      let y0 = by; let y1 = by + 1;
      if (tileY) {
        y0 = by + Math.floor((wt - by) / ch - 1) * ch;
        y1 = wb + ch;
      }

      let img = slot.img;
      let fdx = 0; let fdy = 0;
      if (meta.frames?.length > 1) {
        const index = this.currentFrame(back.art, meta);
        const frame = meta.frames[index];
        const frameSlot = slot.frames ? slot.frames[index] : (index === 0 ? slot : null);
        if (frameSlot?.ok) {
          img = frameSlot.img;
          fdx = frame.dx; fdy = frame.dy;
        } else if (index !== 0) {
          fdx = meta.frames[0].dx; fdy = meta.frames[0].dy;
        }
      }

      ctx.save();
      this.applyCamera(ctx);
      ctx.globalAlpha = Math.max(0, Math.min(1, (back.a ?? 255) / 255));
      const flipped = (back.f ?? 0) === 1;
      for (let y = y0; y < y1; y += ch) {
        for (let x = x0; x < x1; x += cw) {
          if (flipped) {
            ctx.save();
            ctx.translate(x + meta.w, y);
            ctx.scale(-1, 1);
            ctx.drawImage(img, fdx, fdy);
            ctx.restore();
          } else {
            ctx.drawImage(img, x + fdx, y + fdy);
          }
          if (!tileX && x1 - x0 <= 1) break;
        }
        if (!tileY && y1 - y0 <= 1) break;
      }
      ctx.restore();
    }
  }

  drawOverlays(ctx) {
    const s = this.cam.scale;

    if (this.overlays.vr && this.doc.bounds) {
      const b = this.doc.bounds;
      const [x0, y0] = this.toScreen(b.left, b.top);
      const [x1, y1] = this.toScreen(b.right, b.bottom);
      ctx.save();
      ctx.strokeStyle = this.doc.boundsComputed ? '#c9a227' : '#e6c34a';
      ctx.setLineDash([6, 4]);
      ctx.strokeRect(x0, y0, x1 - x0, y1 - y0);
      ctx.restore();
    }

    if (this.overlays.rect) {
      ctx.save();
      ctx.font = '600 11px system-ui, sans-serif';
      ctx.textBaseline = 'middle';
      for (const r of this.doc.rects ?? []) {
        const [x0, y0] = this.toScreen(Math.min(r.x1, r.x2), Math.min(r.y1, r.y2));
        const [x1, y1] = this.toScreen(Math.max(r.x1, r.x2), Math.max(r.y1, r.y2));
        const colors = rectColors(r.kind);
        ctx.fillStyle = colors.fill;
        ctx.fillRect(x0, y0, x1 - x0, y1 - y0);
        ctx.strokeStyle = colors.stroke;
        ctx.lineWidth = 1.5;
        ctx.strokeRect(x0, y0, x1 - x0, y1 - y0);
        if (s >= 0.4) {
          const label = `${r.kind}/${r.name}`;
          const labelW = Math.ceil(ctx.measureText(label).width) + 12;
          const labelH = 19;
          const lx = Math.max(4, Math.min(x0 + 4, this.canvas.clientWidth - labelW - 4));
          const ly = Math.max(4, Math.min(y0 + 4, this.canvas.clientHeight - labelH - 4));
          ctx.fillStyle = colors.badge;
          ctx.fillRect(lx, ly, labelW, labelH);
          ctx.fillStyle = '#fff';
          ctx.fillText(label, lx + 6, ly + labelH / 2 + 0.5);
        }
      }
      ctx.restore();
    }

    if (this.overlays.foothold) {
      ctx.save();
      ctx.strokeStyle = '#61d872';
      ctx.lineWidth = 1.5;
      ctx.beginPath();
      for (const fh of this.doc.footholds) {
        const [ax, ay] = this.toScreen(fh.x1, fh.y1);
        const [bx, by] = this.toScreen(fh.x2, fh.y2);
        ctx.moveTo(ax, ay); ctx.lineTo(bx, by);
      }
      ctx.stroke();
      if (s >= 0.5) {
        ctx.fillStyle = '#61d872';
        for (const fh of this.doc.footholds) {
          const [ax, ay] = this.toScreen(fh.x1, fh.y1);
          const [bx, by] = this.toScreen(fh.x2, fh.y2);
          ctx.fillRect(ax - 2, ay - 2, 4, 4);
          ctx.fillRect(bx - 2, by - 2, 4, 4);
        }
      }
      ctx.restore();
    }

    if (this.overlays.ladder) {
      ctx.save();
      ctx.strokeStyle = '#5ec8e6';
      ctx.lineWidth = 3;
      ctx.beginPath();
      for (const l of this.doc.ladders) {
        const [ax, ay] = this.toScreen(l.x, l.y1);
        const [, by] = this.toScreen(l.x, l.y2);
        ctx.moveTo(ax, ay); ctx.lineTo(ax, by);
      }
      ctx.stroke();
      ctx.restore();
    }

    if (this.overlays.portal) {
      for (const p of this.doc.portals) {
        const [sx, sy] = this.toScreen(p.x, p.y);
        const icon = this.portalIcons?.get(Number(p.pt));
        if (icon?.img) {
          ctx.drawImage(icon.img, sx - icon.ox * s, sy - icon.oy * s,
            icon.w * s, icon.h * s);
        } else {
          ctx.save();
          ctx.fillStyle = 'rgba(120, 160, 255, 0.8)';
          ctx.beginPath();
          ctx.arc(sx, sy, 8, 0, Math.PI * 2);
          ctx.fill();
          ctx.restore();
        }
      }
    }

    if (this.overlays.life) {
      // Sprites live in world space like map art. Draw the actual stand/idle
      // pose first; the anchor and name remain screen-space editor furniture.
      ctx.save();
      this.applyCamera(ctx);
      for (const l of this.doc.life) {
        const item = this.lifeDrawItem(l);
        const slot = l.art ? this.images.get(l.art) : null;
        if (!item || !slot?.ok) continue;
        if (l.hide) {
          ctx.save();
          ctx.globalAlpha = 0.5;
          this.drawItem(ctx, item);
          ctx.restore();
        } else {
          this.drawItem(ctx, item);
        }
      }
      ctx.restore();

      ctx.save();
      ctx.font = '600 11px system-ui, sans-serif';
      ctx.textAlign = 'center';
      for (const l of this.doc.life) {
        const y = l.cy || l.y;
        const [sx, sy] = this.toScreen(l.x, y);
        const npc = (l.type || '').toLowerCase() === 'n';
        const hasSprite = !!this.lifeDrawItem(l) && !!this.images.get(l.art)?.ok;
        ctx.fillStyle = npc ? '#6f9dff' : '#e0605a';
        ctx.beginPath();
        const marker = hasSprite ? 4 : 7;
        ctx.moveTo(sx, sy - marker); ctx.lineTo(sx + marker, sy);
        ctx.lineTo(sx, sy + marker); ctx.lineTo(sx - marker, sy);
        ctx.closePath();
        ctx.fill();
        if (s >= 0.6) {
          const label = l.name || l.id || '';
          if (label) {
            ctx.lineWidth = 3;
            ctx.lineJoin = 'round';
            ctx.strokeStyle = 'rgba(18, 24, 34, 0.82)';
            ctx.strokeText(label, sx, sy + 18);
            ctx.fillStyle = '#fff';
            ctx.fillText(label, sx, sy + 18);
          }
        }
      }
      ctx.restore();
    }

    if (this.overlays.reactor) {
      ctx.save();
      ctx.fillStyle = '#e6a34a';
      for (const r of this.doc.reactors) {
        const [sx, sy] = this.toScreen(r.x, r.y);
        ctx.fillRect(sx - 5, sy - 5, 10, 10);
      }
      ctx.restore();
    }
  }

  /* ====================================================== hover + selection */

  /** Every foothold in the hovered segment's chain — followed through
   *  prev/next by id within the same layer, forks and all. Cached per doc. */
  chainOf(fh) {
    if (!this._chainCache) this._chainCache = new Map();
    const hit = this._chainCache.get(fh);
    if (hit) return hit;

    const byId = new Map();
    for (const f of this.doc.footholds) {
      if (f.layer !== fh.layer) continue;
      if (!byId.has(f.id)) byId.set(f.id, []);
      byId.get(f.id).push(f);
    }
    const chain = new Set([fh]);
    const queue = [fh];
    while (queue.length) {
      const cur = queue.pop();
      for (const refId of [cur.prev, cur.next]) {
        if (!refId) continue;
        for (const f of byId.get(String(refId)) ?? []) {
          if (!chain.has(f)) { chain.add(f); queue.push(f); }
        }
      }
      // Anyone pointing AT cur (forks join through their neighbour).
      for (const f of this.doc.footholds) {
        if (f.layer !== fh.layer || chain.has(f)) continue;
        if (String(f.prev) === cur.id || String(f.next) === cur.id) {
          chain.add(f); queue.push(f);
        }
      }
    }
    const list = [...chain];
    this._chainCache.set(fh, list);
    return list;
  }

  drawHover(ctx) {
    const h = this.hover;
    if (!h || this.gesture) return;
    if (this.sel.some((s) => s.entry === h.entry)) return; // selection styling wins
    ctx.save();
    ctx.strokeStyle = 'rgba(255, 255, 255, 0.65)';
    ctx.lineWidth = 1.5;

    if (h.kind === 'tile' || h.kind === 'obj') {
      const r = h.item.rect;
      const [x0, y0] = this.toScreen(r.x, r.y);
      ctx.strokeRect(x0 - 1, y0 - 1, r.w * this.cam.scale + 2, r.h * this.cam.scale + 2);
    } else if (h.kind === 'foothold') {
      // The whole chain lit thin, the hovered segment thick — "every line
      // that you step on".
      ctx.strokeStyle = 'rgba(97, 216, 114, 0.45)';
      ctx.lineWidth = 4;
      ctx.beginPath();
      for (const f of this.chainOf(h.entry)) {
        const [ax, ay] = this.toScreen(f.x1, f.y1);
        const [bx, by] = this.toScreen(f.x2, f.y2);
        ctx.moveTo(ax, ay); ctx.lineTo(bx, by);
      }
      ctx.stroke();
      ctx.strokeStyle = 'rgba(255, 255, 255, 0.85)';
      ctx.lineWidth = 3;
      const [ax, ay] = this.toScreen(h.entry.x1, h.entry.y1);
      const [bx, by] = this.toScreen(h.entry.x2, h.entry.y2);
      ctx.beginPath();
      ctx.moveTo(ax, ay); ctx.lineTo(bx, by);
      ctx.stroke();
      if (h.vertex) {
        const vx = h.vertex === 1 ? h.entry.x1 : h.entry.x2;
        const vy = h.vertex === 1 ? h.entry.y1 : h.entry.y2;
        const [cx, cy] = this.toScreen(vx, vy);
        ctx.fillStyle = '#fff';
        ctx.fillRect(cx - 4, cy - 4, 8, 8);
      }
    } else if (h.kind === 'ladder') {
      const [ax, ay] = this.toScreen(h.entry.x, h.entry.y1);
      const [, by] = this.toScreen(h.entry.x, h.entry.y2);
      ctx.lineWidth = 5;
      ctx.strokeStyle = 'rgba(255,255,255,0.5)';
      ctx.beginPath(); ctx.moveTo(ax, ay); ctx.lineTo(ax, by); ctx.stroke();
    } else if (h.kind === 'rect') {
      const r = h.entry;
      const [x0, y0] = this.toScreen(Math.min(r.x1, r.x2), Math.min(r.y1, r.y2));
      const [x1, y1] = this.toScreen(Math.max(r.x1, r.x2), Math.max(r.y1, r.y2));
      ctx.strokeStyle = 'rgba(255,255,255,0.7)';
      ctx.strokeRect(x0, y0, x1 - x0, y1 - y0);
    } else if (h.kind === 'life') {
      const r = this.lifeRect(h.entry);
      const [x0, y0] = this.toScreen(r.x, r.y);
      ctx.strokeRect(x0, y0, r.w * this.cam.scale, r.h * this.cam.scale);
    } else if (h.kind === 'back') {
      const r = this.backRect(h.entry);
      if (r) {
        const [x0, y0] = this.toScreen(r.x, r.y);
        ctx.strokeRect(x0, y0, r.w * this.cam.scale, r.h * this.cam.scale);
      }
    } else {
      const y = h.kind === 'life' ? (h.entry.cy || h.entry.y) : h.entry.y;
      const [sx, sy] = this.toScreen(h.entry.x, y);
      ctx.strokeRect(sx - 13, sy - 13, 26, 26);
    }
    ctx.restore();
  }

  drawSelection(ctx) {
    if (!this.sel.length) return;
    ctx.save();
    ctx.strokeStyle = '#ff9d3b';
    ctx.lineWidth = 2;
    const s = this.cam.scale;
    const handle = (x, y, size = 7) => {
      ctx.fillStyle = '#ff9d3b';
      ctx.fillRect(x - size / 2, y - size / 2, size, size);
      ctx.strokeStyle = '#1b2330';
      ctx.lineWidth = 1;
      ctx.strokeRect(x - size / 2 + 0.5, y - size / 2 + 0.5, size - 1, size - 1);
      ctx.strokeStyle = '#ff9d3b';
      ctx.lineWidth = 2;
    };

    for (const sel of this.sel) {
      if (sel.kind === 'tile' || sel.kind === 'obj') {
        const item = sel.item ?? this.staticItems.find((i) => i.entry === sel.entry);
        const r = item ? item.rect : null;
        if (r) {
          const [x0, y0] = this.toScreen(r.x, r.y);
          const w = r.w * s; const h = r.h * s;
          ctx.strokeRect(x0, y0, w, h);
          if (this.sel.length === 1) {
            handle(x0, y0); handle(x0 + w, y0); handle(x0, y0 + h); handle(x0 + w, y0 + h);
          }
        }
      } else if (sel.kind === 'foothold') {
        const fh = sel.entry;
        const [ax, ay] = this.toScreen(fh.x1, fh.y1);
        const [bx, by] = this.toScreen(fh.x2, fh.y2);
        ctx.beginPath(); ctx.moveTo(ax, ay); ctx.lineTo(bx, by); ctx.stroke();
        handle(ax, ay, 9); handle(bx, by, 9);
      } else if (sel.kind === 'ladder') {
        const l = sel.entry;
        const [ax, ay] = this.toScreen(l.x, l.y1);
        const [, by] = this.toScreen(l.x, l.y2);
        ctx.beginPath(); ctx.moveTo(ax, ay); ctx.lineTo(ax, by); ctx.stroke();
        handle(ax, ay, 9); handle(ax, by, 9);
      } else if (sel.kind === 'rect') {
        const r = sel.entry;
        const [x0, y0] = this.toScreen(Math.min(r.x1, r.x2), Math.min(r.y1, r.y2));
        const [x1, y1] = this.toScreen(Math.max(r.x1, r.x2), Math.max(r.y1, r.y2));
        ctx.strokeRect(x0, y0, x1 - x0, y1 - y0);
        const cx = (x0 + x1) / 2; const cy = (y0 + y1) / 2;
        for (const [hx, hy] of [[x0, y0], [cx, y0], [x1, y0], [x0, cy], [x1, cy], [x0, y1], [cx, y1], [x1, y1]])
          handle(hx, hy);
      } else if (sel.kind === 'life') {
        const r = this.lifeRect(sel.entry);
        const [x0, y0] = this.toScreen(r.x, r.y);
        ctx.strokeRect(x0, y0, r.w * s, r.h * s);
      } else if (sel.kind === 'back') {
        const r = this.backRect(sel.entry);
        if (r) {
          const [x0, y0] = this.toScreen(r.x, r.y);
          ctx.strokeRect(x0, y0, r.w * s, r.h * s);
        }
      } else {
        const y = sel.kind === 'life' ? (sel.entry.cy || sel.entry.y) : sel.entry.y;
        const [sx, sy] = this.toScreen(sel.entry.x, y);
        ctx.strokeRect(sx - 12, sy - 12, 24, 24);
      }
    }
    ctx.restore();
  }

  drawMarquee(ctx) {
    const g = this.gesture;
    if (!g || g.kind !== 'marquee') return;
    const [x0, y0] = this.toScreen(Math.min(g.wx0, g.wx1), Math.min(g.wy0, g.wy1));
    const [x1, y1] = this.toScreen(Math.max(g.wx0, g.wx1), Math.max(g.wy0, g.wy1));
    ctx.save();
    ctx.fillStyle = 'rgba(127, 208, 255, 0.08)';
    ctx.fillRect(x0, y0, x1 - x0, y1 - y0);
    ctx.strokeStyle = 'rgba(127, 208, 255, 0.8)';
    ctx.setLineDash([5, 4]);
    ctx.strokeRect(x0 + 0.5, y0 + 0.5, x1 - x0 - 1, y1 - y0 - 1);
    ctx.restore();
  }

  /* ============================================================ hit testing */

  /** Pixel-alpha test on a static item's art: hover and clicks track the
   *  drawn pixels, not the box. Decoded pixels are cached per art (LRU);
   *  oversized art falls back to the box, missing art IS its box. */
  alphaHit(item, wx, wy) {
    const e = item.entry;
    const meta = e.art ? this.doc.art[e.art] : null;
    const slot = e.art ? this.images.get(e.art) : null;
    if (!meta || meta.missing || !slot?.ok || !slot.img) return true;
    if (meta.w * meta.h > ALPHA_MAX_PIXELS) return true;

    let cached = this.alphaCache.get(e.art);
    if (!cached) {
      try {
        const off = document.createElement('canvas');
        off.width = slot.img.naturalWidth; off.height = slot.img.naturalHeight;
        const g = off.getContext('2d', { willReadFrequently: true });
        g.drawImage(slot.img, 0, 0);
        cached = { w: off.width, h: off.height, data: g.getImageData(0, 0, off.width, off.height).data };
      } catch {
        cached = { w: 0, h: 0, data: null };
      }
      if (this.alphaCache.size >= ALPHA_CACHE_MAX) {
        this.alphaCache.delete(this.alphaCache.keys().next().value);
      }
      this.alphaCache.set(e.art, cached);
    }
    if (!cached.data) return true;

    const r = item.rect;
    let u = (wx - r.x) / r.w;
    const v = (wy - r.y) / r.h;
    if ((e.f ?? 0) === 1) u = 1 - u;
    // Animated art: the composed rect is bigger than frame 0; sample frame 0
    // at its offset inside it (paused shows exactly frame 0).
    let px; let py;
    if (meta.frames?.length > 1) {
      const f0 = meta.frames[0];
      px = Math.floor(u * meta.w - f0.dx);
      py = Math.floor(v * meta.h - f0.dy);
    } else {
      px = Math.floor(u * cached.w);
      py = Math.floor(v * cached.h);
    }
    if (px < 0 || py < 0 || px >= cached.w || py >= cached.h) return false;
    return cached.data[(py * cached.w + px) * 4 + 3] >= ALPHA_HIT;
  }

  /**
   * What is under a screen point, best first. Priority: selection handles,
   * then overlay markers (they draw on top), foothold vertices, foothold
   * lines, ladders, rect-zone edges, then static art topmost-first with the
   * pixel test, then plain (type 0) backgrounds.
   */
  hitTest(sx, sy) {
    const [wx, wy] = this.toWorld(sx, sy);
    const s = this.cam.scale;
    const tol = 8 / s;
    const cell = this.cellsAround(wx, wy, tol + 8);

    // Handles of the current selection first — grabbing a handle must win
    // over whatever sits under it.
    const handleHit = this.hitHandles(sx, sy);
    if (handleHit) return handleHit;

    if (this.overlays.portal) {
      for (let i = this.doc.portals.length - 1; i >= 0; i--) {
        const p = this.doc.portals[i];
        if (Math.abs(wx - p.x) < 20 && Math.abs(wy - p.y) < 30) return { kind: 'portal', entry: p };
      }
    }
    if (this.overlays.life) {
      for (let i = this.doc.life.length - 1; i >= 0; i--) {
        const l = this.doc.life[i];
        const item = this.lifeDrawItem(l);
        const r = item?.rect ?? this.lifeRect(l);
        if (wx < r.x || wx > r.x + r.w || wy < r.y || wy > r.y + r.h) continue;
        if (!item || this.alphaHit(item, wx, wy)) return { kind: 'life', entry: l };
      }
    }
    if (this.overlays.reactor) {
      for (let i = this.doc.reactors.length - 1; i >= 0; i--) {
        const r = this.doc.reactors[i];
        if (Math.abs(wx - r.x) < tol && Math.abs(wy - r.y) < tol) return { kind: 'reactor', entry: r };
      }
    }

    if (this.overlays.foothold) {
      let vertexHit = null;
      let lineHit = null;
      let lineDist = Infinity;
      for (const rec of cell) {
        if (rec.type !== 'fh') continue;
        const fh = rec.fh;
        if (!vertexHit) {
          if (Math.abs(wx - fh.x1) < tol && Math.abs(wy - fh.y1) < tol)
            vertexHit = { kind: 'foothold', entry: fh, vertex: 1 };
          else if (Math.abs(wx - fh.x2) < tol && Math.abs(wy - fh.y2) < tol)
            vertexHit = { kind: 'foothold', entry: fh, vertex: 2 };
        }
        const d = pointToSegment(wx, wy, fh.x1, fh.y1, fh.x2, fh.y2);
        if (d < tol / 1.5 && d < lineDist) { lineDist = d; lineHit = { kind: 'foothold', entry: fh }; }
      }
      if (vertexHit) return vertexHit;
      if (lineHit) return lineHit;
    }

    if (this.overlays.ladder) {
      for (const rec of cell) {
        if (rec.type !== 'ladder') continue;
        const l = rec.l;
        const y0 = Math.min(l.y1, l.y2); const y1 = Math.max(l.y1, l.y2);
        if (Math.abs(wx - l.x) < tol && wy >= y0 - tol && wy <= y1 + tol) {
          const vertex = Math.abs(wy - l.y1) < tol * 1.5 ? 1
            : Math.abs(wy - l.y2) < tol * 1.5 ? 2 : 0;
          return { kind: 'ladder', entry: l, vertex: vertex || undefined };
        }
      }
    }

    if (this.overlays.rect) {
      for (const rec of cell) {
        if (rec.type !== 'rect') continue;
        const r = rec.r;
        const x0 = Math.min(r.x1, r.x2); const x1 = Math.max(r.x1, r.x2);
        const y0 = Math.min(r.y1, r.y2); const y1 = Math.max(r.y1, r.y2);
        const onX = wx >= x0 - tol && wx <= x1 + tol;
        const onY = wy >= y0 - tol && wy <= y1 + tol;
        if (onX && onY) return { kind: 'rect', entry: r };
      }
    }

    // Static art, topmost first (draw order index), visible layers only,
    // pixel-tested so hover lands on the tree and not its empty corner.
    let best = null;
    for (const rec of cell) {
      if (rec.type !== 'static') continue;
      const item = rec.item;
      if (!this.layerVisible.get(item.layer)) continue;
      const r = item.rect;
      if (wx < r.x || wx > r.x + r.w || wy < r.y || wy > r.y + r.h) continue;
      if (best && best.order > item.order) continue;
      if (!this.alphaHit(item, wx, wy)) continue;
      best = item;
    }
    if (best) return { kind: best.kind, entry: best.entry, item: best };

    if (this.overlays.back) {
      for (let i = this.doc.backs.length - 1; i >= 0; i--) {
        const back = this.doc.backs[i];
        const r = this.backRect(back);
        if (r && wx >= r.x && wx <= r.x + r.w && wy >= r.y && wy <= r.y + r.h)
          return { kind: 'back', entry: back };
      }
    }
    return null;
  }

  /** Resize/endpoint handles of the CURRENT selection — checked in screen
   *  space, before anything else, so they stay grabbable over clutter. */
  hitHandles(sx, sy) {
    if (this.sel.length !== 1) return null;
    const sel = this.sel[0];
    const near = (x, y, r = 7) => Math.abs(sx - x) <= r && Math.abs(sy - y) <= r;

    if (sel.kind === 'foothold') {
      const [ax, ay] = this.toScreen(sel.entry.x1, sel.entry.y1);
      const [bx, by] = this.toScreen(sel.entry.x2, sel.entry.y2);
      if (near(ax, ay)) return { kind: 'foothold', entry: sel.entry, vertex: 1 };
      if (near(bx, by)) return { kind: 'foothold', entry: sel.entry, vertex: 2 };
    } else if (sel.kind === 'ladder') {
      const [ax, ay] = this.toScreen(sel.entry.x, sel.entry.y1);
      const [, by] = this.toScreen(sel.entry.x, sel.entry.y2);
      if (near(ax, ay)) return { kind: 'ladder', entry: sel.entry, vertex: 1 };
      if (near(ax, by)) return { kind: 'ladder', entry: sel.entry, vertex: 2 };
    } else if (sel.kind === 'rect') {
      const r = sel.entry;
      const [x0, y0] = this.toScreen(Math.min(r.x1, r.x2), Math.min(r.y1, r.y2));
      const [x1, y1] = this.toScreen(Math.max(r.x1, r.x2), Math.max(r.y1, r.y2));
      const cx = (x0 + x1) / 2; const cy = (y0 + y1) / 2;
      const handles = [
        ['nw', x0, y0], ['n', cx, y0], ['ne', x1, y0],
        ['w', x0, cy], ['e', x1, cy],
        ['sw', x0, y1], ['s', cx, y1], ['se', x1, y1],
      ];
      for (const [name, hx, hy] of handles) {
        if (near(hx, hy)) return { kind: 'rect', entry: r, handle: name };
      }
    }
    return null;
  }

  /* ============================================================ pointer */

  pointerDown(e) {
    // One gesture at a time, entered explicitly. A second press while one is
    // running (another button, a second touch) is ignored rather than allowed
    // to restart or re-aim the gesture in flight.
    if (this.gesture) return;

    this.canvas.setPointerCapture(e.pointerId);
    const rect = this.canvas.getBoundingClientRect();
    const sx = e.clientX - rect.left; const sy = e.clientY - rect.top;
    const [wx, wy] = this.toWorld(sx, sy);

    // Pan: space-hold + any button, middle button, or right button.
    // Space-held is MODAL: while it is down no press can select, place or
    // move anything — the press pans, period.
    if (this.spaceHeld || e.button === 1 || e.button === 2) {
      this.gesture = { kind: 'pan', sx, sy, cx: this.cam.x, cy: this.cam.y };
      this.updateCursor();
      return;
    }
    if (e.button !== 0) return;

    // Authoring modes own the left button entirely.
    if (this.fhDraw) {
      if (e.detail >= 2) { this.finishFootholdDraw(); return; }
      this.fhDraw.points.push({ x: Math.round(wx), y: Math.round(wy) });
      this.invalidate();
      return;
    }
    if (this.fhExtend) {
      this.handlers.onExtendFoothold?.(
        this.fhExtend.entry, this.fhExtend.vertex, Math.round(wx), Math.round(wy));
      return;
    }
    if (this.placing) {
      const [px, py] = this.snapPoint(wx, wy);
      if (this.placing.kind === 'ladder') {
        if (!this.ladderStart) {
          this.ladderStart = { x: px, y: py };
          this.invalidate();
        } else {
          const start = this.ladderStart;
          this.ladderStart = null;
          this.handlers.onPlaceLadder?.(start.x, start.y, py);
        }
        return;
      }
      this.handlers.onPlace?.(px, py);
      return;
    }

    const hit = this.hitTest(sx, sy);

    // A press on any member of a MULTI-selection drags the selection — the
    // already-selected thing under the cursor outranks whatever a fresh
    // hit-test would prefer (HaCreator's selectedItemHigher rule). Handles
    // stay live for single selections, where they cannot be ambiguous.
    if (hit && this.sel.length > 1 && !e.shiftKey && !e.ctrlKey && !e.metaKey
        && this.sel.some((s) => s.entry === hit.entry)) {
      this.gesture = {
        kind: 'drag-sel', sx, sy, moved: false,
        starts: this.sel.map((s) => this.captureStart(s)),
      };
      this.invalidate();
      return;
    }

    if (!hit) {
      // Empty space: marquee. Shift/Ctrl keeps the current selection as base.
      const keep = e.shiftKey || e.ctrlKey || e.metaKey;
      this.gesture = {
        kind: 'marquee', sx, sy, wx0: wx, wy0: wy, wx1: wx, wy1: wy,
        base: keep ? [...this.sel] : [],
      };
      if (!keep && this.sel.length) {
        this.sel = [];
        this.handlers.onSelect?.(this.sel);
      }
      this.updateCursor();
      this.invalidate();
      return;
    }

    // Rect resize handle.
    if (hit.kind === 'rect' && hit.handle) {
      this.sel = [{ kind: 'rect', entry: hit.entry }];
      this.handlers.onSelect?.(this.sel);
      const r = hit.entry;
      this.gesture = {
        kind: 'rect-resize', entry: r, handle: hit.handle, sx, sy,
        start: {
          x0: Math.min(r.x1, r.x2), y0: Math.min(r.y1, r.y2),
          x1: Math.max(r.x1, r.x2), y1: Math.max(r.y1, r.y2),
        },
        moved: false,
      };
      this.invalidate();
      return;
    }

    // Foothold vertex drag (from handle or direct hit).
    if (hit.kind === 'foothold' && hit.vertex) {
      this.sel = [{ kind: 'foothold', entry: hit.entry, vertex: hit.vertex }];
      this.handlers.onSelect?.(this.sel);
      const linked = this.linkedEndpoints(hit.entry, hit.vertex);
      this.gesture = {
        kind: 'fh-vertex', entry: hit.entry, vertex: hit.vertex, sx, sy,
        linked, moved: false,
        startX: hit.vertex === 1 ? hit.entry.x1 : hit.entry.x2,
        startY: hit.vertex === 1 ? hit.entry.y1 : hit.entry.y2,
      };
      this.invalidate();
      return;
    }

    // Ladder endpoint drag.
    if (hit.kind === 'ladder' && hit.vertex) {
      this.sel = [{ kind: 'ladder', entry: hit.entry }];
      this.handlers.onSelect?.(this.sel);
      this.gesture = {
        kind: 'ladder-end', entry: hit.entry, vertex: hit.vertex, sx, sy,
        startX: hit.entry.x,
        startY: hit.vertex === 1 ? hit.entry.y1 : hit.entry.y2,
        moved: false,
      };
      this.invalidate();
      return;
    }

    const record = { kind: hit.kind, entry: hit.entry, item: hit.item };
    const already = this.sel.find((s) => s.entry === hit.entry);
    const toggle = e.shiftKey || e.ctrlKey || e.metaKey;

    if (toggle) {
      // Toggle membership; no drag from a modifier click.
      this.sel = already ? this.sel.filter((s) => s.entry !== hit.entry) : [...this.sel, record];
      this.handlers.onSelect?.(this.sel);
      this.invalidate();
      return;
    }

    if (!already) {
      this.sel = [record];
      this.handlers.onSelect?.(this.sel);
    }

    // Whole-selection drag — starts after the threshold, so a click is a click.
    this.gesture = {
      kind: 'drag-sel', sx, sy, moved: false,
      starts: this.sel.map((s) => this.captureStart(s)),
    };
    this.invalidate();
  }

  captureStart(sel) {
    const e = sel.entry;
    if (sel.kind === 'foothold') {
      return {
        sel,
        linked: [
          ...this.linkedEndpoints(e, 1),
          ...this.linkedEndpoints(e, 2),
        ],
      };
    }
    if (sel.kind === 'ladder') return { sel, x: e.x, y1: e.y1, y2: e.y2 };
    if (sel.kind === 'rect') return { sel, x1: e.x1, y1: e.y1, x2: e.x2, y2: e.y2 };
    if (sel.kind === 'life') return { sel, x: e.x, y: e.cy || e.y };
    return { sel, x: e.x, y: e.y };
  }

  /** The endpoints a vertex drags with it, client-side mirror of the
   *  server's rule: same foothold layer, coincident at the point, connected
   *  through actual prev/next links. Returns [{fh, vertex, x, y}]. */
  linkedEndpoints(fh, vertex) {
    const px = vertex === 1 ? fh.x1 : fh.x2;
    const py = vertex === 1 ? fh.y1 : fh.y2;
    const layerMates = this.doc.footholds.filter((f) => f.layer === fh.layer);
    const atPoint = [];
    for (const f of layerMates) {
      if (f.x1 === px && f.y1 === py) atPoint.push({ fh: f, vertex: 1 });
      if (f.x2 === px && f.y2 === py) atPoint.push({ fh: f, vertex: 2 });
    }
    const linked = (a, b) =>
      String(a.prev) === b.id || String(a.next) === b.id ||
      String(b.prev) === a.id || String(b.next) === a.id;
    const component = new Set([fh]);
    let grew = true;
    while (grew) {
      grew = false;
      for (const { fh: f } of atPoint) {
        if (component.has(f)) continue;
        for (const member of component) {
          if (linked(f, member)) { component.add(f); grew = true; break; }
        }
      }
    }
    return atPoint
      .filter((p) => component.has(p.fh))
      .map((p) => ({ fh: p.fh, vertex: p.vertex, x: px, y: py }));
  }

  pointerMove(e) {
    const rect = this.canvas.getBoundingClientRect();
    const sx = e.clientX - rect.left; const sy = e.clientY - rect.top;
    const [wx, wy] = this.toWorld(sx, sy);
    this.mouse = [wx, wy];
    this.mouseScreen = [sx, sy];

    if (this.placing || this.fhDraw || this.fhExtend) this.invalidate();

    const g = this.gesture;
    if (!g) {
      if (this.spaceHeld) {
        // Pan mode: hit-testing suspended; the cursor is a hand, not a picker.
        if (this.hover) {
          this.hover = null;
          this.handlers.onHover?.(null);
          this.invalidate();
        }
        this.reportStatus(wx, wy, null);
        return;
      }
      // Idle: hover. The grid + pixel test keep this far under a frame.
      const hit = this.doc ? this.hitTest(sx, sy) : null;
      const changed = (hit?.entry !== this.hover?.entry)
        || (hit?.vertex !== this.hover?.vertex) || (hit?.handle !== this.hover?.handle);
      if (changed) {
        this.hover = hit;
        this.handlers.onHover?.(hit);
        this.updateCursor();
        this.invalidate();
      }
      this.reportStatus(wx, wy, hit);
      return;
    }

    this.reportStatus(wx, wy, null);

    if (g.kind === 'pan') {
      this.cam.x = g.cx - (sx - g.sx) / this.cam.scale;
      this.cam.y = g.cy - (sy - g.sy) / this.cam.scale;
      this.viewChanged();
      this.invalidate();
      return;
    }

    if (g.kind === 'marquee') {
      g.wx1 = wx; g.wy1 = wy;
      this.updateMarqueeSelection(g);
      this.invalidate();
      return;
    }

    const dx = Math.round((sx - g.sx) / this.cam.scale);
    const dy = Math.round((sy - g.sy) / this.cam.scale);
    if (!g.moved && Math.abs(sx - g.sx) < DRAG_THRESHOLD && Math.abs(sy - g.sy) < DRAG_THRESHOLD) return;
    if (!g.moved) {
      g.moved = true;
      if (g.kind === 'drag-sel') {
        for (const start of g.starts) {
          if (start.sel.kind === 'tile' || start.sel.kind === 'obj') this.liveSet.add(start.sel.entry);
        }
        if (this.liveSet.size) this.dropChunks();
      }
      this.updateCursor();
    }

    if (g.kind === 'fh-vertex') {
      const nx = g.startX + dx; const ny = g.startY + dy;
      for (const end of g.linked) {
        if (end.vertex === 1) { end.fh.x1 = nx; end.fh.y1 = ny; }
        else { end.fh.x2 = nx; end.fh.y2 = ny; }
      }
      this._chainCache = null;
      this.invalidate();
      return;
    }

    if (g.kind === 'ladder-end') {
      const l = g.entry;
      l.x = g.startX + dx;
      if (g.vertex === 1) l.y1 = g.startY + dy; else l.y2 = g.startY + dy;
      this.invalidate();
      return;
    }

    if (g.kind === 'rect-resize') {
      const r = g.entry;
      const st = g.start;
      let { x0, y0, x1, y1 } = st;
      const h = g.handle;
      if (h.includes('w')) x0 = st.x0 + dx;
      if (h.includes('e')) x1 = st.x1 + dx;
      if (h.includes('n')) y0 = st.y0 + dy;
      if (h.includes('s')) y1 = st.y1 + dy;
      r.x1 = Math.min(x0, x1); r.x2 = Math.max(x0, x1);
      r.y1 = Math.min(y0, y1); r.y2 = Math.max(y0, y1);
      this.invalidate();
      return;
    }

    if (g.kind === 'drag-sel') {
      const done = new Set();   // shared junctions across selected segments
      for (const start of g.starts) {
        const s = start.sel;
        const en = s.entry;
        if (s.kind === 'foothold') {
          for (const end of start.linked) {
            const key = `${this.doc.footholds.indexOf(end.fh)}|${end.vertex}`;
            if (done.has(key)) continue;
            done.add(key);
            if (end.vertex === 1) { end.fh.x1 = end.x + dx; end.fh.y1 = end.y + dy; }
            else { end.fh.x2 = end.x + dx; end.fh.y2 = end.y + dy; }
          }
          this._chainCache = null;
        } else if (s.kind === 'ladder') {
          en.x = start.x + dx; en.y1 = start.y1 + dy; en.y2 = start.y2 + dy;
        } else if (s.kind === 'rect') {
          en.x1 = start.x1 + dx; en.x2 = start.x2 + dx;
          en.y1 = start.y1 + dy; en.y2 = start.y2 + dy;
        } else if (s.kind === 'life') {
          en.x = start.x + dx;
          en.cy = start.y + dy;
          en.y = en.cy;
        } else {
          en.x = start.x + dx;
          en.y = start.y + dy;
          if (s.item) s.item.rect = this.itemRect(s.item);
        }
      }
      g.dx = dx; g.dy = dy;
      this.invalidate();
    }
  }

  /** Live marquee membership, HaCreator-style: entering the rect selects,
   *  leaving it deselects — filtered by overlay toggles + layer visibility. */
  updateMarqueeSelection(g) {
    const x0 = Math.min(g.wx0, g.wx1); const x1 = Math.max(g.wx0, g.wx1);
    const y0 = Math.min(g.wy0, g.wy1); const y1 = Math.max(g.wy0, g.wy1);
    const picked = [];
    const baseEntries = new Set(g.base.map((s) => s.entry));

    for (const item of this.staticItems) {
      if (!this.layerVisible.get(item.layer)) continue;
      const r = item.rect;
      if (r.x + r.w > x0 && r.x < x1 && r.y + r.h > y0 && r.y < y1)
        picked.push({ kind: item.kind, entry: item.entry, item });
    }
    if (this.overlays.foothold) {
      for (const fh of this.doc.footholds) {
        const fx0 = Math.min(fh.x1, fh.x2); const fx1 = Math.max(fh.x1, fh.x2);
        const fy0 = Math.min(fh.y1, fh.y2); const fy1 = Math.max(fh.y1, fh.y2);
        if (fx1 > x0 && fx0 < x1 && fy1 > y0 && fy0 < y1)
          picked.push({ kind: 'foothold', entry: fh });
      }
    }
    if (this.overlays.ladder) {
      for (const l of this.doc.ladders) {
        if (l.x > x0 && l.x < x1 && Math.max(l.y1, l.y2) > y0 && Math.min(l.y1, l.y2) < y1)
          picked.push({ kind: 'ladder', entry: l });
      }
    }
    if (this.overlays.portal) {
      for (const p of this.doc.portals) {
        if (p.x > x0 && p.x < x1 && p.y > y0 && p.y < y1)
          picked.push({ kind: 'portal', entry: p });
      }
    }
    if (this.overlays.life) {
      for (const l of this.doc.life) {
        const r = this.lifeRect(l);
        if (r.x + r.w > x0 && r.x < x1 && r.y + r.h > y0 && r.y < y1)
          picked.push({ kind: 'life', entry: l });
      }
    }
    if (this.overlays.reactor) {
      for (const r of this.doc.reactors) {
        if (r.x > x0 && r.x < x1 && r.y > y0 && r.y < y1)
          picked.push({ kind: 'reactor', entry: r });
      }
    }
    if (this.overlays.rect) {
      for (const r of this.doc.rects ?? []) {
        const rx0 = Math.min(r.x1, r.x2); const rx1 = Math.max(r.x1, r.x2);
        const ry0 = Math.min(r.y1, r.y2); const ry1 = Math.max(r.y1, r.y2);
        if (rx0 > x0 && rx1 < x1 && ry0 > y0 && ry1 < y1)
          picked.push({ kind: 'rect', entry: r });
      }
    }

    this.sel = [
      ...g.base,
      ...picked.filter((p) => !baseEntries.has(p.entry)),
    ];
    this.handlers.onSelect?.(this.sel);
  }

  pointerUp() {
    const g = this.gesture;
    this.gesture = null;
    this.updateCursor();
    if (!g) return;

    if (g.kind === 'pan') { this.invalidate(); return; }

    if (g.kind === 'marquee') {
      this.invalidate();
      return; // selection already live
    }

    if (!g.moved) { this.invalidate(); return; }

    if (g.kind === 'fh-vertex') {
      this.handlers.onMoveFoothold?.(g.entry, g.vertex);
    } else if (g.kind === 'ladder-end') {
      this.handlers.onMoveLadder?.(g.entry);
    } else if (g.kind === 'rect-resize') {
      this.handlers.onRectResize?.(g.entry);
    } else if (g.kind === 'drag-sel') {
      const items = g.starts.map((s) => ({ kind: s.sel.kind, entry: s.sel.entry }));
      this.liveSet.clear();
      this.dropChunks();
      this.handlers.onMoveMany?.(items, g.dx ?? 0, g.dy ?? 0);
    }
    this.invalidate();
  }

  doubleClick(e) {
    if (this.placing || this.fhDraw || this.fhExtend || this.spaceHeld) return;
    const rect = this.canvas.getBoundingClientRect();
    const sx = e.clientX - rect.left; const sy = e.clientY - rect.top;
    const [wx, wy] = this.toWorld(sx, sy);
    const hit = this.hitTest(sx, sy);
    if (!hit) return;

    if (hit.kind === 'foothold') {
      if (hit.vertex) {
        // A free chain end extends; a welded junction does not.
        const free = hit.vertex === 1 ? !Number(hit.entry.prev) : !Number(hit.entry.next);
        if (free) {
          this.sel = [{ kind: 'foothold', entry: hit.entry, vertex: hit.vertex }];
          this.handlers.onSelect?.(this.sel);
          this.beginExtend(hit.entry, hit.vertex);
          return;
        }
      }
      // Split the segment at the projected point.
      const p = projectOnSegment(wx, wy, hit.entry.x1, hit.entry.y1, hit.entry.x2, hit.entry.y2);
      this.handlers.onInsertVertex?.(hit.entry, Math.round(p.x), Math.round(p.y));
      return;
    }
    if (hit.kind === 'tile') {
      this.sel = [{ kind: 'tile', entry: hit.entry, item: hit.item }];
      this.handlers.onSelect?.(this.sel);
      this.handlers.onTilePicker?.(this.sel[0], sx, sy);
    }
  }

  wheel(e) {
    e.preventDefault();
    // No zooming out from under a running gesture — a drag keeps the frame of
    // reference it started in.
    if (this.gesture) return;
    const rect = this.canvas.getBoundingClientRect();
    const sx = e.clientX - rect.left; const sy = e.clientY - rect.top;
    // Smooth, delta-proportional: a mouse notch (~100) lands near the old
    // 1.15 step, a trackpad's fine deltas glide.
    const factor = Math.exp(-Math.max(-240, Math.min(240, e.deltaY)) * 0.0014);
    this.zoomBy(factor, sx, sy);
  }

  /* ================================================== selection utilities */

  /** The current selection as batch-op items. */
  selectionItems() {
    return this.sel.map((s) => ({ kind: s.kind, entry: s.entry }));
  }

  /** Applies a local nudge to the whole selection (world px). The caller
   *  commits the accumulated delta through moveMany when the keys go quiet. */
  nudgeSelection(dx, dy) {
    const done = new Set();   // spans the selection: shared junctions move once
    for (const s of this.sel) {
      const e = s.entry;
      if (s.kind === 'foothold') {
        for (const end of [...this.linkedEndpoints(e, 1), ...this.linkedEndpoints(e, 2)]) {
          const key = `${this.doc.footholds.indexOf(end.fh)}|${end.vertex}`;
          if (done.has(key)) continue;
          done.add(key);
          if (end.vertex === 1) { end.fh.x1 += dx; end.fh.y1 += dy; }
          else { end.fh.x2 += dx; end.fh.y2 += dy; }
        }
        this._chainCache = null;
      } else if (s.kind === 'ladder') {
        e.x += dx; e.y1 += dy; e.y2 += dy;
      } else if (s.kind === 'rect') {
        e.x1 += dx; e.x2 += dx; e.y1 += dy; e.y2 += dy;
      } else if (s.kind === 'life') {
        e.x += dx; e.cy = (e.cy || e.y) + dy; e.y = e.cy;
      } else {
        e.x += dx; e.y += dy;
        if (s.item) s.item.rect = this.itemRect(s.item);
      }
    }
    if (this.sel.some((s) => s.kind === 'tile' || s.kind === 'obj')) {
      this.dropChunks();
    }
    this.invalidate();
  }

  /** Everything currently visible and selectable — Ctrl+A within filter. */
  selectAll() {
    const picked = [];
    for (const item of this.staticItems) {
      if (!this.layerVisible.get(item.layer)) continue;
      picked.push({ kind: item.kind, entry: item.entry, item });
    }
    if (this.overlays.foothold) for (const fh of this.doc.footholds) picked.push({ kind: 'foothold', entry: fh });
    if (this.overlays.ladder) for (const l of this.doc.ladders) picked.push({ kind: 'ladder', entry: l });
    if (this.overlays.portal) for (const p of this.doc.portals) picked.push({ kind: 'portal', entry: p });
    if (this.overlays.life) for (const l of this.doc.life) picked.push({ kind: 'life', entry: l });
    if (this.overlays.reactor) for (const r of this.doc.reactors) picked.push({ kind: 'reactor', entry: r });
    if (this.overlays.rect) for (const r of this.doc.rects ?? []) picked.push({ kind: 'rect', entry: r });
    this.sel = picked;
    this.handlers.onSelect?.(this.sel);
    this.invalidate();
  }

  /** Screen-space bounding box of the selection, for the floating panel. */
  selectionScreenBox() {
    if (!this.sel.length) return null;
    let x0 = Infinity; let y0 = Infinity; let x1 = -Infinity; let y1 = -Infinity;
    const grow = (wx, wy) => {
      const [sx, sy] = this.toScreen(wx, wy);
      if (sx < x0) x0 = sx; if (sx > x1) x1 = sx;
      if (sy < y0) y0 = sy; if (sy > y1) y1 = sy;
    };
    for (const s of this.sel) {
      const e = s.entry;
      if (s.kind === 'tile' || s.kind === 'obj') {
        const item = s.item ?? this.staticItems.find((i) => i.entry === e);
        if (item) { grow(item.rect.x, item.rect.y); grow(item.rect.x + item.rect.w, item.rect.y + item.rect.h); }
      } else if (s.kind === 'foothold') {
        grow(e.x1, e.y1); grow(e.x2, e.y2);
      } else if (s.kind === 'ladder') {
        grow(e.x - 6, e.y1); grow(e.x + 6, e.y2);
      } else if (s.kind === 'rect') {
        grow(Math.min(e.x1, e.x2), Math.min(e.y1, e.y2));
        grow(Math.max(e.x1, e.x2), Math.max(e.y1, e.y2));
      } else if (s.kind === 'life') {
        const r = this.lifeRect(e);
        grow(r.x, r.y); grow(r.x + r.w, r.y + r.h);
      } else {
        grow(e.x - 14, e.y - 14); grow(e.x + 14, e.y + 14);
      }
    }
    if (x0 === Infinity) return null;
    return { x0, y0, x1, y1 };
  }

  /* ============================================================ feel */

  reportStatus(wx, wy, hit) {
    const pos = `${Math.round(wx)}, ${Math.round(wy)} · ${Math.round(this.cam.scale * 100)}%`;
    if (!hit) { this.handlers.onStatus?.(pos); return; }
    this.handlers.onStatus?.(`${describe(hit)} · ${pos}`);
  }

  updateCursor() {
    const c = this.canvas;
    const g = this.gesture;
    if (g?.kind === 'pan') { c.style.cursor = 'grabbing'; return; }
    if (this.spaceHeld) { c.style.cursor = 'grab'; return; }
    if (this.placing || this.fhDraw || this.fhExtend) { c.style.cursor = 'crosshair'; return; }
    if (g?.kind === 'marquee') { c.style.cursor = 'crosshair'; return; }
    if (g?.kind === 'rect-resize') { c.style.cursor = resizeCursor(g.handle); return; }
    if (g && g.moved) { c.style.cursor = 'move'; return; }
    const h = this.hover;
    if (h) {
      if (h.kind === 'rect' && h.handle) { c.style.cursor = resizeCursor(h.handle); return; }
      if (h.vertex) { c.style.cursor = 'pointer'; return; }
      c.style.cursor = 'move';
      return;
    }
    c.style.cursor = 'default';
  }
}

function resizeCursor(handle) {
  return {
    n: 'ns-resize', s: 'ns-resize', e: 'ew-resize', w: 'ew-resize',
    ne: 'nesw-resize', sw: 'nesw-resize', nw: 'nwse-resize', se: 'nwse-resize',
  }[handle] ?? 'move';
}

/** One line naming what the cursor is on — the status readout. */
function describe(hit) {
  const e = hit.entry;
  switch (hit.kind) {
    case 'tile': return `tile ${e.u}/${e.no} · zM ${e.zm ?? 0} · (${e.x}, ${e.y})`;
    case 'obj': return `obj ${e.os ?? e.oS ?? ''}/${e.l0}/${e.l1}/${e.l2} · z ${e.z} · (${e.x}, ${e.y})`;
    case 'foothold':
      return `foothold ${e.id}${hit.vertex ? ` · vertex ${hit.vertex}` : ''} · layer ${e.layer} `
        + `· (${e.x1}, ${e.y1})→(${e.x2}, ${e.y2}) · prev ${e.prev} next ${e.next}`;
    case 'ladder': return `${e.l === 1 ? 'ladder' : 'rope'} · x ${e.x} · y ${e.y1}→${e.y2}`;
    case 'portal': return `portal ${e.pn || '(unnamed)'} pt ${e.pt} → ${e.tn || '—'}${e.tm && e.tm !== 999999999 ? ` @ ${e.tm}` : ''} · (${e.x}, ${e.y})`;
    case 'life': return `${(e.type || '').toLowerCase() === 'n' ? 'NPC' : 'mob'} ${e.name ?? e.id} · fh ${e.fh} · (${e.x}, ${e.cy || e.y})`;
    case 'reactor': return `reactor ${e.id ?? ''} · (${e.x}, ${e.y})`;
    case 'rect': return `${e.kind}/${e.name} · (${e.x1}, ${e.y1})→(${e.x2}, ${e.y2})`;
    case 'back': return `back ${e.bs ?? e.bS ?? ''}/${e.no} · type ${e.type} · (${e.x}, ${e.y})`;
    default: return hit.kind;
  }
}

function pointToSegment(px, py, x1, y1, x2, y2) {
  const dx = x2 - x1; const dy = y2 - y1;
  const lengthSq = dx * dx + dy * dy;
  const t = lengthSq === 0 ? 0 : Math.max(0, Math.min(1, ((px - x1) * dx + (py - y1) * dy) / lengthSq));
  const cx = x1 + t * dx; const cy = y1 + t * dy;
  return Math.hypot(px - cx, py - cy);
}

function projectOnSegment(px, py, x1, y1, x2, y2) {
  const dx = x2 - x1; const dy = y2 - y1;
  const lengthSq = dx * dx + dy * dy;
  const t = lengthSq === 0 ? 0 : Math.max(0.02, Math.min(0.98, ((px - x1) * dx + (py - y1) * dy) / lengthSq));
  return { x: x1 + t * dx, y: y1 + t * dy };
}
