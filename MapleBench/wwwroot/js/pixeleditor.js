/**
 * In-browser pixel editor for WZ canvas nodes.
 *
 * Edits happen on an offscreen ImageData at the sprite's true resolution; the
 * visible canvas is only a zoomed blit of it. That keeps every operation exact
 * at the pixel level regardless of zoom, which is the whole point for sprite work.
 *
 * The onion-skin ghosts and the diff tint are composited onto the *visible*
 * canvas only. `buffer` is what Apply encodes, so nothing decorative may ever
 * be drawn into it.
 *
 * Nothing touches the WZ tree until Apply, which POSTs a PNG to /api/canvas.
 */

import { api } from './api.js';
import { el, clear, toast, toastError, modal, fmt } from './ui.js';
import { bustThumb, canvasUrl } from './media.js';
import { icon } from './icons.js';

const TOOLS = [
  ['pencil', 'brush',   'Pencil · B'],
  ['eraser', 'eraser',  'Eraser · E'],
  ['fill',   'bucket',  'Bucket fill · G'],
  ['picker', 'pipette', 'Eyedropper · I'],
  ['line',   'line',    'Line · L'],
  ['rect',   'square',  'Rectangle · R'],
];

/**
 * What each stored WZ pixel format will do to the work on Apply.
 *
 * The editor always works in RGBA8888, but the write path re-encodes into the
 * format the canvas already had (WzEditService.ReplaceCanvas -> MbPngCodec),
 * so anything the format cannot represent is discarded server-side with no
 * error. Format2 (BGRA8888) is the lossless case and is deliberately absent —
 * an unknown format is too, because a guess here would be worse than silence.
 */
const FORMAT_LIMITS = {
  Format1:    'This canvas is BGRA4444. Alpha and colour are stored with 4 bits each, so soft edges and gradients lose precision.',
  Format3:    'This canvas is DXT3 grayscale. Colour is discarded and alpha keeps only 4 bits, so anything but a grey sprite will come back wrong.',
  Format257:  'This canvas is ARGB1555. Alpha is a single bit — every pixel comes back either fully opaque or fully erased, with nothing in between.',
  Format513:  'This canvas is RGB565. It cannot store transparency — erased pixels will come back as black.',
  Format517:  'This canvas is block-compressed RGB565. It cannot store transparency — erased pixels will come back as black — and colour is averaged over 16×16 blocks.',
  Format1026: 'This canvas is DXT3. Colour is compressed in 4×4 blocks and alpha keeps only 4 bits, so fine detail and soft edges will shift.',
  Format2050: 'This canvas is DXT5. Colour is compressed in 4×4 blocks, so fine detail will shift.',
  Format4098: 'This canvas is BC7, which MapleBench can read but not write. Applying stores it as uncompressed BGRA8888 instead, so the file grows.',
};

