/**
 * Compose section: build a whole client from a pristine base plus takes from
 * other versions — the end goal this tool exists for.
 *
 * The reframe the engine enforces, and this screen has to keep visible: a
 * composition is a BUILD, not a sequence of edits. The base is never touched;
 * the output folder is produced from it every time; removing a contribution is
 * deleting a take and building again. So the screen is a manifest editor plus a
 * build button, not an editor of the output.
 *
 * Safety rules the screen leans on rather than re-implements (they live in the
 * service and the builder, where a UI bug cannot skip them):
 *   - the output folder is explicit, required, and never defaulted — a build
 *     writes a whole client there;
 *   - building into the base or any source folder is refused by the builder;
 *   - building into a folder holding an archive mounted in this app is refused
 *     by the run service;
 *   - a non-empty folder this tool did not itself build is refused by name.
 *
 * The one thing this screen adds on top of the API: it remembers every build
 * finished while it is open and compares the last two, because "built twice,
 * byte-identical" is the engine's headline claim and a user should be able to
 * see it hold — or fail — without hashing files by hand.
 */

import { api } from './api.js';
import { el, clear, toast, toastError, fmt } from './ui.js';
import { emptyState } from './inspector.js';
import { icon } from './icons.js';

const STORE_KEY = 'mb.compose';
const POLL_MS = 700;

/** Port kinds a take can name. Offered as suggestions, not enforced — the server refuses unknown ones with its own words. */
const KINDS = ['mob', 'npc', 'map', 'item', 'skill', 'morph', 'quest', 'reactor'];

const gb = (bytes) => (bytes >= 1073741824
  ? `${(bytes / 1073741824).toFixed(2)} GB`
  : bytes >= 1048576
    ? `${Math.max(1, Math.round(bytes / 1048576))} MB`
    : `${Math.max(1, Math.round(bytes / 1024))} KB`);

const shortHash = (sha) => (sha ? `${sha.slice(0, 12)}…` : '');

export class ComposeSection {
  constructor({ host, app }) {
    this.host = host;
    this.app = app;

    /** The manifest under construction, as plain editable state. */
    this.m = this.load() ?? {
      name: '',
      base: { folder: '', archives: [] },
      sources: [],
      takes: [],
      output: '',
      hashParts: false,
    };

    /** folder -> { archives: [{name, bytes}], hasLedger } from /compose/archives. */
    this.folders = new Map();

    this.progress = null;
    this.result = null;

    /** Completed builds this visit, newest last: { when, output, digest, archives } — the byte-identical check. */
    this.finished = [];

    this.showJson = false;
    this.timer = null;
  }

  async open() {
    this.render();
    await this.refreshProgress();
  }

  refresh() { return this.open(); }

  /* ============================================================
     STATE
     ============================================================ */

  load() {
    try {
      const raw = localStorage.getItem(STORE_KEY);
      return raw ? JSON.parse(raw) : null;
    } catch { return null; }
  }

  save() {
    try { localStorage.setItem(STORE_KEY, JSON.stringify(this.m)); } catch { /* full/blocked storage is cosmetic here */ }
  }

  /** The manifest as the server reads it. */
  manifest() {
    return {
      schema: 'maplebench/composition@1',
      name: this.m.name || 'composition',
      base: {
        id: 'base',
        label: 'Base client',
        folder: this.m.base.folder,
        archives: this.m.base.archives.map((name) => ({ name })),
      },
      sources: this.m.sources.map((source) => ({
        id: source.id,
        label: source.label || source.id,
        folder: source.folder,
        archives: source.archives.map((name) => ({ name })),
      })),
      takes: this.m.takes.map((take) => ({
        from: take.from,
        kind: take.kind,
        scope: 'selection',
        into: take.into,
        take: (take.paths || '').split('\n').map((line) => line.trim()).filter(Boolean),
        options: { overwrite: !!take.overwrite, match: false },
        note: take.note || null,
      })),
    };
  }

