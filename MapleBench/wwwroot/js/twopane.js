/**
 * Two-pane import: the source client on the left, the target on the right, and
 * a node dragged across to bring it over.
 *
 * The ordinary importer answers "find me the thing called Zakum"; this answers
 * "show me both clients and let me drag what I can see". A dropped node is
 * NEVER a bare node copy: it goes through the port plan, with its closure, its
 * String row, its sounds, its refusals and its one undo entry. A raw node copy
 * would be a fast way to make the silent, nameless, art-less clients the port
 * machinery exists to prevent.
 *
 * WHAT A DROP MEANS, AND WHY THERE ARE TWO ANSWERS
 *
 * Dropping onto a container means "put these in here". Dropping onto a node
 * that IS one of the things being dragged — same name, same id — means "replace
 * this one". They are different operations with different blast radii, so they
 * are drawn differently and named differently, and the answer is on screen
 * BEFORE the pointer is released: a coral outline that says "into", or a red
 * filled box that says "replace", plus a ghost by the cursor spelling out the
 * sentence. Nothing is decided on release that was not visible before it.
 *
 * "Replace" is the port's `overwrite`, and it never applies silently: the
 * conflict choice names how many existing entries would be replaced.
 *
 * A FOLDER MEANS EVERYTHING INSIDE IT
 *
 * Dragging a container drags its whole subtree, all the way down. The entries
 * that are really in there are resolved server-side through /api/port/entries
 * with `under`, which is the same index the plan resolves against — so what is
 * moved is what can actually be ported, not a guess made from names out here.
 * That request is ALWAYS capped (a scoped query on a large node can answer with
 * 40,000+ rows and 4 MB); the true size is read from `matched`, which is exact
 * however few rows come back.
 *
 * THE CONSEQUENCE IS SHOWN DURING THE HOVER
 *
 * As soon as a drag is over a legal target, a dry-run /api/port/plan is started
 * for exactly that source set and that target archive. When it lands — usually
 * while the pointer is still down — the ghost says what comes with the drop:
 * how many parts would be written, how many the target already has, and which
 * other archives it reaches into for art, names and sounds. It is a plan, so it
 * writes nothing; and the same answer is reused when the pointer is released.
 */

import { api } from './api.js';
import { el, clear, toast, toastError, fmt, modal, debounce } from './ui.js';
import { icon } from './icons.js';
import { TreeView } from './tree.js';
import { openPortDialog } from './port.js';

/** Where the splitter was left. Per browser, not per client pair — it is a shape preference. */
const SPLIT_KEY = 'mb.twopane.split';
const SPLIT_MIN = 20;
const SPLIT_MAX = 80;

/* Drag tuning. Same numbers as tree.js, for the same reasons — see the block
   comment there. A drag that fires when a click was meant is worse here than in
   the Explorer, because the thing it starts is a port. */
const DRAG_THRESHOLD = 6;
const AUTOSCROLL_EDGE = 30;
const AUTOSCROLL_MAX = 20;
const HOVER_EXPAND_MS = 650;

/**
 * How long the pointer must be over a legal target before the plan is asked for.
 *
 * A plan is a real dry run against the archives and takes the session gate for
 * as long as it runs, so it must not fire once per pixel of a sweep across the
 * pane. It is answered from a cache keyed on exactly what it was asked, so
 * moving back onto a target already probed costs nothing at all.
 */
const PROBE_DELAY_MS = 320;

/** Rows one name search shows. The server refuses more, so asking for more is a wasted round trip. */
const SEARCH_LIMIT = 60;

const ROW_H = () => {
  const raw = parseFloat(
    getComputedStyle(document.documentElement).getPropertyValue('--row-h'));
  return Number.isFinite(raw) && raw > 0 ? raw : 28;
};

const leaf = (path) => String(path).split('/').pop();

/** "0100.img" and "0100" are the same name for the purpose of "is this that thing?". */
const normal = (name) => String(name ?? '').toLowerCase().replace(/\.img$/, '');

/**
 * The archive family a file name belongs to: "Mob001.wz" -> "Mob".
 *
 * Mirrors WzSessionService.StripArchiveSuffix, and port.js's own archiveFamily,
 * which is how the server decides a target's Mob001.wz can answer for a
 * source's Mob002.wz.
 */
const family = (name) => String(name ?? '').replace(/\.wz$/i, '').replace(/\d+$/, '');

const bytes = (n) => {
  if (!Number.isFinite(n) || n <= 0) return '';
  const mb = n / 1024 / 1024;
  if (mb < 1) return `${(n / 1024).toFixed(0)} KB`;
  return mb >= 1024 ? `${(mb / 1024).toFixed(1)} GB` : `${mb.toFixed(0)} MB`;
};

const plural = (n, one, many) => `${fmt.format(n)} ${n === 1 ? one : many}`;

/**
 * Opens the two-pane view.
 *
 * @param app           the App, for the file list and for what the port dialog needs
 * @param sourceFileId  an archive to start with on the left, when the caller has one
 *                      (the importer passes the archive it just opened)
 * @param host          the Explorer stage that owns the inline split workspace
 * @param onClose       restores the ordinary Explorer when the split view closes
 */
