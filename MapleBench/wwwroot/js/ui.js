/**
 * Shared UI primitives: DOM helpers, toasts, modals and the confirm dialog.
 */

import { icon } from './icons.js';

export const el = (tag, attrs = {}, ...children) => {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(attrs)) {
    if (value === null || value === undefined || value === false) continue;
    if (key === 'class') node.className = value;
    else if (key === 'text') node.textContent = value;
    else if (key === 'html') node.innerHTML = value;
    else if (key.startsWith('on')) node.addEventListener(key.slice(2).toLowerCase(), value);
    else node.setAttribute(key, value === true ? '' : value);
  }
  for (const child of children.flat()) {
    if (child === null || child === undefined || child === false) continue;
    node.append(child.nodeType ? child : document.createTextNode(String(child)));
  }
  return node;
};

export const $ = (selector, root = document) => root.querySelector(selector);
export const clear = (node) => { while (node.firstChild) node.removeChild(node.firstChild); };

export const fmt = new Intl.NumberFormat();

/**
 * Natural-order comparator: "9.img" sorts before "10.img", and animation
 * frames 0,1,2,10,11 stay in their real numeric order rather than 0,1,10,11,2.
 */
const collator = new Intl.Collator(undefined, { numeric: true, sensitivity: 'base' });

/** Sort modes offered wherever a list of WZ children is shown. */
export const SORT_MODES = [
  ['name', 'Name (A to Z)'],
  ['name-desc', 'Name (Z to A)'],
  ['type', 'Type, then name'],
  ['wz', 'File order'],
];

/**
 * Orders a list of node DTOs. Containers always come before leaves so a
 * directory of folders and images reads as a hierarchy, not a jumble --
 * except in 'wz' mode, which is the untouched on-disk order.
 */
export function sortNodes(nodes, mode = 'name') {
  if (mode === 'wz') return nodes;

  const rank = (node) => {
    if (node.kind === 'Directory') return 0;
    if (node.kind === 'Image') return 1;
    return node.hasChildren ? 2 : 3;
  };

  const sorted = [...nodes];
  sorted.sort((a, b) => {
    if (mode === 'type') {
      const byType = collator.compare(a.type ?? a.kind, b.type ?? b.kind);
      if (byType !== 0) return byType;
      return collator.compare(a.name ?? '', b.name ?? '');
    }
    const byRank = rank(a) - rank(b);
    if (byRank !== 0) return byRank;
    const byName = collator.compare(a.name ?? '', b.name ?? '');
    return mode === 'name-desc' ? -byName : byName;
  });
  return sorted;
}

/**
 * Sizes a sprite to fill its box.
 *
 * WZ art is tiny -- a 32x31 icon in a 150px frame is 95% empty checkerboard.
 * Upscaling uses whole-number factors only, because a fractional nearest-
 * neighbour scale makes some pixels one screen-pixel wider than their
 * neighbours and the sprite visibly wobbles. Oversized art has no integer
 * option, so it falls back to a plain fractional shrink.
 *
 * Returns the factor applied, for captions like "4x".
 */
export function scalePixelArt(image, maxWidth, maxHeight, { max = 12 } = {}) {
  const w = image.naturalWidth;
  const h = image.naturalHeight;
  if (!w || !h) return 1;

  const raw = Math.min(maxWidth / w, maxHeight / h);
  const scale = raw >= 1 ? Math.min(max, Math.floor(raw)) : raw;

  image.style.width = `${Math.round(w * scale)}px`;
  image.style.height = `${Math.round(h * scale)}px`;
  image.style.maxWidth = 'none';
  image.style.maxHeight = 'none';
  return scale;
}

export function debounce(fn, ms) {
  let timer;
  return (...args) => {
    clearTimeout(timer);
    timer = setTimeout(() => fn(...args), ms);
  };
}

/* ============================================================
   MOTION LIFECYCLE

   CSS owns how a surface moves; these helpers own when it may be hidden or
   removed. Keeping that distinction in one place prevents the old pattern
   where dialogs, menus and toasts animated in, then vanished before an exit
   could paint. A timeout is always the final authority so a missed
   animationend can never leave an invisible surface blocking the app.
   ============================================================ */

const motionJobs = new WeakMap();

export const reducedMotion = () =>
  window.matchMedia?.('(prefers-reduced-motion: reduce)').matches === true;