  /** Loads a pasted manifest back into the editable state. */
  fromManifest(json) {
    const read = JSON.parse(json);
    if (read.schema !== 'maplebench/composition@1') {
      throw new Error(`'${read.schema ?? '(no schema)'}' is not a composition manifest this screen understands.`);
    }
    this.m.name = read.name ?? '';
    this.m.base = {
      folder: read.base?.folder ?? '',
      archives: (read.base?.archives ?? []).map((archive) => archive.name),
    };
    this.m.sources = (read.sources ?? []).map((source) => ({
      id: source.id,
      label: source.label ?? source.id,
      folder: source.folder ?? '',
      archives: (source.archives ?? []).map((archive) => archive.name),
    }));
    this.m.takes = (read.takes ?? []).map((take) => ({
      from: take.from,
      kind: take.kind,
      into: take.into,
      paths: (take.take ?? []).join('\n'),
      overwrite: !!take.options?.overwrite,
      note: take.note ?? '',
    }));
    this.save();
  }

  /* ============================================================
     TALKING TO THE SERVER
     ============================================================ */

  async readFolder(folder, assign) {
    if (!folder) return;
    try {
      const info = await api.composeArchives(folder);
      this.folders.set(folder, info);
      // First read of a base folder: everything the folder holds is the
      // sensible starting list, unticked down from rather than built up to.
      if (assign) assign(info);
      this.save();
    } catch (error) {
      toastError(error, 'Could not read that folder');
    }
    this.render();
  }

  async build() {
    const problems = this.problems();
    if (problems.length) {
      toast(problems[0], 'warn', { title: 'Not built' });
      this.render();
      return;
    }

    try {
      this.result = null;
      this.progress = await api.composeBuild({
        manifest: this.manifest(),
        outputFolder: this.m.output,
        hashParts: this.m.hashParts,
        stopOnRefusal: true,
      });
      this.poll();
    } catch (error) {
      // A 409 is another build already running — its message says so.
      toastError(error, 'Could not start the build');
    }
    this.render();
  }

  async cancel() {
    try { this.progress = await api.composeCancel(); } catch { /* the poll re-reads it */ }
    this.render();
  }

  poll() {
    clearTimeout(this.timer);
    this.timer = setTimeout(() => this.refreshProgress(), POLL_MS);
  }

  async refreshProgress() {
    try {
      this.progress = await api.composeProgress();
    } catch {
      return; // the server going away is the app's problem to report
    }
    if (this.progress.state === 'running') { this.poll(); this.render(); return; }
    if (this.progress.state === 'done' && !this.result) {
      try {
        this.result = await api.composeResult();
        this.recordFinished(this.result);
      } catch { /* 404 until one finishes */ }
    }
    this.render();
  }

  recordFinished(result) {
    if (!result || result.outcome !== 'Complete') return;
    const verified = result.ledger?.verification?.archives ?? [];
    const record = {
      when: new Date().toLocaleTimeString(),
      output: result.outputFolder,
      digest: result.digest,
      archives: verified.map((archive) => ({ name: archive.name, sha256: archive.sha256, bytes: archive.bytes })),
    };
    // The same finished build re-read on a later visit must not count twice.
    const last = this.finished[this.finished.length - 1];
    if (last && last.digest === record.digest && last.output === record.output) return;
    this.finished.push(record);
  }

  /* ============================================================
     WHAT WOULD STOP A BUILD, SAID BEFORE THE BUTTON
     ============================================================ */

  problems() {
    const problems = [];
    const m = this.m;
    if (!m.base.folder) problems.push('The base client folder is empty.');
    if (!m.base.archives.length) problems.push('The base lists no archives, so there is nothing to build from.');
    if (!m.takes.length) problems.push('There are no takes. A build with nothing to take is a copy, not a composition.');
    for (const [i, take] of m.takes.entries()) {
      if (!take.from) problems.push(`Take ${i + 1} does not say which source it comes from.`);
      if (!take.kind) problems.push(`Take ${i + 1} has no kind.`);
      if (!take.into) problems.push(`Take ${i + 1} does not say which archive it lands in.`);
      if (!(take.paths || '').trim()) problems.push(`Take ${i + 1} lists nothing to take.`);
    }
    if (!m.output) {
      problems.push('The output folder is empty. It is never defaulted: a build writes a whole client there.');
    }
    const same = (a, b) => a && b && a.replace(/[\\/]+$/, '').toLowerCase() === b.replace(/[\\/]+$/, '').toLowerCase();
    if (same(m.output, m.base.folder)) {
      problems.push('The output folder is the base itself. The base must stay pristine — pick an empty folder.');
    }
    for (const source of m.sources) {
      if (same(m.output, source.folder)) problems.push(`The output folder is source '${source.id}' itself.`);
    }
    return problems;
  }

