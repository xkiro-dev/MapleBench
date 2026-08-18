/**
 * Merged archive families: the dialogs and the one banner.
 *
 * A "family" is Skill.wz + Skill001.wz + Skill002.wz + Skill003.wz — one logical
 * archive that a modern client ships as several files. The Explorer shows the
 * whole family as a single tree; this module is everything around that: finding
 * families, offering the merge, and — the part that matters most — showing which
 * image names exist in more than one of the files.
 *
 * The shadow report is not a diagnostic afterthought. On the v232 client this
 * was built against, seven image names are in two files at once and four of them
 * differ in size, 40000.img among them. Which copy anything sees is decided by
 * mount order alone, so an edit made to the visible copy can be an edit to the
 * one the game does not load — and nothing anywhere else in this app, or in any
 * other WZ tool, would say so.
 */

import { api } from './api.js';
import { el, clear, modal, toast, toastError, fmt, busyWhile } from './ui.js';
import { icon } from './icons.js';

const bytes = (value) => {
  if (!value) return '';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let size = value;
  let unit = 0;
  while (size >= 1024 && unit < units.length - 1) { size /= 1024; unit += 1; }
  return `${size.toFixed(size >= 10 || unit === 0 ? 0 : 1)} ${units[unit]}`;
};

/* ============================================================
   THE OFFER

   Opening a MapleStory folder opens every archive in it, so by the time the
   user sees the tree the four Skill files are already parsed. Asking them to
   pick the folder again to merge those same four would be asking them to
   re-read 4.7 GB, so the offer is made against what is already open and costs
   one request that touches no disk.
   ============================================================ */

/**
 * Families the user has waved away.
 *
 * Two tiers, because "not now" and "not ever" are different answers. The
 * session set covers the ones the app itself must stop offering — unmerging
 * leaves the member archives open, which is exactly the state the offer looks
 * for, so without it "show the four archives separately" was answered
 * immediately by "these four archives are one, merge them?". The stored set is
 * the user's own dismissal, and it outlives the page: an offer that comes back
 * every reload was never dismissible, it was postponable.
 */
const dismissed = new Set();

const DISMISS_KEY = 'mb.family.dismissed';

const keyOf = (folder, name) => `${folder}|${name}`.toLowerCase();

function storedDismissals() {
  try {
    const raw = JSON.parse(localStorage.getItem(DISMISS_KEY) || '[]');
    return new Set(Array.isArray(raw) ? raw : []);
  } catch { return new Set(); }
}

function storeDismissals(keys) {
  try { localStorage.setItem(DISMISS_KEY, JSON.stringify([...keys])); }
  catch { /* a private-mode browser still gets the session-long dismissal */ }
}

/** "Not ever", for the listed families. Per family, so a client folder opened
 *  tomorrow is still offered. */
function dismissForever(candidates) {
  const keys = storedDismissals();
  for (const candidate of candidates) keys.add(keyOf(candidate.folder, candidate.name));
  storeDismissals(keys);
}

/** The way back: everything the user has ever dismissed is offered again. */
function undismissAll() {
  storeDismissals(new Set());
}

/**
 * Stops the hint re-offering a family the user has just taken apart.
 *
 * Session-only on purpose. Taking a family apart to look at one member is not
 * the same as never wanting the offer again, and it would be a poor trade to
 * make a temporary look permanent behind the user's back.
 */
export function stopOffering(folder, name) {
  dismissed.add(keyOf(folder, name));
}

/** Families that could be merged out of what is already open, minus the ones
 *  the user has waved away. */
async function adoptable() {
  try { return await api.familyAdoptable(); }
  catch { return []; }   // an offer that cannot be made is not an error worth a toast
}

function undismissed(candidates) {
  const stored = storedDismissals();
  return candidates.filter((c) => {
    const key = keyOf(c.folder, c.name);
    return !dismissed.has(key) && !stored.has(key);
  });
}

/** Merges one family and takes the user to it. Shared by the hint and the sheet. */
async function adopt(app, candidate, button) {
  if (button) button.disabled = true;
  try {
    const family = await api.familyOpen({
      folder: candidate.folder, name: candidate.name, adopt: true,
    });
    await app.refreshFiles();
    await app.tree.expand(family.id);
    app.navigate(family.id);
    toast(`${family.name} is now one tree. ${family.members.length} archives merged.`, 'success');
    checkShadows(app, family.id);
    return true;
  } catch (error) {
    if (button) button.disabled = false;
    toastError(error, `Could not merge ${candidate.name}`);
    return false;
  }
}

const MERGE_TIP = 'Show them as one tree. Nothing is copied, nothing is written, and each item '
                + 'still says which file it is in.';

