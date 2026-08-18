/**
 * Porting content between two open clients, in one action.
 *
 * The thing this exists to stop: someone needs a mob out of a newer client, so
 * they copy Mob.wz/8800100.img across and are done. In a v232 client that image
 * is nothing but an `info` block pointing at 8800000.img for every frame it
 * draws, its name is in String.wz and its sounds are in Sound.wz — so the copy
 * visibly succeeds and produces an unnamed, silent mob that renders nothing.
 * Select, preview, apply: the preview is the whole feature, and the copy is the
 * easy part.
 *
 * Built in the same shape as the Mobs bulk edit, deliberately. A recipe at the
 * top, a Preview button that must be pressed before Apply comes alive, any
 * change to the recipe throwing the preview away, and a per-row result table
 * afterwards. Someone who has used one has used the other.
 *
 * It is not a mode: it opens over whichever mode the selection came from, and
 * takes a `kind` so the same dialog serves mobs, NPCs and whatever comes next.
 */

import { api } from './api.js';
import { el, clear, toast, toastError, fmt, modal, confirmDialog, statChip } from './ui.js';
import { icon } from './icons.js';

/** How each part status reads, and how it is toned. */
const STATUS = {
  New: { label: 'will be copied', tone: 'good' },
  Conflict: { label: 'already there', tone: 'warn' },
  Same: { label: 'already identical', tone: 'muted' },
  Absent: { label: 'not in the source', tone: 'muted' },
  Blocked: { label: 'cannot be done', tone: 'bad' },
};

const PART_ICON = {
  container: 'folder',
  entry: 'layers',
  'link-entry': 'externalLink',
  string: 'type',
  sound: 'play',
  shop: 'cart',
  'set-info': 'layers',
  'set-effect': 'image',
  named: 'image',
};

/**
 * What each part kind is, said as the thing the player would miss.
 *
 * Used in the two places that have to be honest in one sentence -- the
 * "this did not land" summary and the missing-archives warning -- where
 * "1 string part was Blocked" is true and tells nobody anything.
 */
const PART_CARRIES = {
  entry: 'the entry itself',
  container: 'the image it lives in',
  'link-entry': 'an image it takes its art from',
  string: 'the name it shows in game',
  sound: 'its sounds',
  shop: 'its cash-shop listing',
  'set-info': 'the set window, member list and bonuses',
  'set-effect': 'the set\'s visual effect',
  // A map's scenery, music and mark. Deliberately not "its art": the whole
  // point of this part kind is that the thing is named rather than owned, and
  // a reader who takes it for the entry's own art will not understand why it
  // sometimes lands under a different name.
  named: 'a picture or clip it draws on by name, which other content shares',
};

/**
 * What an archive family carries in a port, for the setup warning.
 *
 * Keyed by the family stem the server reports, not by file name, so a client
 * whose names are Sound001.wz and Sound002.wz is described the same way as one
 * with a plain Sound.wz.
 *
 * Only String states the loss outright, because every item and mob has a
 * String row and so losing it is certain. Sound and Etc are the exception --
 * most equips are in neither -- so they are worded as a possibility. Stating
 * all three the same way read as a promise that the sounds existed.
 */
const ARCHIVE_CARRIES = {
  String: 'the name it shows in game — without it the copy is nameless',
  Sound: 'its sounds, if it has any',
  Etc: 'its set definition when it belongs to a set, and its cash-shop listing if it is sold there',
};

/**
 * What the TARGET client has to have open, from the kind's satellite list.
 *
 * Was the hardcoded sentence "Its String.wz and Sound.wz have to be open too,
 * or the name and the sounds cannot land." That is right for a mob and wrong
 * for every kind whose satellites are a different set, and it named Sound for
 * kinds that have none.
 */
function intoNeeds(spec) {
  const families = [...new Set((spec?.satellites || [])
    .map((s) => archiveFamily(String(s).split('/')[0]))
    .filter(Boolean))];
  if (!families.length) return 'Nothing else has to be open for this kind.';
  if (families.length === 1) return `${families[0]} has to be open here too, or that part has nowhere to land.`;
  const list = `${families.slice(0, -1).join(', ')} and ${families[families.length - 1]}`;
  return `${list} have to be open here too, or those parts have nowhere to land.`;
}

/**
 * The archive family a file name belongs to: "Mob001.wz" -> "Mob".
 *
 * Mirrors WzSessionService.StripArchiveSuffix, which is how the server decides
 * that a target's Mob001.wz can answer for a source's Mob002.wz. Getting this
 * wrong in either direction is the difference between "open your String.wz" and
 * a port that lands nameless.
 */
function archiveFamily(name) {
  return String(name).replace(/\.wz$/i, '').replace(/\d+$/, '');
}

/** "Skill003.wz", "Skill003.wz and String.wz", "A, B and C" — for a sentence. */
function listOf(names) {
  if (!names.length) return '';
  if (names.length === 1) return names[0];
  return `${names.slice(0, -1).join(', ')} and ${names[names.length - 1]}`;
}

/* ============================================================
   WHICH WARNINGS ARE AN INSTRUCTION

   The server sends warnings and per-entry notes as prose, in one flat list
   each. That is fine for "both archives stamp v232" and disastrous for "open
   Skill003.wz" -- the two sat in the same amber block, in the same type, and
   the one that decides whether the ported skills crash the client was
   somewhere between the version stamp and the field comparison.

   So the sentences are read here, and the ones that name a file AND tell you
   to open it are lifted out to the top of the preview. Read rather than
   flagged, because the wire format is a list of strings; if the server ever
   marks them itself, warningText below already takes an object.
   ============================================================ */

/** A warning as text, whether it arrives as a string or as an object with one. */
function warningText(warning) {
  if (typeof warning === 'string') return warning;
  return String(warning?.text ?? warning?.message ?? '');
}

/**
 * The archives and images a sentence names outright: "Skill003.wz", "40000.img".
 *
 * Deliberately literal -- a word with a .wz or .img extension -- because
 * guessing at bare capitalised words would promote half the vocabulary of a
 * warning into a file the user is told to go and find.
 */
function filesNamedIn(text) {
  return [...new Set(String(text).match(/\b[\w-]+\.(?:wz|img)\b/gi) || [])];
}

/**
 * A warning that is an instruction rather than an observation.
 *
 * Both halves are needed. "Open" alone catches the general art-link warning,
 * which merely says outlinked images "travel too" and names nothing to go and
 * find; a named .wz alone catches the version stamp, which names both archives
 * and asks for nothing. Together they catch exactly the sentences the user has
 * to act on before pressing the button -- "Open Skill003.wz from ClientA", and
 * "every part of that family has to be open on BOTH clients".
 */
function isSetupWarning(text) {
  return /\bopen\b/i.test(text) && filesNamedIn(text).some((name) => /\.wz$/i.test(name));
}

/** The plan's warnings, split into the ones to act on and the ones to read. */
function splitWarnings(plan) {
  const all = (plan.warnings || []).map(warningText).filter(Boolean);
  return {
    urgent: all.filter(isSetupWarning),
    rest: all.filter((line) => !isSetupWarning(line)),
  };
}

/**
 * Per-entry notes that say an image could not be found in what is open.
 *
 * These are the ones that matter most and were the least visible: one line at
 * the bottom of one card out of two hundred, phrased identically to the notes
 * about links that resolved fine. The port that took the user's client down had
 * thirty of them.
 */
const NOTE_WANTS_AN_ARCHIVE =
  /not in the archives you have open|open the archive|is not open|nothing open can supply/i;

/** Entries carrying such a note, the distinct notes, and the images they name. */
function unopenedArtNotes(plan) {
  const wants = (note) => NOTE_WANTS_AN_ARCHIVE.test(note);
  const items = (plan.items || []).filter((item) => (item.notes || []).some(wants));
  const notes = [...new Set(items.flatMap((item) => (item.notes || []).filter(wants)))];
  return {
    entries: items.length,
    notes,
    // Only the images, not the client names: ".wz" here would be the archive the
    // note says is NOT open, which the urgent warnings already name properly.
    images: [...new Set(notes.flatMap(filesNamedIn))].filter((name) => /\.img$/i.test(name)),
  };
}