function settleMotion(node, state, timeout, complete) {
  motionJobs.get(node)?.cancel();

  let timer = null;
  let settled = false;
  let resolvePromise;
  const promise = new Promise((resolve) => { resolvePromise = resolve; });
  const onEnd = (event) => {
    if (event.target === node) finish(true);
  };
  const finish = (commit) => {
    if (settled) return;
    settled = true;
    clearTimeout(timer);
    node.removeEventListener('animationend', onEnd);
    node.removeEventListener('animationcancel', onEnd);
    if (motionJobs.get(node)?.cancel === cancel) motionJobs.delete(node);
    if (commit) complete();
    resolvePromise(commit);
  };
  const cancel = () => finish(false);

  motionJobs.set(node, { cancel });
  node.dataset.motion = state;
  node.addEventListener('animationend', onEnd);
  node.addEventListener('animationcancel', onEnd);
  timer = setTimeout(() => finish(true), reducedMotion() ? 0 : timeout);
  return promise;
}

/** Starts/restarts an entrance without replacing the element or its focus. */
export function motionIn(node, { unhide = true, timeout = 260 } = {}) {
  if (!node) return Promise.resolve(false);
  motionJobs.get(node)?.cancel();
  if (unhide) node.hidden = false;
  delete node.dataset.motion;
  // Reopening while an exit is running needs a fresh animation timeline.
  if (node.isConnected) void node.offsetWidth;
  return settleMotion(node, 'opening', timeout, () => {
    if (node.dataset.motion === 'opening') delete node.dataset.motion;
  });
}

/** Lets a surface finish its exit before it is hidden or removed. */
export function motionOut(node, {
  remove = false, hide = !remove, timeout = 180,
} = {}) {
  if (!node) return Promise.resolve(false);
  if (!node.isConnected) {
    if (remove) node.remove();
    else if (hide) node.hidden = true;
    return Promise.resolve(true);
  }
  return settleMotion(node, 'closing', timeout, () => {
    if (remove) node.remove();
    else if (hide) node.hidden = true;
    if (node.dataset.motion === 'closing') delete node.dataset.motion;
  });
}

/** Replays a small CSS entrance on a stable workspace or section element. */
export function replayMotion(node, name) {
  if (!node || reducedMotion()) return;
  node.dataset.motion = '';
  void node.offsetWidth;
  node.dataset.motion = name;
  let timer = null;
  const cleanup = () => {
    clearTimeout(timer);
    node.removeEventListener('animationend', clearState);
    if (node.dataset.motion === name) delete node.dataset.motion;
  };
  const clearState = (event) => {
    if (event.target === node) cleanup();
  };
  node.addEventListener('animationend', clearState);
  timer = setTimeout(cleanup, 360);
}

/* ============================================================
   COUNT CHIPS

   Six sections had their own copy of the same three-line `chip` helper, and
   between them they put fifteen boxes on screen that were already answered by
   the control directly underneath -- Database's ITEMS/MOBS/SKILLS/NPCS/MAPS
   tiles sat 90px above five filter pills of the same five words, and only the
   pills could be clicked. One helper now, with one rule about what earns a box.
   ============================================================ */

/**
 * One count, in a row of counts.
 *
 * `value` carries three states and they are not the same thing. A number is a
 * fact. `null` is "not asked yet, or could not be read", and shows an em dash
 * rather than 0 -- a zero over an archive nobody has read says the archive is
 * empty, which is a different and false claim. And 0 is a fact that is not
 * worth a box: a row that reads "Both 0 · Broken formulas 0" spends two boxes
 * saying nothing happened, and the two that matter get skipped along with them.
 * Pass `drop: false` where a zero is the point (a total that really is zero).
 */
export function statChip(label, value, { tone, tip, text, drop = true } = {}) {
  const known = value !== null && value !== undefined && value !== '';
  if (drop && known && (value === 0 || value === '0')) return null;

  const shown = text
    ?? (known ? (typeof value === 'number' ? fmt.format(value) : String(value)) : '—');

  return el('div', { class: 'stat-chip', 'data-tip': tip || null, 'data-known': known ? 'true' : 'false' },
    el('span', { class: 'stat-label', text: label }),
    el('span', { class: 'stat-value', 'data-tone': tone || null, text: shown }));
}