  /* ============================================================
     RENDER
     ============================================================ */

  render() {
    clear(this.host);
    this.host.className = 'stage-body compose';
    this.host.append(this.head());

    if (this.progress?.state === 'running') {
      this.host.append(this.running());
      return;
    }
    if (this.progress?.state === 'failed') this.host.append(this.failure());
    if (this.progress?.state === 'cancelled') this.host.append(this.cancelled());

    if (this.result) this.host.append(...this.resultView());
    if (this.finished.length >= 2) this.host.append(this.identicalCard());

    this.host.append(
      this.baseCard(),
      this.sourcesCard(),
      this.takesCard(),
      this.jsonCard(),
      this.outputCard());
  }

  head() {
    return el('div', { class: 'compose-head' },
      el('div', {},
        el('h2', { text: 'Compose a client' }),
        el('p', {
          class: 'compose-sub',
          text: 'A pristine base plus an ordered list of takes from other versions, built into a NEW '
              + 'folder with a ledger of what came from where. Re-running rebuilds from the base — it '
              + 'never layers — and the base and every source are opened read-only.',
        })));
  }

  /* ---- base ----------------------------------------------------------- */

  baseCard() {
    const m = this.m;
    const info = this.folders.get(m.base.folder);

    return el('div', { class: 'compose-card' },
      el('h3', { text: '1 · The base client' }),
      el('p', {
        class: 'compose-sub',
        text: 'Every build starts from a byte copy of these archives. The folder is read, never written.',
      }),
      el('div', { class: 'compose-controls' },
        el('input', {
          class: 'input compose-folder',
          type: 'text',
          value: m.base.folder,
          placeholder: 'C:\\clients\\my-base-copy',
          spellcheck: 'false',
          oninput: (event) => { m.base.folder = event.target.value.trim(); this.save(); },
          onkeydown: (event) => { if (event.key === 'Enter') this.readBase(); },
        }),
        el('button', {
          class: 'btn',
          onclick: () => this.readBase(),
          'data-tip': 'List the .wz archives in this folder. Opens nothing.',
        }, icon('folderOpen', { size: 15 }), 'Read folder')),
      info ? this.archivePicker(info, m.base.archives, (names) => { m.base.archives = names; this.save(); }) : null,
      info?.hasLedger
        ? el('p', { class: 'compose-note', text: 'This folder carries a composition ledger — it is itself a built output.' })
        : null,
      m.base.archives.length
        ? el('p', { class: 'compose-dim', text: `${m.base.archives.length} archive${m.base.archives.length === 1 ? '' : 's'} in the base — these are what the output will contain.` })
        : null);
  }

  readBase() {
    return this.readFolder(this.m.base.folder, (info) => {
      if (!this.m.base.archives.length) this.m.base.archives = info.archives.map((archive) => archive.name);
    });
  }

  /** A checkbox per archive the folder holds, with sizes — ticked means listed in the manifest. */
  archivePicker(info, selected, onChange) {
    const set = new Set(selected);
    return el('div', { class: 'compose-archives' },
      ...info.archives.map((archive) => el('label', { class: 'compose-archive' },
        el('input', {
          type: 'checkbox',
          checked: set.has(archive.name) ? 'checked' : undefined,
          onchange: (event) => {
            if (event.target.checked) set.add(archive.name); else set.delete(archive.name);
            onChange(info.archives.map((a) => a.name).filter((name) => set.has(name)));
            this.render();
          },
        }),
        el('code', { text: archive.name }),
        el('span', { class: 'compose-dim', text: gb(archive.bytes) }))),
      info.archives.length === 0
        ? el('p', { class: 'compose-dim', text: 'No .wz archives in this folder.' })
        : null);
  }

  /* ---- sources -------------------------------------------------------- */

  sourcesCard() {
    const m = this.m;

    return el('div', { class: 'compose-card' },
      el('h3', { text: '2 · The donors' }),
      el('p', {
        class: 'compose-sub',
        text: 'The clients content is taken from. Each gets a short handle the takes name it by.',
      }),
      ...m.sources.map((source, index) => this.sourceRow(source, index)),
      el('button', {
        class: 'btn',
        onclick: () => {
          m.sources.push({ id: `v${m.sources.length + 1}`, label: '', folder: '', archives: [] });
          this.save();
          this.render();
        },
      }, icon('plus', { size: 15 }), 'Add a donor'));
  }