/**
 * The block that must not be scrolled past, or null when there is nothing to say.
 *
 * A canvas `_outlink` left pointing into an archive the target client cannot
 * resolve is not a missing picture. It is a link the client follows when the
 * window that draws it opens, and on a v232 client that is a crash -- which is
 * how this feature earned its own bug report. So the sentence naming the files
 * to open goes above the counts, above the cards, and in the colour reserved
 * for "this cannot be done", rather than in the collapsible block below them.
 */
function openTheseArchives(plan) {
  const { urgent } = splitWarnings(plan);
  const art = unopenedArtNotes(plan);
  if (!urgent.length && !art.entries) return null;

  const files = urgent.flatMap(filesNamedIn).filter((name) => /\.wz$/i.test(name));
  const named = [...new Set(files)];
  const shown = art.images.slice(0, 8);

  return el('div', { class: 'notice anim-rise', 'data-tone': 'bad' },
    icon('alert', { size: 18 }),
    el('div', {},
      el('b', { text: named.length
        ? `Open ${listOf(named)} — on both clients — before you import this`
        : 'Some of this art comes out of archives that are not open' }),
      urgent.map((line) => el('p', { text: line })),
      art.entries
        ? el('p', { text:
            `${fmt.format(art.entries)} of the entries below point at art this import cannot see`
            + (shown.length ? `: ${shown.join(', ')}` : '')
            + (art.images.length > shown.length
                ? ` and ${fmt.format(art.images.length - shown.length)} more`
                : '')
            + '. Each of those is repeated on its own card, where it is one line among hundreds — '
            + 'this is the same news, said once, where it can be acted on.' })
        : null,
      // The item list is capped, so this count is read off a sample. Said,
      // because "3 entries" over a four-thousand-entry port reads as "nearly
      // none of them" and the truth may be the opposite.
      art.entries && (plan.itemsTruncated ?? 0) > 0
        ? el('p', { text:
            `That is counted from the ${fmt.format((plan.items || []).length)} entries listed, out of `
            + `${fmt.format(plan.totals?.entries ?? 0)} in the import — a sample, not the total.` })
        : null,
      el('p', { text:
        'Imported as it stands, those frames would arrive as links to art the target client cannot resolve. '
        + 'That is not a blank sprite: it is what takes the client down when the window that draws it '
        + 'opens. An archive family answers as a whole, so the numbered siblings count — Skill001.wz '
        + 'and Skill003.wz are both "Skill", and the icon book a skill points at often lives in one of '
        + 'them rather than in the archive you picked.' })));
}

/**
 * Opens the port dialog for a selection, or for a whole archive.
 *
 * @param app          the App, for files, markDirty and undo
 * @param kind         'mob' | 'npc' | 'item' … — the port catalog's own vocabulary
 * @param paths        session paths of the selected entry images (selection scope)
 * @param scope        'selection' (default), 'folder' or 'archive'
 * @param sourceFileId the archive to take everything from (archive scope only)
 */
/**
 * @param targetFileId  The archive the caller has already chosen, when it has.
 *                      The two-pane view passes the archive the node was
 *                      DROPPED on: the user answered "into what?" with a
 *                      gesture, and asking again -- or worse, silently picking
 *                      a different archive because it happened to be the only
 *                      other one -- would make the drop mean something other
 *                      than what it showed before the release.
 * @param overwrite     Start with Replace selected. In ordinary imports this
 *                      still goes through the preview. In Dual View the user
 *                      has already answered the Windows-style conflict prompt,
 *                      so directTarget applies that exact answer without asking
 *                      for either the destination or conflict policy again.
 * @param directTarget  The dual-view drop already fixed the destination and
 *                      resolved the conflict policy, so neither is asked again.
 */
export async function openPortDialog({ app, kind, paths, scope = 'selection', sourceFileId = null,
                                       targetFileId = null, overwrite = false,
                                       directTarget = false }) {
  // 'folder' is a bulk scope like 'archive': thousands of entries, so it
  // gets the preview and the one overwrite decision rather than the
  // one-click path a hand-picked selection uses.
  const wholeArchive = scope === 'archive' || scope === 'folder';

  if (!wholeArchive && !paths?.length) {
    toast('Select one or more entries first.', 'info');
    return;
  }
  if (wholeArchive && !sourceFileId) {
    toast('Choose a source archive before importing the whole archive.', 'warning');
    return;
  }

  let capabilities;
  try {
    capabilities = await api.portCapabilities();
  } catch (error) {
    toastError(error, 'Could not check which clients are open');
    return;
  }

  const spec = (capabilities.kinds || []).find((k) => k.kind === kind);

  // The kind is declared but not built. Said in as many words, with the reason,
  // rather than hidden -- a missing button reads as "this app cannot do that",
  // which is a different and less useful statement than "not yet, and here is
  // what is hard about it".
  if (spec && !spec.supported) {
    modal({
      title: `Importing ${spec.plural.toLowerCase()} is not available`,
      body: el('div', { class: 'port-blocked' },
        icon('info', { size: 18 }),
        el('p', { text: spec.unsupportedReason })),
    });
    return;
  }

  if (!capabilities.available) {
    modal({
      title: 'Open a source and target client',
      body: el('div', { class: 'port-blocked' },
        icon('alert', { size: 18 }),
        el('div', {},
          el('p', { text: capabilities.reason }),
          el('p', { class: 'port-hint', text:
            'A client is a folder. Open the second client\'s Mob.wz, String.wz and Sound.wz — ' +
            'the importer reads names and sounds from the same folder as the selected content.' }))),
      actions: [
        { label: 'Open a file', class: 'btn-primary', run: () => app.openFilePicker() },
      ],
    });
    return;
  }

  // ONE CLICK.
  //
  // The button does the work; it does not open a form about doing the work. The
  // safety net is that the whole thing is one undoable action and the result is
  // honest about what landed -- not that the user was made to read a table
  // first. The two things that may still interrupt are a genuine choice, not a
  // confirmation:
  //
  //   * more than one client could receive this, so which one is a question
  //     nothing can answer for the user;
  //   * the target already has one of these, which is the single case where
  //     guessing wrong destroys something they had.
  //
  //   * the target client cannot receive the whole thing -- it has no archive
  //     of this kind open, or no String.wz to put the name in. That check used
  //     to happen only inside the plan, one part at a time, so a client with no
  //     String.wz open produced a port that wrote the mob, reported success and
  //     left it nameless: the exact failure this feature exists to prevent,
  //     committed by the feature. It is a genuine choice too, because the fix
  //     is a file the user has to open. See setupGap.
  //
  // Everything else -- an absent sound, art that could not be followed -- is
  // reported afterwards, because none of it can lose data and asking about it
  // every time is how a feature stops being used.
  // Which archives the request is reading out of, however it was expressed. A
  // selection says so in its paths; a whole-archive port says so in one id. Both
  // reduce to the same question -- which client is the source -- and everything
  // below asks it once, here, rather than re-deriving it from paths that a
  // whole-archive port does not have.
  const sourceFileIds = wholeArchive
    ? new Set([sourceFileId])
    : new Set(paths.map((p) => p.split('/')[0]));

  let targets;
  for (;;) {
    targets = targetsFor(capabilities, kind, sourceFileIds);
    const gap = setupGap({ capabilities, kind, sourceFileIds, spec, targets });
    if (!gap) break;

    const answer = await offerMissingArchives({ app, spec, gap });
    if (answer !== 'opened') return undefined;

    // Only ever looped after something was actually opened, so the set of
    // missing families strictly shrinks and this cannot spin.
    capabilities = await api.portCapabilities();
  }

  // A whole archive is never one click.
  //
  // The one-click rule is a claim about blast radius: a handful of entries whose
  // every part is listed afterwards, and one Ctrl+Z if it was wrong. A whole
  // archive is a different animal -- measured on a v232 client, a whole Npc.wz
  // is 10,742 entries and 21,084 written nodes -- and the honest thing at that
  // size is the preview, with its counts and its single overwrite decision,
  // before anything is written. Skill.wz is refused outright at 1,247 MB against
  // the 1,024 MB cap, and the preview is where that refusal can be read.
  // Which archive receives this. A caller that already knows -- because the
  // user dropped onto it -- gets that one; otherwise the one-click path is
  // available only when there is exactly one it could be.
  const chosen = targetFileId
    ? targets.find((t) => t.archive.fileId === targetFileId)
    : (targets.length === 1 ? targets[0] : null);

  // An overwrite never takes the one-click path. The claim that makes one click
  // safe is that nothing it does can destroy something the user had; Replace is
  // exactly that, so it goes to the dialog and the plan.
  if (wholeArchive || !chosen || (overwrite && !directTarget)) {
    return buildDialog({
      app, kind, paths, capabilities, spec, scope, sourceFileId, sourceFileIds,
      targetFileId, overwrite,
    });
  }

  let result;
  try {
    // No acceptDeadCanvasLinks here, and there never can be. That flag says "I
    // have read which archives are missing and I want the dead links anyway",
    // and this path shows no preview at all -- there is nothing for the user to
    // have read. A port refused for dead links comes back through showResult
    // saying so, and the way past it is the dialog, where the plan is on screen.
    result = await api.portApply({
      kind, paths, targetFileId: chosen.archive.fileId,
      overwrite,
      // A direct drop has already run the plan and shown the one conflict
      // decision. Asking again would make the destination gesture meaningless.
      stopOnConflict: !directTarget,
      confirmed: true,
    });
  } catch (error) {
    toastError(error, 'Could not import that');
    return undefined;
  }

  if (result.needsDecision) {
    // The target already has some of these. That is the one thing worth
    // stopping for, and the dialog opens on it with the preview already run.
    return buildDialog({
      app, kind, paths, capabilities, spec, opening: result, sourceFileIds,
      targetFileId: chosen.archive.fileId,
      // The fixed id is rendered as a label, never as a second destination
      // chooser. This fallback can explain an exceptional safety decision,
      // but it cannot reinterpret the user's drop.
    });
  }

  app.markDirty();
  return showResult({ app, spec, result });
}

