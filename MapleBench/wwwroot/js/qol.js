/**
 * Quality-of-life layer: the things that turn a working editor into one you can
 * spend a day in.
 *
 * Changes drawer, pinned nodes, find & replace, multi-select bulk edit,
 * resizable + remembered panels, and session restore.
 */

import { api } from './api.js';
import { el, $, clear, toast, toastError, fmt, modal, confirmDialog, humanContext } from './ui.js';
import { emptyState } from './inspector.js';
import { icon } from './icons.js';

const STORE = {
  pins: 'mb.pins',
  layout: 'mb.layout',
  session: 'mb.session',
};

/* ============================================================
   PINNED NODES
   ============================================================ */

export class Pins {
  constructor(app) {
    this.app = app;
    this.items = this.load();
  }

  /**
   * Pins are stored against the archive's absolute path, never the session id.
   *
   * "f1" is assigned in the order files happen to be opened, so a pin saved as
   * "f1/Achievement/1378.img" pointed at whatever archive was opened first next
   * time — usually a different one, occasionally a node that exists in both and
   * holds something else entirely.
   */
  load() {
    let stored;
    try { stored = JSON.parse(localStorage.getItem(STORE.pins) || '[]'); }
    catch { return []; }
    if (!Array.isArray(stored)) return [];

    let migrated = false;
    const items = stored.map((pin) => {
      if (pin.subPath !== undefined) return pin;
      // Legacy shape: {path: "f1/a/b", filePath, label, file}. Drop the session
      // id and keep the rest of the path, which is the part that is stable.
      migrated = true;
      const path = String(pin.path ?? '');
      const cut = path.indexOf('/');
      return {
        filePath: pin.filePath ?? null,
        subPath: cut < 0 ? '' : path.slice(cut + 1),
        label: pin.label ?? '',
        file: pin.file ?? '',
      };
    }).filter((pin) => pin.filePath || pin.subPath);

    this.items = items;
    if (migrated) this.save();
    return items;
  }

  save() { localStorage.setItem(STORE.pins, JSON.stringify(this.items.slice(0, 200))); }

  /** The live session path for a pin, or null when its archive is not open. */
  resolve(pin) {
    const file = this.app.files.find((f) => f.filePath === pin.filePath)
      // A pin saved before the file path was recorded can still be matched by
      // name; it is a guess, but a better one than a positional id.
      ?? (pin.filePath ? null : this.app.files.find((f) => f.name === pin.file));
    if (!file) return null;
    return pin.subPath ? `${file.id}/${pin.subPath}` : file.id;
  }

  /** Splits a live session path into the parts worth persisting. */
  describe(path) {
    const cut = path.indexOf('/');
    const fileId = cut < 0 ? path : path.slice(0, cut);
    const file = this.app.files.find((f) => f.id === fileId);
    return {
      filePath: file?.filePath ?? null,
      subPath: cut < 0 ? '' : path.slice(cut + 1),
      file: file?.name ?? fileId,
    };
  }

  find(path) {
    const { filePath, subPath } = this.describe(path);
    return this.items.find((p) => p.subPath === subPath
      && (p.filePath === filePath || (!p.filePath && !filePath)));
  }

  has(path) { return Boolean(this.find(path)); }

  toggle(path, label) {
    const existing = this.find(path);
    if (existing) {
      this.items = this.items.filter((p) => p !== existing);
      toast('Removed from pinned.', 'info');
    } else {
      const { filePath, subPath, file } = this.describe(path);
      this.items.unshift({
        filePath,
        subPath,
        label: label || decodeURIComponent(path.split('/').pop()),
        file,
      });
      toast('Pinned.', 'success');
    }
    this.save();
    return this.has(path);
  }

