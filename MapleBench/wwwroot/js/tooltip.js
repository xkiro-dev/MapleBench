/**
 * One global tooltip, driven by [data-tip] on any element.
 *
 * Native `title` was the wrong tool: it waits about a second, cannot be styled,
 * never appears on keyboard focus, and vanishes on touch. This shows after a
 * short delay, stays warm while you scan a toolbar, flips to stay on screen,
 * and appears immediately on :focus-visible so keyboard users get it too.
 */

const OPEN_DELAY = 350;
const WARM_DELAY = 75;      // once one has shown, neighbours appear almost instantly
const CLOSE_DELAY = 75;
const WARM_WINDOW = 500;    // how long "warm" lasts after the last tooltip closed

let host = null;
let openTimer = null;
let closeTimer = null;
let lastClosedAt = 0;
let current = null;

function ensureHost() {
  if (host) return host;
  host = document.createElement('div');
  host.className = 'tip';
  host.setAttribute('role', 'tooltip');
  document.body.append(host);
  return host;
}

function show(target) {
  const text = target.dataset.tip;
  // A re-render can destroy the anchor between the delay and this call.
  if (!text || !target.isConnected) return;

  const node = ensureHost();
  node.textContent = '';

  // "Label · Shortcut" renders the shortcut as a key cap. Split on the FIRST
  // separator only: a label containing one of its own would otherwise lose
  // everything after it.
  const cut = text.indexOf('·');
  const label = (cut < 0 ? text : text.slice(0, cut)).trim();
  const shortcut = cut < 0 ? '' : text.slice(cut + 1).trim();

  node.append(document.createTextNode(label));
  if (shortcut) {
    const kbd = document.createElement('kbd');
    kbd.textContent = shortcut;
    node.append(kbd);
  }

  node.dataset.open = 'true';
  current = target;
  position(target, node);
  watchAnchor();
}

/**
 * Dismisses the tooltip when its anchor leaves the document.
 *
 * Nothing else can: `pointerout` never fires for an element that was removed
 * under the cursor, so a re-render while a tooltip was open used to strand it
 * pointing at nothing. The observer only runs while one is visible.
 */
let anchorWatch = null;
function watchAnchor() {
  anchorWatch ??= new MutationObserver(() => {
    if (current && !current.isConnected) hide();
  });
  anchorWatch.observe(document.body, { childList: true, subtree: true });
}

function position(target, node) {
  const rect = target.getBoundingClientRect();
  const placement = target.dataset.tipPlacement || 'bottom';

  // Measure with the tooltip already visible but off-screen.
  node.style.left = '-9999px';
  node.style.top = '-9999px';
  const size = node.getBoundingClientRect();
  const gap = 8;
  const margin = 8;

  let top;
  let left = rect.left + (rect.width / 2) - (size.width / 2);

  if (placement === 'right') {
    left = rect.right + gap;
    top = rect.top + (rect.height / 2) - (size.height / 2);
  } else if (placement === 'top') {
    top = rect.top - size.height - gap;
  } else {
    top = rect.bottom + gap;
  }

  // Flip rather than run off the bottom of the window.
  if (placement === 'bottom' && top + size.height > window.innerHeight - margin) {
    top = rect.top - size.height - gap;
  }

  // Clamped on both axes: a right-placed tooltip on a control near the bottom
  // of the window used to render off-screen entirely.
  node.style.left = `${Math.max(margin, Math.min(left, window.innerWidth - size.width - margin))}px`;
  node.style.top = `${Math.max(margin, Math.min(top, window.innerHeight - size.height - margin))}px`;
}

function hide() {
  anchorWatch?.disconnect();
  if (!host) return;
  host.dataset.open = 'false';
  current = null;
  lastClosedAt = Date.now();
}

/**
 * Held down while the hover preview is up.
 *
 * Both open off the same pointerover on the same row, and both name the row --
 * so hovering a tree row produced a dark chip reading "Mob.wz" underneath a
 * 236px card reading "Mob.wz · 71 x 66 · 2x", overlapping each other on top of
 * the list. The card is the better answer, and the one that is showing you
 * something you could not otherwise see, so the chip stands down.
 */
let suppressed = false;
export function suppressTips(on) {
  suppressed = on;
  if (on) { clearTimeout(openTimer); hide(); }
}

function scheduleShow(target) {
  clearTimeout(openTimer);
  clearTimeout(closeTimer);
  if (suppressed || current === target) return;

  const warm = Date.now() - lastClosedAt < WARM_WINDOW;
  openTimer = setTimeout(() => show(target), warm ? WARM_DELAY : OPEN_DELAY);
}

function scheduleHide() {
  clearTimeout(openTimer);
  clearTimeout(closeTimer);
  closeTimer = setTimeout(hide, CLOSE_DELAY);
}

export function installTooltips() {
  const find = (event) => event.target?.closest?.('[data-tip]');

  document.addEventListener('pointerover', (event) => {
    const target = find(event);
    if (target) scheduleShow(target);
    else if (current) scheduleHide();
  });

  document.addEventListener('pointerout', (event) => {
    if (find(event)) scheduleHide();
  });

  // Keyboard users get it with no delay at all.
  document.addEventListener('focusin', (event) => {
    const target = find(event);
    if (target && target.matches(':focus-visible')) {
      // Both timers: tabbing to a control the mouse has just left leaves a
      // pending close, which made the tooltip flash and vanish.
      clearTimeout(openTimer);
      clearTimeout(closeTimer);
      show(target);
    }
  });
  document.addEventListener('focusout', () => scheduleHide());

  // Anything that changes the layout under the cursor should dismiss it.
  document.addEventListener('click', hide, true);
  window.addEventListener('scroll', hide, true);
  window.addEventListener('resize', hide);
  document.addEventListener('keydown', (event) => { if (event.key === 'Escape') hide(); });
}

/** Convenience for elements built in JS. */
export const tip = (text, placement) =>
  placement ? { 'data-tip': text, 'data-tip-placement': placement } : { 'data-tip': text };