/**
 * The row those chips sit in, or nothing at all.
 *
 * Returns null when every chip is unknown, because a row of six em dashes is a
 * row of six labelled empty boxes: it occupies the 90px directly above the
 * search field that would otherwise have answered the question, and it says
 * only what the empty state below it already says better.
 */
export function statRow(className, chips) {
  const live = chips.filter(Boolean);
  if (!live.length || live.every((chip) => chip.dataset.known === 'false')) return null;
  return el('div', { class: className }, live);
}

/**
 * A search hit's ancestor chain, ready to read.
 *
 * Nothing but the decode now. This used to strip a leading `f1 / ` as well,
 * because `context` was built from the session path and every one of two
 * thousand rows read `Etc.wz  f1 / ScrollIcon.img` -- an opaque handle beside
 * the archive name it is the handle *for*. The server stopped putting it there
 * (SearchHitDto.Context), so the strip had nothing left to strip HERE; it is
 * still exactly right one function down, where the input really is a session
 * path, and that is where it now lives.
 */
export function humanContext(context) {
  return decodeURIComponent(String(context ?? ''));
}

/**
 * A session path with the internal archive id taken off the front.
 *
 * `f1/Achievement` is the right string to send to the API and the wrong one to
 * show: `f1` is assigned in the order files were opened, so the same archive is
 * f1 in one session and f3 in the next. Callers pair what is left with the
 * archive's real name; an archive root strips to nothing, which is the correct
 * answer for "what is below the root".
 */
export function withoutFileId(path) {
  return decodeURIComponent(String(path ?? ''))
    .replace(/^f\d+\s*\/\s*/, '')
    .replace(/^f\d+$/, '');
}

/* ============================================================
   TELLING TWO ARCHIVES APART
   ============================================================ */

/**
 * A short qualifier for every open archive whose name is not unique.
 *
 * Two clients open at once means two rows called String.wz, one above the
 * other, identical in the tree, in the breadcrumb, in the inspector and in the
 * status bar -- and one of them is the one you are about to edit. Returns a
 * Map of file id -> qualifier, with an entry only for the names that actually
 * collide; a client opened on its own gets nothing added to any row.
 *
 * The qualifier is the shallowest run of trailing folder names that separates
 * the group. Folders named after the archive itself are skipped first: the
 * modern split layout stores String.wz inside a folder called String, and
 * "String.wz (String)" tells nobody anything.
 */
export function archiveQualifiers(files) {
  const groups = new Map();
  for (const file of files) {
    const key = (file.name || '').toLowerCase();
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(file);
  }

  const qualifiers = new Map();
  for (const [, group] of groups) {
    if (group.length < 2) continue;

    const base = (group[0].name || '').replace(/\.[^.]*$/, '').toLowerCase();
    const trails = group.map((file) => {
      const parts = String(file.filePath || '').split(/[\\/]+/).filter(Boolean);
      parts.pop();                                   // the archive's own filename
      while (parts.length && parts[parts.length - 1].toLowerCase() === base) parts.pop();
      return parts;
    });

    // Grow the tail one folder at a time until every member of the group has a
    // different one, then stop -- the shortest thing that answers the question.
    const deepest = Math.max(...trails.map((parts) => parts.length));
    let labels = trails.map(() => '');
    for (let depth = 1; depth <= Math.max(1, deepest); depth++) {
      labels = trails.map((parts) => parts.slice(-depth).join('\\') || '?');
      if (new Set(labels).size === labels.length) break;
    }

    group.forEach((file, index) => qualifiers.set(file.id, labels[index]));
  }
  return qualifiers;
}

/* ============================================================
   TOASTS
   Success/info auto-dismiss; errors never do, because an error the
   user did not read is an error they will hit again.
   ============================================================ */

const DURATIONS = { success: 4000, info: 5000, warning: 7000, error: 0 };
const TOAST_ICONS = { success: 'check', error: 'alert', warning: 'alert', info: 'info' };
const host = () => document.getElementById('toasts');
const toastDismissers = new WeakMap();

