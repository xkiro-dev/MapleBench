/**
 * Imports content from a classic or split client into the writable client.
 *
 * PortService resolves the selected entry and its associated names, sounds,
 * effects, set data, shop rows, and cross-archive canvas links before the user
 * approves the operation. See port.js.
 *
 * Search is the primary discovery path; the tree remains available for unnamed
 * nodes, known paths, and archives without String entries.
 *
 * The search does NOT go through /api/db/search, and that is deliberate. That
 * endpoint answers with ids and no paths, so database.js has to guess a path from
 * naming conventions and check it against the tree — a guess that can miss. This
 * asks /api/port/entries, which answers out of the very index the port plan
 * resolves against, so a row that is offered is a row the plan can locate.
 */

import { api } from './api.js';
import { el, clear, toast, toastError, fmt, modal, confirmDialog, debounce, skeletonRows, busyBar } from './ui.js';
import { icon } from './icons.js';
import { lazySprite } from './media.js';
import { TreeView } from './tree.js';
import { openPortDialog } from './port.js';

const LAST_SOURCE = 'mb.importSource';

/** Rows one search shows. Matches the server's own ceiling; asking for more is refused there. */
const LIMIT = 200;

/**
 * The archive a kind's names live in.
 *
 * Opened alongside the archive being browsed, without asking, and this is not a
 * nicety. A port reads the name it carries out of the SOURCE client's String.wz;
 * with only Mob open, every copy lands nameless and the failure is reported after
 * the fact as "the source has no String.wz open at all". It is also what makes
 * the search box work — there is nothing to search by name against otherwise.
 * String is the cheap one: a split String assembles in well under a second
 * against Mob's 6.7, so it is opened rather than offered.
 */
const NAMES_ARCHIVE = 'String';
const WHOLE_CLIENT = '*whole-client*';

const bytes = (n) => {
  if (!Number.isFinite(n) || n <= 0) return '';
  const mb = n / 1024 / 1024;
  if (n < 1024) return `${n} B`;
  if (mb < 1) return `${(n / 1024).toFixed(0)} KB`;
  return mb >= 1024 ? `${(mb / 1024).toFixed(1)} GB` : `${mb.toFixed(0)} MB`;
};

/** The logical family of a physical classic part; Map2 is not Map, Map001 is. */
const familyOf = (archive) => archive?.family
  || archive?.name?.replace(/([0-9]{3})$/, '')
  || '';

/**
 * Opens the importer.
 *
 * @param app        the App, for the session, the file list and the port dialog
 * @param startPath  a client folder to start on, if the caller knows one
 * @param options    `dual` opens the selected archive in Dual Client View
 */