export async function openTwoPane({ app, sourceFileId = null, host = null, onClose = null } = {}) {
  let capabilities;
  try {
    capabilities = await api.portCapabilities();
  } catch (error) {
    toastError(error, 'Could not check which clients are open');
    return undefined;
  }

  const clients = capabilities.clients || [];

  // Two panes need two clients, and saying which half is missing is the whole
  // difference between an instruction and a dead end.
  if (clients.length < 2 || !clients.some((c) => c.anyWritable)) {
    modal({
      title: 'Dual Client View needs two clients',
      body: el('div', { class: 'port-blocked' },
        icon('alert', { size: 18 }),
        el('div', {},
          el('p', { text: clients.length < 2
            ? `${clients.length === 1 ? 'One client is' : 'No client is'} open. `
              + 'Open the client you want to edit, then choose a source client to place beside it.'
            : 'Every archive open right now is open for reference only, so there is nothing to '
              + 'import into. Open the target client normally first.' }),
          el('p', { class: 'port-hint', text:
            'The second client is always opened read-only. Dragging into the writable client runs '
            + 'the dependency-aware import as one undoable action.' }))),
      actions: [
        { label: 'Choose source client…', class: 'btn-primary', run: () => app.openDualView() },
        { label: 'Open target client', run: () => app.openFilePicker() },
      ],
    });
    return undefined;
  }

  /* ============================================================
     WHICH CLIENT IS WHICH

     The folder is the identity — the same rule the server ports by. Everything
     below asks "which client", never "which archive", because an archive
     answers a different question and the two disagree the moment a client has
     four Skill files.
     ============================================================ */

  const clientOf = (fileId) =>
    clients.find((c) => c.archives.some((a) => a.fileId === fileId)) || null;

  const archiveOf = (fileId) => {
    for (const client of clients) {
      const archive = client.archives.find((a) => a.fileId === fileId);
      if (archive) return { client, archive };
    }
    return null;
  };

  let sourceClient = (sourceFileId && clientOf(sourceFileId))
    // No hint: prefer a read-only client as the source, because that is what an
    // archive opened through Import is, and a client you cannot write to is
    // never the target.
    || clients.find((c) => !c.anyWritable)
    || clients[0];

  let targetClient = clients.find((c) => c.key !== sourceClient.key && c.anyWritable)
    || clients.find((c) => c.key !== sourceClient.key);

  /* ============================================================
     STATE
     ============================================================ */

  let closed = false;

  const pendingImports = new Map();
  let importSeq = 0;
  /** fileId -> the /api/port/entries head for it: kind, label, caps, why not. */
  const kindCache = new Map();

  /** probe key -> { state, plan, error } */
  const probes = new Map();

  const maxSelection = capabilities.maxSelection || 60;

  /* ============================================================
     WHAT AN ARCHIVE HOLDS

     Asked once per archive and remembered. It is the same call the picker uses,
     and it is the only thing that can answer "is this archive a Quest archive"
     — the name is a convention, the index is the fact.
     ============================================================ */

  const kindFor = async (fileId) => {
    if (kindCache.has(fileId)) return kindCache.get(fileId);
    const pending = api.portEntries(fileId, null, '', 1)
      .then((head) => {
        const value = {
          kind: head.kind || null,
          label: head.label || 'entry',
          plural: head.plural || 'entries',
          supported: !!head.supported,
          reason: head.reason || null,
          archive: head.archive,
          total: head.total ?? 0,
          maxSelection: head.maxSelection || maxSelection,
          namesAvailable: !!head.namesAvailable,
          sources: head.sources || [],
        };
        kindCache.set(fileId, value);
        return value;
      })
      .catch((error) => {
        kindCache.delete(fileId);
        throw error;
      });
    kindCache.set(fileId, pending);
    const value = await pending;
    kindCache.set(fileId, value);
    return value;
  };

  /** The cached answer, or null when it has not been asked for yet. */
  const kindKnown = (fileId) => {
    const value = kindCache.get(fileId);
    return value && !(value instanceof Promise) ? value : null;
  };

  /* ============================================================
     THE PANES
     ============================================================ */

  const body = el('div', { class: 'tp' });

  let dialog;
  let closeView;
  if (host) {
    let ended = false;
    dialog = el('section', {
      class: 'tp-inline', role: 'region', 'aria-label': 'Dual Client View',
    });
    closeView = () => {
      if (ended) return;
      ended = true;
      dialog.dispatchEvent(new Event('close'));
      dialog.remove();
      onClose?.();
    };
    dialog.append(
      el('header', { class: 'tp-inline-head' },
        el('div', { class: 'tp-inline-mark', 'aria-hidden': 'true' }, icon('grid', { size: 16 })),
        el('div', { class: 'tp-inline-title' },
          el('h2', { text: 'Dual Client View' }),
          el('p', { text: 'Drag from Other client to Your client. The row you drop onto is the destination.' })),
        el('button', {
          class: 'btn btn-sm btn-ghost', 'aria-label': 'Close Dual Client View',
          'data-tip': 'Return to Explorer', onclick: closeView,
        }, icon('close', { size: 14 }), 'Exit Dual View')),
      body);
    clear(host);
    host.append(dialog);
  } else {
    const opened = modal({
      title: 'Dual Client View',
      subtitle: 'Drag from Other client to Your client. The row you drop onto is the destination.',
      width: 'min(98vw, 1680px)',
      body,
    });
    dialog = opened.dialog;
    closeView = opened.close;
    dialog.classList.add('tp-dialog');
    dialog.addEventListener('close', () => onClose?.(), { once: true });
  }
  dialog.addEventListener('close', () => {
    closed = true;
    cancelDrag();
    for (const probe of probes.values()) probe.abort?.abort();
  });

  const sourcePane = makePane('source');
  const targetPane = makePane('target');

  const splitter = el('div', {
    class: 'tp-splitter', role: 'separator', tabindex: '0',
    'aria-orientation': 'vertical',
    'aria-label': 'Width of the two panes',
    'data-tip': 'Drag to resize · ← → to nudge · Home to centre',
  }, el('span', { class: 'tp-grip', 'aria-hidden': 'true' }));

  const panes = el('div', { class: 'tp-panes' }, sourcePane.root, splitter, targetPane.root);

  body.append(panes);

  /* ---------- the splitter ---------- */

  const applySplit = (percent) => {
    const clamped = Math.max(SPLIT_MIN, Math.min(SPLIT_MAX, percent));
    panes.style.setProperty('--tp-split', `${clamped}%`);
    splitter.setAttribute('aria-valuenow', String(Math.round(clamped)));
    splitter.setAttribute('aria-valuemin', String(SPLIT_MIN));
    splitter.setAttribute('aria-valuemax', String(SPLIT_MAX));
    localStorage.setItem(SPLIT_KEY, String(clamped));
    // The virtualizer sizes itself off clientHeight/clientWidth and hears
    // nothing when a CSS variable changes the box around it.
    sourcePane.tree.scheduleRender();
    targetPane.tree.scheduleRender();
    return clamped;
  };

  let split = applySplit(parseFloat(localStorage.getItem(SPLIT_KEY)) || 50);

  // Under 900px the panes stack, and the same variable drives rows instead of
  // columns -- so the splitter has to read the axis it is actually on, or a
  // vertical grip would answer to horizontal movement.
  const stacked = () => window.matchMedia('(max-width: 900px)').matches;

  splitter.addEventListener('pointerdown', (event) => {
    if (event.button !== 0) return;
    event.preventDefault();
    splitter.setPointerCapture(event.pointerId);
    const move = (e) => {
      const rect = panes.getBoundingClientRect();
      if (stacked()) {
        if (!rect.height) return;
        split = applySplit(((e.clientY - rect.top) / rect.height) * 100);
        return;
      }
      if (!rect.width) return;
      split = applySplit(((e.clientX - rect.left) / rect.width) * 100);
    };
    const up = () => {
      splitter.removeEventListener('pointermove', move);
      splitter.removeEventListener('pointerup', up);
    };
    splitter.addEventListener('pointermove', move);
    splitter.addEventListener('pointerup', up);
  });

  splitter.addEventListener('keydown', (event) => {
    const step = event.shiftKey ? 10 : 2;
    const less = event.key === 'ArrowLeft' || event.key === 'ArrowUp';
    const more = event.key === 'ArrowRight' || event.key === 'ArrowDown';
    if (less) { split = applySplit(split - step); event.preventDefault(); }
    else if (more) { split = applySplit(split + step); event.preventDefault(); }
    else if (event.key === 'Home') { split = applySplit(50); event.preventDefault(); }
  });

  /* ============================================================
     A PANE

     Both sides are ordinary archive trees with one name search. The clients
     are fixed by the gesture that opened Dual View: changing them again here
     would turn a direct manipulation into another setup form.
     ============================================================ */

  function makePane(side) {
    const isSource = side === 'source';

    const clientName = el('strong', { class: 'tp-client-name' });

    const findInput = el('input', {
      type: 'search', class: 'field-input no-icon',
      placeholder: 'Find by name or id…',
      'aria-label': `Find an entry in the ${side} client`,
    });

    const findHost = el('div', { class: 'tp-find', hidden: true });

    const scroll = el('div', { class: 'tree-scroll tp-tree' });
    const sizer = el('div', { class: 'tree-sizer' });
    const rows = el('div', {
      class: 'tree-rows', role: 'tree',
      'aria-label': isSource ? 'The source client' : 'The target client',
      'aria-multiselectable': 'true', tabindex: '0',
    });
    sizer.append(rows);
    scroll.append(sizer);

    const tree = new TreeView({
      scroller: scroll, rows, sizer,
      // Both trees hand the gesture to the cross-pane controller. See tree.js.
      draggable: false,
      checkable: false,
    });
    // Set rather than persisted: setSortMode writes the Explorer's own sort
    // preference to localStorage, and a dialog should not redecorate the window
    // behind it.
    tree.sortMode = 'name';

    findInput.addEventListener('input', debounce(() => runFind(), 220));
    findInput.addEventListener('keydown', (event) => {
      if (event.key === 'Enter') { event.preventDefault(); runFind(); }
      if (event.key === 'Escape') { findInput.value = ''; findHost.hidden = true; clear(findHost); }
    });

    let finding = null;

    const runFind = async () => {
      const query = findInput.value.trim();
      finding?.abort();
      if (!query) { findHost.hidden = true; clear(findHost); return; }

      const roots = tree.roots;
      const mine = new AbortController();
      finding = mine;

      findHost.hidden = false;
      clear(findHost);
      findHost.append(el('div', { class: 'tp-find-note', text: 'Looking…' }));

      // Every open archive on this side, asked in turn. A hit carries the path
      // the PLAN resolves against, so revealing it lands on the row a drag would
      // actually pick up -- which is the whole reason this is /api/port/entries
      // and not a tree walk out here.
      const hits = [];
      let refused = 0;
      for (const root of roots) {
        try {
          const head = await kindFor(root);
          if (!head.supported || !head.kind) { refused++; continue; }
          const found = await api.portEntries(root, head.kind, query, SEARCH_LIMIT,
                                              { signal: mine.signal });
          for (const entry of found.results || []) {
            hits.push({ entry, archive: found.archive, matched: found.matched, root });
          }
        } catch (error) {
          if (mine.signal.aborted) return;
          refused++;
        }
      }
      if (mine.signal.aborted || finding !== mine || closed) return;
      finding = null;

      clear(findHost);
      if (!hits.length) {
        findHost.append(el('div', { class: 'tp-find-note', text: refused
          ? `Nothing matched "${query}". ${plural(refused, 'archive', 'archives')} here `
            + 'contains no independently importable entries, so it was not searched.'
          : `Nothing matched "${query}". Names come from this client's own String archive, so `
            + 'anything it does not name can only be found by id.' }));
        return;
      }

      findHost.append(el('div', { class: 'tp-find-note',
        text: `${plural(hits.length, 'match', 'matches')} — click one to show it in the tree` }));
      for (const hit of hits.slice(0, SEARCH_LIMIT)) {
        findHost.append(el('button', {
          class: 'tp-find-row',
          'data-tip': hit.entry.path,
          onclick: async () => {
            await tree.reveal(hit.entry.path);
            // preventScroll, and it is not a nicety.
            //
            // The rows host is one absolutely-positioned box translated to
            // wherever the virtual window currently is, so focusing it asks the
            // browser to scroll that box into view -- which undid the reveal
            // that had just happened and put the pane back at the top. Measured:
            // reveal put 0309.img at scrollTop 8,428 and the focus call put it
            // back to 0, so every search hit looked like it did nothing.
            rows.focus({ preventScroll: true });
          },
        },
          icon('layers', { size: 13 }),
          el('span', { class: 'tp-find-name',
            text: hit.entry.name || hit.entry.relative || leaf(hit.entry.path) }),
          el('span', { class: 'tp-find-meta', text: `${hit.archive} · ${hit.entry.id || ''}` }),
          hit.entry.inTarget ? el('span', { class: 'tp-have', text: 'in target' }) : null));
      }
    };

    const root = el('div', { class: 'tp-pane', 'data-side': side },
      el('div', { class: 'tp-pane-head' },
        el('div', { class: 'tp-pane-title' },
          el('span', { class: 'tp-pane-role', text: isSource ? 'Other client' : 'Your client' }),
          clientName,
          isSource
            ? el('span', { class: 'tp-lock', 'data-tip':
                'Whatever is open read-only here cannot be edited or saved over.' },
                icon('lock', { size: 11 }), 'read-only')
            : null),
        el('div', { class: 'tp-pane-inputs' },
          el('div', { class: 'tp-input' }, icon('search', { size: 14 }), findInput))),
      findHost,
      scroll);

    return {
      root, tree, scroll, rows, sizer,
      setClient(client) {
        clientName.textContent = client.label;
        clientName.title = client.folder || client.label;
        const files = client.archives.map((a) => ({
          id: a.fileId, name: a.name, dirty: a.dirty, readOnly: a.readOnly,
        }));
        tree.setRoots(files);
        // One archive is unambiguous, so it opens itself: a pane showing one
        // collapsed row is a pane showing nothing.
        if (files.length === 1) tree.expand(files[0].id);
        findInput.value = '';
        findHost.hidden = true;
        clear(findHost);
      },
    };
  }

  /* ============================================================
     RESOLVING WHAT IS BEING DRAGGED

     A container means everything inside it, all the way down, and only the
     server's index can say what "everything" is: an entry is not identifiable
     by depth or by name from out here. Capped, always -- and the cap is one
     more than a selection can carry, because past that the port is refused
     anyway and `matched` still reports the true size.
     ============================================================ */

  /** Drops any path that is already covered by another in the same set. */
  const topMost = (paths) => {
    const sorted = [...new Set(paths)].sort();
    const kept = [];
    for (const path of sorted) {
      if (kept.some((up) => path === up || path.startsWith(`${up}/`))) continue;
      kept.push(path);
    }
    return kept;
  };

  /**
   * @returns { entries, total, capped, kind, head } or throws.
   */
  const resolveSelection = async (paths, tree, signal) => {
    const roots = topMost(paths);
    if (!roots.length) throw new Error('Nothing to import.');

    const fileIds = new Set(roots.map((p) => p.split('/')[0]));
    if (fileIds.size > 1) {
      throw new Error('These come from different archives. Drag one archive\'s worth at a time — '
                    + 'a port carries one kind into one archive.');
    }
    const fileId = [...fileIds][0];
    const head = await kindFor(fileId);
    if (!head.supported || !head.kind) {
      throw new Error(head.reason || `${head.archive} holds nothing this can carry.`);
    }

    const cap = (head.maxSelection || maxSelection) + 1;
    const entries = new Map();
    let total = 0;

    for (const path of roots) {
      const node = tree.getNode(path);
      if (node && !node.hasChildren) {
        // A row with nothing inside is only itself, exactly as the picker treats
        // it. The plan is what decides whether it is really an entry.
        entries.set(path, { path, id: null, name: node.displayName || node.name || leaf(path) });
        total += 1;
        continue;
      }
      const found = await api.portEntries(fileId, head.kind, null, cap, { signal }, path);
      total += found.matched ?? (found.results?.length ?? 0);
      for (const entry of found.results || []) {
        entries.set(entry.path, {
          path: entry.path,
          id: entry.id,
          name: entry.name || entry.relative || leaf(entry.path),
          inTarget: entry.inTarget,
        });
      }
    }

    return {
      fileId, head, kind: head.kind,
      entries: [...entries.values()],
      total,
      capped: total > (head.maxSelection || maxSelection),
    };
  };

  /* ============================================================
     WHAT A DROP ON A GIVEN ROW WOULD DO

     Every branch returns a sentence. A drag that only shows a "no" cursor
     teaches nothing and the next move is to try the same thing harder.
     ============================================================ */

  const planDropOn = (path, resolution) => {
    const node = targetPane.tree.getNode(path);
    if (!node) return { ok: false, reason: 'That row is not loaded yet.' };

    const fileId = path.split('/')[0];
    const found = archiveOf(fileId);
    if (!found) return { ok: false, reason: 'That archive is not available for import.' };
    const { client, archive } = found;

    if (client.key === sourceClient.key) {
      return { ok: false, reason: 'Choose a destination in the other client.' };
    }
    if (archive.readOnly) {
      return { ok: false, reason: `${archive.name} is read-only. Choose a writable target archive.` };
    }

    if (!resolution) {
      return { ok: false, reason: 'Still working out what is inside — hold on.' };
    }
    if (!(archive.kinds || []).includes(resolution.kind)) {
      const canTake = targetClient.archives
        .filter((a) => !a.readOnly && (a.kinds || []).includes(resolution.kind))
        .map((a) => a.name);
      return {
        ok: false,
        reason: `${archive.name} cannot hold ${resolution.head.plural.toLowerCase()}.`
              + (canTake.length ? ` Drop onto ${canTake.join(' or ')}.` : ''),
      };
    }
    if (!resolution.entries.length) {
      return {
        ok: false,
        reason: 'This selection contains no independently importable entries.',
      };
    }

    // Too big, said here rather than after the release.
    //
    // This used to be a legal drop that toasted a refusal once the pointer was
    // already up -- and worse, the ghost read "Put 61 morphs in Morph.wz",
    // because 61 is what one row over the cap was asked for. The number on the
    // label was neither what was inside nor what would be carried. A drag that
    // cannot be honoured is refused while it is still a hover, with the real
    // size and the real ceiling.
    if (resolution.capped) {
      const cap = resolution.head.maxSelection || maxSelection;
      return {
        ok: false,
        reason: `${fmt.format(resolution.total)} ${resolution.head.plural.toLowerCase()} inside — `
              + `more than one import can carry (${fmt.format(cap)}). Choose a narrower folder or row.`,
      };
    }

    // "Replace this one." True when the row aimed at is one of the things being
    // dragged, by the name or the id it is filed under. It is the destructive
    // gesture, so it is the specific one: everything else is "into".
    const wanted = new Map();
    for (const entry of resolution.entries) {
      wanted.set(normal(leaf(entry.path)), entry);
      if (entry.id) wanted.set(normal(String(entry.id)), entry);
    }
    const match = wanted.get(normal(node.name));

    if (match) {
      return {
        ok: true, mode: 'replace',
        targetFileId: archive.fileId, targetArchive: archive.name,
        targetPath: path, targetName: node.name,
        label: resolution.entries.length === 1
          ? `Replace ${node.name} in ${archive.name}`
          : `Replace ${node.name} and add ${plural(resolution.entries.length - 1, 'entry', 'entries')} to ${archive.name}`,
        hint: `${archive.name}'s own ${node.name} is overwritten. One undo puts it back, until you save.`,
      };
    }

    const container = node.kind === 'File' || node.kind === 'Directory' || node.kind === 'Image'
                   || node.hasChildren;
    if (!container) {
      return {
        ok: false,
        reason: `${node.name} cannot contain this selection. Drop onto a folder or image, or onto `
              + 'the matching target entry to replace it.',
      };
    }

    return {
      ok: true, mode: 'into',
      targetFileId: archive.fileId, targetArchive: archive.name,
      targetPath: path, targetName: node.name,
      label: `Add ${plural(resolution.entries.length, resolution.head.label.toLowerCase(),
                           resolution.head.plural.toLowerCase())} to ${archive.name}`,
      hint: `They land where ${sourceClient.label} has them, inside ${archive.name}. `
          + 'Nothing the target already has is touched.',
    };
  };

  /* ============================================================
     THE CONSEQUENCE, WHILE IT IS STILL A HOVER

     A dry run. It writes nothing, it is cached on exactly what it was asked,
     and it is the same call the port dialog's Preview makes -- so the sentence
     in the ghost and the table in the dialog cannot disagree.
     ============================================================ */

  // JSON rather than a joined string: a WZ name may contain any separator
  // character worth picking, so two different path sets could otherwise key
  // the same cached plan -- and a plan is what the user reads before dropping.
  const probeKey = (kind, targetFileId, paths, overwrite) =>
    `${kind}|${targetFileId}|${overwrite ? 'r' : 'a'}|${JSON.stringify([...paths].sort())}`;

  const probeFor = (kind, targetFileId, paths, overwrite, onSettled) => {
    const key = probeKey(kind, targetFileId, paths, overwrite);
    const existing = probes.get(key);
    if (existing) return existing;

    const abort = new AbortController();
    const probe = { state: 'loading', plan: null, error: null, abort, key };
    probes.set(key, probe);

    probe.promise = api.portPlan({
      kind,
      scope: 'selection',
      paths: [...paths],
      sourceFileId: null,
      targetFileId,
      followLinks: true,
      includeArtOutlinks: true,
      overwrite,
      match: false,
      acceptDeadCanvasLinks: false,
      acceptMissingNames: false,
      stopOnConflict: false,
    }, { signal: abort.signal })
      .then((plan) => { probe.state = 'ok'; probe.plan = plan; return plan; })
      .catch((error) => {
        if (abort.signal.aborted) { probes.delete(key); return; }
        probe.state = 'error';
        probe.error = error;
        return null;
      })
      .finally(() => { if (!closed) onSettled?.(probe); });

    return probe;
  };

  /**
   * The plan, said in one or two lines.
   *
   * Deliberately reports the things a drop cannot show on its own: what comes
   * WITH it. The entry is the part the user already knows about.
   */
  const consequence = (probe) => {
    if (!probe) return null;
    if (probe.state === 'loading') return { tone: 'wait', lines: ['Working out what comes with it…'] };
    if (probe.state === 'error') return { tone: 'bad', lines: [probe.error.message] };

    const plan = probe.plan;
    if (plan.blocked) {
      return { tone: 'bad', lines: [`Refused: ${plan.blockedReason || 'this port cannot run.'}`] };
    }

    const totals = plan.totals || {};
    const parts = (plan.items || []).flatMap((item) => item.parts || []);
    const carried = parts.filter((p) => p.status !== 'Absent' && p.kind !== 'container');

    const art = carried.filter((p) => p.kind === 'link-entry' || p.kind === 'named').length;
    // A part names its archive FAMILY ("Sound"); the plan names the file
    // ("Morph.wz"). Comparing the two directly made every plan claim it was
    // reaching into another archive, including a morph port whose every part
    // came out of the one archive it was dragged from. Both sides are reduced
    // to the family, the same way the server decides that a target's Mob001.wz
    // can answer for a source's Mob002.wz.
    const home = family(plan.sourceArchive);
    const elsewhere = [...new Set(carried
      .filter((p) => p.sourceArchive && family(p.sourceArchive) !== home)
      .map((p) => p.sourceArchive))];
    const pulled = (plan.items || []).filter((i) => !i.requested).length;

    const lines = [];
    lines.push(`${plural(totals.willWrite ?? 0, 'part', 'parts')} would be written`
      + (totals.entriesAlreadyThere
          ? ` · ${fmt.format(totals.entriesAlreadyThere)} already there`
          : '')
      + (totals.bytes ? ` · ${bytes(totals.bytes)}` : ''));

    const extras = [];
    if (pulled) extras.push(`${plural(pulled, 'entry', 'entries')} pulled in by links`);
    if (art) extras.push(`${plural(art, 'picture', 'pictures')} it draws with`);
    if (elsewhere.length) extras.push(`reads ${elsewhere.join(', ')}`);
    if (extras.length) lines.push(`Comes with it: ${extras.join(' · ')}`);

    if (totals.blocked) lines.push(`${plural(totals.blocked, 'part', 'parts')} cannot be carried.`);

    return {
      tone: totals.blocked ? 'warn' : 'ok',
      lines,
      plan,
    };
  };

  /* ============================================================
     THE CROSS-PANE DRAG

     Pointer events, not HTML5 drag-and-drop, for the reasons tree.js gives:
     the browser's own threshold cannot be raised, and its drag image is taken
     from a DOM element that a virtualized list recycles out from under it.
     ============================================================ */

  let press = null;
  let drag = null;

  sourcePane.rows.addEventListener('pointerdown', (event) => {
    if (event.button !== 0) return;
    if (event.pointerType === 'touch') return;                  // see tree.js
    if (event.target.closest('.tree-twisty')) return;
    if (event.target.classList?.contains('tree-check')) return;

    const row = event.target.closest('.tree-row');
    const path = row?.dataset.path;
    if (!path || !sourcePane.tree.getNode(path)) return;

    press = { pointerId: event.pointerId, path, x: event.clientX, y: event.clientY };
  });

  window.addEventListener('pointermove', onPointerMove);
  window.addEventListener('pointerup', onPointerUp);
  window.addEventListener('pointercancel', cancelDrag);
  window.addEventListener('keydown', onWindowKey);

  // The listeners above are on the window and would outlive the dialog.
  dialog.addEventListener('close', () => {
    window.removeEventListener('pointermove', onPointerMove);
    window.removeEventListener('pointerup', onPointerUp);
    window.removeEventListener('pointercancel', cancelDrag);
    window.removeEventListener('keydown', onWindowKey);
  }, { once: true });

  function onWindowKey(event) {
    if (event.key === 'Escape' && drag) { event.preventDefault(); event.stopPropagation(); cancelDrag(); }
  }

  function onPointerMove(event) {
    if (drag) {
      if (event.pointerId !== drag.pointerId) return;
      drag.x = event.clientX;
      drag.y = event.clientY;
      updateDrag();
      event.preventDefault();
      return;
    }
    if (!press || event.pointerId !== press.pointerId) return;
    if (Math.hypot(event.clientX - press.x, event.clientY - press.y) < DRAG_THRESHOLD) return;
    beginDrag(event);
  }

  function onPointerUp(event) {
    if (!drag) { press = null; return; }
    if (event.pointerId !== drag.pointerId) return;
    const plan = drag.plan;
    const resolution = drag.resolution;
    const probe = drag.probe;
    cancelDrag();
    if (plan?.ok && resolution) void commitDrop(plan, resolution, probe);
  }

  function beginDrag(event) {
    const { path } = press;
    press = null;

    const tree = sourcePane.tree;
    // A drag that starts inside a multi-selection carries the whole selection;
    // one that starts outside it carries only the row grabbed, and deliberately
    // does not change the selection.
    const paths = tree.selection.has(path) && tree.selection.size > 1
      ? tree.flat.filter((row) => row.node && tree.selection.has(row.path)).map((row) => row.path)
      : [path];

    drag = {
      pointerId: event.pointerId,
      paths,
      x: event.clientX, y: event.clientY,
      plan: null,
      resolution: null,
      resolving: true,
      error: null,
      probe: null,
      probeAt: 0, probeKeyFor: null,
      hoverPath: null, hoverAt: 0,
      abort: new AbortController(),
      label: paths.length === 1
        ? (tree.getNode(path)?.name ?? leaf(path))
        : `${paths.length} rows`,
    };

    try { sourcePane.rows.setPointerCapture(event.pointerId); } catch { /* carry on */ }
    document.body.dataset.tpDragging = 'true';
    targetPane.scroll.dataset.dragging = 'true';

    const mine = drag;
    resolveSelection(paths, tree, mine.abort.signal)
      .then((resolution) => {
        if (drag !== mine) return;
        mine.resolution = resolution;
        mine.resolving = false;
        updateDrag();
      })
      .catch((error) => {
        if (drag !== mine || mine.abort.signal.aborted) return;
        mine.resolving = false;
        mine.error = error.message;
        updateDrag();
      });

    updateDrag();
    dragTick();
  }

  function dragTick() {
    if (!drag) return;
    drag.frame = requestAnimationFrame(dragTick);
    autoScroll();
    updateDrag();
  }

  function autoScroll() {
    const scroller = targetPane.scroll;
    const rect = scroller.getBoundingClientRect();
    if (drag.x < rect.left || drag.x > rect.right) return;
    const above = rect.top + AUTOSCROLL_EDGE - drag.y;
    const below = drag.y - (rect.bottom - AUTOSCROLL_EDGE);
    let delta = 0;
    if (above > 0) delta = -Math.min(AUTOSCROLL_MAX, (above / AUTOSCROLL_EDGE) * AUTOSCROLL_MAX);
    else if (below > 0) delta = Math.min(AUTOSCROLL_MAX, (below / AUTOSCROLL_EDGE) * AUTOSCROLL_MAX);
    if (delta) scroller.scrollTop += delta;
  }

  /** Which target row is under the pointer, if the pointer is over the target pane at all. */
  function hitTarget() {
    const rect = targetPane.scroll.getBoundingClientRect();
    if (drag.x < rect.left || drag.x > rect.right || drag.y < rect.top || drag.y > rect.bottom) {
      return null;
    }
    const tree = targetPane.tree;
    if (!tree.flat.length) return null;
    const box = targetPane.sizer.getBoundingClientRect();
    if (!box.height) return null;
    const h = ROW_H();
    const index = Math.floor((drag.y - box.top) / h);
    if (index < 0 || index >= tree.flat.length) return null;
    const entry = tree.flat[index];
    if (!entry || entry.skeleton) return null;
    return { index, entry };
  }

  function updateDrag() {
    if (!drag) return;
    const hit = hitTarget();

    if (drag.error) drag.plan = { ok: false, reason: drag.error };
    else if (!hit) {
      drag.plan = {
        ok: false,
        reason: drag.resolving
          ? 'Working out what is inside…'
          : 'Drop onto the target client on the right.',
      };
    } else {
      drag.plan = planDropOn(hit.entry.path, drag.resolution);
    }

    considerHoverExpand(hit);
    considerProbe();
    paintDrag(hit);
  }

  function considerHoverExpand(hit) {
    const path = drag.plan?.ok ? drag.plan.targetPath : null;
    if (path !== drag.hoverPath) { drag.hoverPath = path; drag.hoverAt = performance.now(); return; }
    if (!path || targetPane.tree.expanded.has(path)) return;
    if (performance.now() - drag.hoverAt < HOVER_EXPAND_MS) return;
    drag.hoverAt = Infinity;                       // one attempt per hover
    const node = targetPane.tree.getNode(path);
    if (node?.hasChildren) targetPane.tree.expand(path);
  }

  /**
   * Starts the dry run once the pointer has settled on a legal target.
   *
   * Keyed on the target ARCHIVE and the mode, not on the row: two rows in the
   * same archive with the same mode are the same port, and asking twice would
   * be a second gate-holding scan for an answer already on screen.
   */
  function considerProbe() {
    const plan = drag.plan;
    if (!plan?.ok || !drag.resolution) { drag.probe = null; drag.probeKeyFor = null; return; }
    if (drag.resolution.capped) { drag.probe = null; return; }

    const key = `${plan.targetFileId}|${plan.mode}`;
    if (key !== drag.probeKeyFor) { drag.probeKeyFor = key; drag.probeAt = performance.now(); }
    if (performance.now() - drag.probeAt < PROBE_DELAY_MS) return;

    drag.probe = probeFor(
      drag.resolution.kind,
      plan.targetFileId,
      drag.resolution.entries.map((e) => e.path),
      plan.mode === 'replace',
      () => { if (drag) paintDrag(hitTarget()); });
  }

  /* ---------- drag chrome ---------- */

  let ghostEl = null;
  let dropEl = null;

  function ghost() {
    if (!ghostEl) {
      ghostEl = el('div', { class: 'tp-ghost', role: 'status', 'aria-live': 'polite' },
        el('div', { class: 'tp-ghost-head' },
          el('span', { class: 'tp-ghost-mode' }),
          el('strong', { class: 'tp-ghost-label' })),
        el('div', { class: 'tp-ghost-hint' }),
        el('div', { class: 'tp-ghost-plan' }));
      // Inside the dialog, not on the body.
      //
      // The Explorer's own drag ghost goes on the body because it drags in the
      // page. This one drags inside a native <dialog>, and a modal dialog is in
      // the TOP LAYER: it paints above everything in the ordinary stacking
      // order, whatever z-index they carry. A ghost on the body was therefore
      // computed, positioned, read back correctly by every check -- and drawn
      // behind the dialog, where nobody could see the sentence it exists to
      // show. Caught by screenshotting a live drag; no assertion would have.
      dialog.append(ghostEl);
    }
    return ghostEl;
  }

  function dropBox() {
    if (!dropEl || !dropEl.isConnected) {
      dropEl = el('div', { class: 'tp-drop', 'aria-hidden': 'true' });
      targetPane.sizer.append(dropEl);
    }
    return dropEl;
  }

  function paintDrag(hit) {
    if (!drag) return;
    const plan = drag.plan;
    const node = ghost();

    node.style.transform = `translate(${drag.x + 16}px, ${drag.y + 18}px)`;
    node.dataset.mode = plan?.ok ? plan.mode : 'no';

    const mode = node.querySelector('.tp-ghost-mode');
    mode.textContent = plan?.ok ? (plan.mode === 'replace' ? 'REPLACE' : 'INTO') : 'NO';

    const label = node.querySelector('.tp-ghost-label');
    label.textContent = plan?.ok ? plan.label : (plan?.reason ?? '');

    const hint = node.querySelector('.tp-ghost-hint');
    // What is being carried, said once the count is real. Before that it says
    // it is still counting rather than showing a number that is about to change.
    const carrying = drag.resolution && !drag.resolution.capped
      ? `${plural(drag.resolution.total, drag.resolution.head.label.toLowerCase(),
                  drag.resolution.head.plural.toLowerCase())} from ${drag.resolution.head.archive}`
      : (drag.resolving ? 'Counting what is inside…' : '');
    hint.textContent = plan?.ok ? `${carrying}${plan.hint ? ` — ${plan.hint}` : ''}` : carrying;
    hint.hidden = !hint.textContent;

    const planLine = node.querySelector('.tp-ghost-plan');
    const said = plan?.ok ? consequence(drag.probe) : null;
    clear(planLine);
    planLine.hidden = !said;
    if (said) {
      planLine.dataset.tone = said.tone;
      for (const line of said.lines) planLine.append(el('div', { text: line }));
    }

    targetPane.scroll.dataset.drop = plan?.ok ? plan.mode : 'no';

    const box = dropBox();
    if (!plan?.ok || !hit) { box.hidden = true; return; }
    box.hidden = false;
    box.dataset.mode = plan.mode;
    box.style.transform = `translateY(${hit.index * ROW_H()}px)`;
    box.style.setProperty('--lvl', String(hit.entry.level));
  }

  function cancelDrag() {
    press = null;
    if (!drag) return;
    cancelAnimationFrame(drag.frame);
    drag.abort.abort();
    try { sourcePane.rows.releasePointerCapture(drag.pointerId); } catch { /* already gone */ }
    drag = null;
    delete document.body.dataset.tpDragging;
    delete targetPane.scroll.dataset.dragging;
    delete targetPane.scroll.dataset.drop;
    ghostEl?.remove();
    ghostEl = null;
    if (dropEl) dropEl.hidden = true;
  }

  /* ============================================================
     DROP

     The pointer supplied both source and destination. The only remaining
     question is the one Windows asks too: what to do when a name exists.
     ============================================================ */

  const chooseConflictPolicy = (count, targetArchive) => new Promise((resolve) => {
    let settled = false;
    const finish = (value) => {
      if (settled) return;
      settled = true;
      resolve(value);
    };

    const { dialog: conflictDialog } = modal({
      title: count === 1 ? 'An entry already exists' : `${fmt.format(count)} entries already exist`,
      subtitle: `Choose how MapleBench should handle conflicts in ${targetArchive}.`,
      width: 'min(92vw, 560px)',
      body: el('div', { class: 'tp-conflict' },
        el('div', { class: 'tp-conflict-icon', 'aria-hidden': 'true' }, icon('replace', { size: 22 })),
        el('div', {},
          el('b', { text: 'What do you want to do with the existing entries?' }),
          el('p', { text: 'Keep existing imports only entries that are missing. Replace uses the '
            + 'source versions for conflicts. The destination comes from where you dropped.' }))),
      actions: [
        { label: 'Cancel', run: () => finish(null) },
        { label: 'Keep existing', class: 'btn-primary', run: () => finish('into') },
        { label: 'Replace', class: 'btn-red', run: () => finish('replace') },
      ],
      onOpen: (node) => node.querySelectorAll('.modal-foot .btn')[1]?.focus(),
    });
    conflictDialog.addEventListener('close', () => finish(null), { once: true });
  });

  const commitDrop = async (plan, resolution, probe) => {
    let checked = probe;
    if (!checked) {
      checked = probeFor(
        resolution.kind,
        plan.targetFileId,
        resolution.entries.map((entry) => entry.path),
        plan.mode === 'replace');
    }
    if (checked?.state === 'loading') await checked.promise;
    if (closed) return;
    if (checked?.state !== 'ok') {
      toastError(checked?.error ?? new Error('The import plan could not be checked.'),
        'Nothing was imported');
      return;
    }

    const reported = Number(checked.plan?.totals?.entriesAlreadyThere || 0);
    const conflicts = Math.max(reported, plan.mode === 'replace' ? 1 : 0);
    if (conflicts) {
      const policy = await chooseConflictPolicy(conflicts, plan.targetArchive);
      if (!policy || closed) return;
      plan = { ...plan, mode: policy };
      probe = null;
    }

    // The drag already answered both questions: what is moving, and where it
    // is going. Run that exact import now instead of making the user restage it
    // in a tray and choose the destination a second time.
    const group = queueDirectImport({ resolution, plan, probe });
    if (!group) return;
    await runPendingImports();
    pendingImports.delete(group.id);
  };

  function queueDirectImport({ resolution, plan, probe }) {
    if (resolution.capped) {
      toast(`${fmt.format(resolution.total)} exceeds the per-import limit `
          + `(${fmt.format(resolution.head.maxSelection || maxSelection)}). Nothing was imported; `
          + 'choose a narrower folder or row.',
            'warning');
      return null;
    }

    const id = `d${++importSeq}`;
    const group = {
      id,
      kind: resolution.kind,
      head: resolution.head,
      sourceFileId: resolution.fileId,
      sourceArchive: resolution.head.archive,
      paths: resolution.entries.map((e) => e.path),
      entries: resolution.entries,
      total: resolution.total,
      targetFileId: plan.targetFileId,
      targetArchive: plan.targetArchive,
      mode: plan.mode,
      dropOn: plan.targetName,
      probe: probe || null,
      done: null,
    };
    pendingImports.set(id, group);
    return group;
  }

  /* ============================================================
     THE IMPORT ITSELF

     One line, and that is the point: openPortDialog. Everything hard about
     bringing a thing across belongs to PortService and has had real defects
     beaten out of it. A second copy path here would start that from zero and
     risk producing the same incomplete-client failures.
     ============================================================ */

  async function runPendingImports() {
    const groups = [...pendingImports.values()].filter((g) => !g.done);
    if (!groups.length) return;

    for (const group of groups) {
      if (closed) return;

      // Counted BEFORE, because "these ids are in the target" is not evidence
      // that this port put them there -- most of the time some of them were
      // already there, and a check that cannot tell the two apart would report
      // a cancelled port as a successful one.
      const before = await sampleTarget(group);

      await openPortDialog({
        app,
        kind: group.kind,
        paths: group.paths,
        // The drop chose both the destination and the action. The importer is
        // told those answers rather than opening a second destination form.
        targetFileId: group.targetFileId,
        overwrite: group.mode === 'replace',
        directTarget: true,
      });

      // What actually landed is read back off the archive, not assumed from the
      // dialog: the port is one undo entry and the user may well have undone it
      // from inside the result panel.
      await confirmLanded(group, before);
    }

    // A port writes into the target's in-memory tree, so every cached child
    // list on that side is now a picture of the archive before the import.
    targetPane.tree.invalidateAll();
    await app.refreshFiles?.();
  }

  /** The ids of a sample of this group that the TARGET archive holds right now. */
  async function sampleTarget(group) {
    const withIds = group.entries.filter((e) => e.id).slice(0, 5);
    if (!withIds.length) return null;
    const held = new Set();
    // A handful, not all of them: this is a confirmation, not an audit, and
    // each one is a scoped query on the target's own index.
    for (const entry of withIds) {
      try {
        const hit = await api.portEntries(group.targetFileId, group.kind, String(entry.id), 5);
        if ((hit.results || []).some((r) => String(r.id) === String(entry.id))) held.add(entry.id);
      } catch {
        return null;             // cannot be read; say so rather than guessing
      }
    }
    return { held, checked: withIds.length };
  }

  /**
   * Did it land? Asked of the TARGET archive's own index, after the fact.
   *
   * The port dialog reports what it wrote; this asks the archive what it holds,
   * which is a different question and the only one worth trusting. It is a
   * before-and-after comparison rather than a lookup, because a lookup cannot
   * tell "this port wrote it" from "the target always had it" -- and it is
   * exactly the entries a target already has that a lookup would find. A port
   * that wrote nothing, or that was cancelled, or that was undone from the
   * result panel, all come out of this as "nothing changed", which is the
   * truth in all three cases.
   */
  async function confirmLanded(group, before) {
    const after = await sampleTarget(group);

    if (!after) {
      group.done = group.entries.some((e) => e.id)
        ? `${group.targetArchive} could not be re-read, so nothing here can say whether it landed.`
        : 'Imported. These have no id to look up, so check them in the tree.';
      return;
    }

    const gained = [...after.held].filter((id) => !before?.held?.has(id)).length;
    const missing = after.checked - after.held.size;

    if (gained > 0 && missing === 0) {
      group.done = `In ${group.targetArchive} now — ${gained} of the ${after.checked} checked were `
                 + 'not there before and are now. Save it to keep them.';
    } else if (gained > 0) {
      group.done = `${gained} newly in ${group.targetArchive}, and ${missing} of the ${after.checked} `
                 + 'checked are still not there. The port result says which.';
    } else if (missing === 0) {
      group.done = `${group.targetArchive} holds all ${after.checked} checked — and held them before `
                 + 'this too, so nothing here proves the port wrote anything. Read its result.';
    } else {
      group.done = `Nothing changed in ${group.targetArchive}: ${missing} of the ${after.checked} `
                 + 'checked are still missing. The port was cancelled, refused, or undone.';
    }
  }

  /* ============================================================
     GO
     ============================================================ */

  sourcePane.setClient(sourceClient);
  targetPane.setClient(targetClient);

  // The archive the caller pointed at, opened and shown, rather than left as
  // one collapsed row among several.
  if (sourceFileId && sourceClient.archives.some((a) => a.fileId === sourceFileId)) {
    sourcePane.tree.expand(sourceFileId);
    kindFor(sourceFileId).catch(() => { /* the refusal is said at the drop */ });
  }

  return { close: closeView, element: dialog };
}