export function toast(message, kind = 'info', { title, action } = {}) {
  const node = el('div', {
    class: 'toast',
    'data-kind': kind,
    role: kind === 'error' ? 'alert' : 'status',
  });

  node.append(el('span', { class: 'toast-glyph' }, icon(TOAST_ICONS[kind] ?? 'info', { size: 17 })));

  const text = el('div', { class: 'toast-text' });
  if (title) text.append(el('div', { class: 'toast-title', text: title }));
  text.append(document.createTextNode(message));
  node.append(text);

  let timer = null;
  let dismissed = false;
  const removeFromStack = () => {
    const siblings = [...host().querySelectorAll('.toast')].filter((item) => item !== node);
    const before = new Map(siblings.map((item) => [item, item.getBoundingClientRect()]));
    toastDismissers.delete(node);
    node.remove();
    if (reducedMotion()) return;
    for (const item of siblings) {
      if (!item.isConnected) continue;
      const old = before.get(item);
      const now = item.getBoundingClientRect();
      const dy = old.top - now.top;
      if (Math.abs(dy) < 1) continue;
      item.animate([
        { transform: `translateY(${dy}px)` },
        { transform: 'translateY(0)' },
      ], { duration: 160, easing: 'cubic-bezier(0.16, 1, 0.3, 1)' });
    }
  };
  const dismiss = () => {
    if (dismissed) return;
    dismissed = true;
    clearTimeout(timer);
    motionOut(node, { hide: false, timeout: 150 }).then((finished) => {
      if (finished) removeFromStack();
    });
  };
  toastDismissers.set(node, dismiss);

  if (action) {
    node.append(el('button', {
      class: 'btn btn-sm',
      text: action.label,
      onclick: () => { dismiss(); action.run(); },
    }));
  }
  node.append(el('button', {
    class: 'btn btn-sm btn-icon btn-ghost', 'aria-label': 'Dismiss',
    onclick: dismiss,
  }, icon('close', { size: 14 })));

  host().append(node);
  motionIn(node, { unhide: false, timeout: 220 });

  // Keep at most three; the oldest goes rather than growing a wall.
  const all = host().querySelectorAll('.toast');
  if (all.length > 3) {
    const oldest = all[0];
    (toastDismissers.get(oldest) ?? (() => oldest.remove()))();
  }

  const ms = DURATIONS[kind] ?? 5000;
  if (ms > 0) {
    timer = setTimeout(dismiss, ms);
    // Pause while the pointer or focus is on it, so it can be read and acted on.
    node.addEventListener('mouseenter', () => clearTimeout(timer));
    node.addEventListener('focusin', () => clearTimeout(timer));
    const resume = () => { timer = setTimeout(() => node.remove(), 2000); };
    node.addEventListener('mouseleave', resume);
    node.addEventListener('focusout', resume);
  }
  return node;
}

// The default title is the fallback for the two callers that pass none. It
// matches the "Could not X" family every other call site uses, and it says the
// one thing the user needs before reading the detail: the action did not happen.
export const toastError = (error, title = 'Could not finish that') => {
  const message = error?.message ?? String(error);
  return toast(error?.detail ? `${message}\n${error.detail}` : message, 'error', { title });
};

/* ============================================================
   ONE AT A TIME
   ============================================================ */

const inFlight = new Set();

/**
 * Runs an async action at most once at a time, keyed.
 *
 * Nearly every mutating button in the app stays live while its request is in
 * the air, and most of them have a keyboard shortcut and a menu entry pointing
 * at the same code -- so a second trigger issued the write twice. A save is the
 * worst of them: two of them verify and swap the same archive against each
 * other. A call made while its key is busy is dropped, not queued.
 */
export async function runOnce(key, run) {
  if (inFlight.has(key)) return;
  inFlight.add(key);
  try {
    await run();
  } finally {
    inFlight.delete(key);
  }
}

/** Whether runOnce is currently holding `key`; for entry points that want to say so. */
export const isRunning = (key) => inFlight.has(key);

/* ============================================================
   WAITING

   A wz archive is a gigabyte and the work behind a screen is measured in
   seconds, not milliseconds. That is not going away entirely, so the rule this
   app holds itself to is: no wait is ever silent, and no wait is ever shown as
   something else.

   The second half is the one that bites. A section that paints "No Mob.wz open"
   while it is busy reading Mob.wz has not merely failed to reassure -- it has
   told the user something false about their own files, and they go off to
   re-open an archive that was open all along.

   These are the shared pieces. They existed as twelve copies of the same
   skeleton loop and two hand-rolled busy bars before they lived here.
   ============================================================ */

