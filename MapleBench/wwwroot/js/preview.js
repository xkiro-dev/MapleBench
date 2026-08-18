/**
 * Hover preview.
 *
 * Hovering a recognizable asset entry shows its sprite. Archive roots, broad
 * folders and editable fields are deliberately excluded: showing the first
 * unrelated sprite found below Map.wz labels that sprite as "Map.wz" and makes
 * the preview look authoritative when it is not.
 *
 * The backend caps its search and sets a cache header, and misses are
 * remembered here, so scrolling a list of 2,000 rows costs at most one request
 * per row and usually none.
 */

import { thumbUrl, mediaEpoch } from './media.js';
import { el, clear, scalePixelArt } from './ui.js';
import { suppressTips } from './tooltip.js';

const OPEN_DELAY = 400;
const WARM_DELAY = 100;
const WARM_WINDOW = 600;
const PREVIEW_SELECTOR = [
  '.tree-row[data-path]', '.child-row[data-path]', '.mob-card[data-path]',
  '.npc-card[data-path]', '.sk-card[data-path]', '.thumb[data-preview-label]',
].join(',');

/** path -> 'hit' | 'miss'; avoids re-requesting sprites that do not exist. */
const known = new Map();
let knownEpoch = mediaEpoch();

let host = null;
let openTimer = null;
let closeTimer = null;
let lastClosedAt = 0;
let currentPath = null;

function ensureHost() {
  if (host) return host;
  host = el('div', { class: 'hover-preview' });
  document.body.append(host);
  return host;
}

function place(anchor) {
  const rect = anchor.getBoundingClientRect();
  const node = ensureHost();
  const size = node.getBoundingClientRect();
  const gap = 12;

  // Prefer the right of the row; flip to the left when there is no room.
  let left = rect.right + gap;
  if (left + size.width > window.innerWidth - 12) left = rect.left - size.width - gap;
  if (left < 12) left = 12;

  let top = rect.top + (rect.height / 2) - (size.height / 2);
  top = Math.max(12, Math.min(top, window.innerHeight - size.height - 12));

  node.style.left = `${left}px`;
  node.style.top = `${top}px`;
}

function show(anchor, path, label) {
  const node = ensureHost();
  clear(node);
  currentPath = path;

  const stage = el('div', { class: 'hover-preview-stage checker' });
  const image = el('img', { alt: '' });

  const reveal = () => {
    // A 204 (no image) resolves with no dimensions in some browsers.
    if (!image.naturalWidth) { known.set(path, 'miss'); hide(); return; }
    known.set(path, 'hit');
    const scale = scalePixelArt(image, 216, 190);

    clear(node);
    stage.append(image);
    node.append(
      stage,
      el('div', { class: 'hover-preview-cap' },
        el('div', { class: 'hp-name', text: label }),
        el('div', { class: 'hp-meta num',
          text: `${image.naturalWidth} x ${image.naturalHeight}` +
                (scale > 1 ? `  \u00b7  ${scale}x` : '') })));
    node.dataset.open = 'true';
    // Two popovers naming the same row, on top of each other, over the list
    // the user is trying to read. This card says everything the text tip does
    // and adds the picture, so the tip stands down for as long as it is up.
    suppressTips(true);
    place(anchor);
  };

  image.addEventListener('load', reveal);
  image.addEventListener('error', () => { known.set(path, 'miss'); hide(); });

  // Set src only after the listeners exist: a cached sprite fires `load`
  // immediately, and assigning src first would lose that event entirely.
  image.src = thumbUrl(path);
  if (image.complete) reveal();
}

function hide() {
  suppressTips(false);
  if (!host) return;
  host.dataset.open = 'false';
  clear(host);
  currentPath = null;
  lastClosedAt = Date.now();
}

function scheduleShow(row) {
  const path = row.dataset.path;
  if (!path || path === currentPath) return;
  const namedCard = row.matches('.mob-card, .npc-card, .sk-card, .thumb[data-preview-label]');
  if (!namedCard && row.dataset.kind !== 'Image' && row.dataset.type !== 'Canvas') return;

  // These are session paths, and a session id is reused for a different archive
  // after a close, so a remembered "miss" would suppress a sprite that exists.
  if (knownEpoch !== mediaEpoch()) { knownEpoch = mediaEpoch(); known.clear(); }
  if (known.get(path) === 'miss') return;

  clearTimeout(openTimer);
  clearTimeout(closeTimer);
  const warm = Date.now() - lastClosedAt < WARM_WINDOW;
  const label = row.dataset.previewLabel
    || row.querySelector('.tree-label, .child-name span')?.textContent
    || '';
  openTimer = setTimeout(() => show(row, path, label), warm ? WARM_DELAY : OPEN_DELAY);
}

function scheduleHide() {
  clearTimeout(openTimer);
  clearTimeout(closeTimer);
  closeTimer = setTimeout(hide, 120);
}

export function installHoverPreview() {
  document.addEventListener('pointerover', (event) => {
    const row = event.target?.closest?.(PREVIEW_SELECTOR);
    if (row) scheduleShow(row);
    else if (currentPath) scheduleHide();
  });
  document.addEventListener('pointerout', (event) => {
    if (event.target?.closest?.(PREVIEW_SELECTOR)) scheduleHide();
  });
  window.addEventListener('scroll', hide, true);
  window.addEventListener('resize', hide);
  document.addEventListener('click', hide, true);
  document.addEventListener('keydown', (event) => { if (event.key === 'Escape') hide(); });
}