/** Archives in another client that could receive this kind, and are writable. */
function targetsFor(capabilities, kind, sourceFileIds) {
  const clients = capabilities.clients || [];
  const source = clients.find((c) => c.archives.some((a) => sourceFileIds.has(a.fileId)));

  return clients
    .filter((c) => c.key !== source?.key)
    .flatMap((c) => c.archives
      .filter((a) => (a.kinds || []).includes(kind) && !a.readOnly)
      .map((a) => ({ client: c, archive: a })));
}

/* ============================================================
   THE SETUP, BEFORE ANYTHING IS WRITTEN

   A port needs more than two clients open: it needs the *target* client to
   have, writable, an archive of the kind being ported and the satellite
   archives that hold the name and the sounds. The capabilities call cannot
   answer that, because it does not know which client is the target until a
   selection exists -- so it says "available" for a session that will produce a
   nameless, silent copy.

   Everything below is that check, done in the one place that knows both.
   ============================================================ */

/**
 * What the target client is missing, or null when it can receive this whole.
 *
 * Two grades, and they are not the same problem:
 *
 *   * blocking -- no other client has a writable archive of this kind at all.
 *     There is nothing to port into, and the old behaviour here was to open the
 *     full options dialog with an empty target list and a Preview that only
 *     ever toasted. A dead end with no instruction in it.
 *
 *   * partial -- the target has somewhere to put the entry but not its name or
 *     its sounds. The port would write, report success, and leave a copy that
 *     is nameless in game. Worth stopping for, and worth naming the file.
 */
function setupGap({ capabilities, kind, sourceFileIds, spec, targets }) {
  const clients = capabilities.clients || [];
  const source = clients.find((c) => c.archives.some((a) => sourceFileIds.has(a.fileId)));
  const others = clients.filter((c) => c.key !== source?.key);

  // Which families the target must hold: the kind's own archives, plus one per
  // satellite. Taken from the capabilities catalog rather than hard-coded here,
  // so a kind added on the server does not need this file edited to be checked.
  const satelliteFamilies = [...new Set((spec?.satellites || [])
    .map((s) => archiveFamily(String(s).split('/')[0])))];

  if (!targets.length) {
    // No client can receive it. When there is exactly one other client, its
    // folder is where the missing archive would come from; with several, there
    // is nothing to guess and the picker is the honest answer.
    const client = others.length === 1 ? others[0] : null;
    const families = [...new Set((spec?.archives || []).map(archiveFamily))];
    return {
      blocking: true, client, source,
      families: families.concat(missingFamilies(client, satelliteFamilies)),
      readOnly: others.some((c) => c.archives.some(
        (a) => (a.kinds || []).includes(kind) && a.readOnly)),
    };
  }

  // A missing source family is not proof that the selected entries have no
  // dependency there. Sound, Effect and Etc rows are indexed from outside the
  // entry itself, so the only complete answer is to open the family, inspect it,
  // and carry the rows that actually exist. This is checked before optional
  // target gaps so the import cannot accidentally skip the source
  // inspection as well.
  const sourceFamilies = missingOpenFamilies(source, satelliteFamilies);
  if (sourceFamilies.length) {
    return {
      blocking: true,
      client: source,
      source,
      families: sourceFamilies,
      sourceSide: true,
    };
  }

  if (targets.length === 1) {
    const client = targets[0].client;
    const families = missingFamilies(client, satelliteFamilies);
    if (families.length) return { blocking: false, client, source, families };
  }

  return null;
}

/** Families of `wanted` that the client has no writable archive for. */
function missingFamilies(client, wanted) {
  if (!client) return wanted;
  const open = new Set(client.archives
    .filter((a) => !a.readOnly)
    .map((a) => archiveFamily(a.name)));
  return wanted.filter((family) => !open.has(family));
}

/** Source archives only have to be readable; reference-only is valid here. */
function missingOpenFamilies(client, wanted) {
  if (!client) return wanted;
  const open = new Set(client.archives.map((a) => archiveFamily(a.name)));
  return wanted.filter((family) => !open.has(family));
}

/**
 * Names the missing files, and offers to open them.
 *
 * Resolves to 'opened' when something was opened and the caller should look
 * again, or null to stop. Missing dependencies are never bypassed.
 *
 * The offer is only as good as what is on disk, so the folder is scanned first
 * and the button says how many files and how many megabytes -- opening a
 * client's Sound.wz is a second and a gigabyte, and a button that did that
 * without saying so would be the wrong kind of helpful.
 */