/**
 * Placeholder rows in the shape of a list. The widths are staggered by a fixed
 * pattern rather than at random, so a repaint does not reshuffle them and the
 * panel reads as one thing loading instead of a flicker.
 */
export function skeletonRows(count = 6) {
  const host = document.createDocumentFragment();
  for (let i = 0; i < count; i++) {
    host.append(el('div', { class: 'skeleton skeleton-row', style: `width:${45 + ((i * 37) % 40)}%` }));
  }
  return host;
}

/**
 * The first-load panel: a moving bar, a sentence, and rows in the shape of what
 * is coming.
 *
 * `note` should say what is being read and roughly how long it takes, in real
 * measured numbers. "About ten seconds the first time" is a wait; ten
 * unexplained seconds is a bug report.
 */
export function busyPanel({ title, note, rows = 8, className = '' }) {
  return el('div', { class: `busy-panel busy-host ${className}`.trim() },
    el('div', { class: 'busy-bar' }),
    el('div', { class: 'busy-panel-text' },
      el('b', { text: title }),
      note ? el('span', { text: ` ${note}` }) : null),
    el('div', { class: 'busy-panel-rows' }, skeletonRows(rows)));
}

/**
 * Puts the indeterminate bar at the top of `host` and returns the remover.
 *
 * For the reload case, where there is already content on screen worth keeping:
 * replacing a full grid with skeletons because one value changed is a worse
 * experience than the wait it was meant to explain.
 */
export function busyBar(host) {
  if (!host) return () => {};
  host.classList.add('busy-host');
  const bar = el('div', { class: 'busy-bar' });
  host.prepend(bar);
  return () => {
    bar.remove();
    // Only drop the positioning class if nothing else is still using it --
    // two overlapping reloads would otherwise have the first one finish and
    // strip the containment out from under the second's bar.
    if (!host.querySelector(':scope > .busy-bar')) host.classList.remove('busy-host');
  };
}

/**
 * Marks a node as busy for the life of a promise. Styling comes from
 * `[aria-busy="true"]`, so this also tells a screen reader.
 *
 * Returns the promise untouched, including its rejection: this is decoration
 * and must never swallow an error the caller is about to report.
 */
export function busyWhile(node, promise) {
  node?.setAttribute?.('aria-busy', 'true');
  return promise.finally(() => node?.removeAttribute?.('aria-busy'));
}

/**
 * Disables a button and says what it is doing, returning the restore function.
 *
 * The label matters as much as the disable. A greyed-out button with its
 * original text reads as "not allowed"; one that says "Saving…" reads as
 * "working", and those are opposite messages.
 */
export function busyButton(button, label = 'Working…') {
  if (!button) return () => {};
  const wasDisabled = button.disabled;
  const before = button.innerHTML;
  button.disabled = true;
  button.textContent = label;
  return () => {
    button.disabled = wasDisabled;
    button.innerHTML = before;
  };
}

/* ============================================================
   MODALS
   Native <dialog> gives the focus trap, the top layer and Esc for free.
   ============================================================ */