/**
 * The hint above the tree: ONE line, whatever the folder holds.
 *
 * It used to be a tinted panel with a stacked row per family — a name, every
 * filename in the family, a button and a dismiss, wrapped over three lines in a
 * 286px pane. Three families made it 235px tall, which in a 700px side panel is
 * a third of the pane and more height than the seven archive rows it was
 * talking about. The offer is worth making; it is not worth a third of the
 * screen, and it is certainly not worth three of them.
 *
 * So: one row, ~26px, that says how many and opens the detail on click. One
 * family goes straight to the merge, because for one family the detail is the
 * sentence already on the row.
 */
export async function offerMerge(app) {
  const host = document.querySelector('#family-offer');
  if (!host) return;

  const fresh = undismissed(await adoptable());
  clear(host);
  host.hidden = fresh.length === 0;
  if (!fresh.length) return;

  const one = fresh.length === 1 ? fresh[0] : null;
  // Short enough to survive a 286px pane without an ellipsis eating the verb:
  // the line has to still be an offer at the width it is usually read at.
  const label = one
    ? `${one.name} is ${one.files.length} files — browse as one tree`
    : `${fresh.length} split archives — browse as one tree`;

  host.append(el('div', { class: 'family-hint' },
    el('button', {
      class: 'family-hint-open',
      'data-tip': one ? `${one.files.join(', ')}. ${MERGE_TIP}` : `${label}. ${MERGE_TIP}`,
      onclick: (event) => {
        if (one) adopt(app, one, event.currentTarget);
        else openSplitSheet(app);
      },
    }, icon('layers', { size: 13 }), el('span', { class: 'family-hint-text', text: label })),
    el('button', {
      class: 'family-hint-x', 'aria-label': 'Dismiss',
      'data-tip': 'Stop offering this. Split archives stay reachable from the ••• menu.',
      onclick: () => { dismissForever(fresh); offerMerge(app); },
    }, icon('close', { size: 12 }))));
}

/* ============================================================
   THE SHEET

   Where the detail went. Opened from the hint when more than one family is
   waiting, and from the ••• menu at any time — including by someone who
   dismissed the hint and changed their mind, which is why it lists dismissed
   families too and offers to start reminding again.
   ============================================================ */

export async function openSplitSheet(app, candidates) {
  const all = candidates ?? await adoptable();
  if (!all.length) { openFamilyPicker(app); return; }

  const live = undismissed(all);
  const body = el('div');
  const rows = el('div', { class: 'family-results' });
  let sheet = null;

  const draw = () => {
    clear(rows);
    for (const candidate of all) {
      const stored = storedDismissals().has(keyOf(candidate.folder, candidate.name));
      rows.append(el('div', { class: 'family-row' },
        el('div', { style: 'flex:1;min-width:0' },
          el('div', { class: 'family-row-name' },
            el('strong', { text: candidate.name }),
            stored ? el('span', { class: 'shadow-tag shadow-tag-mild', text: 'dismissed' }) : null),
          el('div', { class: 'family-row-files', text: candidate.files.join('  ·  ') })),
        el('button', {
          class: 'btn btn-sm btn-primary', 'data-tip': MERGE_TIP,
          onclick: async (event) => {
            if (await adopt(app, candidate, event.currentTarget)) {
              // Merged families are no longer adoptable, so a sheet left open
              // would keep offering something that has already happened.
              sheet?.close();
            }
          },
        }, 'Browse as one tree')));
    }
  };
  draw();

  body.append(
    el('p', { class: 'family-lead',
      text: 'These archives sit in the same folder and are one archive each, split across files. '
          + 'Browsing a family as one tree copies nothing, writes nothing, and every item still '
          + 'says which file it is in.' }),
    rows);

  sheet = modal({
    title: 'Split archives',
    subtitle: `${all.length} famil${all.length === 1 ? 'y' : 'ies'} in what is already open`,
    width: '560px',
    body,
    actions: [
      live.length
        ? { label: 'Don’t offer again', class: 'btn-ghost',
            run: () => { dismissForever(all); offerMerge(app); } }
        : { label: 'Offer these again', class: 'btn-ghost',
            run: () => { undismissAll(); offerMerge(app); } },
      { label: 'Look in another folder…', class: 'btn-ghost',
        run: () => { setTimeout(() => openFamilyPicker(app), 0); } },
      { label: 'Close' },
    ],
  });
}

/**
 * Scans a freshly merged family for shadowed images and says so if there are
 * any.
 *
 * Fired automatically, and that is deliberate: a duplicate that has to be
 * looked for is a duplicate nobody looks for. The scan reads directory tables
 * that are already in memory — 466 images across the four Skill archives in
 * under a second — so there is no cost to justify making it opt-in.
 */
export async function checkShadows(app, familyId) {
  let report;
  try { report = await api.familyShadows(familyId, 200); }
  catch { return; }
  if (!report.total) return;

  toast(report.summary, 'warning', {
    title: `${report.total} duplicate image name${report.total === 1 ? '' : 's'} in ${report.name}`,
    action: { label: 'Show them', run: () => showShadows(app, familyId, report) },
  });
}