async function offerMissingArchives({ app, spec, gap }) {
  const label = (spec?.plural || 'entries').toLowerCase();
  const candidates = await findArchives(gap.client, gap.families);

  const bytes = candidates.reduce((sum, f) => sum + (f.size || 0), 0);
  // Shadowing the module-level megabytes() helper here would be a trap for
  // the next reader; this one is a number, not a formatter.
  const sizeMb = Math.round(bytes / (1024 * 1024));

  const lines = el('ul', { class: 'port-lines' },
    gap.families.map((family) => {
      const found = candidates.filter((f) => archiveFamily(f.name) === family);
      return el('li', {},
        el('b', { text: found.length ? found.map((f) => f.name).join(', ') : `${family}.wz` }),
        el('span', { text: ' — ' + (ARCHIVE_CARRIES[family]
          ?? `where ${gap.client?.label ?? 'the target'} keeps its ${label}`) }),
        found.length ? null : el('div', { class: 'port-hint', text:
          `Not in ${gap.client?.folder ?? 'that folder'} — open it from wherever that client keeps it.` }));
    }));

  return new Promise((resolve) => {
    let answer = null;
    // The open runs after the dialog has closed -- modal's actions are
    // synchronous -- so the caller must not re-read capabilities until it has
    // finished, or it reads the session as it was before the open and asks the
    // same question again.
    let pending = null;
    const { dialog } = modal({
      title: gap.sourceSide
        ? `${gap.client?.label ?? 'The source'} needs ${fmt.format(gap.families.length)} more source file`
          + `${gap.families.length === 1 ? '' : 's'} for a complete import`
        : gap.blocking
        ? `${gap.client?.label ?? 'The other client'} has nowhere to put ${label}`
        : `${gap.client?.label ?? 'The target'} is missing ${fmt.format(gap.families.length)} file`
          + `${gap.families.length === 1 ? '' : 's'} this import needs`,
      subtitle: gap.client?.folder,
      width: 'min(96vw, 720px)',
      body: el('div', { class: 'port' },
        el('div', { class: 'port-empty', 'data-tone': gap.blocking ? 'bad' : null },
          icon(gap.blocking ? 'alert' : 'info', { size: 15 }),
          el('span', { text: gap.sourceSide
            ? 'MapleBench must inspect every source archive this kind can depend on before it can tell which '
              + 'parts the selection actually uses. Open these read-only; only dependencies that really exist '
              + 'for the selected entries will be copied.'
            : gap.blocking
            ? (gap.readOnly
                ? 'Its archive of this kind is open for reference only, so nothing can be written to it. '
                  + 'Unlock it in the Files panel, or open a writable one.'
                : 'An import writes into an archive that is already open. Until one of these is open there is '
                  + 'nowhere for the copy to go.')
            : 'The copy itself would land, and everything below would not. That is the failure this tool '
              + 'exists to catch, so it is asking first rather than reporting it afterwards.' })),
        lines),
      actions: [
        candidates.length
          ? {
              label: `Open ${fmt.format(candidates.length)} file${candidates.length === 1 ? '' : 's'}`
                + (sizeMb ? ` · ${fmt.format(sizeMb)} MB` : ''),
              class: 'btn-primary',
              run: () => {
                answer = 'opened';
                pending = openArchives(app, candidates);
              },
            }
          : null,
        {
          label: candidates.length ? 'Choose files…' : 'Open a file',
          class: candidates.length ? '' : 'btn-primary',
          run: () => { app.openFilePicker(gap.client?.folder); },
        },
        { label: 'Cancel' },
      ].filter(Boolean),
    });
    dialog.addEventListener('close', async () => {
      try { await pending; } catch { answer = null; }
      resolve(answer);
    }, { once: true });
  });
}

/**
 * The files in the client's own folder that would fill the missing families.
 *
 * Prefers the plain name -- String.wz over String001.wz -- and only falls back
 * to the numbered siblings when there is no plain one, because a client that
 * splits Sound across three 800 MB archives should not have all three opened to
 * find one Mob.img.
 */
async function findArchives(client, families) {
  if (!client?.folder) return [];

  // /api/scan already groups a folder by archive family -- the same grouping
  // the picker shows and the same stem rule the server ports by -- so this asks
  // for the family rather than re-deriving it from file names here.
  let groups;
  try { ({ groups } = await api.scan(client.folder)); } catch { return []; }

  const found = [];
  for (const family of families) {
    const files = (groups || []).find((g) => g.family === family)?.files || [];
    const plain = files.find((f) => f.name.toLowerCase() === `${family.toLowerCase()}.wz`);
    found.push(...(plain ? [plain] : files));
  }
  return found;
}

async function openArchives(app, files) {
  const busy = toast(`Opening ${files.map((f) => f.name).join(', ')}…`, 'info');
  try {
    const result = await api.openMany(files.map((f) => f.path));
    busy.remove();
    for (const failure of result.failed || []) {
      toast(`${failure.name} could not be opened: ${failure.message}`, 'error');
    }
    await app.refreshFiles();
  } catch (error) {
    busy.remove();
    toastError(error, 'Could not open those');
  }
}

/**
 * What landed, after the fact.
 *
 * "1 click" means the user does not have to ask; it does not mean the tool gets
 * to be quiet. A port that silently dropped the String.wz entry is exactly the
 * failure this exists to prevent -- the node arrives, looks right in the
 * Explorer, and is nameless in game.
 */
function showResult({ app, spec, result }) {
  const plan = result.plan || {};
  const missed = shortfall(plan);
  const { dialog } = modal({
    // "Ported into ClientB" over a port whose String.wz part was blocked is the
    // single most expensive lie this screen can tell: the copy is there, it
    // looks right in the Explorer, and it is nameless in game. The headline
    // counts what did not land, not only what threw.
    // A refused plan writes nothing and still arrived here, because the apply
    // route answers with the reason rather than throwing. "Ported into ClientB"
    // over a port that was refused is the same lie as over one that dropped a
    // name, so it gets the same treatment.
    title: plan.blocked
      ? `Nothing was imported into ${plan.targetClient}`
      : missed.length
        ? `Imported into ${plan.targetClient} — ${fmt.format(missed.length)} dependency issue`
          + `${missed.length === 1 ? '' : 's'} remain`
        : `Imported into ${plan.targetClient}`,
    subtitle: `${fmt.format(plan.totals?.entries ?? 0)} ${(plan.totals?.entries === 1 ? spec.label : spec.plural).toLowerCase()}`
      + ` · ${fmt.format(result.written)} node${result.written === 1 ? '' : 's'} written`
      + ` · ${(result.seconds ?? 0).toFixed(1)}s`,
    width: 'min(96vw, 1000px)',
    body: el('div', { class: 'port' }, renderPlan(plan, { app, result })),
    actions: [
      // A refused port wrote nothing, and the archives it wanted are named in the
      // panel above. The picker is the action that answers that panel; Undo is
      // not, and offering it here would undo whatever the user did BEFORE this.
      plan.blocked
        ? { label: 'Open a file', class: 'btn-primary', run: () => app.openFilePicker() }
        : null,
      result.written
        ? { label: 'Undo import', run: () => { app.undo(); } }
        : null,
      { label: 'Done', class: plan.blocked ? '' : 'btn-primary' },
    ].filter(Boolean),
  });
  return new Promise((resolve) => dialog.addEventListener('close', resolve, { once: true }));
}

