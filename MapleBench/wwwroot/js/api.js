/**
 * Thin wrapper over the MapleBench HTTP API.
 *
 * Every failure path funnels through ApiError so callers can show the
 * backend's message verbatim -- the services are written to produce text that
 * is safe and useful to put in front of a user.
 */

export class ApiError extends Error {
  constructor(message, detail, status) {
    super(message);
    this.name = 'ApiError';
    this.detail = detail;
    this.status = status;
  }
}

/* ============================================================
   ACTIVITY

   One counter of requests in flight, and a custom event when it goes from
   nothing to something and back. app.js turns that into a bar across the top
   of the window.

   Here rather than at the call sites because the guarantee wanted is "no wait
   is ever silent", and a guarantee that has to be remembered at seventy call
   sites is not one. Sections still show their own panels and skeletons -- those
   say what is loading, which is better -- but this catches everything nobody
   thought to cover, including whatever gets added next.

   Nothing is emitted for the first 200 ms. Below that a bar is a flicker, and a
   flicker on every keystroke-commit is worse than nothing.
   ============================================================ */

const ACTIVITY_DELAY_MS = 200;
let inFlight = 0;
let activityTimer = null;
let activityShown = false;

function announce(active) {
  if (activityShown === active) return;
  activityShown = active;
  window.dispatchEvent(new CustomEvent('mb:activity', { detail: { active } }));
}

function activityStarted() {
  inFlight++;
  if (activityTimer === null && !activityShown) {
    activityTimer = setTimeout(() => {
      activityTimer = null;
      if (inFlight > 0) announce(true);
    }, ACTIVITY_DELAY_MS);
  }
}

function activityFinished() {
  inFlight = Math.max(0, inFlight - 1);
  if (inFlight > 0) return;
  if (activityTimer !== null) { clearTimeout(activityTimer); activityTimer = null; }
  announce(false);
}

async function request(method, url, body, options = {}) {
  const init = { method, headers: {} };

  if (body instanceof FormData) {
    init.body = body;
  } else if (body !== undefined) {
    init.headers['Content-Type'] = 'application/json';
    init.body = JSON.stringify(body);
  }
  if (options.signal) init.signal = options.signal;

  activityStarted();
  try {
    return await send(url, init, options);
  } finally {
    activityFinished();
  }
}

async function send(url, init, options = {}) {
  const response = await fetch(url, init);

  if (response.status === 204) return null;

  if (!response.ok) {
    let message = `${response.status} ${response.statusText}`;
    let detail;
    try {
      const payload = await response.json();
      message = payload.message || message;
      detail = payload.detail;
    } catch {
      /* non-JSON error body; keep the status line */
    }
    throw new ApiError(message, detail, response.status);
  }

  // A caller that needs the headers as well as the body asks for the Response
  // itself. The dumper is the one that does: it reads what did NOT export out
  // of X-Dump-Issues, and one of its endpoints answers with a file whose content
  // type is application/json -- which the line below would otherwise parse,
  // dropping the headers with the raw bytes. Errors are still ApiError above.
  if (options.raw) return response;

  const type = response.headers.get('content-type') || '';
  if (type.includes('application/json')) return response.json();
  return response;
}

const get = (url, options) => request('GET', url, undefined, options);
const post = (url, body, options) => request('POST', url, body, options);
const put = (url, body) => request('PUT', url, body);
const del = (url) => request('DELETE', url);

/** Session paths are '/'-separated; only '/' and '%' inside a name need escaping. */
export const q = (value) => encodeURIComponent(value);