/* ============================================================
   THE REPORT
   ============================================================ */

export async function showShadows(app, familyId, preloaded) {
  let report = preloaded;
  if (!report) {
    try { report = await api.familyShadows(familyId, 200); }
    catch (error) { toastError(error, 'Could not check for duplicates'); return; }
  }

  // Declared before the buttons that use it: a <dialog> opened with showModal()
  // covers the tree, so a click that navigates to a hidden copy has to take the
  // report off the screen, or the user ends up reading a report about where they
  // no longer are.
  let sheet = null;

  const body = el('div');
  body.append(el('p', { class: 'family-lead', text: report.summary }));

  if (!report.total) {
    body.append(el('p', { class: 'field-hint',
      text: 'Every image in this family has exactly one home, so the merged tree is showing all of it.' }));
  } else {
    body.append(el('p', { class: 'field-hint' },
      el('span', { text: 'Only the copy marked ' }),
      el('span', { class: 'shadow-wins', text: 'shown' }),
      el('span', {
        text: ' appears in the merged tree; the others are hidden behind it. Which one wins is decided '
            + 'by mount order and nothing else — the archives do not record which the game loads — so '
            + 'use "Let the last file win" on the family to see the other side, and open the copy you '
            + 'actually mean to edit before you edit it.',
      })));

    const table = el('div', { class: 'shadow-table' });
    for (const entry of report.entries) {
      const row = el('div', { class: 'shadow-entry', 'data-differs': entry.differs ? 'true' : 'false' });
      row.append(el('div', { class: 'shadow-name' },
        icon(entry.differs ? 'alert' : 'copy', { size: 14 }),
        el('span', { text: entry.path }),
        entry.differs
          ? el('span', { class: 'shadow-tag', text: 'different sizes' })
          : el('span', { class: 'shadow-tag shadow-tag-mild', text: 'same size' })));

      const copies = el('div', { class: 'shadow-copies' });
      for (const copy of entry.copies) {
        copies.append(el('button', {
          class: 'shadow-copy',
          'data-wins': copy.wins ? 'true' : 'false',
          'data-tip': `Open ${copy.nodePath}`,
          onclick: () => { sheet?.close(); app.navigate(copy.nodePath); },
        },
          el('span', { class: 'shadow-file', text: copy.file }),
          el('span', { class: 'shadow-bytes', text: bytes(copy.bytes) }),
          copy.wins ? el('span', { class: 'shadow-wins', text: 'shown' }) : null));
      }
      row.append(copies);
      table.append(row);
    }
    body.append(table);

    if (report.truncated) {
      body.append(el('p', { class: 'field-hint',
        text: `Showing the first ${report.entries.length} of ${report.total}.` }));
    }
  }

  body.append(el('p', { class: 'field-hint',
    text: `${fmt.format(report.imagesScanned)} images checked across the family.` }));

  sheet = modal({
    title: `Duplicate names in ${report.name}`,
    subtitle: report.total
      ? 'Two files, one name. Only one of them is visible.'
      : 'Nothing is hidden behind anything else.',
    width: '640px',
    body,
    actions: [{ label: 'Close' }],
  });
}

/* ============================================================
   THE PICKER

   For merging a family that is not open yet -- a client folder the user has
   not opened, or one archive family out of a folder they do not want opened
   whole.
   ============================================================ */