export function openPixelEditor(node, app) {
  const state = {
    tool: 'pencil',
    colour: [0, 0, 0, 255],
    zoom: 8,
    width: 0,
    height: 0,
    image: null,        // ImageData at native resolution
    original: null,     // the load-time pixels, for the diff view
    changed: 0,         // pixels differing from `original`, kept incrementally
    undo: [],
    redo: [],
    drawing: false,
    erasing: false,
    last: null,
    anchor: null,       // where the current line/rect stroke started
    cursor: null,       // last hovered pixel, or null when outside the sprite
    showGrid: true,
    showDiff: false,
    flipping: false,    // '\' held: show the original instead of the work
    onion: false,
    panning: false,     // Space held
    panPointer: null,   // pointerId of an in-progress pan
    panFrom: null,
    applying: false,
  };

  // One reusable buffer at the sprite's native size. paint() runs on every
  // pointermove while drawing, and allocating a canvas per call made a fast
  // stroke on a 200x200 sprite churn megabytes through the GC.
  const buffer = document.createElement('canvas');
  const bufferContext = buffer.getContext('2d');

  // Three more full-frame surfaces, all created once: the load-time image for
  // the A/B flip, the diff tint, and a scratch for tinting a ghost frame. The
  // undo stack already holds up to 60 copies of the sprite, so this is noise
  // beside what the editor is carrying anyway.
  let originalCanvas = null;
  let diffCanvas = null;
  let diffContext = null;
  let diffImage = null;
  let ghostCanvas = null;
  let ghostContext = null;

  /* ---------- chrome ---------- */
  const view = el('canvas', { style: 'image-rendering:pixelated;cursor:crosshair;display:block' });
  const stage = el('div', {
    style: `overflow:auto;max-height:56vh;padding:16px;border-radius:var(--r-md);
            display:grid;place-items:center;
            background-color:var(--checker-a);
            background-image:linear-gradient(45deg,var(--checker-b) 25%,transparent 25%),
              linear-gradient(-45deg,var(--checker-b) 25%,transparent 25%),
              linear-gradient(45deg,transparent 75%,var(--checker-b) 75%),
              linear-gradient(-45deg,transparent 75%,var(--checker-b) 75%);
            background-size:16px 16px;background-position:0 0,0 8px,8px -8px,-8px 0`,
  }, view);

  const swatch = el('input', { type: 'color', value: '#000000', title: 'Colour', style: 'width:38px;height:34px;padding:2px;border:1px solid var(--border);border-radius:var(--r-sm);background:var(--bg-field)' });
  const alpha = el('input', { type: 'range', min: '0', max: '255', value: '255', title: 'Opacity', style: 'width:80px' });
  const zoomLabel = el('span', { style: 'font-size:var(--fs-12);color:var(--text-3);min-width:44px;text-align:center', text: '800%' });
  const info = el('span', { style: 'font-size:var(--fs-12);color:var(--text-3);margin-left:auto;font-variant-numeric:tabular-nums' });
  const palette = el('div', { style: 'display:flex;flex-wrap:wrap;gap:4px;margin-top:10px' });

  // Persistent, not a toast: what it says stays true for the whole session and
  // the user needs it in view while they decide how to paint.
  const limitBar = el('div', {
    style: `display:none;align-items:flex-start;gap:9px;padding:10px 12px;margin-bottom:12px;
            border-radius:var(--r-md);background:var(--amber-tint);color:var(--amber-ink);
            font-size:var(--fs-12);line-height:1.45`,
  });

  const toolButtons = new Map();
  const toolbar = el('div', { style: 'display:flex;align-items:center;gap:6px;flex-wrap:wrap;margin-bottom:12px' });

  for (const [id, iconName, label] of TOOLS) {
    const button = el('button', {
      class: 'btn btn-icon', 'data-tip': label,
      onclick: () => setTool(id),
    }, icon(iconName, { size: 16 }));
    toolButtons.set(id, button);
    toolbar.append(button);
  }

  // The three view toggles carry aria-pressed so the toolbar says which of them
  // are on; the grid one used to look identical whether it was or not.
  const gridButton = el('button', {
    class: 'btn btn-icon btn-ghost', 'aria-pressed': 'true', 'data-tip': 'Toggle the pixel grid',
    onclick: () => {
      state.showGrid = !state.showGrid;
      gridButton.setAttribute('aria-pressed', state.showGrid ? 'true' : 'false');
      paint();
    },
  }, icon('grid4', { size: 15 }));

  const diffButton = el('button', {
    class: 'btn btn-icon btn-ghost', 'aria-pressed': 'false',
    'data-tip': 'Highlight what you changed · D  (hold \\ to see the original)',
    onclick: () => setDiff(!state.showDiff),
  }, icon('eye', { size: 15 }));

  // Only inserted once there is an animation to ghost -- see loadOnion().
  let onionButton = null;

  toolbar.append(
    el('span', { style: 'width:1px;height:22px;background:var(--border)' }),
    swatch, alpha,
    el('span', { style: 'width:1px;height:22px;background:var(--border)' }),
    el('button', { class: 'btn btn-icon btn-ghost', 'data-tip': 'Zoom out · −',
      onclick: () => setZoom(state.zoom / 2) }, icon('zoomOut', { size: 15 })),
    zoomLabel,
    el('button', { class: 'btn btn-icon btn-ghost', 'data-tip': 'Zoom in · +',
      onclick: () => setZoom(state.zoom * 2) }, icon('zoomIn', { size: 15 })),
    gridButton,
    diffButton,
    el('span', { style: 'width:1px;height:22px;background:var(--border)' }),
    el('button', { class: 'btn btn-icon btn-ghost', 'data-tip': 'Undo · Ctrl Z',
      onclick: undo }, icon('undo', { size: 15 })),
    el('button', { class: 'btn btn-icon btn-ghost', 'data-tip': 'Redo · Ctrl Shift Z',
      onclick: redo }, icon('redo', { size: 15 })),
    info);

  const body = el('div', {}, limitBar, toolbar, stage, palette,
    el('div', { style: 'margin-top:10px;font-size:var(--fs-12);color:var(--text-3)',
      text: 'Left click draws, right click erases. Hold Space (or the middle button) and drag to pan; '
          + 'Ctrl and the wheel zoom around the pointer. D tints what you have changed, and holding \\ '
          + 'shows the original underneath. Nothing is written to the WZ file until you press Apply.' }));

  const { dialog, close } = modal({
    title: `Pixel editor — ${node.name}`,
    width: 'min(96vw, 1000px)',
    body,
    actions: [
      { label: 'Cancel' },
      { label: 'Apply to canvas', class: 'btn-primary', run: (done) => { apply(done); return false; } },
    ],
  });

  /* ---------- load ---------- */
  const source = new Image();
  source.crossOrigin = 'anonymous';
  source.onload = () => {
    state.width = source.naturalWidth;
    state.height = source.naturalHeight;

    const scratch = document.createElement('canvas');
    scratch.width = state.width;
    scratch.height = state.height;
    const context = scratch.getContext('2d', { willReadFrequently: true });
    context.drawImage(source, 0, 0);
    state.image = context.getImageData(0, 0, state.width, state.height);

    // The pristine pixels, kept for the diff view and the A/B flip. `scratch`
    // still holds exactly them, so it becomes the flip surface as-is rather
    // than being redrawn.
    state.original = new Uint8ClampedArray(state.image.data);
    state.changed = 0;
    originalCanvas = scratch;

    // Start at a zoom that shows the whole sprite comfortably.
    const fit = Math.max(1, Math.min(16, Math.floor(640 / Math.max(state.width, state.height))));
    state.zoom = fit;
    buildPalette();
    describeLimits();
    setTool('pencil');
    setZoom(fit);
    loadOnion();
  };
  source.onerror = () => {
    clear(stage);
    stage.append(el('div', { style: 'padding:40px;color:var(--text-3);text-align:center',
      text: 'This canvas has no image data to edit.' }));
  };
  source.src = canvasUrl(node.path);

  /* ---------- rendering ---------- */

  /** Copies the working ImageData into the reusable native-size canvas. */
  function syncBuffer() {
    if (buffer.width !== state.width || buffer.height !== state.height) {
      buffer.width = state.width;
      buffer.height = state.height;
    }
    bufferContext.putImageData(state.image, 0, 0);
    return buffer;
  }

  function paint() {
    if (!state.image) return;
    const width = state.width * state.zoom;
    const height = state.height * state.zoom;
    // Assigning width/height reallocates the backing store and clears it, so
    // only do it when the size actually changed.
    if (view.width !== width || view.height !== height) {
      view.width = width;
      view.height = height;
    }

    const context = view.getContext('2d');
    context.imageSmoothingEnabled = false;
    context.clearRect(0, 0, view.width, view.height);

    if (state.onion) drawGhosts(context);

    // '\' held swaps in the load-time surface. It is a pure read -- the working
    // ImageData is untouched -- so releasing the key costs one repaint.
    context.drawImage(state.flipping && originalCanvas ? originalCanvas : syncBuffer(),
                      0, 0, view.width, view.height);

    // Pointless while flipping: the flip already shows the "before" side.
    if (state.showDiff && !state.flipping) {
      context.drawImage(diffOverlay(), 0, 0, view.width, view.height);
    }

    // A grid below 5px per pixel is noise rather than help.
    if (state.showGrid && state.zoom >= 5) {
      context.strokeStyle = 'rgba(128,128,128,0.28)';
      context.lineWidth = 1;
      context.beginPath();
      for (let x = 0; x <= state.width; x++) {
        context.moveTo(x * state.zoom + 0.5, 0);
        context.lineTo(x * state.zoom + 0.5, view.height);
      }
      for (let y = 0; y <= state.height; y++) {
        context.moveTo(0, y * state.zoom + 0.5);
        context.lineTo(view.width, y * state.zoom + 0.5);
      }
      context.stroke();
    }

    updateInfo();
  }

  /** The one place the status line is composed, so the pieces cannot disagree. */
  function updateInfo() {
    const parts = [`${state.width} × ${state.height}`];
    if (state.cursor) parts.push(`${state.cursor[0]}, ${state.cursor[1]}`);
    if (state.changed) {
      parts.push(`${fmt.format(state.changed)} pixel${state.changed === 1 ? '' : 's'} changed`);
    }
    info.textContent = parts.join('   •   ');
  }

  function setZoom(zoom) {
    state.zoom = Math.max(1, Math.min(32, Math.round(zoom)));
    zoomLabel.textContent = `${state.zoom * 100}%`;
    paint();
  }

  /**
   * Zooms about a point on screen rather than the canvas's top-left, so the
   * pixel under the cursor stays under the cursor.
   *
   * The stage centres content that fits and scrolls content that does not, so
   * where the canvas lands after a zoom cannot be predicted from the scroll
   * position alone -- it is measured after the relayout instead.
   */
  function zoomAt(zoom, clientX, clientY) {
    const before = view.getBoundingClientRect();
    const px = (clientX - before.left) / state.zoom;
    const py = (clientY - before.top) / state.zoom;
    setZoom(zoom);
    const after = view.getBoundingClientRect();
    stage.scrollLeft += after.left + px * state.zoom - clientX;
    stage.scrollTop += after.top + py * state.zoom - clientY;
  }

  function setTool(tool) {
    state.tool = tool;
    for (const [id, button] of toolButtons) {
      button.setAttribute('aria-pressed', id === tool ? 'true' : 'false');
    }
  }

  function setDiff(on) {
    state.showDiff = on;
    diffButton.setAttribute('aria-pressed', on ? 'true' : 'false');
    paint();
  }

  /* ---------- what changed ---------- */

  /** True when the pixel at byte offset `i` differs from the load-time image. */
  function differsAt(i) {
    const now = state.image.data;
    const was = state.original;
    return now[i] !== was[i] || now[i + 1] !== was[i + 1]
        || now[i + 2] !== was[i + 2] || now[i + 3] !== was[i + 3];
  }

  /** Full recount, for the paths that replace the whole buffer at once. */
  function recount() {
    let changed = 0;
    for (let i = 0; i < state.image.data.length; i += 4) if (differsAt(i)) changed++;
    state.changed = changed;
  }

  /**
   * A tint over every pixel that differs from the load-time image.
   *
   * Rebuilt on each repaint while the overlay is on rather than cached and
   * invalidated: the compare-and-fill pass measured 0.10 ms on a 200x200
   * sprite and 1.9 ms on 1024x768 (V8, same loop the browser runs), which is
   * beside the point next to the scaled blit it accompanies.
   */
  function diffOverlay() {
    if (!diffCanvas) {
      diffCanvas = document.createElement('canvas');
      diffContext = diffCanvas.getContext('2d');
    }
    if (diffCanvas.width !== state.width || diffCanvas.height !== state.height) {
      diffCanvas.width = state.width;
      diffCanvas.height = state.height;
      diffImage = diffContext.createImageData(state.width, state.height);
    }

    const out = diffImage.data;
    const now = state.image.data;
    const was = state.original;
    for (let i = 0; i < now.length; i += 4) {
      // --coral, so the tint reads as "this is yours" rather than as an error.
      out[i] = 255; out[i + 1] = 95; out[i + 2] = 51;
      out[i + 3] = (now[i] !== was[i] || now[i + 1] !== was[i + 1]
                 || now[i + 2] !== was[i + 2] || now[i + 3] !== was[i + 3]) ? 150 : 0;
    }
    diffContext.putImageData(diffImage, 0, 0);
    return diffCanvas;
  }

  /* ---------- what this canvas cannot keep ---------- */

  /**
   * Names the limits of the *stored* format up front.
   *
   * Editing happens in RGBA8888 whatever the canvas is, and Apply re-encodes
   * into the format it already had, so on an RGB565 canvas an afternoon of
   * feathered edges is discarded server-side without an error. Rule 12: name
   * the limit where the user reaches for it.
   */
  function describeLimits() {
    const notes = [];

    const limit = FORMAT_LIMITS[node.extra?.format];
    if (limit) notes.push(limit);

    // A linked canvas stores a placeholder and is *drawn* from its link, so the
    // pixels on screen here are not the pixels Apply would overwrite.
    const link = node.extra?.inlink || node.extra?.outlink;
    if (link) {
      notes.push(`These pixels come from "${link}". This canvas holds only a placeholder, `
               + 'so applying an edit here changes nothing on screen.');
    }

    if (!notes.length) return;
    limitBar.style.display = 'flex';
    clear(limitBar);
    limitBar.append(
      el('span', { style: 'flex:none;margin-top:1px' }, icon('alert', { size: 15 })),
      el('span', { style: 'flex:1' }, notes.map((note, index) =>
        el('div', { text: note, style: index ? 'margin-top:6px' : '' }))));
  }

  /* ---------- onion skin ---------- */
  const onion = { ready: false, originX: 0, originY: 0, ghosts: [] };

  /**
   * Loads the previous and next frames of the animation this canvas belongs to.
   *
   * Only numerically-named canvases can be frames, and that test is free, so an
   * ordinary sprite never costs a request. Everything after it degrades to "no
   * ghosts, no button" rather than to an error -- a canvas that is not a frame
   * is the common case, not a fault.
   */
  async function loadOnion() {
    if (!/^\d+$/.test(node.name || '')) return;
    const cut = node.path.lastIndexOf('/');
    if (cut <= 0) return;

    let data;
    try {
      data = await api.animation(node.path.slice(0, cut));
    } catch {
      return;
    }
    if (dialog.signal.aborted || !data?.isAnimation) return;

    // Matched on path, not name: sibling frames may share a name and carry a
    // '#n' occurrence suffix, and only the path distinguishes those.
    const index = data.frames.findIndex((frame) => frame.path === node.path);
    if (index < 0) return;

    const count = data.frames.length;
    // Wrapped, because a WZ frame strip is a loop -- the player defaults to
    // looping too -- so frame 0's visual predecessor really is the last frame.
    const neighbours = [
      [data.frames[(index - 1 + count) % count], 'rgba(255,95,51,0.72)'],  // --coral: behind
      [data.frames[(index + 1) % count], 'rgba(37,99,235,0.72)'],          // --blue-fill: ahead
    ].filter(([frame]) => frame && frame.path !== node.path);

    onion.originX = data.frames[index].originX;
    onion.originY = data.frames[index].originY;
    onion.ghosts = await Promise.all(neighbours.map(async ([frame, tint]) => ({
      originX: frame.originX,
      originY: frame.originY,
      tint,
      image: await loadFrame(canvasUrl(frame.path)),
    })));

    if (dialog.signal.aborted || !onion.ghosts.some((ghost) => ghost.image)) return;
    onion.ready = true;

    // Added only now: a toggle that provably has nothing to show is worse than
    // no toggle at all.
    onionButton = el('button', {
      class: 'btn btn-icon btn-ghost', 'aria-pressed': 'false',
      'data-tip': 'Onion skin — the frames either side of this one · O',
      onclick: () => setOnion(!state.onion),
    }, icon('layers', { size: 15 }));
    toolbar.insertBefore(onionButton, info);
  }

  function setOnion(on) {
    state.onion = on && onion.ready;
    if (onionButton) onionButton.setAttribute('aria-pressed', state.onion ? 'true' : 'false');
    paint();
  }

  /**
   * Draws the neighbouring frames beneath the work, aligned by origin.
   *
   * Frames are cropped to their own bounds and placed by an 'origin' vector, so
   * drawing them at (0,0) would jitter. The backend composes an animation by
   * blitting each frame at (anchor - origin) for a shared anchor; expressed
   * relative to *this* frame that is (anchor - theirOrigin) - (anchor - ourOrigin)
   * = ourOrigin - theirOrigin, and the anchor cancels out entirely. So this
   * needs only the two origins, which api.animation() already returns.
   *
   * A ghost that reaches outside this frame's bounds is clipped: the view is a
   * blit of one sprite at one native size, and growing it would break toPixel().
   */
  function drawGhosts(context) {
    for (const ghost of onion.ghosts) {
      if (!ghost.image) continue;
      const width = ghost.image.naturalWidth;
      const height = ghost.image.naturalHeight;
      if (!width || !height) continue;

      context.globalAlpha = 0.4;
      context.drawImage(tintGhost(ghost.image, ghost.tint),
                        (onion.originX - ghost.originX) * state.zoom,
                        (onion.originY - ghost.originY) * state.zoom,
                        width * state.zoom, height * state.zoom);
      context.globalAlpha = 1;
    }
  }

  /**
   * A frame washed in one colour, so the two ghosts stay told apart from each
   * other and from the work even where all three are dark. The wash is partly
   * transparent rather than a flat silhouette: shape alone is not enough to
   * line up an eye or a weapon tip against the neighbouring frame.
   */
  function tintGhost(image, colour) {
    if (!ghostCanvas) {
      ghostCanvas = document.createElement('canvas');
      ghostContext = ghostCanvas.getContext('2d');
    }
    if (ghostCanvas.width !== image.naturalWidth || ghostCanvas.height !== image.naturalHeight) {
      ghostCanvas.width = image.naturalWidth;
      ghostCanvas.height = image.naturalHeight;
    }
    ghostContext.globalCompositeOperation = 'source-over';
    ghostContext.clearRect(0, 0, ghostCanvas.width, ghostCanvas.height);
    ghostContext.drawImage(image, 0, 0);
    // source-atop keeps the frame's own alpha, so the fill follows its shape.
    ghostContext.globalCompositeOperation = 'source-atop';
    ghostContext.fillStyle = colour;
    ghostContext.fillRect(0, 0, ghostCanvas.width, ghostCanvas.height);
    ghostContext.globalCompositeOperation = 'source-over';
    return ghostCanvas;
  }

  /**
   * animation.js has the same loader but does not export it, and this file may
   * not edit that one. Same contract: never rejects, never hangs -- one stalled
   * ghost must not leave the editor waiting.
   */
  function loadFrame(src) {
    return new Promise((resolve) => {
      const image = new Image();
      const done = (value) => { clearTimeout(timer); resolve(value); };
      const timer = setTimeout(() => done(null), 8000);
      image.onload = () => done(image);
      image.onerror = () => done(null);
      image.src = src;
    });
  }

  /* ---------- palette sampled from the sprite ---------- */
  function buildPalette() {
    const counts = new Map();
    const data = state.image.data;
    for (let i = 0; i < data.length; i += 4) {
      if (data[i + 3] === 0) continue;
      const key = `${data[i]},${data[i + 1]},${data[i + 2]},${data[i + 3]}`;
      counts.set(key, (counts.get(key) || 0) + 1);
    }
    const top = [...counts.entries()].sort((a, b) => b[1] - a[1]).slice(0, 24);

    clear(palette);
    for (const [key] of top) {
      const [r, g, b, a] = key.split(',').map(Number);
      palette.append(el('button', {
        title: `rgba(${r}, ${g}, ${b}, ${a})`,
        style: `width:22px;height:22px;border-radius:var(--r-xs);border:1px solid var(--border);
                background:rgba(${r},${g},${b},${a / 255})`,
        onclick: () => {
          state.colour = [r, g, b, a];
          swatch.value = `#${[r, g, b].map((c) => c.toString(16).padStart(2, '0')).join('')}`;
          alpha.value = String(a);
        },
      }));
    }
  }

  swatch.addEventListener('input', () => {
    const hex = swatch.value;
    state.colour = [
      parseInt(hex.slice(1, 3), 16),
      parseInt(hex.slice(3, 5), 16),
      parseInt(hex.slice(5, 7), 16),
      Number(alpha.value),
    ];
  });
  alpha.addEventListener('input', () => { state.colour[3] = Number(alpha.value); });

  /* ---------- pixel operations ---------- */
  const at = (x, y) => (y * state.width + x) * 4;
  const inside = (x, y) => x >= 0 && y >= 0 && x < state.width && y < state.height;

  function setPixel(x, y, colour) {
    if (!inside(x, y)) return;
    const i = at(x, y);
    // The changed count is maintained here rather than rescanned per repaint:
    // this is the hot path -- a Bresenham drag calls it once per pixel -- and
    // four byte compares either side of the write beat a full-frame count.
    const before = differsAt(i);
    state.image.data[i] = colour[0];
    state.image.data[i + 1] = colour[1];
    state.image.data[i + 2] = colour[2];
    state.image.data[i + 3] = colour[3];
    if (before !== differsAt(i)) state.changed += before ? -1 : 1;
  }

  function getPixel(x, y) {
    const i = at(x, y);
    return [state.image.data[i], state.image.data[i + 1], state.image.data[i + 2], state.image.data[i + 3]];
  }

  /** Bresenham, so dragging fast does not leave gaps. */
  function line(x0, y0, x1, y1, colour) {
    const dx = Math.abs(x1 - x0);
    const dy = Math.abs(y1 - y0);
    const sx = x0 < x1 ? 1 : -1;
    const sy = y0 < y1 ? 1 : -1;
    let error = dx - dy;
    for (;;) {
      setPixel(x0, y0, colour);
      if (x0 === x1 && y0 === y1) break;
      const e2 = error * 2;
      if (e2 > -dy) { error -= dy; x0 += sx; }
      if (e2 < dx) { error += dx; y0 += sy; }
    }
  }

  /** Scanline flood fill -- a recursive one blows the stack on a large sprite. */
  function fill(x, y, colour) {
    const target = getPixel(x, y);
    if (target.every((c, i) => c === colour[i])) return;

    const stack = [[x, y]];
    const matches = (px, py) => {
      const p = getPixel(px, py);
      return p[0] === target[0] && p[1] === target[1] && p[2] === target[2] && p[3] === target[3];
    };

    while (stack.length) {
      const [sx, sy] = stack.pop();
      if (!inside(sx, sy) || !matches(sx, sy)) continue;

      let left = sx;
      while (left > 0 && matches(left - 1, sy)) left--;
      let right = sx;
      while (right < state.width - 1 && matches(right + 1, sy)) right++;

      for (let i = left; i <= right; i++) {
        setPixel(i, sy, colour);
        if (sy > 0 && matches(i, sy - 1)) stack.push([i, sy - 1]);
        if (sy < state.height - 1 && matches(i, sy + 1)) stack.push([i, sy + 1]);
      }
    }
  }

  /* ---------- undo ---------- */
  function snapshot() {
    state.undo.push(new Uint8ClampedArray(state.image.data));
    if (state.undo.length > 60) state.undo.shift();
    state.redo.length = 0;
  }

  // Both of these replace the buffer wholesale, so the incremental count kept
  // by setPixel cannot follow them and has to be rebuilt.
  function undo() {
    if (!state.undo.length) return;
    state.redo.push(new Uint8ClampedArray(state.image.data));
    state.image.data.set(state.undo.pop());
    recount();
    paint();
  }

  function redo() {
    if (!state.redo.length) return;
    state.undo.push(new Uint8ClampedArray(state.image.data));
    state.image.data.set(state.redo.pop());
    recount();
    paint();
  }

  /* ---------- pointer ---------- */
  const toPixel = (event) => {
    const rect = view.getBoundingClientRect();
    return [
      Math.floor((event.clientX - rect.left) / state.zoom),
      Math.floor((event.clientY - rect.top) / state.zoom),
    ];
  };

  view.addEventListener('contextmenu', (event) => event.preventDefault());

  /* ---------- panning ---------- */
  // The stage is the only scroller, so panning is scrolling it. Doing it here
  // rather than leaning on the browser's own drag-scroll means it works at any
  // zoom and never begins a stroke.
  function beginPan(event) {
    state.panPointer = event.pointerId;
    state.panFrom = [event.clientX, event.clientY, stage.scrollLeft, stage.scrollTop];
    view.setPointerCapture(event.pointerId);
    view.style.cursor = 'grabbing';
  }

  function endPan() {
    state.panPointer = null;
    state.panFrom = null;
    view.style.cursor = state.panning ? 'grab' : 'crosshair';
  }

  /** The stroke's colour, decided once at pointerdown and used until it ends. */
  const strokeColour = () => (state.erasing || state.tool === 'eraser') ? [0, 0, 0, 0] : state.colour;

  view.addEventListener('pointerdown', (event) => {
    if (!state.image) return;
    event.preventDefault();

    // Before anything else, so a pan never lands an undo entry or a stray dot.
    // Middle-drag pans too; it is what every other canvas tool does.
    if (state.panning || event.button === 1) { beginPan(event); return; }

    view.setPointerCapture(event.pointerId);

    const [x, y] = toPixel(event);
    // A click on the exact right or bottom edge floors to width/height, which is
    // outside the sprite. Bail before snapshot(), which used to push a full undo
    // entry for an operation that then did nothing.
    if (!inside(x, y)) return;

    // Latched here rather than re-read per event: `button` is only meaningful on
    // pointerdown and `buttons` only during the drag, so a right-drag with the
    // line or rect tool used to commit in the paint colour.
    state.erasing = event.button === 2;

    if (state.tool === 'picker') {
      const picked = getPixel(x, y);
      state.colour = picked;
      swatch.value = `#${picked.slice(0, 3).map((c) => c.toString(16).padStart(2, '0')).join('')}`;
      alpha.value = String(picked[3]);
      return;
    }

    snapshot();
    state.drawing = true;
    state.last = [x, y];
    state.anchor = [x, y];

    const colour = strokeColour();
    if (state.tool === 'fill') { fill(x, y, colour); state.drawing = false; }
    else if (state.tool === 'pencil' || state.tool === 'eraser') setPixel(x, y, colour);
    paint();
  });

  view.addEventListener('pointermove', (event) => {
    if (!state.image) return;

    if (state.panPointer !== null) {
      const [fromX, fromY, left, top] = state.panFrom;
      stage.scrollLeft = left - (event.clientX - fromX);
      stage.scrollTop = top - (event.clientY - fromY);
      return;
    }

    const [x, y] = toPixel(event);
    state.cursor = inside(x, y) ? [x, y] : null;
    updateInfo();

    if (!state.drawing) return;
    if (state.tool === 'pencil' || state.tool === 'eraser') {
      line(state.last[0], state.last[1], x, y, strokeColour());
      state.last = [x, y];
      paint();
    }
  });

  view.addEventListener('pointerleave', () => {
    if (state.cursor) { state.cursor = null; updateInfo(); }
  });

  view.addEventListener('pointerup', (event) => {
    if (state.panPointer !== null) { endPan(); return; }
    if (!state.drawing) return;
    const [x, y] = toPixel(event);
    const colour = strokeColour();

    if (state.tool === 'line') {
      line(state.anchor[0], state.anchor[1], x, y, colour);
    } else if (state.tool === 'rect') {
      const [ax, ay] = state.anchor;
      line(ax, ay, x, ay, colour); line(x, ay, x, y, colour);
      line(x, y, ax, y, colour); line(ax, y, ax, ay, colour);
    }
    state.drawing = false;
    state.erasing = false;
    paint();
  });

  // A gesture the browser takes over -- a touch turning into a scroll, the
  // window losing focus mid-drag -- fires neither pointerup nor pointermove.
  // Without this the editor stayed in `drawing` and the next move drew a line
  // across the sprite from wherever the cancelled stroke stopped.
  const cancelStroke = () => {
    state.drawing = false;
    state.erasing = false;
    if (state.panPointer !== null) endPan();
  };
  view.addEventListener('pointercancel', cancelStroke);
  view.addEventListener('lostpointercapture', cancelStroke);

  // Ctrl (or Cmd) turns the wheel into a zoom; a bare wheel keeps the stage's
  // own scrolling. Steps are the same doubling the toolbar uses -- setZoom
  // rounds to whole pixels, so a gentler factor would round straight back at
  // the low zooms where a sprite is usually worked on.
  stage.addEventListener('wheel', (event) => {
    if (!state.image) return;
    if (event.ctrlKey || event.metaKey) {
      event.preventDefault();
      zoomAt(event.deltaY < 0 ? state.zoom * 2 : state.zoom / 2, event.clientX, event.clientY);
    } else if (event.shiftKey && event.deltaX === 0) {
      // Shift+wheel is horizontal scroll everywhere else, but browsers only
      // translate it for the viewport, not for a nested scroller.
      event.preventDefault();
      stage.scrollLeft += event.deltaY;
    }
  }, { passive: false });

  // Bound to this modal's scope: without the signal the handler outlives the
  // editor, retaining its ImageData and undo stack, and fires in every later
  // dialog -- typing 'b' in a form would run setTool() on a dead editor.
  dialog.addEventListener('keydown', (event) => {
    if (/^(INPUT|TEXTAREA|SELECT)$/.test(event.target.tagName) || event.target.isContentEditable) return;
    const mod = event.ctrlKey || event.metaKey;
    if (mod && event.key.toLowerCase() === 'z') {
      event.preventDefault();
      event.shiftKey ? redo() : undo();
      return;
    }
    // Every shortcut below is an unmodified key, so a Ctrl/Cmd combo is the
    // browser's (Ctrl+D bookmarks, Ctrl+P prints). Swallowing those to reach
    // the diff toggle or the pencil would be a genuine surprise; propagation
    // still stops here so the app-level shortcuts stay out of the modal.
    if (mod) { event.stopPropagation(); return; }

    // Held rather than toggled, so panning is a gesture you back out of by
    // letting go. preventDefault also stops Space activating whichever toolbar
    // button happens to have focus, and stops the page scrolling behind us.
    if (event.code === 'Space') {
      event.preventDefault();
      event.stopPropagation();
      if (!state.panning) {
        state.panning = true;
        if (state.panPointer === null) view.style.cursor = 'grab';
      }
      return;
    }

    // '\' is the A/B flip: held, so it is a glance rather than a mode.
    if (event.key === '\\') {
      event.preventDefault();
      event.stopPropagation();
      if (!state.flipping) { state.flipping = true; paint(); }
      return;
    }

    const map = { b: 'pencil', e: 'eraser', g: 'fill', i: 'picker', l: 'line', r: 'rect' };
    const key = event.key.toLowerCase();
    if (map[key]) { event.preventDefault(); setTool(map[key]); }
    else if (key === 'd') { event.preventDefault(); setDiff(!state.showDiff); }
    else if (key === 'o') { event.preventDefault(); setOnion(!state.onion); }
    else if (event.key === '+' || event.key === '=') { event.preventDefault(); setZoom(state.zoom * 2); }
    else if (event.key === '-') { event.preventDefault(); setZoom(state.zoom / 2); }
    event.stopPropagation();   // the app-level shortcuts must not also fire
  }, { signal: dialog.signal });

  dialog.addEventListener('keyup', (event) => {
    if (event.code === 'Space') {
      state.panning = false;
      if (state.panPointer === null) view.style.cursor = 'crosshair';
    } else if (event.key === '\\' && state.flipping) {
      state.flipping = false;
      paint();
    }
  }, { signal: dialog.signal });

  // A held key released over another window never delivers its keyup here,
  // which used to be the difference between a glance at the original and an
  // editor stuck showing it. Scoped to the dialog's signal like the rest.
  window.addEventListener('blur', () => {
    state.panning = false;
    if (state.panPointer === null) view.style.cursor = 'crosshair';
    if (state.flipping) { state.flipping = false; paint(); }
  }, { signal: dialog.signal });

  /* ---------- apply ---------- */
  function apply(done) {
    if (!state.image) { done(); return; }
    // The dialog stays open while the POST is in flight, so without this a
    // second click on Apply posted the sprite twice.
    if (state.applying) return;
    state.applying = true;

    const applyButton = dialog.querySelector('.modal-foot .btn-primary');
    if (applyButton) applyButton.disabled = true;
    const finish = () => {
      state.applying = false;
      if (applyButton) applyButton.disabled = false;
    };

    syncBuffer().toBlob(async (blob) => {
      if (!blob) {
        finish();
        toast('The browser could not encode this sprite as a PNG.', 'error',
          { title: 'Could not apply the image' });
        return;
      }
      try {
        const result = await api.replaceCanvas(node.path, blob);
        // The row thumbnail and every parent preview show this sprite and are
        // cached for an hour; without the bump they keep the pre-edit bitmap.
        bustThumb(node.path);
        done();
        toast('Canvas updated. Save the file to write it to disk.', 'success');
        if (result?.warning) toast(result.warning, 'warning', { title: 'Image format changed' });
        app.markDirty();
        app.inspector.refresh();
      } catch (error) {
        toastError(error, 'Could not apply the image');
      } finally {
        finish();
      }
    }, 'image/png');
  }
}
