/**
 * Animation player for WZ frame sequences.
 *
 * Frames in a MapleStory animation are cropped to their own bounds and are
 * positioned by an 'origin' vector. Drawing them all at (0,0) makes the sprite
 * jump around, so each frame is blitted at the offset the backend computed from
 * a shared anchor (anchor = the largest origin on each axis, so offsets are
 * never negative).
 */

import { api } from './api.js';
import { el, clear, toastError, scalePixelArt } from './ui.js';
import { canvasUrl } from './media.js';
import { icon } from './icons.js';

export class AnimationPlayer {
  constructor({ app }) {
    this.app = app;
    this.data = null;
    this.images = [];
    this.frame = 0;
    this.playing = false;
    this.speed = 1;
    this.loop = true;
    this.timer = null;
    this.showOrigin = false;
  }

  /**
   * Returns a section element if the node is an animation, otherwise null so
   * the inspector can skip it entirely.
   */
  async build(path) {
    // Listeners added below hang off this, so destroy() can remove them all.
    this.scope = new AbortController();

    let data;
    try {
      data = await api.animation(path);
    } catch (error) {
      // "Not an animation" is a 200 with isAnimation:false, and a vanished node
      // is a 404 -- both are silent by design. Anything else is a real failure,
      // and swallowing it rendered nothing with no way to tell why.
      if (error?.status !== 404) toastError(error, 'Could not read this animation');
      return null;
    }
    if (this.cancelled || !data.isAnimation || !data.frames.length) return null;

    this.data = data;
    this.frame = 0;
    this.playing = false;

    const canvas = el('canvas', {
      width: Math.max(1, data.width),
      height: Math.max(1, data.height),
      style: 'image-rendering:pixelated;max-width:100%',
    });
    this.canvas = canvas;

    // The composed canvas is at native resolution; CSS scales it up by a
    // whole number so a 40px sprite is actually visible.
    const scale = Math.max(1, Math.min(8, Math.floor(Math.min(320 / Math.max(1, data.width),
                                                              240 / Math.max(1, data.height)))));

    // Width in pixels, height left to the browser.
    //
    // Both used to be pinned in pixels alongside `max-width: 100%`. In a pane
    // too narrow for the scaled sprite -- a part-opened inspector, a dragged
    // splitter, a small window -- max-width shrank the width while the pinned
    // height stayed, so the animation played stretched. Height auto lets the
    // canvas keep its intrinsic ratio at any width, because a canvas is a
    // replaced element and its width/height attributes are that ratio.
    //
    // The trade is that a squeezed sprite lands on a fractional scale rather
    // than a whole one. `image-rendering: pixelated` keeps it blocky rather
    // than blurry, and a correct sprite at an awkward size beats a distorted
    // one at a tidy size.
    canvas.style.width = `${data.width * scale}px`;
    canvas.style.height = 'auto';
    canvas.style.maxHeight = `${data.height * scale}px`;

    const stage = el('div', { class: 'preview-stage checker', style: 'min-height:170px' }, canvas);

    const playButton = el('button', {
      class: 'btn btn-sm', 'data-tip': 'Play the animation',
      onclick: () => this.toggle(),
    }, icon('play', { size: 13 }), el('span', { text: 'Play' }));
    this.playButton = playButton;

    const scrubber = el('input', {
      type: 'range', min: '0', max: String(data.frames.length - 1), value: '0',
      style: 'flex:1', 'aria-label': 'Frame',
    });
    scrubber.addEventListener('input', () => {
      this.pause();
      this.frame = Number(scrubber.value);
      this.draw();
      this.updateLabel();
    });
    this.scrubber = scrubber;

    const speedSelect = el('select', { style: 'height:28px;border:1px solid var(--border);border-radius:var(--r-sm);background:var(--bg-field);color:var(--text-1)' },
      [0.25, 0.5, 1, 2, 4].map((s) => el('option', { value: s, text: `${s}×`, selected: s === 1 })));
    speedSelect.addEventListener('change', () => { this.speed = Number(speedSelect.value); });

    const label = el('div', {
      style: 'font-size:var(--fs-11);color:var(--text-3);font-variant-numeric:tabular-nums;margin-top:6px',
    });
    this.label = label;

    // Clicking a thumbnail selects that frame and opens it in the tree, which
    // is how you get from "that frame looks wrong" to editing it.
    const strip = el('div', { style: 'display:flex;gap:6px;overflow-x:auto;margin-top:10px;padding-bottom:4px' });
    data.frames.forEach((frame, index) => {
      const thumb = el('button', {
        class: 'btn btn-sm',
        title: `Frame ${frame.name} · ${frame.delay} ms · origin ${frame.originX}, ${frame.originY}`,
        style: 'flex:none;width:52px;height:52px;padding:2px;display:grid;place-items:center',
        onclick: () => {
          this.pause();
          this.frame = index;
          this.draw();
          this.updateLabel();
          this.app.navigate(frame.path);
        },
      }, (() => {
        const shot = el('img', { alt: '', style: 'image-rendering:pixelated' });
        shot.addEventListener('load', () => scalePixelArt(shot, 44, 44));
        shot.src = canvasUrl(frame.path);
        return shot;
      })());
      strip.append(thumb);
    });
    this.strip = strip;

    // Preload so playback does not stutter on the first pass.
    this.images = await Promise.all(data.frames.map((frame) => loadImage(canvasUrl(frame.path))));
    if (this.cancelled) return null;
    this.draw();
    this.updateLabel();

    return el('details', { class: 'insp-section', open: true },
      el('summary', { text: `Animation · ${data.frames.length} frames` }),
      stage,
      el('div', { style: 'display:flex;align-items:center;gap:8px;margin-top:10px' },
        playButton,
        el('button', { class: 'btn btn-sm btn-icon btn-ghost', 'data-tip': 'Previous frame',
          onclick: () => this.step(-1) }, icon('arrowLeft', { size: 14 })),
        el('button', { class: 'btn btn-sm btn-icon btn-ghost', 'data-tip': 'Next frame',
          onclick: () => this.step(1) }, icon('arrowRight', { size: 14 })),
        scrubber, speedSelect),
      el('div', { style: 'display:flex;gap:14px;margin-top:8px' },
        el('label', { class: 'check-row', style: 'font-size:var(--fs-12)' },
          el('input', { type: 'checkbox', checked: true, onchange: (e) => { this.loop = e.target.checked; } }), 'Loop'),
        el('label', { class: 'check-row', style: 'font-size:var(--fs-12)' },
          el('input', { type: 'checkbox', onchange: (e) => { this.showOrigin = e.target.checked; this.draw(); } }), 'Show origin')),
      label,
      strip);
  }