  sourceRow(source, index) {
    const info = this.folders.get(source.folder);

    return el('div', { class: 'compose-source' },
      el('div', { class: 'compose-controls' },
        el('input', {
          class: 'input compose-id', type: 'text', value: source.id, placeholder: 'v233',
          'data-tip': 'The handle takes use. Unique within the manifest.',
          oninput: (event) => { source.id = event.target.value.trim(); this.save(); },
        }),
        el('input', {
          class: 'input compose-label', type: 'text', value: source.label, placeholder: 'label (optional)',
          oninput: (event) => { source.label = event.target.value; this.save(); },
        }),
        el('input', {
          class: 'input compose-folder', type: 'text', value: source.folder,
          placeholder: 'C:\\clients\\donor-copy', spellcheck: 'false',
          oninput: (event) => { source.folder = event.target.value.trim(); this.save(); },
          onkeydown: (event) => { if (event.key === 'Enter') this.readSource(source); },
        }),
        el('button', { class: 'btn', onclick: () => this.readSource(source) },
          icon('folderOpen', { size: 15 }), 'Read'),
        el('button', {
          class: 'btn', 'data-tip': 'Remove this donor from the manifest.',
          onclick: () => { this.m.sources.splice(index, 1); this.save(); this.render(); },
        }, icon('trash', { size: 15 }))),
      info ? this.archivePicker(info, source.archives, (names) => { source.archives = names; this.save(); }) : null);
  }

  readSource(source) {
    return this.readFolder(source.folder, (info) => {
      if (!source.archives.length) source.archives = info.archives.map((archive) => archive.name);
    });
  }

  /* ---- takes ---------------------------------------------------------- */

  takesCard() {
    const m = this.m;

    return el('div', { class: 'compose-card' },
      el('h3', { text: '3 · The takes, in order' }),
      el('p', {
        class: 'compose-sub',
        text: 'One contribution each: what to take, from which donor, into which archive. Order is '
            + 'meaningful — a later take may overwrite an earlier one, and the ledger records that it did.',
      }),
      el('datalist', { id: 'compose-kinds' }, ...KINDS.map((kind) => el('option', { value: kind }))),
      ...m.takes.map((take, index) => this.takeRow(take, index)),
      el('button', {
        class: 'btn',
        onclick: () => {
          m.takes.push({
            from: m.sources[0]?.id ?? '', kind: '', into: m.base.archives[0] ?? '',
            paths: '', overwrite: false, note: '',
          });
          this.save();
          this.render();
        },
      }, icon('plus', { size: 15 }), 'Add a take'));
  }

  takeRow(take, index) {
    const select = (value, entries, onchange, tip) => el('select', {
      class: 'input compose-select', 'data-tip': tip, onchange,
    }, ...entries.map(([v, label]) => {
      const option = el('option', { value: v, text: label });
      if (v === value) option.selected = true;
      return option;
    }));

    return el('div', { class: 'compose-take' },
      el('div', { class: 'compose-take-head' },
        el('span', { class: 'compose-take-n', text: `#${index + 1}` }),
        select(take.from,
          [['', '(source…)'], ...this.m.sources.map((source) => [source.id, source.id])],
          (event) => { take.from = event.target.value; this.save(); this.renderProblems(); },
          'Which donor this comes from'),
        el('input', {
          class: 'input compose-kind', type: 'text', value: take.kind, placeholder: 'kind (mob, map…)',
          list: 'compose-kinds',
          oninput: (event) => { take.kind = event.target.value.trim(); this.save(); this.renderProblems(); },
        }),
        el('span', { class: 'compose-dim', text: 'into' }),
        select(take.into,
          [['', '(archive…)'], ...this.m.base.archives.map((name) => [name, name])],
          (event) => { take.into = event.target.value; this.save(); this.renderProblems(); },
          'The output archive this lands in'),
        el('label', { class: 'compose-flag', 'data-tip': 'Replace what the target already holds under these names. Off, the target\'s own content wins and the refusal is recorded.' },
          el('input', {
            type: 'checkbox', checked: take.overwrite ? 'checked' : undefined,
            onchange: (event) => { take.overwrite = event.target.checked; this.save(); },
          }),
          el('span', { text: 'overwrite' })),
        el('button', {
          class: 'btn', 'data-tip': 'Remove this take. Rebuilding without it IS the undo.',
          onclick: () => { this.m.takes.splice(index, 1); this.save(); this.render(); },
        }, icon('trash', { size: 15 }))),
      el('textarea', {
        class: 'input compose-paths',
        rows: Math.max(2, (take.paths || '').split('\n').length),
        placeholder: 'Archive-relative paths, one per line:\nMob.wz/8800100.img',
        spellcheck: 'false',
        oninput: (event) => { take.paths = event.target.value; this.save(); this.renderProblems(); },
      }, take.paths || ''),
      el('input', {
        class: 'input compose-note-input', type: 'text', value: take.note,
        placeholder: 'why this take exists (carried into the ledger)',
        oninput: (event) => { take.note = event.target.value; this.save(); },
      }));
  }