  render(host) {
    clear(host);
    if (!this.items.length) {
      host.append(emptyState('star', 'Nothing pinned',
        'Press Ctrl D on a node, or use its right-click menu, to keep it one click away.'));
      return;
    }

    for (const pin of this.items) {
      const target = this.resolve(pin);
      const subtitle = target
        ? pin.file
        : `${pin.file} · not open`;

      host.append(el('div', {
        style: 'display:flex;align-items:center;gap:6px;padding:6px 8px;border-radius:var(--r-sm)',
        onmouseenter: (e) => { e.currentTarget.style.background = 'var(--bg-hover)'; },
        onmouseleave: (e) => { e.currentTarget.style.background = 'transparent'; },
      },
        el('button', {
          style: 'flex:1;text-align:left;border:0;background:transparent;font-size:var(--fs-13);overflow:hidden;' +
                 `color:var(--text-${target ? '1' : '3'})`,
          // A pin whose archive is closed says so rather than failing on click.
          title: target ? decodeURIComponent(target) : `Open ${pin.filePath ?? pin.file} to use this pin.`,
          onclick: () => {
            const live = this.resolve(pin);
            if (live) this.app.navigate(live);
            else toast(`${pin.file} is not open.`, 'warning');
          },
        },
          el('div', { style: 'font-weight:600;overflow:hidden;text-overflow:ellipsis;white-space:nowrap', text: pin.label }),
          el('div', { style: 'font-size:var(--fs-10);color:var(--text-3)', text: subtitle })),
        el('button', {
          class: 'btn btn-sm btn-icon btn-ghost', 'data-tip': 'Unpin',
          onclick: () => {
            this.items = this.items.filter((p) => p !== pin);
            this.save();
            toast('Removed from pinned.', 'info');
            this.render(host);
          },
        }, icon('close', { size: 13 }))));
    }
  }
}

/* ============================================================
   CHANGES DRAWER
   ============================================================ */