function buildDialog({
  app, kind, paths, capabilities, spec, opening = null,
  scope = 'selection', sourceFileId = null, sourceFileIds = null,
  targetFileId = null, overwrite: startOverwritten = false,
}) {
  const clients = capabilities.clients || [];
  // 'folder' is a bulk scope like 'archive': thousands of entries, so it
  // gets the preview and the one overwrite decision rather than the
  // one-click path a hand-picked selection uses.
  const wholeArchive = scope === 'archive' || scope === 'folder';

  // Which client the selection came from, worked out the same way the server
  // does it -- by the folder the archives sit in. Shown rather than asked,
  // because the paths already answer it and a second answer that could
  // disagree with them is a second way to get this wrong.
  const sources = sourceFileIds ?? new Set((paths || []).map((p) => p.split('/')[0]));
  const sourceClient = clients.find((c) => c.archives.some((a) => sources.has(a.fileId)));
  const sourceArchive = sourceClient?.archives.find((a) => sources.has(a.fileId));

  /** Archives that could receive this kind: right family, writable, other client. */
  const targets = clients
    .filter((c) => c.key !== sourceClient?.key)
    .flatMap((c) => c.archives
      .filter((a) => a.role === kind && !a.readOnly)
      .map((a) => ({ client: c, archive: a })));

  // The archive the caller already chose, when this list does not contain it.
  //
  // This filters on `role` -- what the port machinery matched an archive AS --
  // while the one-click path filters on `kinds`, everything it COULD hold. The
  // two agree for the ordinary case and can differ for an archive that answers
  // for more than one kind, and the difference must not turn a drop onto a
  // named archive into a port into a different one. So a caller-chosen target
  // is added rather than quietly dropped.
  if (targetFileId && !targets.some((t) => t.archive.fileId === targetFileId)) {
    for (const client of clients) {
      const archive = client.archives.find((a) => a.fileId === targetFileId && !a.readOnly);
      if (archive) { targets.unshift({ client, archive }); break; }
    }
  }

  const targetSelect = el('select', { class: 'mob-select', 'aria-label': 'Where to put them' },
    targets.length
      ? targets.map(({ client, archive }) => el('option', {
          value: archive.fileId,
          text: `${client.label} · ${archive.name}`,
        }))
      : el('option', { value: '', text: 'No other client has a writable archive of this kind' }));

  // Preselected rather than defaulted: a drop already said which archive, and
  // the select is here so it can be read and changed, not so it can decide.
  if (targetFileId && targets.some((t) => t.archive.fileId === targetFileId)) {
    targetSelect.value = targetFileId;
  }
  const fixedTarget = targetFileId
    ? targets.find((target) => target.archive.fileId === targetFileId)
    : null;
  const targetControl = fixedTarget
    ? el('div', { class: 'port-fixed-target' },
        icon('check', { size: 14 }),
        el('b', { text: fixedTarget.client.label }),
        el('span', { text: fixedTarget.archive.name }))
    : targetSelect;

  const overwrite = checkbox('Replace anything the target already has', false,
    'Off by default and never assumed. Losing a mob the target already had is worse than ' +
    'not importing one.');
  overwrite.wrapper.dataset.danger = 'true';
  // Ticked before the user sees it only when they have already made the replace
  // gesture somewhere else. It changes nothing about what happens next: the
  // preview still has to be run and the confirm still has to be answered, and
  // both of those name the count being overwritten.
  if (startOverwritten) overwrite.input.checked = true;

  // The other half of 'replace'.
  //
  // Replacing writes the newer version of everything it carries, but a build
  // removes skills as well as adding them -- so the target keeps the ones the
  // new build dropped, sitting beside the ones it gained, and the book ends up
  // as neither version. This deletes those, so what lands is the newer build's
  // content in the older client's format.
  //
  // Only alongside Replace, because on its own it would delete what it is not
  // allowed to overwrite.
  const match = checkbox('Match the source — remove what the newer version no longer has', false,
    'Only inside the containers this import writes to. An id goes only when the source has ' +
    'that same container and no such id in it, so another book is never touched. One undo ' +
    'puts them back.');
  match.wrapper.dataset.danger = 'true';
  match.input.disabled = true;

  // Kept honest rather than merely documented: unticking Replace turns this
  // off, so the pair can never be left in the state the server refuses.
  const syncMatch = () => {
    match.input.disabled = !overwrite.input.checked;
    if (!overwrite.input.checked) match.input.checked = false;
  };
  overwrite.input.addEventListener('change', syncMatch);
  syncMatch();

  const previewHost = el('div', { class: 'port-preview-host' });
  let plan = null;

  const previewButton = el('button', { class: 'btn' }, icon('eye', { size: 15 }), 'Preview import');
  const applyButton = el('button', {
    class: 'btn btn-primary', disabled: true,
    'data-tip': 'Preview first — nothing is written until you have seen what it would do',
  }, icon('check', { size: 15 }), 'Import');

  /**
   * Any change to the recipe throws the preview away.
   *
   * An Apply that stays live after "Replace anything the target already has" is
   * ticked is an Apply that writes something other than what is on screen --
   * and in that particular case what it writes over is the target's own data.
   */
  const invalidate = () => {
    plan = null;
    applyButton.disabled = true;
    clear(previewHost);
    previewHost.append(el('div', { class: 'port-empty' },
      icon('info', { size: 15 }),
      el('span', { text:
        'Preview to see every file this would touch, what the target already has, and what it ' +
        'cannot carry. Nothing is written until then.' })));
  };
  for (const box of [overwrite]) {
    box.input.addEventListener('change', invalidate);
  }
  targetSelect.addEventListener('change', invalidate);
  invalidate();

  // Opened because a one-click port hit a conflict: the plan is already
  // computed, so show it rather than making the user press Preview to find out
  // what the interruption was about.
  if (opening) {
    plan = opening.plan;
    clear(previewHost);
    previewHost.append(el('div', { class: 'port-empty', 'data-tone': 'bad' },
      icon('alert', { size: 15 }),
      el('span', { text:
        `${fmt.format(plan.totals?.entriesAlreadyThere ?? 0)} of these are already in `
        + `${plan.targetClient} and nothing was written. Select Replace to overwrite them, or import the `
        + 'rest by deselecting those.' })));
    previewHost.append(renderPlan(plan, { app }));
    applyButton.disabled = false;
  }

  const recipe = () => ({
    kind,
    scope,
    // Sent for the scope that uses it and left out of the other, rather than
    // sent empty: the server reads `paths` only for a selection and
    // `sourceFileId` only for an archive, and a request carrying both invites
    // the two to disagree about what is being ported.
    // A folder port needs BOTH: the folders themselves are in `paths`, and the
    // archive they came from is the source. Only a whole-archive port has no
    // paths at all, so keying this on `wholeArchive` -- which now also covers
    // folders -- would send a folder port with nothing to port.
    paths: scope === 'archive' ? [] : paths,
    sourceFileId: scope === 'selection' ? null : sourceFileId,
    targetFileId: targetSelect.value,
    followLinks: true,
    includeArtOutlinks: true,
    overwrite: overwrite.input.checked,
    match: match.input.checked,
    // The public workflow never writes unresolved art or missing map assets.
    acceptDeadCanvasLinks: false,
    acceptMissingNames: false,
    // Off from this dialog, always.
    //
    // stopOnConflict is the server's interrupt for the ONE-CLICK path: it exists
    // so a port that would meet something the target already has comes back and
    // asks instead of writing. By the time this recipe runs, that has already
    // happened -- the user is looking at the preview, the "already there" count
    // is a tile on screen, and the confirm they just answered named the number
    // being left alone. Leaving it on made the apply return needsDecision a
    // second time and write NOTHING, so a port with one conflict and thirty new
    // entries reported "0 written" over a plan that said one would be copied.
    // Measured: two Mushmoms into ClientB, 4 entries, 1 to copy, 3 already
    // there -- 0 written, undo entry none.
    stopOnConflict: false,
  });

  let busy = false;
  /**
   * The panel that says something is happening, with the seconds counting.
   *
   * Apply had nothing at all. It disabled both buttons, closed the confirm
   * dialog and awaited in silence, so a port that took a while was
   * indistinguishable from one that had hung -- and the honest response to
   * that is to close the window, which is what happened. The elapsed count is
   * the part that matters: a number that moves is the difference between
   * "slow" and "stuck", and only one of those is worth killing.
   */
  const working = (text) => {
    clear(previewHost);
    const elapsed = el('span', { class: 'port-elapsed', text: '0s' });
    previewHost.append(el('div', { class: 'port-empty' },
      el('span', { text }), elapsed));
    const started = performance.now();
    const tick = setInterval(() => {
      elapsed.textContent = ` ${Math.round((performance.now() - started) / 1000)}s`;
    }, 500);
    return () => clearInterval(tick);
  };

  const run = async (fn) => {
    // Local rather than runOnce's shared set: the two buttons are exclusive of
    // each other, and an apply issued twice would port twice.
    if (busy) return;
    busy = true;
    previewButton.disabled = true;
    applyButton.disabled = true;
    try { await fn(); } finally { busy = false; previewButton.disabled = false; }
  };

  /**
   * Runs the plan and draws it. Named rather than inline because the override
   * has to run it a second time: accepting dead canvas links changes what the
   * server would write, and an Apply armed off the plan that was REFUSED would
   * write something nobody has seen. See acceptDeadLinks.
   */
  const preview = async () => {
    if (!targetSelect.value) {
      toast('There is no other client open with a writable archive to import into.', 'warning');
      return;
    }
    const stop = working('Working out what would be written…');
    try {
      plan = await api.portPlan(recipe());
      stop();
      clear(previewHost);
      showUsedArchives(plan);
      previewHost.append(renderPlan(plan, { app }));
      applyButton.disabled = plan.blocked || (plan.totals?.willWrite ?? 0) === 0;
      applyButton.dataset.tip = applyButton.disabled
        ? 'Nothing here would be written'
        : `Write ${fmt.format(plan.totals.willWrite)} file entries`;
    } catch (error) {
      plan = null;
      clear(previewHost);
      previewHost.append(el('div', { class: 'port-empty', 'data-tone': 'bad' },
        icon('alert', { size: 15 }), el('span', { text: error.message })));
    } finally {
      stop();
    }
  };

  previewButton.addEventListener('click', () => run(preview));

  // Two different questions, and the header used to answer the wrong one.
  //
  // It listed every archive open in the source client. Opening Sound.wz to
  // find out whether an item HAS a sound therefore made the header say the
  // sound was coming -- shown as "From Character.wz, String.wz, Sound.wz,
  // Etc.wz" for an item that exists in two of them. Before a preview there is
  // nothing to say but what is open, so it says so in those words. Once a plan
  // exists it switches to the archives the plan actually found something in.
  const fromArchives = el('span', { class: 'port-end-sub', text: sourceClient
    ? `Open: ${sourceClient.archives.map((a) => a.name).join(' · ')}`
    : '' });

  const showUsedArchives = (result) => {
    if (!sourceClient) return;
    const used = [...new Set((result?.items || [])
      .flatMap((item) => item.parts || [])
      .filter((part) => part.sourceArchive && part.status !== 'Absent')
      .map((part) => part.sourceArchive))];
    fromArchives.textContent = used.length
      ? `Using: ${used.join(' · ')}`
      : `Open: ${sourceClient.archives.map((a) => a.name).join(' · ')}`;
  };

  const { dialog, close } = modal({
    title: wholeArchive
      ? `Import every ${spec.label.toLowerCase()} in ${sourceArchive?.name ?? 'the archive'}`
      : `Import ${fmt.format(paths.length)} ${paths.length === 1 ? spec.label.toLowerCase() : spec.plural.toLowerCase()}`,
    subtitle: wholeArchive
      // Named as a preview rather than as a run. At this size the Preview button
      // is not a formality the user may skip -- it is the only thing between them
      // and thousands of writes, and the button below stays dead until it is
      // pressed.
      ? `from ${sourceClient?.label ?? 'the source client'} — preview it before anything is written`
      : sourceClient
        ? `from ${sourceClient.label}`
        : 'from the archives the selection came from',
    width: 'min(96vw, 1000px)',
    body: el('div', { class: 'port' },
      el('div', { class: 'port-route' },
        el('div', { class: 'port-end' },
          el('span', { class: 'port-end-label', text: 'From' }),
          el('b', { text: sourceClient?.label ?? 'the selection' }),
          fromArchives),
        el('span', { class: 'port-arrow' }, icon('arrowRight', { size: 18 })),
        el('div', { class: 'port-end' },
          el('span', { class: 'port-end-label', text: 'Into' }),
          targetControl,
          el('span', { class: 'port-end-sub', text: intoNeeds(spec) }))),
      el('div', { class: 'port-options' },
        overwrite.wrapper,
        wholeArchive ? null : match.wrapper),
      wholeArchive
        ? el('div', { class: 'port-empty' }, icon('info', { size: 15 }),
            el('span', { text:
              'Everything of this kind in the archive comes across, so there is nothing for the link '
              + 'switches to add — what one entry borrows from another is already in the import. Preview '
              + 'first: it counts what is new, what the target already has and what cannot be carried, '
              + 'and it is where an import too big to run says so.' }))
        : null,
      el('div', { class: 'port-actions' }, previewButton, applyButton),
      previewHost),
  });

  applyButton.addEventListener('click', () => run(async () => {
    if (!plan) return;
    const willWrite = plan.totals?.willWrite ?? 0;
    const conflicts = plan.totals?.conflicts ?? 0;

    const ok = await confirmDialog({
      title: `Import ${fmt.format(plan.totals.entries)} ${plan.totals.entries === 1 ? spec.label.toLowerCase() : spec.plural.toLowerCase()} into ${plan.targetClient}?`,
      message:
        `${fmt.format(willWrite)} ${willWrite === 1 ? 'entry' : 'entries'} will be written` +
        (overwrite.input.checked && conflicts
          ? `, and ${fmt.format(conflicts)} of them REPLACE what ${plan.targetClient} already has. ` +
            'That data is recoverable with one undo until you save, and not after.'
          : conflicts
            ? `. ${fmt.format(conflicts)} the target already has are being left alone.`
            : '.') +
        ' Nothing reaches disk until you save.',
      confirmLabel: 'Import',
      danger: overwrite.input.checked && conflicts > 0,
    });
    if (!ok) return;

    // Named for what it is doing and roughly how big it is, because "please
    // wait" on a job that may run for a minute tells the user nothing they can
    // act on.
    const stop = working(
      `Importing ${fmt.format(willWrite)} ${willWrite === 1 ? 'entry' : 'entries'} into `
      + `${plan.targetClient}. Art is copied frame by frame, so a boss takes longer than an item —`);
    try {
      const result = await api.portApply({ ...recipe(), confirmed: true });
      stop();
      clear(previewHost);
      previewHost.append(renderPlan(result.plan, { app, result }));
      applyButton.disabled = true;

      app.markDirty();
      // Same rule as the result dialog's headline: a toast that says "Ported"
      // over a port that dropped the names is the version of this the user
      // actually reads, because the dialog is still open behind it.
      const missed = shortfall(result.plan);
      toast(
        missed.length
          ? `Imported ${fmt.format(result.written)} entries; ${fmt.format(missed.length)} dependency issue`
            + `${missed.length === 1 ? '' : 's'} remain — the panel lists them.`
          : `Imported ${fmt.format(result.written)} entries into ${result.plan.targetClient}. Save each archive to keep them.`,
        missed.length ? 'warning' : 'success',
        result.written
          ? { action: { label: 'Undo import', run: async () => { await app.undo(); close(); } } }
          : undefined);
    } catch (error) {
      clear(previewHost);
      previewHost.append(el('div', { class: 'port-empty', 'data-tone': 'bad' },
        icon('alert', { size: 15 }), el('span', { text: error.message })));
      toastError(error, 'Could not import that');
    } finally {
      stop();
    }
  }));

  return new Promise((resolve) => dialog.addEventListener('close', resolve, { once: true }));
}