export function modal({ title, subtitle, body, actions = [], width, onOpen }) {
  // A fresh <dialog> per modal, removed when it closes. One shared element
  // could only ever hold one modal, so opening a second from inside the first
  // -- a confirm over a half-filled form -- emptied the element that form was
  // living in, and cancelling returned to nothing. The top layer stacks
  // dialogs correctly, so nesting works once each owns its own element.
  const dialog = el('dialog');
  dialog.style.width = width || '';

  // Listeners the body hangs on the dialog element itself go with it, but the
  // signal lets a long-lived one -- the pixel editor's keyboard map -- stop
  // firing the moment this dialog closes.
  const scope = new AbortController();
  dialog.signal = scope.signal;

  const nativeClose = dialog.close.bind(dialog);
  let closing = false;
  const close = (returnValue) => {
    if (closing || !dialog.open) return;
    closing = true;
    motionOut(dialog, { hide: false, timeout: 180 }).then((finished) => {
      if (finished && dialog.open) nativeClose(returnValue);
    });
  };
  // Callers that hold the native element sometimes use dialog.close()
  // directly. Route those calls through the same exit instead of maintaining
  // two close behaviours.
  dialog.close = close;
  dialog.addEventListener('cancel', (event) => {
    event.preventDefault();
    close();
  });
  dialog.addEventListener('close', () => { scope.abort(); dialog.remove(); }, { once: true });

  const footer = actions.length
    ? el('div', { class: 'modal-foot' },
        actions.map((action) => el('button', {
          class: `btn ${action.class || ''}`,
          text: action.label,
          onclick: () => {
            if (action.run) {
              const result = action.run(close);
              if (result === false) return;
            }
            if (action.closes !== false) close();
          },
        })))
    : null;

  dialog.append(
    el('div', { class: 'modal-head' },
      el('div', { style: 'flex:1' },
        el('h2', { class: 'modal-title', text: title }),
        subtitle ? el('p', { class: 'modal-sub', text: subtitle }) : null),
      el('button', {
        class: 'btn btn-icon btn-ghost', 'aria-label': 'Close',
        'data-tip': 'Close · Esc', onclick: close,
      }, icon('close', { size: 16 }))),
    el('div', { class: 'modal-body' }, body),
  );
  if (footer) dialog.append(footer);

  document.body.append(dialog);
  dialog.showModal();
  motionIn(dialog, { unhide: false, timeout: 240 });
  if (onOpen) onOpen(dialog, close);
  return { dialog, close };
}

/**
 * A confirm that names the consequence rather than asking "are you sure".
 * Focus starts on Cancel so a stray Enter never destroys anything.
 */
/**
 * A confirm with one text field, for the cases where the answer is a value
 * rather than a yes -- a destination path, a new name.
 *
 * Resolves to the trimmed string, or null if the user cancelled or left it
 * empty. Enter submits, so it is as quick as a native prompt.
 */
export function promptForText({ title, message, value = '', confirmLabel = 'OK', placeholder = '' }) {
  return new Promise((resolve) => {
    let settled = false;
    const finish = (result) => { if (!settled) { settled = true; resolve(result); } };

    const input = el('input', {
      value, placeholder,
      style: 'width:100%;height:36px;padding:0 12px;border:1px solid var(--border);' +
             'border-radius:var(--r-sm);background:var(--bg-field);color:var(--text-1);' +
             'font-family:var(--font-mono);font-size:var(--fs-12)',
    });

    const submit = () => {
      const text = input.value.trim();
      finish(text.length ? text : null);
      dialog.close();
    };

    input.addEventListener('keydown', (event) => {
      if (event.key === 'Enter') { event.preventDefault(); submit(); }
    });

    const { dialog } = modal({
      title,
      width: '520px',
      body: el('div', {},
        message
          ? el('p', { text: message, style: 'margin:0 0 12px;color:var(--text-2);font-size:var(--fs-13);line-height:1.5' })
          : null,
        input),
      actions: [
        { label: 'Cancel', run: () => finish(null) },
        { label: confirmLabel, class: 'btn-primary', run: () => { submit(); return false; }, closes: false },
      ],
      onOpen: () => {
        input.focus();
        // Select the file name but not the directory: the path is usually
        // right and only the last part needs changing.
        const cut = Math.max(value.lastIndexOf('/'), value.lastIndexOf('\\'));
        input.setSelectionRange(cut + 1, value.length);
      },
    });

    // Esc and the close button both fire this. The element belongs to this
    // modal alone, so it cannot be woken by an unrelated dialog closing --
    // which is what happened while every modal shared one <dialog>.
    dialog.addEventListener('close', () => finish(null), { once: true });
  });
}

export function confirmDialog({ title, message, confirmLabel = 'Confirm', danger = true }) {
  return new Promise((resolve) => {
    let settled = false;
    const finish = (value) => { if (!settled) { settled = true; resolve(value); } };

    const { dialog } = modal({
      title,
      body: el('p', { text: message, style: 'margin:0 0 8px;color:var(--text-2);line-height:1.5' }),
      actions: [
        { label: 'Cancel', run: () => finish(false) },
        { label: confirmLabel, class: danger ? 'btn-red' : 'btn-primary', run: () => finish(true) },
      ],
      onOpen: (node) => {
        node.querySelector('.modal-foot .btn')?.focus();
      },
    });
    dialog.addEventListener('close', () => finish(false), { once: true });
  });
}