export async function renderChanges(host, app) {
  clear(host);
  host.append(el('div', { class: 'skeleton skeleton-row', style: 'width:70%' }));

  let data;
  let history = null;
  try {
    // Asked for together because the two answer different halves of the same
    // question and either alone can be read as "nothing happened" -- see the
    // structural-change note below.
    [data, history] = await Promise.all([api.changes(), api.history().catch(() => null)]);
  } catch (error) {
    clear(host);
    host.append(emptyState('alert', 'Could not read changes', error.message));
    return;
  }

  clear(host);
  if (!data.files.length) {
    host.append(emptyState('check', 'No unsaved changes',
      'Everything on disk matches what you see here.'));
    return;
  }

  // What is pending, in words, above the per-archive rows.
  //
  // This panel used to be able to say only "N images", and an edit that changes
  // an archive's *structure* -- deleting a node, adding one, renaming, moving --
  // dirties no image at all. So deleting a directory produced a drawer headed
  // "Etc.wz  0 images" with a live Save button beside it and nothing listed
  // underneath: the one screen whose entire job is to say what will be written
  // reported a zero, which reads as "nothing will be", next to a button that
  // would have written the deletion. Undo depth is the count that is never zero
  // when something is pending, so it leads.
  if (history?.undoDepth) {
    host.append(el('div', {
      style: 'display:flex;align-items:baseline;gap:6px;padding:8px 8px 2px;font-size:var(--fs-11);color:var(--text-2)',
    },
      el('b', { text: `${fmt.format(history.undoDepth)} change${history.undoDepth === 1 ? '' : 's'} not yet written` }),
      history.nextUndo
        ? el('span', { style: 'color:var(--text-3)', text: `· last was “${history.nextUndo}”` })
        : null));
  }

  for (const entry of data.files) {
    // The server counts both halves of "unsaved" and says so in changeCount:
    // changed images plus the structural edits no image flag reports. This
    // panel used to count the rows it had -- entry.images -- which is the
    // capped list of one half, so a deleted directory was headed "0 images"
    // beside a live Save button that would have written the deletion.
    const listed = entry.images.length + (entry.nodes?.length ?? 0);
    const count = entry.changeCount ?? listed;
    host.append(el('div', {
      style: 'display:flex;align-items:center;gap:6px;padding:8px;font-weight:700;font-size:var(--fs-12)',
    },
      el('span', { text: entry.file.name }),
      el('span', {
        style: 'color:var(--text-3);font-weight:500',
        title: entry.structuralEdits
          ? `${entry.structuralEdits} of these added, removed, renamed or moved nodes. Those live in `
            + 'the archive’s structure rather than inside an image, and the whole archive is '
            + 'rewritten on save either way.'
          : null,
        // Never zero while the archive is dirty: unlistedChanges covers the
        // case where the work is real and cannot be enumerated, and it gets a
        // line of its own below rather than a count that reads as "nothing".
        text: entry.unlistedChanges
          ? 'unsaved work'
          : `${fmt.format(count)} change${count === 1 ? '' : 's'}`,
      }),
      el('button', {
        class: 'btn btn-sm btn-primary', style: 'margin-left:auto', text: 'Save',
        // Dead for the length of the write: the row stays on screen throughout,
        // and app.runSave drops a second call rather than queueing it, so a
        // live button would just be one that does nothing when clicked.
        onclick: async (event) => {
          const button = event.currentTarget;
          button.disabled = true;
          button.textContent = 'Saving…';
          try { await app.runSave([entry.file], true); }
          finally { renderChanges(host, app); }
        },
      })));

    const row = (title, target, label, glyph) => el('button', {
      style: 'display:flex;align-items:center;gap:7px;width:100%;text-align:left;' +
             'padding:5px 8px 5px 18px;font-size:var(--fs-12);color:var(--text-2);border-radius:var(--r-sm)',
      title,
      // A structural edit with no target is still worth listing -- what it was
      // is the point -- but it must not pretend to be a link to nowhere.
      onclick: target ? () => app.navigate(target) : null,
      onmouseenter: (e) => { e.currentTarget.style.background = 'var(--bg-hover)'; },
      onmouseleave: (e) => { e.currentTarget.style.background = 'transparent'; },
    }, icon(glyph, { size: 9 }), el('span', { text: label }));

    for (const image of entry.images) {
      host.append(row(image.fullPath, image.path, image.name, 'dot'));
    }

    // The other half of unsaved, and the half that was never on screen: edits
    // that changed the archive's shape. Each is one undo entry, labelled as the
    // history labels it, and it navigates to where the edit landed -- after a
    // delete that is the container, which is the only thing left to look at.
    for (const node of entry.nodes ?? []) {
      host.append(row(
        node.paths?.length ? decodeURIComponent(node.paths[0]) : 'Nothing left to navigate to',
        node.paths?.[0] || null, node.label, 'edit'));
    }

    if (entry.unlistedChanges) {
      host.append(el('div', {
        style: 'padding:2px 8px 6px 18px;font-size:var(--fs-11);color:var(--text-3)',
        text: 'This archive has unsaved work that cannot be listed — an edit older than the undo '
            + 'history, or one recorded without an undo entry. Saving writes it; closing loses it.',
      }));
    }

    // Both lists are capped server-side; the count above them is not.
    if (count > listed) {
      host.append(el('div', {
        style: 'padding:2px 8px 6px 18px;font-size:var(--fs-11);color:var(--text-3)',
        text: `Showing the first ${fmt.format(listed)} of ${fmt.format(count)}.`,
      }));
    }
  }
}

/* ============================================================
   FIND & REPLACE
   ============================================================ */