export const api = {
  /* ---------- session ---------- */
  about: () => get('/api/about'),
  files: () => get('/api/files'),
  open: (payload) => post('/api/files/open', payload),
  close: (fileId) => del(`/api/files/${q(fileId)}`),
  save: (payload) => post('/api/files/save', payload),
  preflight: (fileId) => get(`/api/files/${q(fileId)}/preflight`),
  browse: (path) => get(`/api/browse${path ? `?path=${q(path)}` : ''}`),
  openMany: (paths, options = {}) => post('/api/files/open-many', { paths, ...options }),
  openImgFolder: (path, options = {}) => post('/api/files/open-img-folder', { path, ...options }),
  scan: (path) => get(`/api/scan?path=${q(path)}`),
  changes: () => get('/api/changes'),

  /* ---------- merged archive families ---------- */
  // Skill.wz + Skill001.wz + Skill002.wz + Skill003.wz browsed as one tree. The
  // merge is a view: every leaf still carries the real session path of the real
  // file it lives in, so nothing else in this object needs a merged variant.
  familyDetect: (path) => get(`/api/family/detect?path=${q(path)}`),
  // Families that could be built out of what is already open, costing no disk.
  familyAdoptable: () => get('/api/family/adoptable'),
  families: () => get('/api/families'),
  familyOpen: (payload) => post('/api/family/open', payload),
  // Unmerge, not close: the archives stay open and stay editable.
  familyUnmerge: (familyId) => del(`/api/family/${q(familyId)}`),
  familyPrecedence: (familyId, lastWins) =>
    post(`/api/family/${q(familyId)}/precedence`, { lastWins }),
  // Which image names are in more than one member -- the reason the merged view
  // is worth having rather than a convenience on top of it.
  familyShadows: (familyId, limit = 200) =>
    get(`/api/family/${q(familyId)}/shadows?limit=${limit}`),
  // The tree row chain for a real path, because in a merged tree a node's
  // ancestors are not its path's prefixes.
  familyLocate: (path) => get(`/api/family/locate?path=${q(path)}`),

  /* ---------- cross-client import ---------- */
  // Separate from scan() because detection has to understand both ordinary .wz
  // files and split clients whose archives are directories under Data\\.
  importDetect: (path) => get(`/api/import/detect?path=${q(path)}`),
  // The single front door. A classic .wz opens normally; a split archive is
  // merged in memory. Either returns an ordinary read-only OpenFileDto and uses
  // the same tree, search and dependency-aware Port flow from then on.
  importOpen: (payload) => post('/api/import/open', payload),
  // Dual View's folder route: detect once and mount every archive from that
  // client read-only, so the source pane is a complete client Explorer.
  importOpenClient: (payload) => post('/api/import/open-client', payload),
  // No client-side timeout: a 13 GB archive is minutes of work and the answer is
  // only meaningful once the written file has been verified.
  importArchive: (payload) => post('/api/import/archive', payload),
  importProgress: () => get('/api/import/progress'),
  // Cooperative, so this asks rather than tells: the work stops between source
  // files, and a cancel that lands during a 13 GB write is honoured once that
  // write finishes. The reply is the progress snapshot rather than 204, so a
  // caller that races the finish still learns the terminal state from this call.
  importCancel: () => post('/api/import/cancel'),
  exportImagesUrl: (path) => `/api/export/images?path=${q(path)}`,
  exportImgUrl: (path) => `/api/export/img?path=${q(path)}`,
  exportJsonUrl: (path, depth = 12) => `/api/export/json?path=${q(path)}&depth=${depth}`,
  // Server-ready classic XML: one .img, or a whole branch as a ZIP mirroring
  // the WZ hierarchy. Sprites are stripped -- an emulator never reads them.
  exportXmlUrl: (path) => `/api/export/xml?path=${q(path)}`,
  exportXmlZipUrl: (path, limit = 2000) => `/api/export/xml-zip?path=${q(path)}&limit=${limit}`,

  /* ---------- tree ---------- */
  node: (path) => get(`/api/node?path=${q(path)}`),
  children: (path) => get(`/api/children?path=${q(path)}`),
  inspect: (path) => get(`/api/inspect?path=${q(path)}`),

  /* ---------- edit ---------- */
  setValue: (path, value) => put('/api/node/value', { path, value }),
  setValues: (paths, value) => put('/api/node/values', { paths, value }),
  // Preview and write are the same call. dryRun:true returns the before -> after
  // table without touching anything, so the numbers shown and the numbers
  // written come out of one implementation.
  computeValues: (paths, expression, dryRun) =>
    post('/api/node/compute', { paths, expression, dryRun }),
  rename: (path, name) => put('/api/node/name', { path, name }),
  addNode: (payload) => post('/api/node', payload),
  deleteNodes: (paths) => post('/api/node/delete', { paths }),
  duplicate: (paths) => post('/api/node/duplicate', { paths }),
  transfer: (payload) => post('/api/node/transfer', payload),
  reorder: (path, index) => post('/api/node/reorder', { path, index }),
  types: () => get('/api/node/types'),
  undo: () => post('/api/undo'),
  redo: () => post('/api/redo'),
  history: () => get('/api/history'),

  /* ---------- media ---------- */
  animation: (path) => get(`/api/animation?path=${q(path)}`),
  preview: (path, limit = 60) => get(`/api/preview?path=${q(path)}&limit=${limit}`),
  thumbUrl: (path) => `/api/thumb?path=${q(path)}`,
  canvasUrl: (path) => `/api/canvas?path=${q(path)}`,
  audioUrl: (path) => `/api/audio?path=${q(path)}`,
  rawUrl: (path) => `/api/raw?path=${q(path)}`,
  replaceCanvas: (path, blob) => {
    const form = new FormData();
    form.append('image', blob, 'image.png');
    return post(`/api/canvas?path=${q(path)}`, form);
  },

  /* ---------- search ---------- */
  search: (payload, options) => post('/api/search', payload, options),
  replace: (payload) => post('/api/replace', payload),

  /* ---------- cash shop ---------- */
  shopItems: (fileId, names = true) =>
    get(`/api/cashshop/items?fileId=${q(fileId)}&names=${names}`),
  shopNextSn: (fileId) => get(`/api/cashshop/next-sn?fileId=${q(fileId)}`),
  shopUpsert: (payload) => post('/api/cashshop/item', payload),
  shopBulk: (fileId, items) => post('/api/cashshop/items', { fileId, items }),
  shopClone: (fileId, key, itemId) => post('/api/cashshop/clone', { fileId, key, itemId }),
  shopDelete: (fileId, keys) => post('/api/cashshop/delete', { fileId, keys }),
  shopCapabilities: () => get('/api/cashshop/capabilities'),
  iconUrl: (itemId) => `/api/cashshop/icon/${itemId}`,

  /* ---------- mobs ---------- */
  mobCapabilities: () => get('/api/mob/capabilities'),
  // fileId is optional and omitted rather than sent empty: no fileId means
  // "every open Mob archive", which is not the same request as fileId="".
  mobList: (fileId, names = true) =>
    get(`/api/mob/list?${fileId ? `fileId=${q(fileId)}&` : ''}names=${names}`),
  mobDetail: (path) => get(`/api/mob/detail?path=${q(path)}`),
  mobFields: (path, fields) => post('/api/mob/fields', { path, fields }),
  mobBulk: (payload) => post('/api/mob/bulk', payload),

  /* ---------- database ---------- */
  dbCapabilities: () => get('/api/db/capabilities'),
  // `options` carries the AbortSignal. Database mode searches per keystroke, so
  // without it a slow answer for "ab" lands after a quick one for "abc" and
  // repaints the grid with results for what the box no longer says.
  // kind is omitted rather than sent as "all": no kind means every pool, which
  // is not the same request as kind="all" (a pool that does not exist).
  dbSearch: (query, kind, limit, options) =>
    get(`/api/db/search?q=${q(query)}${kind && kind !== 'all' ? `&kind=${q(kind)}` : ''}&limit=${limit ?? 200}`,
      options),

  /* ---------- skills ---------- */
  skillCapabilities: () => get('/api/skill/capabilities'),
  skillBooks: (fileId) => get(`/api/skill/books${fileId ? `?fileId=${q(fileId)}` : ''}`),
  // `book` is the book's session PATH ("f1/1100.img"), not its id -- SkillService
  // compares it against the image path, so passing "1100" matches nothing and
  // silently returns an empty list. Omit it for every book.
  skillList: (fileId, book, names = true) =>
    get(`/api/skill/list?${fileId ? `fileId=${q(fileId)}&` : ''}${book ? `book=${q(book)}&` : ''}names=${names}`),
  // `vars` supplies values for free variables -- names a formula reads that the
  // skill's own common block does not define, like the x30 in "200+4*x30". They
  // only affect what the table computes for display; nothing is written.
  skillDetail: (path, vars) => {
    const pairs = Object.entries(vars ?? {})
      .filter(([, value]) => value !== '' && value != null && Number.isFinite(Number(value)))
      .map(([name, value]) => `${name}=${Number(value)}`)
      .join(';');
    return get(`/api/skill/detail?path=${q(path)}${pairs ? `&vars=${q(pairs)}` : ''}`);
  },
  skillWriteLevels: (path, cells) => post('/api/skill/levels', { path, cells }),
  // op: add | clone | remove | rename
  skillLevelOp: (payload) => post('/api/skill/level', payload),
  // Bakes common/ formulas into explicit level nodes. Dry-run first: it deletes
  // the common block, without which the client keeps reading the formulas and
  // the bake has no in-game effect.
  skillExpandCommon: (payload) => post('/api/skill/expand-common', payload),
  skillBulk: (payload) => post('/api/skill/bulk', payload),

  /* ---------- npcs ---------- */
  npcCapabilities: () => get('/api/npc/capabilities'),
  npcList: (fileId, names = true) =>
    get(`/api/npc/list?${fileId ? `fileId=${q(fileId)}&` : ''}names=${names}`),
  npcDetail: (path) => get(`/api/npc/detail?path=${q(path)}`),
  npcFields: (path, fields) => post('/api/npc/fields', { path, fields }),
  npcBulk: (payload) => post('/api/npc/bulk', payload),

  /* ---------- strings ---------- */
  /* ---- client integrity audit ----------------------------------------
     Folder-based, not session-based: see AuditEndpoints for why. The run is
     minutes long, so start returns immediately and the section polls. */
  auditPlan: (path) => get(`/api/audit/plan?path=${q(path)}`),
  auditStart: (payload) => post('/api/audit/start', payload),
  auditProgress: () => get('/api/audit/progress'),
  auditCancel: () => post('/api/audit/cancel', {}),
  auditReport: () => get('/api/audit/report'),

  /* ---- canvas format repair ------------------------------------------
     The repair for what the audit's `canvas.row_width_zero` check finds.
     Scan is read-only and safe on anything; apply writes a NEW archive
     beside the source and refuses without an explicit confirm. Neither
     ever writes to the archive it read, so there is no call here that can
     damage a client the user is playing. */
  repairScanStart: (payload) => post('/api/repair/canvas-format/scan', payload),
  repairProgress: () => get('/api/repair/canvas-format/progress'),
  repairCancel: () => post('/api/repair/canvas-format/cancel', {}),
  repairScan: () => get('/api/repair/canvas-format/scan'),
  repairApply: (payload) => post('/api/repair/canvas-format/apply', payload),
  repairResult: () => get('/api/repair/canvas-format/result'),

  /* ---- generation chooser --------------------------------------------
     The donor restore's per-skill judgement surface. Prepare is read-only
     (the restore's own scan plus decoding both generations of every
     conflicted skill); build drives the donor restore with the accepted
     skill set and then the canvas-format repair over its output, chained
     in the repair ledger. Frame images are fetched straight by URL:
     /api/repair/genchoice/frame/{id}. */
  genchoicePrepare: (payload) => post('/api/repair/genchoice/prepare', payload),
  genchoiceProgress: () => get('/api/repair/genchoice/progress'),
  genchoiceCancel: () => post('/api/repair/genchoice/cancel', {}),
  genchoiceReport: () => get('/api/repair/genchoice/report'),
  genchoiceBuild: (payload) => post('/api/repair/genchoice/build', payload),
  genchoiceResult: () => get('/api/repair/genchoice/result'),

  /* ---- composition builder -------------------------------------------
     pristine base + manifest -> a whole client, written to an explicit
     output folder. Start returns immediately (409 when a build is already
     running), progress is polled, and the result 404s until a build has
     finished. The base and every source are opened read-only; the only
     folder written is the one the request names. */
  composeArchives: (path) => get(`/api/compose/archives?path=${q(path)}`),
  composeLedger: (path) => get(`/api/compose/ledger?path=${q(path)}`),
  composeBuild: (payload) => post('/api/compose/build', payload),
  composeProgress: () => get('/api/compose/progress'),
  composeCancel: () => post('/api/compose/cancel', {}),
  composeResult: () => get('/api/compose/result'),

  /* ---------- map editor ---------- */
  // Only the probe lives here; the section's own calls stay in mapedit.js,
  // which owns their request/response shapes.
  mapeditCapabilities: () => get('/api/mapedit/capabilities'),

  stringCapabilities: () => get('/api/string/capabilities'),
  // kind: item | mob | skill | npc | map
  stringList: (kind, query, limit, options) =>
    get(`/api/string/list?kind=${q(kind)}${query ? `&q=${q(query)}` : ''}&limit=${limit ?? 200}`, options),
  stringEntry: (kind, id) => get(`/api/string/entry?kind=${q(kind)}&id=${id}`),
  stringWrite: (payload) => post('/api/string/write', payload),
  stringBulk: (payload) => post('/api/string/bulk', payload),

  /* ---------- porting between clients ---------- */
  portCapabilities: () => get('/api/port/capabilities'),
  portClients: () => get('/api/port/clients'),
  // What one open archive holds that could be ported, by name or id. The paths
  // it answers with are the ones the plan resolves against, which is why the
  // import picker asks this rather than /api/db/search: that one answers with
  // ids and leaves the browser to guess a path from naming conventions.
  // `options` carries the AbortSignal — this is called per keystroke.
  // `under` scopes the list to one subtree. The import picker's tree uses it to
  // answer 'what inside this row can actually be imported' -- a question only
  // the index can answer, because an entry is not identifiable by depth or by
  // name from out here.
  portEntries: (fileId, kind, query, limit, options, under) =>
    get(`/api/port/entries?fileId=${q(fileId)}${kind ? `&kind=${q(kind)}` : ''}`
      + `${query ? `&q=${q(query)}` : ''}${limit ? `&limit=${limit}` : ''}`
      + `${under ? `&under=${q(under)}` : ''}`, options),
  // The dry run. Same body as portApply minus `confirmed`, so the two cannot
  // describe different work -- the server recomputes the plan on apply anyway.
  // `options` carries an AbortSignal. The two-pane view runs this during a
  // drag, where the answer stops being wanted the moment the pointer moves to
  // a different archive or the dialog is closed -- and a plan is a real scan
  // holding the session gate, so leaving one running is not free.
  portPlan: (payload, options) => post('/api/port/plan', payload, options),
  // The only call here that writes, and it refuses without `confirmed: true`.
  portApply: (payload) => post('/api/port/apply', payload),

  /* ---------- dumping anything ----------------------------------------
     Asked once per context menu, so it decodes nothing: it answers what the
     node IS and which formats that makes possible, and the export dialog
     renders exactly that list rather than a fixed menu of verbs.

     The one-file formats are ordinary GETs whose URL comes back inside the
     answer -- there is no url helper here on purpose, because a second place
     that knows how to spell those URLs is a second place that can disagree
     with DumpService about them. */
  dumpOptions: (path) => get(`/api/dump/options?path=${q(path)}`),
  // The raw Response, for the X-Dump-* headers: a download that saved something
  // incomplete says so there and nowhere else the browser can reach.
  dumpDownload: (url) => get(url, { raw: true }),

  // Separate from dumpStart so a refusal can be shown beside the folder that
  // caused it rather than after the button that was never going to work. Same
  // body both times, so the two cannot describe different work.
  dumpPreflight: (payload) => post('/api/dump/preflight', payload),
  dumpStart: (payload) => post('/api/dump/start', payload),
  dumpProgress: () => get('/api/dump/progress'),
  // Cooperative: the dump stops between nodes, so what is already written stays
  // written and the result says how far it got. The reply is the snapshot rather
  // than 204, so a caller that races the finish still learns the terminal state.
  dumpCancel: () => post('/api/dump/cancel', {}),
  // 404s until a dump has finished in this session -- "nothing was skipped" and
  // "nothing has run" are different answers and the server refuses to conflate
  // them, so a caller must treat the 404 as the second one.
  dumpReport: () => get('/api/dump/report'),

  /* ---------- reference-only archives ---------- */
  setReadOnly: (fileId, readOnly) => post(`/api/files/${q(fileId)}/readonly`, { readOnly }),
};