export function openFamilyPicker(app, startPath) {
  let sheet = null;
  const start = startPath || localStorage.getItem('mb.lastDir') || '';
  const input = el('input', {
    value: start, placeholder: 'C:\\MapleStory\\232',
    style: 'flex:1;height:34px;padding:0 10px;border:1px solid var(--border);'
         + 'border-radius:var(--r-sm);background:var(--bg-field)',
  });
  const results = el('div', { class: 'family-results' });

  const scan = async () => {
    clear(results);
    const folder = input.value.trim();
    if (!folder) return;
    results.append(el('p', { class: 'field-hint', text: 'Looking…' }));
    let found;
    try {
      found = await api.familyDetect(folder);
    } catch (error) {
      clear(results);
      results.append(el('p', { class: 'field-hint', text: error.message }));
      return;
    }

    clear(results);
    if (!found.length) {
      results.append(el('p', { class: 'field-hint',
        text: 'No split archive families here. A family is a base archive with at least one numbered '
            + 'part beside it (Skill.wz with Skill001.wz), or a modern Data\\ folder whose archives '
            + 'are directories.' }));
      return;
    }

    for (const candidate of found) {
      const open = el('button', {
        class: 'btn btn-sm btn-primary',
        disabled: candidate.kind !== 'classic' || Boolean(candidate.openAs),
        onclick: async (event) => {
          const button = event.currentTarget;
          button.disabled = true;
          try {
            const family = await api.familyOpen({ folder: candidate.folder, name: candidate.name });
            localStorage.setItem('mb.lastDir', candidate.folder);
            sheet?.close();
            await app.refreshFiles();
            await app.tree.expand(family.id);
            app.navigate(family.id);
            toast(`${family.name} merged from ${family.members.length} archives.`, 'success');
            checkShadows(app, family.id);
          } catch (error) {
            button.disabled = false;
            toastError(error, `Could not merge ${candidate.name}`);
          }
        },
      }, candidate.openAs ? 'Already merged' : 'Browse as one tree');

      results.append(el('div', { class: 'family-row' },
        el('div', { style: 'flex:1;min-width:0' },
          el('div', { class: 'family-row-name' },
            el('strong', { text: candidate.name }),
            candidate.kind === 'split-data'
              ? el('span', { class: 'shadow-tag shadow-tag-mild', text: 'split Data' })
              : null,
            candidate.bytes ? el('span', { class: 'family-row-size', text: bytes(candidate.bytes) }) : null),
          el('div', { class: 'family-row-files', text: candidate.files.join('  ·  ') }),
          candidate.kind === 'split-data'
            ? el('div', { class: 'field-hint',
                text: 'This shape is merged by "Import from another client", which opens it read-only '
                    + 'as a single archive.' })
            : null),
        open));
    }
  };

  sheet = modal({
    title: 'Browse a split archive as one tree',
    subtitle: 'Skill.wz, Skill001.wz, Skill002.wz and Skill003.wz are one archive in four files.',
    width: '620px',
    body: el('div', {},
      el('div', { style: 'display:flex;gap:8px;margin-bottom:12px' },
        input,
        el('button', { class: 'btn', onclick: () => busyWhile(results, scan()) }, 'Look')),
      results),
    actions: [{ label: 'Close' }],
    onOpen: () => { if (start) scan(); },
  });
}

/* ============================================================
   THE FAMILY'S OWN MENU
   ============================================================ */

/** Context-menu items for a merged family's root row. */
export function familyMenuItems(app, family) {
  const winner = family.lastWins
    ? family.members[family.members.length - 1]?.name
    : family.members[0]?.name;

  return [
    { icon: 'chevronDown', label: app.tree.expanded.has(family.id) ? 'Collapse' : 'Expand',
      run: () => app.tree.toggle(family.id) },
    { icon: 'alert',
      label: family.shadowedImages > 0
        ? `Show ${family.shadowedImages} duplicate name${family.shadowedImages === 1 ? '' : 's'}…`
        : 'Check for duplicate names…',
      run: () => showShadows(app, family.id) },
    { icon: 'refresh',
      label: family.lastWins
        ? `Let the base file win duplicates (now ${winner})`
        : `Let the last file win duplicates (now ${winner})`,
      run: async () => {
        try {
          const updated = await api.familyPrecedence(family.id, !family.lastWins);
          // Wholesale, not just the family root: flipping precedence changes
          // which FILE each duplicated image is read from, so the cached rows
          // under f1/... are now describing a copy that no longer wins.
          app.tree.invalidateAll();
          await app.refreshFiles();
          await app.tree.expand(family.id);
          toast(updated.summary, 'success');
        } catch (error) {
          toastError(error, 'Could not change which file wins');
        }
      } },
    { icon: 'save', label: `Save all ${family.members.length} archives`,
      run: () => {
        const dirty = family.members
          .map((m) => app.files.find((f) => f.id === m.fileId))
          .filter((f) => f?.dirty);
        if (!dirty.length) { toast('Nothing in this family has unsaved changes.', 'info'); return; }
        app.runSave(dirty, true);
      } },
    { icon: 'list', label: `Show the ${family.members.length} archives separately`,
      run: async () => {
        try {
          await api.familyUnmerge(family.id);
          stopOffering(family.folder, family.name);
          app.tree.invalidateAll();
          await app.refreshFiles();
          toast(`${family.name} is back to ${family.members.length} separate archives. `
              + 'Nothing was closed.', 'success');
        } catch (error) {
          toastError(error, 'Could not unmerge');
        }
      } },
    { icon: 'copy', label: 'Copy folder path', run: () => app.copyPath(family.folder) },
    { icon: 'close', label: `Close all ${family.members.length} archives`, danger: true,
      // One at a time through the ordinary close, so a member with unsaved work
      // still gets its own "discard?" prompt. A merged root must not become a
      // way to close four archives with one confirmation -- or with none.
      run: async () => {
        stopOffering(family.folder, family.name);
        await api.familyUnmerge(family.id).catch(() => {});
        for (const member of family.members) {
          await app.closeFile(member.fileId);
        }
      } },
  ];
}