export function openFindReplace(app, scopePath) {
  const style = 'width:100%;height:34px;padding:0 10px;border:1px solid var(--border);' +
                'border-radius:var(--r-sm);background:var(--bg-field);color:var(--text-1)';

  const find = el('input', { placeholder: 'Find', style });
  const replace = el('input', { placeholder: 'Replace with', style });
  const scope = el('input', {
    value: scopePath ? decodeURIComponent(scopePath) : '',
    placeholder: 'All open files',
    style: style + ';font-family:var(--font-mono);font-size:var(--fs-12)',
    readonly: true,
  });
  const inValues = el('input', { type: 'checkbox', checked: true });
  const inNames = el('input', { type: 'checkbox' });
  const useRegex = el('input', { type: 'checkbox' });
  const caseSensitive = el('input', { type: 'checkbox' });
  const results = el('div', { style: 'margin-top:14px;max-height:38vh;overflow:auto' });

  let lastPreview = null;
  let previewButton = null;
  let applyButton = null;

  // One ceiling for both the preview and the apply. They used to differ (2 000
  // vs 20 000), so the confirmation asked you to approve one number and the
  // toast afterwards reported a larger one.
  const LIMIT = 5000;

  // Any change to the terms invalidates the preview. Without this you could
  // preview one replace, retype the search, and then be asked to confirm the
  // old count for a completely different operation.
  const invalidate = () => { lastPreview = null; syncApplyButton(); };
  for (const control of [find, replace, inValues, inNames, useRegex, caseSensitive]) {
    control.addEventListener('input', invalidate);
    control.addEventListener('change', invalidate);
  }

  const body = () => ({
    path: scopePath || null,
    find: find.value,
    replace: replace.value,
    inValues: inValues.checked,
    inNames: inNames.checked,
    regex: useRegex.checked,
    caseSensitive: caseSensitive.checked,
  });

  // Both actions are async and both used to be clickable throughout, so a
  // double-click sent a second replace while the first was still running.
  let busy = false;
  const setBusy = (state, label) => {
    busy = state;
    if (previewButton) {
      previewButton.disabled = state;
      previewButton.textContent = state && label === 'preview' ? 'Previewing…' : 'Preview';
    }
    syncApplyButton();
  };

  // "Replace all" stays disabled until there is something to apply, rather
  // than accepting the click and answering with "preview first".
  const syncApplyButton = () => {
    if (!applyButton) return;
    const ready = !busy && Boolean(lastPreview?.changed.length);
    applyButton.disabled = !ready;
    applyButton.title = ready
      ? ''
      : (busy ? 'Working…' : 'Preview first, so you can see what will change.');
  };

  const preview = async () => {
    if (busy) return;
    if (!find.value) { toast('Enter something to find.', 'warning'); return; }
    setBusy(true, 'preview');
    clear(results);
    results.append(el('div', { class: 'skeleton skeleton-row', style: 'width:60%' }));
    try {
      const data = await api.replace({ ...body(), dryRun: true, limit: LIMIT });
      lastPreview = data;
      clear(results);

      if (!data.changed.length) {
        results.append(el('div', {
          style: 'padding:20px;text-align:center;color:var(--text-3);font-size:var(--fs-13)',
          text: `Nothing matches "${find.value}".`,
        }));
        return;
      }

      results.append(el('div', {
        style: 'font-size:var(--fs-12);color:var(--text-2);margin-bottom:8px;font-weight:600',
        text: `${fmt.format(data.changed.length)} change${data.changed.length === 1 ? '' : 's'} would be made`,
      }));

      // The ceiling, said before the write rather than discovered after it.
      //
      // "(list truncated)" was doing two jobs badly. The limit is not a display
      // cap -- the apply passes the same LIMIT -- so a truncated preview means
      // the replace itself stops at 5,000 and leaves every later match alone,
      // and the toast afterwards says "Replaced 5000 values" with no hint that
      // it is a partial job. A replace you believe finished and did not is the
      // worst outcome this dialog has, so it is stated here, in the preview,
      // where the decision is actually made.
      if (data.truncated) {
        results.append(el('div', {
          style: 'margin-bottom:10px;padding:8px 10px;border-radius:var(--r-sm);' +
                 'border:1px solid var(--warn-border,var(--border));background:var(--warn-bg,var(--bg-subtle));' +
                 'font-size:var(--fs-11);line-height:1.5;color:var(--text-2)',
        },
          el('b', { text: `There are more than ${fmt.format(LIMIT)} matches.` }),
          ' Replacing now changes the first ',
          el('b', { text: fmt.format(data.changed.length) }),
          ' and leaves the rest exactly as they are. Narrow the scope to one archive or one branch and run it again, ' +
          'or run this repeatedly until the count comes back under the ceiling.'));
      }

      for (const hit of data.changed.slice(0, 300)) {
        results.append(el('div', {
          style: 'padding:5px 8px;border-bottom:1px solid var(--border-subtle);font-size:var(--fs-12);cursor:pointer',
          onclick: () => { dialog.close(); app.navigate(hit.path); },
        },
          el('div', { style: 'font-weight:600', text: hit.name }),
          el('div', { style: 'color:var(--text-3);font-family:var(--font-mono);font-size:var(--fs-11)', text: hit.value }),
          el('div', { style: 'color:var(--text-3);font-size:var(--fs-10)', text: humanContext(hit.context) })));
      }
      if (data.changed.length > 300) {
        results.append(el('div', {
          style: 'padding:8px;font-size:var(--fs-11);color:var(--text-3)',
          // A list that silently stops at 300 of 4,000 invites the reading that
          // 300 is the whole job.
          text: `Showing the first 300 of ${fmt.format(data.changed.length)}. All ${fmt.format(data.changed.length)} are replaced.`,
        }));
      }
    } catch (error) {
      clear(results);
      results.append(el('div', { style: 'padding:16px;color:var(--red-ink)', text: error.message }));
    } finally {
      setBusy(false);
    }
  };

  const apply = async () => {
    if (busy || !lastPreview?.changed.length) return;

    const ok = await confirmDialog({
      title: `Apply ${fmt.format(lastPreview.changed.length)} change${lastPreview.changed.length === 1 ? '' : 's'}?`,
      message: (lastPreview.truncated
        ? `This is the first ${fmt.format(lastPreview.changed.length)} of more matches than the ceiling allows. ` +
          'Everything past it stays as it is, and this dialog cannot tell you how many that is. '
        : '') +
        'Nothing is written to disk until you save. The whole replace is recorded as a single undo step.',
      confirmLabel: 'Replace all',
      danger: Boolean(lastPreview.truncated),
    });
    if (!ok) return;

    setBusy(true, 'apply');
    try {
      const data = await api.replace({ ...body(), dryRun: false, limit: LIMIT });
      dialog.close();
      // Reported as what happened, including the part that did not: a bare
      // "Replaced 5000 values." after a capped run is a success message for a
      // job that stopped half-way.
      if (data.truncated) {
        toast(`Replaced ${fmt.format(data.changed.length)} value${data.changed.length === 1 ? '' : 's'} — the ceiling. ` +
              'There are more matches left untouched; run it again to continue.',
        'warning', { title: 'Partly replaced' });
      } else {
        toast(`Replaced ${fmt.format(data.changed.length)} value${data.changed.length === 1 ? '' : 's'}.`, 'success');
      }
      if (scopePath) {
        app.afterStructureChange(scopePath);
      } else {
        // Session-wide replace has no single path to invalidate, and without
        // this the tree kept rendering the pre-replace values.
        app.tree.invalidateAll();
        app.markDirty();
        if (app.mode === 'explorer') app.inspector.refresh();
        else app.shop.load();
      }
    } catch (error) {
      toastError(error, 'Replace failed');
    } finally {
      setBusy(false);
    }
  };

  find.addEventListener('keydown', (e) => { if (e.key === 'Enter') preview(); });

  const { dialog } = modal({
    title: 'Find and replace',
    width: '640px',
    body: el('div', {},
      el('label', { style: 'font-size:var(--fs-11);font-weight:600;color:var(--text-2)', text: 'Scope' }),
      scope,
      el('div', { style: 'height:10px' }),
      find,
      el('div', { style: 'height:8px' }),
      replace,
      el('div', { style: 'display:flex;gap:16px;flex-wrap:wrap;margin-top:12px' },
        el('label', { class: 'check-row' }, inValues, 'Values'),
        el('label', { class: 'check-row' }, inNames, 'Names'),
        el('label', { class: 'check-row' }, useRegex, 'Regex'),
        el('label', { class: 'check-row' }, caseSensitive, 'Match case')),
      results),
    actions: [
      { label: 'Cancel' },
      { label: 'Preview', run: () => { preview(); return false; }, closes: false },
      { label: 'Replace all', class: 'btn-primary', run: () => { apply(); return false; }, closes: false },
    ],
    // The dialog argument, not the `dialog` const below: onOpen runs inside
    // modal(), before the destructuring assignment has happened.
    onOpen: (host) => {
      find.focus();
      const buttons = [...host.querySelectorAll('.modal-foot button')];
      previewButton = buttons.find((b) => b.textContent.startsWith('Preview')) ?? null;
      applyButton = buttons.find((b) => b.classList.contains('btn-primary')) ?? null;
      syncApplyButton();
    },
  });
}

