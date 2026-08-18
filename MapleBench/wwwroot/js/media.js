/**
 * Media URLs, and the one loader every grid in the app pulls its sprites
 * through.
 *
 * `/api/thumb` is served with `private, max-age=3600` (Endpoints.cs), which is
 * what makes scrolling 2,000 rows affordable -- and what made a replaced sprite
 * keep rendering the old bitmap for an hour, across inspector refreshes and
 * page reloads. `/api/canvas` carries no cache header at all today, so it is
 * re-decoded server-side on every request; the stamp below is what will keep it
 * honest the moment it gets one, and until then it costs nothing. Every media
 * URL therefore carries a revision that a write bumps, so the browser cache
 * stays useful and still cannot serve stale art.
 *
 * The other half of the problem is the session id: `f1` is handed out in open
 * order, so after a close+reopen the same path addresses a different archive.
 * An epoch counter retires every key at once when that happens.
 */

import { api } from './api.js';

/**
 * session path -> revision. Absent means "never rewritten".
 *
 * One entry per path *and per ancestor* of every write, so a long editing
 * session grows it without ever dropping anything. Bounded below by retiring
 * the whole generation rather than by evicting single entries: forgetting one
 * path's revision would hand that path back its pre-edit URL, and the browser
 * would then serve the sprite the user just replaced. Clearing the map and
 * bumping the epoch changes every URL at once, which can only ever cost a
 * refetch.
 */
const revisions = new Map();
const MAX_REVISIONS = 5000;

let epoch = 0;

const revisionOf = (path) => revisions.get(path) ?? 0;

/** The generation of every media key; changes when the session is reset. */
export const mediaEpoch = () => epoch;

/**
 * Marks a node's art as changed.
 *
 * Ancestors are bumped too: a directory or image row shows the first canvas
 * found beneath it, so rewriting one leaf changes what every node above it
 * renders.
 */
export function bustThumb(path) {
  if (!path) return;
  let running = path;
  for (;;) {
    revisions.set(running, revisionOf(running) + 1);
    const cut = running.lastIndexOf('/');
    if (cut < 0) break;
    running = running.slice(0, cut);
  }
  if (revisions.size > MAX_REVISIONS) resetMediaCache();
}

/**
 * Retires every cached bitmap and icon. Called when a file is closed: the ids
 * the keys are built from are about to be reused for a different archive.
 */
export function resetMediaCache() {
  revisions.clear();
  requested.clear();
  epoch++;
}

/* The node URLs already carry `?path=`, so their stamp joins with '&'; the icon
   URL is a bare path and takes the '?'. */
const stamp = (path) => `${epoch}.${revisionOf(path)}`;

export const thumbUrl = (path) => `${api.thumbUrl(path)}&v=${stamp(path)}`;
export const canvasUrl = (path) => `${api.canvasUrl(path)}&v=${stamp(path)}`;

/**
 * Cash shop icons are keyed on item id by both the server and the browser, and
 * an item id means nothing without the archive it was read from -- closing
 * Item.wz used to leave its icons rendering over the next session's items.
 */
export const iconUrl = (itemId) => `${api.iconUrl(itemId)}?v=${epoch}`;

/* ============================================================
   SPRITE LOADING
   ============================================================ */

/**
 * Every grid in the app puts a picture on every row -- 2,742 mobs, 10,742
 * NPCs, 4,846 skills, and a child table that reaches 9,697 rows -- and each
 * one is a /api/thumb, measured at a 4.6-24.4 ms mean and taken while holding
 * the session's single gate. Left to the browser, one pass down Mob.wz is
 * ~84 s of gate time with every other read queued behind it.
 *
 * So all four grids come through here, and three rules apply once instead of
 * four times:
 *
 *   - nothing is requested until it is near the viewport (IntersectionObserver,
 *     200px margin), so a 60-card page asks for the dozen that are on screen;
 *   - at most MAX_IN_FLIGHT are in the air at once, so however fast the user
 *     scrolls, only that many requests are ever committed to the gate;
 *   - a URL already asked for is never asked for twice, however many recycled
 *     virtualiser rows point at it.
 *
 * Cancellation is per scope: a grid passes the signal of an AbortController it
 * aborts on its next repaint. Queued rows are dropped, and a request still in
 * the air has its <img> src removed, which is what actually cancels an image
 * load -- an AbortSignal cannot reach one.
 */

const MAX_IN_FLIGHT = 5;

/**
 * url -> promise that settles `true` when the first <img> asking for it
 * loaded. Insertion-ordered and capped: this is the dedupe, not a bitmap
 * cache, so an evicted URL costs one extra conditional request and nothing
 * else. The bitmaps themselves live in the browser's cache, under the header
 * the thumb endpoint sets.
 */
const requested = new Map();
const MAX_REQUESTED = 800;

const queue = [];
let inFlight = 0;

/** img -> what it is currently waiting for. */
const bindings = new WeakMap();
/** watched element -> the same binding; see lazySprite for why it is not the img. */
const watched = new WeakMap();

const observer = typeof IntersectionObserver === 'function'
  ? new IntersectionObserver((entries) => {
      for (const entry of entries) {
        if (!entry.isIntersecting) continue;
        // One shot: the row only has to become visible once.
        observer.unobserve(entry.target);
        const binding = watched.get(entry.target);
        watched.delete(entry.target);
        if (binding && !binding.released) enqueue(binding.img, binding);
      }
    }, { rootMargin: '200px' })
  : null;