/* ============================================================
   THE PREVIEW
   ============================================================ */

/**
 * @param plan               the plan to draw
 * @param app                the App, for "show me this in the Explorer"
 * @param result             the apply result, when this is being drawn after one
 */
function renderPlan(plan, { app, result } = {}) {
  const host = el('div', { class: 'port-plan' });

  // Above everything, including a refusal: the refusal says the port cannot be
  // done, and this says which files would let it be done.
  const alarm = openTheseArchives(plan);
  if (alarm) host.append(alarm);

  if (plan.blocked) {
    host.append(el('div', { class: 'port-empty', 'data-tone': 'bad' },
      icon('alert', { size: 15 }), el('span', { text: plan.blockedReason })));
    return host;
  }

  const totals = plan.totals || {};

  if (result) {
    const missed = shortfall(plan);

    // The green banner is an assertion either way. With a shortfall it names
    // every part that did not land, in the words of the thing the player would
    // miss; with none it says so in as many words, so that a quiet banner is a
    // claim someone made rather than an absence of news.
    host.append(el('div', { class: 'port-result', 'data-tone': missed.length ? 'warn' : 'good' },
      icon(missed.length ? 'alert' : 'check', { size: 16 }),
      el('div', {},
        el('b', { text: `${fmt.format(result.written)} written` +
                        (result.failed ? `, ${fmt.format(result.failed)} failed` : '') }),
        missed.length
          ? el('ul', { class: 'port-lines' }, missed.map((line) => el('li', { text: line })))
          : el('div', { text: 'Every part the source had for these was written — the entries, and each '
                            + 'name, sound and listing that existed to carry. Review the import boundaries '
                            + 'below, before you call it done.' }),
        el('div', { class: 'port-hint', text:
          `Undo entry: ${result.undoLabel ?? 'none — nothing was written'}. ` +
          'Every archive below is now unsaved; save each one to keep the import.' }))));
  }

  // Zeros are dropped -- by statChip now, which is where the rest of the app
  // gets the same rule from.
  //
  // "Blocked 0 · Not in source 0 · Identical 0" is three tiles of reassurance
  // nobody asked for, sitting beside the two numbers that actually decide
  // whether to press the button. Importing something new out of a newer client
  // -- the ordinary case -- makes all three zero every time, so they are pure
  // furniture exactly when the screen is busiest. A number appears here when it
  // has something to say; the two that frame the decision stay even at zero.
  host.append(el('div', { class: 'port-totals' },
    statChip('Entries', totals.entries ?? 0, { drop: false }),
    statChip('To copy', totals.new ?? 0, { tone: 'good', drop: false }),
    statChip('Already there', totals.conflicts, { tone: 'warn' }),
    statChip('Blocked', totals.blocked, { tone: 'bad' }),
    statChip('Identical', totals.identical)));
  // "Not in source" is gone entirely, at any value.
  //
  // Dropping the zero was not enough. Porting Zakum into a client that has
  // never heard of it -- the case this feature exists for -- still reported
  // "NOT IN SOURCE 3", because neither client has a MobVoice entry for any of
  // the three mobs. Nothing is missing: there was nothing to carry. The number
  // can only ever prompt a hunt for a loss that did not happen, and the parts
  // it counts are already summarised as "needed nothing doing" on the entry
  // that owns them, where they can be read in context.

  /* --- what could go wrong in game --- */
  //
  // Only the observations. The instructions -- "open Skill003.wz" -- are in the
  // red block at the top instead of being the fourth bullet of an amber list
  // whose first three are about version stamps.
  const { rest } = splitWarnings(plan);
  if (rest.length) {
    host.append(section('Read before you apply', 'alert', 'warn', rest));
  }

  /* --- per entry --- */

  // Said above the cards, not below them.
  //
  // The server caps this list -- a 4,067-entry port sends 200 -- and computed
  // `itemsTruncated` from the very first version, put it on the wire, and
  // rendered it in exactly one place: inside shortfall(), which only runs after
  // an apply and only when something else already went wrong. So the preview,
  // which is the whole feature, showed 200 cards for 4,067 entries and read as
  // complete. Someone scrolling a list they believe is the whole port and
  // finding the mob they were worried about absent from it has been told a
  // falsehood by omission at the exact moment they were checking.
  //
  // Above rather than below because 200 cards is more than anyone scrolls to
  // the end of, and this is the sentence that decides whether the list below it
  // means anything.
  const truncated = plan.itemsTruncated ?? 0;
  if (truncated > 0) {
    host.append(el('div', { class: 'port-empty', 'data-tone': 'warn' },
      icon('info', { size: 15 }),
      el('span', { text:
        `Showing ${fmt.format((plan.items || []).length)} of ${fmt.format(totals.entries ?? 0)} entries. `
        + `The other ${fmt.format(truncated)} are in the import and in the counts above — they are not `
        + 'listed because nobody can read four thousand rows. Use the selection or folder scope if you '
        + 'need to check each one.' })));
  }

  const list = el('div', { class: 'port-items' });
  for (const item of plan.items || []) list.append(renderItem(item, plan, app));
  host.append(list);

  /* --- suggestions, clearly labelled as guesses --- */
  if (plan.suggestions?.length) {
    const rows = el('div', { class: 'port-suggestions' });
    for (const suggestion of plan.suggestions.slice(0, 40)) {
      rows.append(el('div', { class: 'port-suggestion' },
        el('b', { text: String(suggestion.id) }),
        el('span', { text: suggestion.name || 'no name in String.wz' }),
        el('span', { class: 'port-hint', text: suggestion.reason }),
        app
          ? el('button', {
              class: 'btn btn-sm',
              onclick: () => { app.setMode('explorer'); app.navigate(suggestion.path); },
            }, icon('externalLink', { size: 13 }), 'Show')
          : null));
    }
    host.append(el('details', { class: 'port-section', 'data-tone': 'muted' },
      el('summary', {},
        icon('help', { size: 15 }),
        el('span', { text: `${fmt.format(plan.suggestions.length)} nearby ids you may also want` }),
        el('span', { class: 'port-hint', text: 'suggestions — nothing here is selected or copied' })),
      rows));
  }

  /* --- the boundaries of this import. Always shown. --- */
  host.append(section('What this does not do, in any client', 'info', 'muted', plan.limits || [], true));

  return host;
}