/* ============================================================
   MULTI-SELECT BULK EDIT
   ============================================================ */

/**
 * Bulk edit as an *operation*, not a literal.
 *
 * The dialog this replaces wrote one typed string to every selected node, which
 * is the wrong shape for the job it is reached for. Rebalancing is the most
 * repeated numeric task in WZ work and it is almost never "make these all the
 * same"; it is "make this range 15% stronger", which has to preserve the spread
 * the original data has. See MapleBench/Services/ValueMath.cs for the grammar.
 *
 * A literal typed into this box still means exactly what it always meant, so
 * nobody's habit breaks — "150" is still "set them all to 150".
 *
 * The preview is a real dry run against the server, not a client-side guess.
 * Both buttons hit POST /api/node/compute and differ only in `dryRun`, so the
 * before -> after table you approve is produced by the code that does the
 * writing. Computing it twice, in two languages, is how an editor ends up
 * showing "15 -> 23" and storing something else — and this one writes to files
 * people cannot get back.
 */
export function openBulkEdit(app, paths) {
  const expression = el('input', {
    placeholder: '*1.5   +50   -10%   clamp 1 999   =150',
    style: 'width:100%;height:36px;padding:0 12px;border:1px solid var(--border);' +
           'border-radius:var(--r-sm);background:var(--bg-field);color:var(--text-1);' +
           'font-family:var(--font-mono)',
  });

  const preview = el('div', { style: 'margin-top:12px;max-height:34vh;overflow:auto' });
  let planned = null;      // the dry run the Apply button is allowed to commit
  let applyButton = null;
  let busy = false;

  // Any edit to the expression invalidates the plan. Without this you could
  // preview "*2", retype "*10", and be shown one set of numbers while applying
  // another — the same trap openFindReplace guards above.
  const invalidate = () => { planned = null; syncApply(); };
  expression.addEventListener('input', invalidate);

  const syncApply = () => {
    if (!applyButton) return;
    const ready = !busy && planned !== null && planned.changed > 0;
    applyButton.disabled = !ready;
    applyButton.textContent = ready ? `Apply to ${planned.changed}` : 'Apply';
    applyButton.title = ready
      ? planned.description
      : (busy ? 'Working…' : 'Preview first, so you can see what will change.');
  };

  const rowStyle = 'display:flex;align-items:baseline;gap:8px;padding:3px 6px;' +
                   'font-size:var(--fs-11);border-bottom:1px solid var(--border-subtle)';

  const renderPlan = (result) => {
    clear(preview);

    preview.append(el('div', {
      style: 'font-size:var(--fs-12);font-weight:600;margin-bottom:6px;color:var(--text-2)',
      // The count is the plan's, never the selection's. Saying "312" over 40
      // actual writes is the failure this whole path is shaped to avoid.
      text: `${result.description} · ${result.changed} of ${paths.length} would change` +
            (result.skipped ? ` · ${result.skipped} skipped` : ''),
    }));

    // Changed rows first: the skipped ones are diagnostics and the changed ones
    // are the decision.
    const ordered = [...result.results].sort((a, b) => (a.skipped ? 1 : 0) - (b.skipped ? 1 : 0));

    for (const row of ordered.slice(0, 400)) {
      preview.append(el('div', { style: rowStyle, title: decodeURIComponent(row.path) },
        el('span', {
          style: 'flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap',
          text: row.displayName ? `${row.name} · ${row.displayName}` : row.name,
        }),
        row.skipped
          ? el('span', { style: 'color:var(--text-3)', text: row.skipped })
          : el('span', { style: 'font-family:var(--font-mono)' },
              el('span', { style: 'color:var(--text-3)', text: row.before ?? '—' }),
              el('span', { style: 'color:var(--text-3)', text: '  →  ' }),
              el('b', { text: row.after ?? '' }))));
    }

    if (ordered.length > 400) {
      preview.append(el('div', {
        style: 'padding:6px;font-size:var(--fs-11);color:var(--text-3)',
        text: `…and ${ordered.length - 400} more. All of them are included.`,
      }));
    }
  };

  const runPreview = async () => {
    if (busy) return;
    busy = true; syncApply();
    clear(preview);
    preview.append(el('div', { class: 'skeleton skeleton-row', style: 'width:60%' }));
    try {
      const result = await api.computeValues(paths, expression.value, true);
      planned = result;
      renderPlan(result);
    } catch (error) {
      planned = null;
      clear(preview);
      preview.append(el('div', { style: 'padding:12px;color:var(--red-ink)', text: error.message }));
    } finally {
      busy = false; syncApply();
    }
  };

  const apply = async () => {
    if (busy || !planned?.changed) return;
    busy = true; syncApply();
    try {
      const result = await api.computeValues(paths, expression.value, false);
      dialog.close();

      // Reported from what came back, not from what was asked for.
      if (!result.applied) {
        toast('Nothing was changed.', 'warning');
      } else if (result.skipped) {
        toast(`Changed ${result.changed} value${result.changed === 1 ? '' : 's'}. ` +
              `${result.skipped} could not be and ${result.skipped === 1 ? 'was' : 'were'} left alone.`,
              'warning', { action: { label: 'Undo', run: () => app.undo() } });
      } else {
        toast(`Changed ${result.changed} value${result.changed === 1 ? '' : 's'} — ${result.description}.`,
              'success', { action: { label: 'Undo', run: () => app.undo() } });
      }

      app.markDirty();
      app.inspector.refresh();
      // The results panel is very often where this selection came from, and its
      // rows show the old numbers until it re-reads them.
      if (app.panel === 'results') app.results.refreshValues();
    } catch (error) {
      toastError(error, 'Bulk edit failed');
    } finally {
      busy = false; syncApply();
    }
  };

  expression.addEventListener('keydown', (event) => {
    if (event.key !== 'Enter') return;
    event.preventDefault();
    // Enter previews, then Enter applies. One key runs the whole loop without
    // ever confirming something that has not been shown first.
    if (planned?.changed) apply(); else runPreview();
  });

  const { dialog } = modal({
    title: `Change ${paths.length} value${paths.length === 1 ? '' : 's'}`,
    width: '560px',
    body: el('div', {},
      el('p', {
        style: 'margin:0 0 10px;color:var(--text-2);font-size:var(--fs-13);line-height:1.5',
        text: 'Type an operation to apply to each value, or a plain value to set them all the same. ' +
              'Nothing is written until you apply, and the whole change is one undo step.',
      }),
      expression,
      el('div', {
        style: 'margin-top:8px;font-size:var(--fs-11);color:var(--text-3);line-height:1.6',
        html: '<code>+50</code> <code>-10</code> <code>*1.5</code> <code>/2</code> ' +
              '<code>+15%</code> <code>round</code> <code>floor</code> <code>ceil</code> ' +
              '<code>min 1</code> <code>max 999</code> <code>clamp 1 999</code> ' +
              '<code>=150</code> sets a literal',
      }),
      preview),
    actions: [
      { label: 'Cancel' },
      { label: 'Preview', run: () => { runPreview(); return false; }, closes: false },
      { label: 'Apply', class: 'btn-primary', run: () => { apply(); return false; }, closes: false },
    ],
    onOpen: (host) => {
      expression.focus();
      applyButton = [...host.querySelectorAll('.modal-foot button')]
        .find((b) => b.classList.contains('btn-primary')) ?? null;
      syncApply();
    },
  });
}

