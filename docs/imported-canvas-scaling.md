# Imported equipment and legacy scaling

Verified 2026-11-12 against Daruma's shipped v232 engine on Mac Wine 11.18.

ARGB1555 (WZ format257) can decode, lock, copy and draw as a standalone layer successfully, yet become entirely transparent when `IWzCanvas::CopyEx` scales it to 2x. The call returns success. Presence in an archive or a successful preview therefore does not establish compatibility.

Native tests reproduced the boundary with Robo-Kitty Hoodie (1054714) and Speed-o'-Sound Sonic Ninja Sword (1103953). The actual game's icon getter returned nonempty raw canvases. At2x the originals had zero visible pixels; conversion before scaling restored2672 and1020 respectively. An older overall control (1052038, format1) retained2400 pixels both ways. The hoodie's regular format2 inventory icon already worked.

PortService now expands format257 to format2 (BGRA32) before ordinary format retargeting, even when the sampled target vocabulary is empty or contains257. It also normalizes257 selected by the automatic PNG encoder. Conversion expands5-bit colors to8-bit and preserves binary alpha, dimensions and canvas metadata/links, without System.Drawing or lossy recompression. Other formats retain the existing policy. Nonzero magnification refuses conversion rather than guessing its storage geometry. Failures remain reported by the existing refused-frame mechanism; this does not establish universal newer-client compatibility.

`WzPngProperty.NormalizeForLegacyScaling()` is idempotent and performs conversion only after decoding and encoding succeed. Run the release build and `tests/ImportRegression`; current42 checks pass, including empty/contaminated vocabulary and link preservation. The regression first failed before implementation. The native scaling reproduction is separate from managed tests.

A private repair candidate for the installed Character.wz baseline MD5 `0a8c75958f0d75d6403f5e69b5cc3866` converted55,539 canvases in663 equipment images. Reopening verified all663 repaired image serializations and63,240 byte-identical untouched images. Native2x scaling of all663 raw icons passed:660 nonblank and3 existing linked placeholders. This is the repair scope, not a claim every one of those items had been independently reported broken.

The candidate is installed only in the owner's local Mac test client with its original archive backed up. After relaunch the owner reported “seems to work”; the game remained in-world. Exact tooltip/worn actions were owner-reported, not independently captured. This editor correction and the Character.wz repair are separate releases. Pixel changes require ordinary archive publication with managed binary deltas, not a Data.wz scalar overlay; the editor executable does not publish game assets or activate a server.

## Editor release validation

The five-file importer correction was independently reviewed on 2026-11-12. Release builds on macOS and isolated Windows completed with zero warnings/errors; all 42 ImportRegression checks passed on both. The existing `scripts/publish.ps1 -SingleFile` smoke test passed from an otherwise empty directory: UI page, JavaScript, stylesheet and both JSON endpoints with their expected fields. No additional source defect was found. Version `1.0.1-importfix.20261112` is assigned to the reviewed editor prerelease; its GitHub release records the exact source commit, executable SHA-256 and build provenance. Broader future-client compatibility and interactive Windows editor use are not implied by these checks.