/**
 * Loads `url` into `img` when it comes into view, under the shared cap.
 *
 * The caller keeps its own `load` and `error` listeners and its own fallback:
 * a 204 (no art under this node) still arrives as an `error`, exactly as it
 * did when the src was assigned directly, so nothing about how a missing
 * sprite is handled changes here.
 *
 * Calling it again on the same <img> -- which is what a recycled row does --
 * releases the previous binding first.
 */
/**
 * @param eager skip the visibility filter and queue immediately.
 *
 * For grids inside a modal &lt;dialog&gt;. A dialog opened with showModal() renders
 * in the top layer, and an IntersectionObserver with the implicit (viewport)
 * root never reports its contents: measured in the import grid, 200 frames of
 * 42x42 px sitting at y=365 in an 811 px viewport, not one callback, not one
 * src assigned, 200 empty boxes. The same code outside a dialog -- the Mobs
 * grid, the inspector's child list -- has always worked, which is what points
 * at the dialog rather than at the loader.
 *
 * Eager still goes through the queue, so the cap and the dedupe still apply and
 * the abort signal still cancels: what is skipped is only the "is it on screen"
 * test. That is affordable here because an import grid is capped at 200 cards,
 * where the tree is capped at nothing.
 */
export function lazySprite(img, url, { signal, eager = false } = {}) {
  releaseSprite(img);
  if (!url || signal?.aborted) return;

  // What is watched is the frame around the picture, never the <img> itself: an
  // <img> with no src has no box, and an element with no box is never seen to
  // come into view -- so watching it directly meant nothing was ever requested.
  // Every caller puts its <img> inside a fixed-size frame (.row-thumb,
  // .mob-sprite, .npc-portrait, .sk-icon, .db-thumb), which is the element that
  // actually occupies the row.
  const binding = { img, url, signal, released: false, job: null, onAbort: null, watch: img.parentElement || img };
  bindings.set(img, binding);

  if (signal) {
    binding.onAbort = () => releaseSprite(img);
    signal.addEventListener('abort', binding.onAbort, { once: true });
  }

  // No IntersectionObserver (very old browsers): still queued and still
  // deduped, just without the visibility filter. Degrading to "slower" is
  // fine; degrading to "no picture" is not.
  if (observer && !eager) { watched.set(binding.watch, binding); observer.observe(binding.watch); }
  else enqueue(img, binding);
}

/** Stops whatever `img` was waiting for. Safe to call on an unbound element. */
export function releaseSprite(img) {
  const binding = bindings.get(img);
  if (!binding) return;
  bindings.delete(img);
  binding.released = true;
  if (watched.get(binding.watch) === binding) {
    watched.delete(binding.watch);
    observer?.unobserve(binding.watch);
  }
  binding.onAbort && binding.signal.removeEventListener('abort', binding.onAbort);

  const job = binding.job;
  if (!job) return;
  job.cancelled = true;
  // Started but unfinished: drop the URL so the next row that wants it asks
  // again, rather than waiting on a promise that will never settle.
  if (job.stop) { requested.delete(binding.url); job.stop(); }
}

function enqueue(img, binding) {
  const shared = requested.get(binding.url);
  if (shared) {
    // Already on the way, or already in the browser cache. Wait for the answer
    // instead of opening a second request for the same bytes.
    shared.then((ok) => {
      if (binding.released) return;
      if (ok) img.src = binding.url;
      else enqueue(img, binding);          // the first asker gave up; take over
    });
    return;
  }

  const job = { cancelled: false, url: binding.url, stop: null, done: null, start: null };
  binding.job = job;
  remember(binding.url, new Promise((resolve) => { job.done = resolve; }));

  let settled = false;
  const finish = (ok) => {
    if (settled) return;
    settled = true;
    inFlight--;
    job.stop = null;
    // A failure is not a fact worth remembering here: a 204 has to reach every
    // <img> that asked, because `error` is what makes the caller show its glyph
    // -- and skill.js's second guess at where the art lives is in that handler.
    // The server marks "no art here" cacheable, so the retry costs no work
    // beyond a cache read.
    if (!ok) requested.delete(job.url);
    job.done(ok);
    drain();
  };

  job.start = () => {
    // Removing the attribute is the only way to cancel an image load; an
    // AbortSignal does not reach one. Only ever called before the load
    // settles, so a sprite already on screen is never blanked.
    job.stop = () => { img.removeAttribute('src'); finish(false); };
    img.addEventListener('load', () => finish(true), { once: true });
    img.addEventListener('error', () => finish(false), { once: true });
    img.src = binding.url;
  };

  queue.push(job);
  drain();
}

/** Starts as many queued sprites as the cap allows. */
function drain() {
  while (inFlight < MAX_IN_FLIGHT && queue.length) {
    const job = queue.shift();
    if (job.cancelled) {
      // Nobody is waiting on this one any more; forget the URL so a later row
      // asking for it starts a fresh request instead of awaiting a promise
      // that will never settle.
      requested.delete(job.url);
      job.done(false);
      continue;
    }
    inFlight++;
    job.start();
  }
}

function remember(url, promise) {
  requested.set(url, promise);
  // Oldest first, which is what Map iteration order gives us.
  while (requested.size > MAX_REQUESTED) {
    const oldest = requested.keys().next().value;
    requested.delete(oldest);
  }
}