/* ============================================================
   RESIZABLE PANELS
   ============================================================ */

export function installPanelResizing() {
  const workspace = $('#workspace');
  const saved = readLayout();
  if (saved.tree) workspace.style.setProperty('--tree-w', `${saved.tree}px`);
  if (saved.inspector) workspace.style.setProperty('--inspector-w', `${saved.inspector}px`);

  addHandle($('#tree-pane'), 'right', (width) => {
    const clamped = Math.max(200, Math.min(520, width));
    workspace.style.setProperty('--tree-w', `${clamped}px`);
    writeLayout({ ...readLayout(), tree: clamped });
  });

  addHandle($('#inspector'), 'left', (width) => {
    const clamped = Math.max(300, Math.min(640, width));
    workspace.style.setProperty('--inspector-w', `${clamped}px`);
    writeLayout({ ...readLayout(), inspector: clamped });
  });
}

function addHandle(panel, side, onResize) {
  if (!panel) return;
  const handle = el('div', {
    class: 'resize-handle',
    style: `${side}:0`,
    role: 'separator',
    tabindex: '0',
    'aria-orientation': 'vertical',
    'aria-label': side === 'right' ? 'Resize file pane' : 'Resize inspector pane',
    'data-tip': 'Drag to resize · Arrow keys also work',
  });
  panel.style.position = 'relative';
  panel.append(handle);

  handle.addEventListener('pointerdown', (event) => {
    event.preventDefault();
    handle.setPointerCapture(event.pointerId);
    const startX = event.clientX;
    const startWidth = panel.getBoundingClientRect().width;

    const move = (e) => {
      const delta = side === 'right' ? e.clientX - startX : startX - e.clientX;
      onResize(startWidth + delta);
    };
    const up = () => {
      handle.removeEventListener('pointermove', move);
      handle.removeEventListener('pointerup', up);
    };
    handle.addEventListener('pointermove', move);
    handle.addEventListener('pointerup', up);
  });

  handle.addEventListener('keydown', (event) => {
    if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return;
    event.preventDefault();
    const direction = event.key === 'ArrowRight' ? 1 : -1;
    const step = event.shiftKey ? 50 : 10;
    const width = panel.getBoundingClientRect().width;
    // The inspector's handle is on its left edge, so moving that edge right
    // makes the pane smaller; the tree handle on the right behaves opposite.
    onResize(width + direction * step * (side === 'right' ? 1 : -1));
  });
}