/**
 * Everything the port did not carry, as sentences, from the plan's own statuses.
 *
 * Read off `status` and `error` rather than off the prose notes, because a
 * summary that has to guess what a sentence meant is a summary that will get it
 * wrong quietly. The counts come from `totals`, which covers the entries the
 * plan only counted; the reasons come from the listed items, which is a sample
 * at archive scale and says so.
 *
 * "Absent" is deliberately not in here. A mob with no sound in the source has
 * nothing missing -- there was nothing to carry -- and listing it would train
 * people to ignore this block, which is the only way it can fail.
 */
function shortfall(plan) {
  const totals = plan.totals || {};
  const items = plan.items || [];
  const parts = items.flatMap((item) => item.parts || []);
  const lines = [];

  const failed = parts.filter((part) => part.error);
  if (failed.length) {
    lines.push(`${fmt.format(failed.length)} part${failed.length === 1 ? '' : 's'} could not be written: `
      + [...new Set(failed.map((part) => part.error))].slice(0, 3).join(' · '));
  }

  // Blocked, grouped by what the player loses. The reason is the server's and
  // is actionable ("open that client's String.wz"), so it is quoted rather than
  // summarised away.
  const blocked = parts.filter((part) => part.status === 'Blocked');
  const byKind = new Map();
  for (const part of blocked) {
    if (!byKind.has(part.kind)) byKind.set(part.kind, []);
    byKind.get(part.kind).push(part);
  }
  for (const [kind, group] of byKind) {
    const what = PART_CARRIES[kind] || kind;
    lines.push(`${what} was not carried for ${fmt.format(group.length)} of them. ${group[0].reason ?? ''}`.trim());
  }

  // A conflict left alone is the right outcome and still a thing that did not
  // land: the target kept its own version, and someone who read "Ported" and
  // stopped would never know their newer copy is not in there.
  const kept = parts.filter((part) => part.status === 'Conflict' && part.applied !== true);
  if (kept.length) {
    lines.push(`${fmt.format(kept.length)} part${kept.length === 1 ? '' : 's'} were left as `
      + `${plan.targetClient} already had them — nothing is replaced unless you tick Replace. `
      + 'The table below shows what it currently holds for each.');
  }

  // The notes themselves, not a count of them.
  //
  // This used to say "4 entries carry notes below about art or ids this could
  // not follow", then reassure that they are "things nothing here can do for
  // you" -- which announced a problem, denied it was one, and never said what
  // it was, leaving the reader to hunt four cards out of seventeen. The claim
  // was also untrue: one of these notes ends "add it to the selection if the
  // target client does not already have it", which is an instruction. Quoted
  // the way a Blocked reason is, for the same reason.
  const noted = items.filter((item) => item.notes?.length);
  if (noted.length) {
    const all = noted.flatMap((item) => item.notes);
    const distinct = [...new Set(all)];
    lines.push(distinct[0]);
    if (distinct.length > 1) {
      lines.push(`${fmt.format(distinct.length - 1)} other note`
        + `${distinct.length === 2 ? '' : 's'} like it, across `
        + `${fmt.format(noted.length)} ${noted.length === 1 ? 'entry' : 'entries'} — each one is on its card below.`);
    }
  }

  if (lines.length && (plan.itemsTruncated ?? 0) > 0) {
    lines.push(`Only ${fmt.format(items.length)} of ${fmt.format(totals.entries ?? 0)} entries are listed, `
      + `so the reasons above are a sample. The counts above them are not: ${fmt.format(totals.blocked ?? 0)} `
      + 'parts were blocked in total.');
  }

  return lines;
}