export function openImporter(app, startPath, { dual = false } = {}) {
  /* ---------------- state ---------------- */

  /** 'client' while choosing, 'pick' in the importer, 'dual' while handing off. */
  let stage = 'client';

  let layout = null;          // last /api/import/detect result
  let browsing = false;       // the in-dialog folder list is up (browser tab only)
  let opening = null;         // archive name currently being opened
  let closed = false;

  /** The opened source archive: { fileId, name, seconds }. */
  let source = null;
  /** The last /api/port/entries answer for it. */
  let entries = null;
  /** Ticked paths -> { id, name, label }. A Map so the tray can name them. */
  const picked = new Map();
  // Entries the target client already has are disabled by default; this puts
  // them back. Off by default because the common case is looking for what is
  // NEW in a newer client, and those rows are noise for that.
  let showExisting = false;

  let query = '';
  let mode = 'search';        // 'search' | 'tree'
  let tree = null;            // the virtualized TreeView, built on first use
  let searching = null;       // AbortController for the request in the air
  let sprites = null;         // AbortController for the icons of the current repaint

  // Two folder clicks race two detects, and without this the slower answer wins:
  // you would see folder A's archives, pick one, and open B's.
  let generation = 0;

  /* ---------------- chrome ---------------- */

  const sourceInput = el('input', {
    class: 'field-input no-icon',
    placeholder: 'e.g. C:\\Nexon\\Library\\maplestory\\appdata',
    value: startPath ?? localStorage.getItem(LAST_SOURCE) ?? '',
  });

  const body = el('div', { class: 'imp' });

  const { dialog } = modal({
    title: dual ? 'Open Dual Client View' : 'Import from another client',
    subtitle: dual
      ? 'Choose the second client folder. Open the complete client, or one archive when you only need that family.'
      : 'Choose a source, select entries, then review every required dependency before importing.',
    width: 'min(92vw, 760px)',
    body,
    onOpen: () => queueMicrotask(() => sourceInput.focus()),
  });

  dialog.addEventListener('close', () => {
    closed = true;
    generation++;
    searching?.abort();
    sprites?.abort();
  });

  /* ============================================================
     STAGE ONE — WHICH CLIENT, WHICH ARCHIVE
     ============================================================ */

  const renderClient = () => {
    dialog.style.width = 'min(92vw, 760px)';
    clear(body);
    sprites?.abort();

    body.append(el('div', { class: 'imp-field' },
      el('label', { class: 'imp-label', text: 'Source client' }),
      el('div', { class: 'imp-row' },
        sourceInput,
        el('button', { class: 'btn btn-sm', onclick: () => pickFolder() },
          icon('folder', { size: 14 }), 'Browse'))));

    const list = el('div', { class: 'picker-list', id: 'imp-archives' });
    body.append(bannerHost, list);

    if (browsing) return;                 // the folder list owns the panel
    renderArchives();
  };

  const bannerHost = el('div', { class: 'picker-banner' });

  const archiveList = () => body.querySelector('#imp-archives');

  const pickFolder = async () => {
    const native = window.mapleBenchDesktop;
    if (native) {
      try {
        const chosen = await native.pickFolder('Choose the client to take from, or its Data folder');
        if (!chosen) return undefined;         // cancelled; leave everything alone
        sourceInput.value = chosen;
        browsing = false;
        return detect();
      } catch (error) {
        // A failed native dialog is not a reason to lose the gesture -- fall
        // through to the in-dialog list rather than leaving the click with no
        // outcome at all.
        toastError(error, 'The folder dialog could not be opened');
      }
    }
    return browse(sourceInput.value.trim() || null);
  };

  const browse = async (path) => {
    browsing = true;
    const mine = ++generation;
    const list = archiveList();
    if (!list) return;
    clear(list);
    list.append(skeletonRows(3));
    try {
      const result = await api.browse(path);
      if (mine !== generation || closed) return;
      clear(list);
      list.append(el('div', { class: 'imp-crumb', text: path || 'This computer' }));
      if (path) {
        list.append(folderRow('check', 'Use this folder', () => {
          sourceInput.value = path;
          browsing = false;
          detect();
        }));
        const parent = path.replace(/[\\/][^\\/]+[\\/]?$/, '');
        if (parent && parent !== path) list.append(folderRow('arrowUp', '..', () => browse(parent)));
      }
      for (const dir of result.directories) {
        list.append(folderRow('folder', dir.name, () => browse(dir.path)));
      }
    } catch (error) {
      if (mine !== generation || closed) return;
      clear(list);
      list.append(errorRow(error.message));
    }
  };

  const folderRow = (glyph, name, onclick) =>
    el('button', { class: 'picker-row', onclick },
      icon(glyph, { size: 16 }), el('span', { class: 'name', text: name }));

  const detect = async () => {
    const path = sourceInput.value.trim();
    if (!path) return;
    browsing = false;
    const mine = ++generation;

    const list = archiveList();
    if (list) { clear(list); list.append(skeletonRows(5)); }

    try {
      const result = await api.importDetect(path);
      if (mine !== generation || closed) return;
      layout = result;
      localStorage.setItem(LAST_SOURCE, path);
      renderArchives();
    } catch (error) {
      if (mine !== generation || closed) return;
      layout = null;
      clear(bannerHost);
      const host = archiveList();
      if (host) { clear(host); host.append(errorRow(error.message)); }
    }
  };

  const renderArchives = () => {
    clear(bannerHost);
    const list = archiveList();
    if (!list) return;
    clear(list);

    if (!layout) {
      list.append(el('div', { class: 'imp-empty' },
        icon('info', { size: 15 }),
        el('span', { text: dual
          ? 'Choose the second client. MapleBench recognizes ordinary .wz archives and modern '
            + 'split Data\\ clients, and keeps this side read-only.'
          : 'Choose a client folder. MapleBench recognizes ordinary .wz archives and modern split '
            + 'Data\\ clients, then opens either layout through the same read-only import workflow.' })));
      return;
    }

    if (layout.kind !== 'split' && layout.kind !== 'classic') {
      bannerHost.append(el('div', { class: 'notice anim-rise', 'data-tone': 'info' },
        icon('info', { size: 16 }),
        el('span', { text: `${layout.summary} There is nothing here to import from.` })));
      return;
    }

    bannerHost.append(el('div', { class: 'notice anim-rise', 'data-tone': 'good' },
      icon('check', { size: 16 }),
      el('span', { text: layout.summary })));

    if (dual) list.append(clientRow());
    for (const archive of layout.archives) list.append(archiveRow(archive, list));
  };

  const clientRow = () => {
    const archives = layout.archives.filter((archive) => archive.supported);
    const totalBytes = archives.reduce((sum, archive) => sum + (archive.sourceBytes || 0), 0);
    return el('button', {
      class: 'picker-row imp-archive imp-client',
      disabled: !archives.length || opening !== null,
      'aria-busy': opening === WHOLE_CLIENT ? 'true' : null,
      'data-tip': 'Open every archive from this folder read-only and show the whole client in the left pane.',
      onclick: () => openClient(),
    },
      icon('folderOpen', { size: 17 }),
      el('span', { class: 'name', text: 'Open entire client folder' }),
      el('span', { class: 'imp-archive-meta', text:
        `${fmt.format(archives.length)} archive${archives.length === 1 ? '' : 's'} · read-only` }),
      el('span', { class: 'size', text: bytes(totalBytes) }),
      el('span', { class: 'imp-archive-go', text:
        opening === WHOLE_CLIENT ? 'Opening client…' : 'Open folder' }));
  };

  const archiveRow = (archive, list) => {
    // Cross-referenced against the session rather than against what this dialog
    // did, so an archive opened before a page reload still reads as open.
    const already = app.files.find(
      (f) => f.filePath?.toLowerCase() === archive.path?.toLowerCase());

    const bits = [];
    if (layout.kind === 'split') {
      if (archive.parts) bits.push(`${archive.parts} part${archive.parts === 1 ? '' : 's'}`);
      if (archive.packs) bits.push(`${archive.packs} pack${archive.packs === 1 ? '' : 's'}`);
      if (archive.subArchives?.length) bits.push(archive.subArchives.join(', '));
    } else {
      bits.push('classic .wz');
      if (familyOf(archive) !== archive.name) bits.push(`${familyOf(archive)} family`);
    }

    // The whole row is the button, and the action is Open. That is the change:
    // opening costs seconds and writes nothing, it works for every archive
    // including the five that can never be a .wz, and it is what somebody taking
    // a few mobs across actually wants.
    const row = el('button', {
      class: 'picker-row imp-archive',
      disabled: !archive.supported || opening !== null,
      'data-open': already ? 'true' : null,
      'data-tip': archive.supported
        ? (dual
          ? `Open ${archive.name} read-only beside your current client.`
          : (layout.kind === 'split'
            ? `Merge ${archive.name} in memory and add it here, read-only. Nothing is written to disk.`
            : `Open ${archive.name}.wz as a read-only import source. Nothing is written to disk.`))
        : archive.reason,
      onclick: () => openArchive(archive),
    },
      icon(already ? 'check' : 'archive', { size: 16 }),
      el('span', { class: 'name', text: archive.name }),
      el('span', { class: 'imp-archive-meta', text: bits.join(' · ') }),
      el('span', { class: 'size', text: bytes(archive.sourceBytes) }),
      el('span', { class: 'imp-archive-go' },
        opening === archive.name ? 'Opening…'
          : dual ? 'Open in Dual View'
            : already ? 'Open again' : 'Open'));

    if (opening === archive.name) row.setAttribute('aria-busy', 'true');
    void list;
    return row;
  };

  /* ============================================================
     OPENING ONE ARCHIVE

     Read-only, always. Split archives are locked when assembled and classic WZ
     files are locked before they enter the session. WzEditService and
     WzSaveService both refuse either one. It is a source here and nothing else.
     ============================================================ */

  const openArchive = async (archive) => {
    opening = archive.name;
    renderArchives();
    const started = performance.now();

    try {
      const file = await api.importOpen({
        sourceFolder: layout.dataPath ?? layout.path,
        archive: archive.name,
      });
      const took = (performance.now() - started) / 1000;

      // The source client's String archive, opened alongside without being
      // asked. Not a convenience: the port reads the name it carries out of the
      // SOURCE client's String.wz, so with only Mob open every copy would land
      // nameless -- reported afterwards rather than prevented. It is also the
      // only thing the search box can match names against.
      await openNames(archive.name);

      await app.refreshFiles();

      source = { fileId: file.id, name: file.name, seconds: took };
      picked.clear();
      query = '';
      searchInput.value = '';
      mode = 'search';
      // Dropped rather than re-rooted: it is a different archive, so every
      // cached node DTO and every expanded path in it belongs to the last one.
      tree = null;
      treeHost = null;

      if (dual) {
        stage = 'dual';
        dialog.addEventListener('close', () => {
          void app.showDualView(file.id);
        }, { once: true });
        dialog.close();
        return;
      }

      stage = 'pick';
      render();
      await loadEntries();
    } catch (error) {
      toastError(error, `${archive.name} could not be opened`);
    } finally {
      opening = null;
      if (stage === 'client') renderArchives();
    }
  };

  /** Opens every detected archive as one read-only source client for Dual View. */
  const openClient = async () => {
    opening = WHOLE_CLIENT;
    renderArchives();
    const busy = toast('Opening the source client read-only…', 'info');

    try {
      const result = await api.importOpenClient({
        sourceFolder: layout.dataPath ?? layout.path,
      });
      await app.refreshFiles();
      busy.remove();

      if (result.failed?.length) {
        toast(result.failed.map((failure) => `${failure.name}: ${failure.message}`).join('\n'),
          'warning', {
            title: `${fmt.format(result.failed.length)} archive${result.failed.length === 1 ? '' : 's'} could not be opened`,
          });
      }

      const first = result.opened?.find((file) => file.readOnly);
      if (!first) {
        toast('No source archives were opened. The Dual View was not changed.', 'error');
        return;
      }

      stage = 'dual';
      dialog.addEventListener('close', () => {
        void app.showDualView(first.id);
      }, { once: true });
      dialog.close();
    } catch (error) {
      busy.remove();
      toastError(error, 'The source client could not be opened');
    } finally {
      opening = null;
      if (stage === 'client') renderArchives();
    }
  };

  const archivesForFamily = (family) => (layout?.archives || []).filter((archive) =>
    archive.supported && familyOf(archive).toLowerCase() === family.toLowerCase());

  const archiveIsReady = (archive) => app.files.some((file) =>
    file.readOnly && file.filePath?.toLowerCase() === archive.path?.toLowerCase());

  /** Opens every part of the client's String family if it is not already open. */
  const openNames = async (skipIfNamed) => {
    if (familyOf({ name: skipIfNamed }) === NAMES_ARCHIVE) return;
    const names = archivesForFamily(NAMES_ARCHIVE).filter((archive) =>
      !archiveIsReady(archive));
    if (!names.length) return;

    for (const archive of names) {
      try {
        await api.importOpen({
          sourceFolder: layout.dataPath ?? layout.path,
          archive: archive.name,
        });
      } catch (error) {
        // Not fatal, and not silent. Without every String part some rows may
        // have no names, and the source checklist will keep reporting the gap.
        toast(`${archive.name} could not be opened, so some names may be unavailable: ${error.message}`,
              'warning');
      }
    }
  };

  /* ============================================================
     STAGE TWO — PICKING
     ============================================================ */

  const searchInput = el('input', {
    type: 'search', class: 'field-input no-icon',
    placeholder: 'Search by name or id — "Zakum", "8800000", "Cap"…',
    'aria-label': 'Search this archive',
  });
  searchInput.addEventListener('input', debounce(() => {
    query = searchInput.value;
    loadEntries();
  }, 160));

  const resultHost = el('div', { class: 'imp-results' });
  const trayHost = el('div', { class: 'imp-tray' });
  const sourcesHost = el('div');

  const render = () => {
    if (stage === 'client') { renderClient(); return; }

    dialog.style.width = 'min(96vw, 1000px)';
    clear(body);
    sprites?.abort();

    body.append(el('div', { class: 'imp-head' },
      el('button', {
        class: 'btn btn-sm btn-ghost',
        'data-tip': 'Back to the archive list. What is open stays open.',
        onclick: () => { stage = 'client'; render(); },
      }, icon('arrowUp', { size: 13 }), 'Archives'),
      el('div', { class: 'imp-head-text' },
        el('b', { text: source.name }),
        el('span', { class: 'imp-head-sub', text: subtitle() })),
      el('span', { class: 'imp-lock', 'data-tip':
        'Source opened read-only. MapleBench will not edit or save it.' },
        icon('lock', { size: 12 }), 'read-only')));

    body.append(sourcesHost);

    body.append(el('div', { class: 'imp-toolbar' },
      el('div', { class: 'imp-search' }, icon('search', { size: 15 }), searchInput),
      el('div', { class: 'cat-tabs' },
        tab('search', 'Search'),
        tab('tree', 'Browse the tree'))));

    body.append(resultHost, trayHost);
    renderSources();
    renderTray();
    if (mode === 'tree') renderTree();
    else renderResults();
  };

  const subtitle = () => {
    const bits = [];
    if (entries?.supported) {
      bits.unshift(`${fmt.format(entries.total)} ${(entries.total === 1
        ? entries.label : entries.plural).toLowerCase()}`);
    }
    return bits.join(' · ') || 'Ready to search';
  };

  const tab = (value, label) => el('button', {
    class: 'cat-tab', 'aria-pressed': mode === value ? 'true' : 'false',
    onclick: () => {
      if (mode === value) return;
      mode = value;
      render();
    },
  }, label);

  /* ---------- what the source itself is missing ---------- */

  const missingSourceNeeds = () => (entries?.sources || []).filter((need) => {
    const family = archivesForFamily(need.family);
    // `need.open` is the port engine's content check. The second half is the
    // importer's stricter promise: every physical classic part is present and
    // locked, not merely one member that happened to satisfy the family role.
    return !need.open || !family.length || family.some((archive) => !archiveIsReady(archive));
  });

  const archivesForNeed = (need) => archivesForFamily(need.family);

  const uniqueArchives = (archives) => [...new Map((archives || [])
    .filter(Boolean)
    .map((archive) => [(archive.path || archive.name).toLowerCase(), archive])).values()];

  /**
   * The source-side archives this kind takes parts from, and whether each is open.
   *
   * Nothing checked this before. port.js's setup gap asks whether the TARGET has
   * somewhere to put the name; it is silent on whether the source has a name to
   * give. Opening a split client's Mob on its own and porting out of it produces
   * a copy that is nameless and silent for exactly that reason, and the result
   * panel reports it after the fact rather than before.
   */
  const renderSources = () => {
    clear(sourcesHost);
    const missing = missingSourceNeeds();
    if (!entries?.supported || !missing.length) return;

    // Nothing is asked for until something is picked. Once there is a selection,
    // every satellite family has to be inspected: an item does not carry a flag
    // saying "I have an ItemEff row" or "I have a Commodity row", so an unopened
    // archive is not evidence that the dependency is absent. Opening the family
    // lets the plan copy only the rows these picks actually use.
    if (!picked.size) return;

    const n = picked.size;
    const families = sentenceList(missing.map((need) => need.family));

    sourcesHost.append(el('div', { class: 'notice', 'data-tone': 'warn' },
      icon('alert', { size: 15 }),
      el('div', {},
        el('div', { text: `${families} ${missing.length === 1 ? 'is' : 'are'} not open. MapleBench must `
          + `inspect ${missing.length === 1 ? 'it' : 'them'} before importing the ${fmt.format(n)} selected `
          + `${n === 1 ? 'entry' : 'entries'}, or it cannot know which dependencies they have.` }),
        el('div', { class: 'imp-hint', text:
          'Open them before importing. Only the names, sounds, set definitions, shop rows and effects '
          + 'actually used by the selection will be copied.' })),
      el('div', { class: 'imp-gap-actions' }, gapActions(missing))));
  };

  /** "Sound", "Sound and Etc", "Sound, Etc and Map" — for a sentence, not a list. */
  const sentenceList = (names) => (names.length < 2
    ? names[0] ?? ''
    : `${names.slice(0, -1).join(', ')} and ${names[names.length - 1]}`);

  /**
   * The buttons that close the gap.
   *
   * One button per missing archive got the job done and made the user do it
   * one at a time -- and each round trip re-reads the entry list, so opening
   * two meant two waits for a decision they had already made once. When more
   * than one is missing the first button opens all of them, with the total
   * size on it, because the size is the whole reason this is a choice: a split
   * Sound is 8 GB and holding one costs about 2.2 GB of memory.
   *
   * The individual buttons stay, so "I only want the names" is still one click
   * rather than a thing you have to talk the dialog out of.
   */
  const gapActions = (missing) => {
    const found = missing
      .map((s) => ({
        need: s,
        archives: archivesForNeed(s),
      }));

    const openable = found.filter((f) => f.archives.length);
    const allArchives = uniqueArchives(openable.flatMap((f) => f.archives));
    const buttons = [];

    if (openable.length > 1) {
      const total = allArchives.reduce((sum, archive) => sum + (archive.sourceBytes || 0), 0);
      buttons.push(el('button', {
        class: 'btn btn-sm btn-primary',
        'data-tip': 'Opens all of them read-only, in one go: '
                  + allArchives.map((archive) => archive.name).join(', ') + '.',
        onclick: () => openSatellites(allArchives),
      }, icon('folderOpen', { size: 13 }),
        `Open all ${allArchives.length} · ${bytes(total)}`));
    }

    for (const { need, archives } of found) {
      const total = archives.reduce((sum, archive) => sum + (archive.sourceBytes || 0), 0);
      buttons.push(el('button', {
        class: 'btn btn-sm',
        disabled: !archives.length,
        'data-tip': archives.length
          ? `Open ${archives.map((archive) => archive.name).join(', ')} read-only — ${bytes(total)}.`
          : `This client has no ${need.family} archive that can be opened.`,
        onclick: () => openSatellites(archives),
      }, icon('folderOpen', { size: 13 }),
        `Open ${need.family}${archives.length ? ` · ${bytes(total)}` : ''}`));
    }
    return buttons;
  };

  const openSatellites = async (archives) => {
    const list = (archives || []).filter(Boolean);
    if (!list.length) return { opened: [], failed: [] };

    // Sequentially, because the server runs one import at a time on purpose:
    // assembling two split archives at once has them competing for the same
    // disk and the same memory, and the gate would queue them anyway.
    const busy = toast(list.length === 1
      ? `Opening ${list[0].name} — ${bytes(list[0].sourceBytes)}…`
      : `Opening ${list.length} archives — ${bytes(list.reduce((n, a) => n + (a.sourceBytes || 0), 0))}…`,
      'info');

    const opened = [];
    const failed = [];
    for (const archive of list) {
      try {
        await api.importOpen({
          sourceFolder: layout.dataPath ?? layout.path,
          archive: archive.name,
        });
        opened.push(archive.name);
      } catch (error) {
        failed.push(`${archive.name}: ${error.message}`);
      }
    }

    busy.remove();
    await app.refreshFiles();
    // Re-asked once, at the end, rather than per archive: whether a satellite
    // counts as open is the server's answer, and a local guess that disagreed
    // with it would be a green tick over a part that still cannot travel.
    await loadEntries();

    if (opened.length) {
      toast(`${opened.join(', ')} open, read-only. Those parts can travel now.`, 'success');
    }
    // Named individually. "2 of 3 opened" tells nobody which one is still
    // missing, and the missing one is the part that will not travel.
    for (const problem of failed) toast(problem, 'error');
    return { opened, failed };
  };

  /* ---------- the search ---------- */

  /**
   * Runs the current query, cancelling whatever was already in the air.
   *
   * Debouncing thins the requests out but does not order them: a slow answer for
   * "za" landing after a quick one for "zakum" would repaint the grid with
   * results for what the box no longer says. Same rule as database.js.
   */
  const loadEntries = async () => {
    if (!source) return;
    searching?.abort();
    const mine = new AbortController();
    searching = mine;

    const done = mode === 'search' ? busyBar(resultHost) : () => {};
    try {
      const data = await api.portEntries(source.fileId, null, query.trim(), LIMIT,
                                         { signal: mine.signal });
      if (searching !== mine || closed) return;
      searching = null;
      entries = data;
      done();
      renderSources();
      const head = body.querySelector('.imp-head-sub');
      if (head) head.textContent = subtitle();
      if (mode === 'search') renderResults();
      renderTray();
    } catch (error) {
      done();
      if (mine.signal.aborted || searching !== mine || closed) return;
      searching = null;
      if (mode === 'search') {
        clear(resultHost);
        resultHost.append(errorRow(error.message));
      }
    }
  };

  const renderResults = () => {
    clear(resultHost);
    // One scope per repaint: this grid repaints per keystroke, and the icons the
    // last one asked for are already stale by the time they arrive.
    sprites?.abort();
    sprites = new AbortController();

    if (!entries) {
      resultHost.append(skeletonRows(6));
      return;
    }

    // An archive that holds nothing portable, and one whose kind is declared and
    // refused, are different answers. Both are said out loud, in the server's own
    // words, rather than collapsed into an empty grid.
    if (!entries.supported) {
      resultHost.append(el('div', { class: 'notice', 'data-tone': 'bad' },
        icon('info', { size: 15 }),
        el('div', {},
          el('b', { text: entries.kind
            ? `${entries.plural} cannot be imported as one archive`
            : `${entries.archive} holds nothing this can carry` }),
          el('p', { text: entries.reason || '' }))));
      return;
    }

    if (!entries.namesAvailable) {
      resultHost.append(el('div', { class: 'imp-note' },
        icon('info', { size: 14 }),
        el('span', { text: 'No String archive is open for this client, so nothing here has a name and '
                         + 'only ids and paths can be searched.' })));
    }

    resultHost.append(el('div', { class: 'imp-result-head' },
      el('span', { text: query.trim()
        ? `${fmt.format(entries.matched)} match${entries.matched === 1 ? '' : 'es'}`
          + (entries.truncated ? ` · showing the best ${fmt.format(entries.results.length)}` : '')
        : `${fmt.format(entries.total)} ${entries.plural.toLowerCase()} in ${entries.archive}`
          + (entries.truncated ? ` · showing the first ${fmt.format(entries.results.length)}` : '') }),
      el('button', {
        class: 'btn btn-sm btn-ghost',
        'data-tip': `Select everything on screen, up to the ${fmt.format(entries.maxSelection ?? 60)} `
                  + 'a selection import carries.',
        onclick: () => pickAllShown(),
      }, icon('check', { size: 13 }), 'Select all shown'),
      alreadyCount() ? el('button', {
        class: 'btn btn-sm btn-ghost', 'data-active': showExisting ? 'true' : null,
        'data-tip': 'Ids are added between builds, not reused, so anything already in the '
                  + 'target is the same entry you already have. Shown greyed out and not '
                  + 'importable unless you turn this on.',
        onclick: () => { showExisting = !showExisting; renderResults(); },
      }, icon('eye', { size: 13 }),
         `${fmt.format(alreadyCount())} already yours`) : null));

    if (!entries.results.length) {
      resultHost.append(el('div', { class: 'imp-empty' },
        icon('search', { size: 15 }),
        el('span', { text: query.trim()
          ? `Nothing in ${entries.archive} matches "${query.trim()}". Names come from this client's own `
            + 'String archive, so anything it does not name can only be found by id or by path.'
          : `${entries.archive} has no ${entries.plural.toLowerCase()} in it.` })));
      return;
    }

    const grid = el('div', { class: 'imp-grid' });
    for (const entry of entries.results) grid.append(card(entry));
    resultHost.append(grid);

    if (entries.truncated) {
      resultHost.append(el('div', { class: 'imp-note' },
        icon('alert', { size: 14 }),
        el('span', { text: `More than ${fmt.format(entries.results.length)} match this. Type more of the `
                         + 'name to narrow it, or take the whole archive at the bottom.' })));
    }
  };

  const card = (entry) => {
    const label = entry.name || `${entries.label} ${entry.id}`;

    // Already in the client you would put it in.
    //
    // MapleStory builds add ids, they do not reassign them, so an id present in
    // both clients is the same thing in both: importing it gains nothing and
    // risks overwriting something already edited. Disabled rather than hidden,
    // because "it is here and it is already yours" is a useful answer and a
    // missing row is not -- and `showExisting` puts them back for the case
    // where a build really did rework an entry under its old id.
    const already = entry.inTarget && !showExisting;

    const box = el('input', {
      type: 'checkbox', class: 'imp-check', checked: picked.has(entry.path),
      disabled: already || null,
      'aria-label': already ? `${label} is already in the target client` : `Include ${label}`,
    });
    box.addEventListener('change', () => {
      setPicked(entry.path, box.checked ? { id: entry.id, label } : null);
      node.dataset.picked = box.checked ? 'true' : 'false';
    });

    const thumb = el('div', { class: 'imp-thumb' });
    const img = el('img', { alt: '', decoding: 'async' });
    img.addEventListener('error', () => {
      // A 204 -- there is no art under this node -- arrives as an error, and a
      // broken-image glyph reads as a failure rather than as "nothing to show".
      img.remove();
      if (!thumb.firstChild) thumb.append(el('span', { class: 'placeholder' }, icon('layers', { size: 18 })));
    });
    thumb.append(img);
    // Through the shared loader: a grid of 200 rows on the session's single gate
    // is what made the Database search feel slower the more it matched. Only what
    // is visible is asked for, and the previous keystroke's are cancelled.
    // eager: this grid lives in a modal <dialog>, where the intersection
    // observer never reports and every frame stayed empty. See lazySprite.
    lazySprite(img, api.thumbUrl(entry.path), { signal: sprites?.signal, eager: true });

    const node = el('label', {
      class: 'imp-card', 'data-picked': picked.has(entry.path) ? 'true' : 'false',
      'data-already': already ? 'true' : null,
      'data-tip': already
        ? `${entry.path} — the target client already has ${entry.id}`
        : entry.path,
    },
      box, thumb,
      el('div', { class: 'imp-card-text' },
        el('div', { class: 'imp-card-name', text: label }),
        el('div', { class: 'imp-card-id', text: entry.relative || String(entry.id) })),
      already ? el('span', { class: 'imp-have', text: 'have' }) : null);
    return node;
  };

  /** How many of the rows on screen the target client already holds. */
  const alreadyCount = () => (entries?.results ?? []).filter((e) => e.inTarget).length;

  const pickAllShown = () => {
    // Bounded by what a selection port carries, and it says so rather than
    // arming 200 paths the plan would refuse in one sentence at the end.
    const room = (entries.maxSelection ?? 60) - picked.size;
    if (room <= 0) {
      toast(`A selection import carries ${fmt.format(entries.maxSelection ?? 60)} at a time, and that is `
          + 'already selected. Take the whole archive instead, or import these first.', 'warning');
      return;
    }
    const untickedBefore = entries.results.filter((e) => !picked.has(e.path)).length;
    let added = 0;
    for (const entry of entries.results) {
      if (added >= room) break;
      if (picked.has(entry.path)) continue;
      // Tick-all must not arm the rows the grid is greying out; that would
      // quietly re-import everything the user already has.
      if (entry.inTarget && !showExisting) continue;
      picked.set(entry.path, { id: entry.id, label: entry.name || `${entries.label} ${entry.id}` });
      added++;
    }
    // Said when it bit, and only then. "Ticked 60 of 200" over a screen where 60
    // was all there was would be the tool inventing a limitation it did not hit.
    if (added < untickedBefore) {
      toast(`Selected ${fmt.format(added)} of ${fmt.format(untickedBefore)}. A selection import carries `
          + `${fmt.format(entries.maxSelection ?? 60)} at a time — take the whole archive for more.`,
            'info');
    }
    for (const path of picked.keys()) tree?.checked.add(path);
    renderResults();
    renderTray();
  };

  const setPicked = (path, value) => {
    if (value) picked.set(path, value);
    else picked.delete(path);
    // The tree keeps its own Set because it repaints from one; both are kept in
    // step here so switching tabs never loses a tick.
    if (tree) {
      if (value) tree.checked.add(path);
      else tree.checked.delete(path);
      tree.scheduleRender();
    }
    renderTray();
  };

  /* ---------- the tree, for what search cannot reach ---------- */

  /** The tree's own scroller, kept across tab switches. Null until first asked for. */
  let treeHost = null;

  const renderTree = () => {
    clear(resultHost);

    resultHost.append(el('div', { class: 'imp-note' },
      icon('info', { size: 14 }),
      el('span', { text: 'Every node in the archive, as it is really laid out. Select an entry — an image or '
                       + 'a property whose name is its id — and it imports the same way a searched one '
                       + 'does. Select a folder or image that opens to include every importable entry inside it.' })));

    // Built once and re-appended, never rebuilt per tab switch. A TreeView
    // registers pointermove, pointerup and keydown on the window for its drag
    // gesture and never takes them off, so a fresh one every time this tab is
    // chosen would leave a set of live listeners behind on each switch -- and
    // the tree would forget which folders were open, which is the visible half
    // of the same bug.
    if (!treeHost) {
      // The same virtualizer the Explorer uses. A folder here holds up to 12,134
      // children (Character.wz/Hair on a v232 client) and only the rows in view
      // exist in the DOM; building them all would be forty thousand elements for
      // a list nobody can read.
      const scroll = el('div', { class: 'tree-scroll imp-tree' });
      const sizer = el('div', { class: 'tree-sizer' });
      const rows = el('div', {
        class: 'tree-rows', role: 'tree', 'aria-label': 'The source archive',
        'aria-multiselectable': 'true', tabindex: '0',
      });
      sizer.append(rows);
      scroll.append(sizer);
      treeHost = scroll;

      tree = new TreeView({
        scroller: scroll, rows, sizer,
        checkable: true,
        // A folder tick means everything under it.
        //
        // It used to mean almost nothing, and the note above the tree said so
        // outright. What it actually did was worse than nothing: the folder
        // went into the tray as one entry, counted against the cap, and was
        // sent as a path to a selection import that has no notion of a
        // directory. Ticking the images inside one at a time was the only way
        // to say what the gesture plainly meant.
        onCheckToggle: async (path, node, on) => {
          // Everything below runs inside a promise the tree does not hold, so a
          // throw in here would reject into nothing: the box springs back, no
          // row is ticked, and the only trace is a console line nobody opens.
          // A tick that cannot be honoured has to say why.
          try {
            // A row with nothing inside is only itself.
            if (!node?.hasChildren) {
              if (on) {
                picked.set(path, {
                  id: null,
                  label: node?.displayName || node?.name || path.split('/').pop(),
                });
              } else picked.delete(path);
              tree.setChecked(path, on);
              return;
            }

            // Unticking is answered from the Set, with no fetch: undoing the
            // gesture must not cost more than making it.
            if (!on) { tree.setCheckedMany(tree.checkedUnder(path), false); return; }

            const max = entries?.maxSelection ?? 60;

            // Everything inside, never a sample of it.
            //
            // This used to stop at whatever was left of the 60-entry selection
            // cap, which turned 'tick this book' into 'tick some of this book'
            // and left the rest looking deliberately excluded. Someone ticking a
            // container is saying they want its contents; if they wanted a
            // narrower set they would have ticked a narrower row. The cap still
            // exists and the tray still refuses to import over it -- but that is
            // a decision to hand back, not one to make quietly by ticking less
            // than was asked for.
            // One more row than an import can carry, and not one more than that.
            //
            // Asking for everything under a large node meant 42,332 rows and
            // 4.3 MB arriving after 43 seconds -- a window that is not responding
            // is a crash as far as anyone using it is concerned. Nothing is lost
            // by asking for less: past the cap the import is refused anyway, and
            // `matched` still reports the true size however few rows come back,
            // so the count below is exact.
            //
            // The TICKING is not limited by this. That is applied to the subtree
            // directly and costs no request at all -- which is the whole reason
            // the two are separate.
            const found = await api.portEntries(
              source.fileId, entries?.kind ?? null, null, max + 1, undefined, path);
            const total = found?.matched ?? 0;
            const take = (found?.results ?? []).filter((e) => !tree.checked.has(e.path));

            if (take.length === 0) {
              // Already ticked is not a failure: the box just catches up.
              if (tree.checkedUnder(path).length > 0) { tree.checkAll([path]); return; }
              toast(`There is nothing inside that this archive can import. ${node.name} holds `
                  + 'art or settings rather than standalone entries.', 'info');
              return;
            }

            // Ticked first, chosen second, and the order is not a style question.
            //
            // onCheck reconciles one way: anything in `picked` that is not ticked
            // is dropped. Filling `picked` before the ticks land meant the first
            // repaint saw entries that were not yet in the Set and threw every
            // one of them away -- boxes all filled in, nothing selected, an
            // import that could not see a thing.
            //
            // Two ticking statements, and both are needed. checkAll takes the row
            // and everything already loaded beneath it -- folders, images, the
            // property nodes in between -- because that is what 'this is
            // included' looks like. The second takes the entries themselves,
            // which sit deeper than anything loaded yet. What is unloaded in
            // between is caught by loadChildren as it arrives.
            tree.checkAll([path]);
            tree.setCheckedMany(take.map((e) => e.path), true);

            for (const entry of take) {
              picked.set(entry.path, {
                id: entry.id,
                label: entry.name || entry.relative || entry.path.split('/').pop(),
              });
            }
            renderTray();

            // Said with the real number, which is the one thing the tray cannot
            // show: it counts what is selected, and past the cap that is
            // deliberately fewer than what is inside.
            if (total > max) {
              toast(`Everything under ${node.name} is selected — ${fmt.format(total)} entries, which `
                  + `is more than one selection import carries (${fmt.format(max)}). Pick a `
                  + 'narrower row, or take the whole archive.', 'info');
            }
          } catch (error) {
            toastError(error, 'That could not be selected');
          }
        },
        onCheck: (checked) => {
          // Removal only.
          //
          // What goes IN is decided where the tick happens, because only there is
          // it known whether a row is a thing to import or one of the many rows
          // ticked to show that it is included. Adding every ticked path here is
          // what put folders, books and bare property nodes in the tray, counting
          // them against the cap and sending them to a port with no meaning for
          // them.
          for (const path of [...picked.keys()]) {
            if (!checked.has(path)) picked.delete(path);
          }
          renderTray();
        },
      });
      tree.setSortMode('name');
      for (const path of picked.keys()) tree.checked.add(path);
      tree.setRoots([{ id: source.fileId, name: source.name, dirty: false, readOnly: true }]);
      tree.expand(source.fileId);
    }

    resultHost.append(treeHost);
    // The virtualizer sizes itself off the scroller's clientHeight, which is
    // zero while the element is detached. Re-attaching does not fire its scroll
    // or resize listeners, so it is told directly.
    tree.scheduleRender();
  };

  /* ---------- what is ticked, and what happens to it ---------- */

  const renderTray = () => {
    // Picking is what makes the source gap answerable, so it is recomputed on
    // every pick rather than only when the kind changes.
    renderSources();
    clear(trayHost);
    if (!entries) return;

    const max = entries.maxSelection ?? 60;
    const over = picked.size > max;

    const names = [...picked.values()].slice(0, 6).map((p) => p.label);
    trayHost.append(el('div', { class: 'imp-tray-text' },
      picked.size
        ? el('span', {},
            el('b', { text: `${fmt.format(picked.size)} selected` }),
            el('span', { class: 'imp-hint', text: ` — ${names.join(', ')}`
              + (picked.size > names.length ? ` and ${fmt.format(picked.size - names.length)} more` : '') }))
        : el('span', { class: 'imp-hint', text:
            'Nothing selected yet. MapleBench checks names, effects, sounds, set data, and referenced art '
            + 'before the import can continue.' }),
      picked.size
        ? el('button', { class: 'btn btn-sm btn-ghost', onclick: () => clearPicked() },
            icon('close', { size: 13 }), 'Clear')
        : null));

    if (over) {
      trayHost.append(el('div', { class: 'imp-note', 'data-tone': 'bad' },
        icon('alert', { size: 14 }),
        el('span', { text: `${fmt.format(picked.size)} is more than one selection import carries `
                         + `(${fmt.format(max)}). An import holds a full copy of every node in memory `
                         + 'until you save. Select fewer entries, or take the whole archive.' })));
    }

    const canPort = entries.supported;

    trayHost.append(el('div', { class: 'imp-actions' },
      el('button', {
        class: 'btn btn-primary',
        disabled: !canPort || picked.size === 0 || over,
        'data-tip': canPort
          ? 'Review dependencies, then copy these into the target client.'
          : entries.reason,
        onclick: () => runImport('selection'),
      }, icon('arrowRight', { size: 15 }),
        picked.size
          ? `Import ${fmt.format(picked.size)} ${picked.size === 1 ? entries.label.toLowerCase() : entries.plural.toLowerCase()}`
          : 'Import'),

      // The whole archive, at the far end of the same mechanism. Its own button
      // rather than a mode switch, and it says its size on its face: measured on
      // a v232 client a whole Npc.wz is 10,742 entries and 21,084 written nodes
      // in 19.3 s, and Skill.wz is refused outright at 1,247 MB against a
      // 1,024 MB cap. Learning that from the button beats learning it from a
      // preview that ran for ten seconds first.
      canPort
        ? el('button', {
            class: 'btn',
            disabled: entries.total === 0,
            'data-tip': archiveTip(),
            onclick: () => runImport('archive'),
          }, icon('layers', { size: 15 }),
            `Import every ${entries.label.toLowerCase()} · ${fmt.format(entries.total)}`
            + (entries.totalBytes ? ` · ${bytes(entries.totalBytes)}` : ''))
        : null));

    if (canPort && entries.totalBytes > (entries.maxArchiveBytes ?? Infinity)) {
      trayHost.append(el('div', { class: 'imp-note', 'data-tone': 'bad' },
        icon('alert', { size: 14 }),
        el('span', { text: `Taking the whole archive would be ${bytes(entries.totalBytes)}, over the `
                         + `${bytes(entries.maxArchiveBytes)} cap, and will be refused. The cap is memory: `
                         + 'an import holds a full copy of every node it wrote until you save. Pick what '
                         + 'you need instead.' })));
    } else if (canPort && !entries.totalBytes && entries.total > 0) {
      // An archive assembled from a split client reports no size at all. Its
      // images were merged in memory rather than read out of a .wz, so they carry
      // no on-disk block size -- measured on this client's Mob, which indexes
      // 11,959 entries and totals zero bytes. That is not a small archive; it is
      // an unmeasured one, and the byte cap that would normally refuse a port
      // this big cannot fire on a number that is zero. Said here rather than
      // discovered when the process runs out of memory.
      trayHost.append(el('div', { class: 'imp-note', 'data-tone': 'bad' },
        icon('alert', { size: 14 }),
        el('span', { text: `${entries.archive} was assembled in memory from the split client, so its `
                         + 'entries carry no on-disk size and nothing can weigh this before it runs — '
                         + `including the ${bytes(entries.maxArchiveBytes)} cap that would otherwise `
                         + `refuse it. ${fmt.format(entries.total)} entries is a lot to hold at once. `
                         + 'Preview it, read the counts, and prefer picking what you need.' })));
    }
  };

  const archiveTip = () => {
    const bits = [
      `Every ${entries.label.toLowerCase()} in ${entries.archive}, with each one's name, sounds and `
      + 'shop row.',
      'You will get the preview first — at this size nothing is written until you have seen the counts.',
    ];
    // Said on the button, because it is the one thing about this scope that
    // differs from ticking things by hand, and finding it in the small print
    // afterwards is finding it too late.
    bits.push('At this scale nothing is parsed to chase art links: everything of this kind is coming '
            + 'anyway, but art an entry borrows from another ARCHIVE is not followed. Import a handful '
            + 'the ordinary way if you want that checked.');
    if (entries.totalBytes > (entries.maxArchiveBytes ?? Infinity)) {
      bits.push(`This one is ${bytes(entries.totalBytes)} against a ${bytes(entries.maxArchiveBytes)} `
              + 'cap and will be refused.');
    } else if (!entries.totalBytes) {
      bits.push('This archive was assembled in memory, so nothing can say what it weighs before it runs.');
    }
    return bits.join(' ');
  };

  const clearPicked = () => {
    picked.clear();
    tree?.clearChecked();
    if (mode === 'search') renderResults();
    renderTray();
  };

  /* ============================================================
     THE IMPORT ITSELF

     One line, and that is the point. Everything hard about bringing a thing
     across -- following info/link and info/revive and canvas outlinks, sweeping
     String.wz for the name in the right image AND the right category, Sound.wz
     for the clips, Etc.wz/Commodity.img for the shop row, checking what the
     target already has, doing the whole thing as one undo entry, and reporting
     what did not travel -- is PortService's, and it has had real defects beaten
     out of it. A second copy path here would start that from zero.
     ============================================================ */

  const runImport = async (scope) => {
    if (!entries?.supported) return;

    // "Everything it needs" cannot be established from the entry archive
    // alone. Effect, Sound and Etc rows are keyed from the outside, and an
    // unopened family looks exactly like "this entry has no row". Resolve that
    // uncertainty before the plan is allowed to run; after these are open the
    // server can distinguish a real absence from an omitted dependency.
    const missing = missingSourceNeeds();
    if (missing.length) {
      const found = missing.map((need) => ({ need, archives: archivesForNeed(need) }));
      const unavailable = found.filter(({ archives }) => !archives.length);
      if (unavailable.length) {
        toast(`A complete import cannot be checked because this client has no openable `
          + `${sentenceList(unavailable.map(({ need }) => need.family))} archive. Nothing was imported.`,
        'warning');
        return;
      }

      const archives = uniqueArchives(found.flatMap(({ archives: family }) => family));
      const total = archives.reduce((sum, archive) => sum + (archive.sourceBytes || 0), 0);
      const names = sentenceList(archives.map((archive) => archive.name));
      const proceed = await confirmDialog({
        title: 'Open the selection\'s dependency archives?',
        message: `${names} must be inspected before MapleBench can verify the selection's required `
          + `dependencies. They open read-only and nothing on disk is changed.`
          + (total ? ` Their source size is ${bytes(total)}.` : ''),
        confirmLabel: `Open ${fmt.format(archives.length)} and continue`,
        danger: false,
      });
      if (!proceed) return;

      const outcome = await openSatellites(archives);
      if (outcome.failed.length || missingSourceNeeds().length) {
        toast('The dependency set is still incomplete, so nothing was imported.', 'warning');
        return;
      }
    }

    const request = scope === 'archive'
      ? { app, kind: entries.kind, paths: [], scope: 'archive', sourceFileId: source.fileId }
      : { app, kind: entries.kind, paths: [...picked.keys()] };

    // The picker stays up behind it. Native <dialog> stacks in the top layer, so
    // the port dialog opens over this one and closing it returns here with the
    // ticks intact -- which is what makes importing a second batch one gesture
    // rather than five.
    await openPortDialog(request);
  };

  /* ---------------- shared bits ---------------- */

  const errorRow = (message) => el('div', { class: 'notice', 'data-tone': 'bad' },
    icon('alert', { size: 15 }), el('span', { text: message }));

  /* ---------------- go ---------------- */

  render();
  if (sourceInput.value.trim()) detect();
  else browse(null);

  sourceInput.addEventListener('keydown', (e) => { if (e.key === 'Enter') detect(); });
}