  /* ---- manifest as a document ----------------------------------------- */

  jsonCard() {
    if (!this.showJson) {
      return el('div', { class: 'compose-card compose-muted' },
        el('button', { class: 'btn', onclick: () => { this.showJson = true; this.render(); } },
          icon('file', { size: 15 }), 'Show the manifest as JSON'));
    }

    const area = el('textarea', {
      class: 'input compose-json', rows: 16, spellcheck: 'false',
    }, JSON.stringify(this.manifest(), null, 2));

    return el('div', { class: 'compose-card compose-muted' },
      el('h3', { text: 'The manifest, as the document it is' }),
      el('p', {
        class: 'compose-sub',
        text: 'Editable. "Use this JSON" replaces the form above with what is pasted here — the manifest '
            + 'is the source of truth, the form is a view of it.',
      }),
      area,
      el('div', { class: 'compose-controls' },
        el('button', {
          class: 'btn',
          onclick: () => {
            try { this.fromManifest(area.value); toast('Manifest loaded'); this.render(); }
            catch (error) { toastError(error, 'Not a manifest this screen can read'); }
          },
        }, 'Use this JSON'),
        el('button', {
          class: 'btn',
          onclick: () => { navigator.clipboard?.writeText(area.value); toast('Manifest copied'); },
        }, 'Copy'),
        el('button', { class: 'btn', onclick: () => { this.showJson = false; this.render(); } }, 'Hide')));
  }

  /* ---- output + build -------------------------------------------------- */

  outputCard() {
    // The list is a live element updated in place on every keystroke, because a
    // full render would steal the focus mid-word — and a DISABLED build button
    // fed by a stale render is worse than an enabled one that refuses with the
    // reason: the first is a silent no-op, the failure mode this project keeps
    // paying for.
    this.problemsHost = el('ul', { class: 'compose-problems' });
    this.renderProblems();

    return el('div', { class: 'compose-card' },
      el('h3', { text: '4 · Build' }),
      el('p', {
        class: 'compose-sub',
        text: 'The output folder gets a full copy of every base archive with the takes applied, plus '
            + 'composition-ledger.json saying what came from where. It must be empty, or a folder this '
            + 'tool built before. It is deliberately never defaulted.',
      }),
      el('div', { class: 'compose-controls' },
        el('input', {
          class: 'input compose-folder',
          type: 'text',
          value: this.m.output,
          placeholder: 'an empty folder for the composed client',
          spellcheck: 'false',
          oninput: (event) => { this.m.output = event.target.value.trim(); this.save(); this.renderProblems(); },
        }),
        el('label', { class: 'compose-flag', 'data-tip': 'Record a content hash for every node that lands. A second full parse of everything that moved — slower, stronger ledger.' },
          el('input', {
            type: 'checkbox', checked: this.m.hashParts ? 'checked' : undefined,
            onchange: (event) => { this.m.hashParts = event.target.checked; this.save(); },
          }),
          el('span', { text: 'hash parts' })),
        el('button', {
          class: 'btn btn-primary',
          'data-tip': 'Copies the base, applies the takes, saves, then verifies the saved files. Refuses with the reason when the manifest is not ready.',
          onclick: () => this.build(),
        }, icon('play', { size: 15 }), 'Build')),
      this.problemsHost);
  }

  /** Refreshes the problem list in place, without a render that would steal focus. */
  renderProblems() {
    if (!this.problemsHost) return;
    clear(this.problemsHost);
    this.problemsHost.append(...this.problems().map((problem) => el('li', { text: problem })));
  }