function readLayout() {
  try { return JSON.parse(localStorage.getItem(STORE.layout) || '{}'); }
  catch { return {}; }
}

function writeLayout(layout) {
  localStorage.setItem(STORE.layout, JSON.stringify(layout));
}

/* ============================================================
   SESSION RESTORE
   ============================================================ */

export function rememberSession(files) {
  localStorage.setItem(STORE.session, JSON.stringify(files.map((f) => ({
    path: f.filePath,
    mapleVersion: f.mapleVersion,
  }))));
}

export function readSession() {
  try { return JSON.parse(localStorage.getItem(STORE.session) || '[]'); }
  catch { return []; }
}

/**
 * Offers to reopen last session's files rather than doing it silently — opening
 * several large archives is slow enough that it should be the user's choice.
 */
export function offerSessionRestore(app) {
  const previous = readSession();
  if (!previous.length) return null;

  return el('button', {
    class: 'btn',
    title: previous.map((f) => f.path).join('\n'),
    text: `Reopen last session (${previous.length} file${previous.length === 1 ? '' : 's'})`,
    onclick: async () => {
      for (const entry of previous) {
        try {
          await app.openPath(entry.path, { mapleVersion: entry.mapleVersion });
        } catch { /* openPath already surfaced the failure */ }
      }
    },
  });
}