function renderItem(item, plan, app) {
  const card = el('div', { class: 'port-item' });

  card.append(el('div', { class: 'port-item-head' },
    el('div', {},
      el('span', { class: 'port-item-name', text: item.name || `${plan.label} ${item.id}` }),
      el('span', { class: 'port-item-id', text: item.id ? `ID ${item.id}` : item.sourcePath })),
    item.requested
      ? null
      : el('span', { class: 'port-badge', 'data-kind': 'link',
                     text: item.pulledInBy ? `added: ${item.pulledInBy}` : 'added by a link' })));

  // Only the parts that are telling you something.
  //
  // This listed every part, and for the ordinary case -- taking something
  // genuinely new out of a newer client -- most of them said nothing worth
  // reading: "not in the source" for a mob whose source has no voice clip,
  // "already identical" for a sound both clients share. Neither is a loss and
  // neither needs a decision.
  //
  // That is not a cosmetic problem. A table where most rows do not matter is a
  // table nobody reads, and the one Blocked row is the one that had to be read.
  // What is kept is what landed, what clashed, and what could not be done.
  const quiet = new Set(['Absent', 'Same']);
  const parts = item.parts || [];
  const worth = parts.filter((part) => part.error || !quiet.has(part.status));
  const dull = parts.filter((part) => !worth.includes(part));

  const rows = el('table', { class: 'port-parts' },
    el('tbody', {}, worth.map((part) => renderPart(part, app))));
  card.append(rows);

  // Counted in one line, with a way to see them. Deliberately not a warning:
  // there is nothing here to act on.
  if (dull.length) {
    const extra = el('tbody');
    const label = el('span', { text: `${fmt.format(dull.length)} needed nothing doing` });
    const toggle = el('button', {
      class: 'port-quiet',
      'data-tip': 'The source had none, or the target already matches. Nothing was lost.',
      onclick: () => {
        const showing = extra.childElementCount > 0;
        clear(extra);
        if (!showing) for (const part of dull) extra.append(renderPart(part, app));
        label.textContent = showing ? `${fmt.format(dull.length)} needed nothing doing` : 'hide';
      },
    }, icon('info', { size: 12 }), label);
    rows.append(extra);
    card.append(toggle);
  }

  // A note that says an image cannot be found is toned like the block at the top
  // that summarises it, so that finding this card from that summary is one look
  // rather than a read of every note on it. The rest stay grey: "its art links
  // out to something in this same client" is a remark, not a problem.
  for (const note of item.notes || []) {
    const wanted = NOTE_WANTS_AN_ARCHIVE.test(note);
    card.append(el('div', { class: wanted ? 'port-note notice' : 'port-note',
                            'data-tone': wanted ? 'warn' : null },
      icon(wanted ? 'alert' : 'info', { size: 13 }), el('span', { text: note })));
  }
  return card;
}

function renderPart(part, app) {
  const status = part.status === 'New' && part.willWrite !== true
    ? { label: 'will not be copied', tone: 'muted' }
    : (STATUS[part.status] || { label: part.status, tone: 'muted' });

  // The result of an apply overrides the plan's own status: a row that said
  // "will be copied" and then failed must not still read as a success.
  const applied = part.applied === true;
  const failed = Boolean(part.error);

  // A written row's target path is the answer to "where did it go", and until
  // now it was grey text you had to retype into the tree. Only on rows that
  // were actually written: a path that does not exist yet is not somewhere to
  // send anyone.
  const where = applied && part.targetPath && app
    ? el('button', {
        class: 'port-path',
        'data-tip': 'Show this in the Explorer',
        // The dialog closes first. Navigating underneath a modal that is still
        // up moves the tree where the user cannot see it, which reads as a
        // button that does nothing.
        onclick: (event) => {
          event.target.closest('dialog')?.close();
          app.setMode('explorer');
          app.navigate(part.targetPath);
        },
      }, icon('externalLink', { size: 11 }), el('span', { text: part.targetPath }))
    : part.targetPath
      ? el('div', { class: 'port-hint', text: part.targetPath })
      : null;

  return el('tr', {
    'data-tone': failed ? 'bad' : applied ? 'good' : status.tone,
    'data-write': part.willWrite ? 'true' : null,
  },
    el('td', { class: 'port-part-icon' }, icon(PART_ICON[part.kind] || 'file', { size: 14 })),
    el('td', { class: 'port-part-label' },
      el('div', { text: part.label }),
      // Only where the number is a surprise, which the server decides by
      // setting it at all. A map is 51 KB and one of the pictures it names is
      // 21.7 MB, so a row that says only "Obj/acc1.img" understates what the
      // user is about to move by five hundred times.
      part.bytes > 0
        ? el('div', { class: 'port-hint', text: megabytes(part.bytes) })
        : null,
      where),
    el('td', { class: 'port-part-status' },
      el('span', { class: 'port-chip', 'data-tone': failed ? 'bad' : applied ? 'good' : status.tone,
                   text: failed ? 'failed' : applied ? 'copied' : status.label })),
    el('td', { class: 'port-part-note' },
      part.error ? el('div', { class: 'port-hint', 'data-tone': 'bad', text: part.error }) : null,
      part.existing ? el('div', { class: 'port-hint', text: `target holds: ${part.existing}` }) : null,
      part.reason ? el('div', { class: 'port-hint', text: part.reason }) : null));
}

/* ============================================================
   SMALL PARTS
   ============================================================ */

/**
 * A byte count as something a person can weigh a decision against.
 *
 * KB below a megabyte on purpose: a v232 map image is 51,482 bytes and the
 * object book it names is 21,732,097, and rendering the first as "0 MB" hides
 * exactly the comparison this number exists to make.
 */
function megabytes(bytes) {
  if (bytes >= 1024 * 1024) return `${fmt.format(Math.round(bytes / (1024 * 1024)))} MB`;
  if (bytes >= 1024) return `${fmt.format(Math.round(bytes / 1024))} KB`;
  return `${fmt.format(bytes)} bytes`;
}

function section(title, glyph, tone, lines, collapsed = false) {
  const body = el('ul', { class: 'port-lines' }, lines.map((line) => el('li', { text: line })));
  const node = el('details', { class: 'port-section', 'data-tone': tone, open: !collapsed });
  node.append(
    el('summary', {}, icon(glyph, { size: 15 }), el('span', { text: title }),
      el('span', { class: 'port-hint', text: `${lines.length}` })),
    body);
  return node;
}

function checkbox(label, checked, hint) {
  const input = el('input', { type: 'checkbox', checked });
  const wrapper = el('label', { class: 'port-option' },
    input,
    el('span', {},
      el('span', { class: 'port-option-label', text: label }),
      hint ? el('span', { class: 'port-hint', text: hint }) : null));
  return { input, wrapper };
}