  /* ---- progress / terminal states -------------------------------------- */

  running() {
    const p = this.progress;
    const share = p.takesTotal ? Math.round((p.takesDone / p.takesTotal) * 100) : 0;

    return el('div', { class: 'compose-card compose-running' },
      el('h3', { text: `${p.phase}${p.detail ? ` — ${p.detail}` : ''}` }),
      el('div', { class: 'compose-bar' }, el('div', { class: 'compose-bar-fill', style: `width:${share}%` })),
      el('p', {
        class: 'compose-sub',
        text: `${p.takesDone} of ${p.takesTotal} takes · ${p.seconds.toFixed(0)}s · building into ${p.output ?? ''}`,
      }),
      el('button', {
        class: 'btn',
        onclick: () => this.cancel(),
        'data-tip': 'Stops the build and discards the half-built output. The base and sources are untouched.',
      }, 'Stop'));
  }

  failure() {
    return el('div', { class: 'compose-card compose-failed' },
      el('h3', { text: 'The build stopped' }),
      el('p', { class: 'compose-sub', text: this.progress.error ?? 'No reason was reported.' }));
  }

  cancelled() {
    return el('div', { class: 'compose-card compose-muted' },
      el('h3', { text: 'Cancelled' }),
      el('p', { class: 'compose-sub', text: this.progress.detail ?? 'The build was stopped.' }));
  }

  /* ---- the finished build ---------------------------------------------- */

  resultView() {
    const result = this.result;
    const ledger = result.ledger ?? {};
    const verification = ledger.verification ?? {};
    const outcome = result.outcome;

    const banner = el('div', {
      class: 'compose-card compose-outcome',
      'data-outcome': outcome,
    },
      el('h3', {
        text: outcome === 'Complete'
          ? `Complete — ${result.outputFolder}`
          : outcome === 'Refused'
            ? 'Refused — nothing was written'
            : `Partial — ${result.outputFolder} is missing a contribution`,
      }),
      outcome === 'Refused'
        ? el('pre', { class: 'compose-refusal' }, el('code', { text: result.refusal ?? '' }))
        : el('p', {
            class: 'compose-sub',
            text: `${(ledger.takes ?? []).length} takes · ledger digest ${shortHash(result.digest)} · ${result.seconds.toFixed(1)}s`,
          }));

    if (outcome === 'Refused') return [banner];

    const archives = el('div', { class: 'compose-card' },
      el('h3', {
        text: verification.ran
          ? 'Verified on the saved files, read back off disk'
          : 'NOT verified — treat the output as unproven',
      }),
      el('table', { class: 'table compose-table' },
        el('thead', {}, el('tr', {},
          el('th', { text: 'Archive' }), el('th', { text: 'SHA-256' }),
          el('th', { class: 'num', text: 'Bytes' }),
          el('th', { class: 'num', text: 'Landings checked' }),
          el('th', { class: 'num', text: 'Missing' }))),
        el('tbody', {}, ...(verification.archives ?? []).map((archive) => el('tr', {},
          el('td', {}, el('code', { text: archive.name })),
          el('td', {}, el('code', { 'data-tip': archive.sha256, text: shortHash(archive.sha256) })),
          el('td', { class: 'num', text: fmt.format(archive.bytes) }),
          el('td', { class: 'num', text: String(archive.checked) }),
          el('td', {
            class: `num${archive.missing?.length ? ' compose-bad' : ''}`,
            text: String(archive.missing?.length ?? 0),
          }))))));

    const takes = (ledger.takes ?? []).map((take) => this.takeReport(take));

    const notes = (ledger.notes ?? []).length
      ? el('div', { class: 'compose-card compose-muted' },
          el('h3', { text: 'Notes on the whole build' }),
          el('ul', {}, ...ledger.notes.map((note) => el('li', { text: note }))))
      : null;

    return [banner, archives, ...takes, notes].filter(Boolean);
  }