  draw() {
    if (!this.data || !this.canvas) return;
    const frame = this.data.frames[this.frame];
    const image = this.images[this.frame];
    const context = this.canvas.getContext('2d');

    context.imageSmoothingEnabled = false;
    context.clearRect(0, 0, this.canvas.width, this.canvas.height);
    if (image) context.drawImage(image, frame.offsetX, frame.offsetY);

    if (this.showOrigin) {
      // The anchor is where every frame's origin lands in the composed canvas.
      context.strokeStyle = 'rgba(255,80,80,0.9)';
      context.lineWidth = 1;
      context.beginPath();
      context.moveTo(this.data.anchorX + 0.5, 0);
      context.lineTo(this.data.anchorX + 0.5, this.canvas.height);
      context.moveTo(0, this.data.anchorY + 0.5);
      context.lineTo(this.canvas.width, this.data.anchorY + 0.5);
      context.stroke();
    }

    [...this.strip?.children ?? []].forEach((thumb, index) => {
      thumb.style.borderColor = index === this.frame ? 'var(--coral-fill)' : 'var(--border)';
    });
  }

  updateLabel() {
    if (!this.data) return;
    const frame = this.data.frames[this.frame];
    this.label.textContent =
      `Frame ${this.frame + 1} of ${this.data.frames.length}  ·  name "${frame.name}"  ·  ` +
      `${frame.delay} ms  ·  ${frame.width}×${frame.height}  ·  origin ${frame.originX}, ${frame.originY}  ·  ` +
      `total ${this.data.totalMs} ms`;
    if (this.scrubber) this.scrubber.value = String(this.frame);
  }

  step(delta) {
    this.pause();
    const count = this.data.frames.length;
    this.frame = (this.frame + delta + count) % count;
    this.draw();
    this.updateLabel();
  }

  toggle() {
    if (this.playing) this.pause();
    else this.play();
  }

  play() {
    if (!this.data) return;
    this.playing = true;
    if (this.playButton) this.paintPlayButton(this.playButton);
    this.tick();
  }

  /**
   * Repainting lives here rather than at the call sites, because the scrubber,
   * the frame steppers and the end of a non-looping run all stop playback and
   * all used to leave the button reading "Pause".
   */
  pause() {
    this.playing = false;
    clearTimeout(this.timer);
    if (this.playButton) this.paintPlayButton(this.playButton);
  }

  /** Keeps the transport button's icon and label in step with playback. */
  paintPlayButton(button) {
    clear(button);
    button.append(icon(this.playing ? 'pause' : 'play', { size: 13 }),
                  el('span', { text: this.playing ? 'Pause' : 'Play' }));
  }

  tick() {
    if (!this.playing) return;
    const frame = this.data.frames[this.frame];
    // Delays are per-frame, so schedule the next tick rather than using a
    // fixed interval.
    this.timer = setTimeout(() => {
      if (!this.playing) return;
      const next = this.frame + 1;
      if (next >= this.data.frames.length && !this.loop) { this.pause(); return; }
      this.frame = next % this.data.frames.length;
      this.draw();
      this.updateLabel();
      this.tick();
    }, Math.max(16, frame.delay / this.speed));
  }

  /**
   * Releases everything the player is holding.
   *
   * The inspector correctly detects a stale player, but this used to only stop
   * the timer -- leaving N decoded frames, five listeners and an in-flight
   * preload batch alive per node visited. Twenty animation nodes meant twenty
   * abandoned download batches competing with the thumbnails on screen.
   */
  destroy() {
    this.pause();
    this.cancelled = true;
    this.scope?.abort();
    this.images = [];
    this.data = null;
    this.canvas = null;
    this.strip = null;
    this.playButton = null;
    this.scrubber = null;
  }
}

function loadImage(src) {
  return new Promise((resolve) => {
    const image = new Image();
    // Never rejects, and never hangs: one stalled frame would otherwise wedge
    // the whole preload and keep everything behind it alive.
    const done = (value) => { clearTimeout(timer); resolve(value); };
    const timer = setTimeout(() => done(null), 8000);
    image.onload = () => done(image);
    image.onerror = () => done(null);
    image.src = src;
  });
}