  takeReport(take) {
    const cap = 40;
    const took = take.took ?? [];
    const refused = take.refused ?? [];
    const renamed = take.renamed ?? [];

    return el('div', { class: 'compose-card compose-muted' },
      el('h3', {
        text: `Take ${take.sequence} · ${take.kind} from ${take.source?.label ?? take.source?.id ?? '?'} `
            + `into ${take.into} — ${take.written} written, ${take.failed} failed, ${take.skipped} skipped`,
      }),
      took.length
        ? el('details', {},
            el('summary', { text: `${took.length} part${took.length === 1 ? '' : 's'} landed` }),
            el('ul', { class: 'compose-parts' },
              ...took.slice(0, cap).map((part) => el('li', {},
                el('code', { text: part.from ?? '' }),
                el('span', { text: ' → ' }),
                el('code', { text: part.to ?? '' }),
                part.contentHash ? el('span', { class: 'compose-dim', text: ` ${shortHash(part.contentHash)}` }) : null)),
              took.length > cap
                ? el('li', { class: 'compose-dim', text: `…and ${took.length - cap} more. The full list is in composition-ledger.json beside the output.` })
                : null))
        : el('p', { class: 'compose-dim', text: 'Nothing landed — everything this take wanted was already there.' }),
      renamed.length
        ? el('details', {},
            el('summary', { text: `${renamed.length} landed under a different name` }),
            el('ul', { class: 'compose-parts' }, ...renamed.map((rename) => el('li', {},
              el('code', { text: rename.from }), el('span', { text: ' → ' }),
              el('code', { text: rename.to }), el('span', { class: 'compose-dim', text: ` — ${rename.why}` })))))
        : null,
      refused.length
        ? el('details', {},
            el('summary', { text: `${refused.length} not carried, each with its reason` }),
            el('ul', { class: 'compose-parts' }, ...refused.slice(0, cap).map((refusal) => el('li', {},
              el('span', { class: 'compose-refclass', text: refusal.class }),
              el('code', { text: ` ${refusal.what}` }),
              el('span', { class: 'compose-dim', text: ` — ${refusal.why}` }))),
              refused.length > cap ? el('li', { class: 'compose-dim', text: `…and ${refused.length - cap} more, in the ledger file.` }) : null))
        : null);
  }

  /* ---- built twice, compared ------------------------------------------- */

  identicalCard() {
    const a = this.finished[this.finished.length - 2];
    const b = this.finished[this.finished.length - 1];

    const ledgersMatch = a.digest === b.digest;
    const names = new Set([...a.archives.map((x) => x.name), ...b.archives.map((x) => x.name)]);
    const rows = [...names].map((name) => {
      const left = a.archives.find((x) => x.name === name);
      const right = b.archives.find((x) => x.name === name);
      return { name, left, right, same: !!left && !!right && left.sha256 === right.sha256 };
    });
    const allSame = ledgersMatch && rows.every((row) => row.same);

    return el('div', { class: 'compose-card compose-compare', 'data-same': allSame ? 'true' : 'false' },
      el('h3', {
        text: allSame
          ? 'Built twice, byte-identical'
          : 'The last two builds DIFFER',
      }),
      el('p', {
        class: 'compose-sub',
        text: allSame
          ? `The last two completed builds (${a.output} · ${b.output}) produced archives with identical `
            + 'SHA-256s and ledgers with the same digest. Same inputs, same bytes — the reproducibility claim, held.'
          : 'A rebuild that differs means something read a clock, a hash seed or a dictionary order into the '
            + 'file — or the inputs changed. The rows below say which archives moved.',
      }),
      el('table', { class: 'table compose-table' },
        el('thead', {}, el('tr', {},
          el('th', { text: 'Archive' }),
          el('th', { text: `build @ ${a.when}` }),
          el('th', { text: `build @ ${b.when}` }),
          el('th', { text: 'Same bytes' }))),
        el('tbody', {}, ...rows.map((row) => el('tr', {},
          el('td', {}, el('code', { text: row.name })),
          el('td', {}, el('code', { 'data-tip': row.left?.sha256, text: shortHash(row.left?.sha256) || '(absent)' })),
          el('td', {}, el('code', { 'data-tip': row.right?.sha256, text: shortHash(row.right?.sha256) || '(absent)' })),
          el('td', { class: row.same ? 'compose-good' : 'compose-bad', text: row.same ? 'identical' : 'DIFFERENT' }))))),
      el('p', {
        class: 'compose-dim',
        text: `Ledger digests: ${shortHash(a.digest)} vs ${shortHash(b.digest)} — ${ledgersMatch ? 'equal' : 'NOT equal'}.`,
      }));
  }
}
