# Repository format 4

This is the active Fluff-History storage specification. Codecs in
`FluffHistory.Storage` and frozen models in `FluffHistory.Core` are the implementation
authority; regression tests validate bindings, ordering, malformed input, publication
and recovery. These contracts define the current storage format.

Numeric development formats 2 and 3, foreign product headers and compression-only
Format-4 headers without explicit bootstrap encryption schema are unsupported.
No numeric-format migration, relabeling or dual writer is supplied. Encryption
conversion changes physical representation within Format 4, not logical history.

## Layout and authority

```text
repository.json                    # product/mode/unlock bootstrap, installed last on creation
writer.lock                        # persistent exclusive-handle marker; never stolen/recreated
state.json                         # sole mutable history-authority pointer
state.<transaction UUID N>.tmp      # non-authoritative pending publication
prepare.<transaction UUID N>.root   # candidate-bound prepared root body
metadata/prepare.<transaction UUID N>.<segment UUID N>.tmp
metadata/<segment UUID N>.seg       # independently encoded metadata blocks
versions/<first two UUID hex>/<version UUID N>.bin
staging/<writer-owner UUID N>/<version UUID N>.part
cache/reusable-payloads/            # optional exact-provenance unpublished-copy cache
onedrive-account-recovery.json      # optional non-authoritative protected recovery half
```

Generated UUID filenames use lowercase N form. Logical source names never become
physical payload paths. Ordinary open never initializes, repairs or adopts unknown
files. Missing header is undiscovered; malformed/unsupported recognized data fails.
Creation requires an absent/effectively empty admitted destination and installs the
header only after initialized state/metadata. A narrow local-volume-root OS-metadata
exception does not permit repurposing unrelated content.

State/candidate canonical JSON field order is `formatVersion`, `repositoryId`,
`epoch`, `generation`, `mutationSequence`, `transactionId`, `root`, `sha256`.
Root address fields are `segmentId`, `segmentOrdinal`, `offset`, `length`, `kind`,
`serial`, `sha256`, `blockOffset`. The state checksum covers canonical bytes with
the outer checksum empty; encrypted mode seals those bytes. Root/frame binding
checks repository/epoch/generation/sequence/transaction and committed frontier.
JSON uses minified UTF-8 without BOM, camelCase, strict round-trip canonical encoding,
lowercase D UUIDs, uppercase hash hex and UTC DateTimeOffset representation.
Missing/duplicate/unknown/noncanonical fields fail before authority is accepted.

Recovery-password rotation and current-user DPAPI enrollment replace only bootstrap
wrappers under exclusive writer ownership with exact predecessor/readback checks.
OneDrive recovery is a separate bounded canonical sidecar; legacy
`microsoft-account-recovery.json` is cleanup-only. Neither sidecar authorizes history.
The fenced marker, source/target state, payload verification and authenticated Ready
protocol for resumable conversion are described in
[security and recovery](SECURITY-AND-RECOVERY.md). Do not manually edit controls.

## Primitive encodings and finite limits

All binary numeric fields are little-endian, except the ordered key suffix below.
UUID fields use RFC/network byte order (the .NET `bigEndian: true` form). SHA-256
digests occupy 32 raw bytes. Strings use strict UTF-8 without BOM, prefixed by an
Int32 byte length; malformed UTF-8 and trailing bytes fail. Nullable addresses use
one byte, 0 for null or 1 followed by the address. No other flag is valid.

| Limit | Frozen value |
|---|---:|
| Segment header | 160 bytes |
| Frame header | 96 bytes |
| Address | 88 bytes |
| Index page body | 16,384 bytes |
| Entries per page | 128 maximum, also limited by encoded bytes |
| Tree levels | 16 maximum (leaf level 0) |
| Frame body | 1,048,576 bytes maximum |
| Segment logical length | 67,108,864 bytes maximum (64 MiB) |
| Seal frame | 112 bytes including header |
| Small JSON file | 8,192 bytes maximum |
| Logical path | 2,048 UTF-16 code units maximum |
| Largest index key | 4,126 bytes |
| Mutation requests / outstanding prepared versions | 4,096 per batch/session |
| Source exclusions | 256 distinct entries; aggregate config text 256 KiB |
| Source descriptive location | 32,760 UTF-16 units; 131,040 encoded bytes maximum |
| Reader caches | 64 pages per index, 8 segment handles |

Lengths are checked before allocation/traversal. Appended readback buffering is
bounded by 4096 frames or 8 MiB plus the last bounded frame. Maintenance defaults
to 2,000,000 semantic records, 1,000,000 distinct membership/config keys, and 1 GiB
of newly appended metadata. Callers can set explicit budgets. Budget failure
leaves prior authority valid and unpublished evidence intact. Output space is
checked before appending (including rotation reserve); no implicit maintenance
runs on opening or no-op work. Disk-full errors fail closed. Budget counts are
engineering bounds, not a promise of measured peak RAM or free-space availability.

## Logical keys and comparator

The canonical fold is the checked-in `src/FluffHistory.Core/OrdinalCaseMap.txt`: 1448 scalar
mappings, SHA-256
`2AEE4E3EDD477A245CF1F1636F32240D7EF2AC1C8B07EC3C897AC20B199D7E9C`.
It freezes the .NET 10 ordinal-insensitive equivalence mapping for Unicode 15.0,
matching the frozen comparator contract. Newer Unicode 16 mappings seen on the development
host are deliberately excluded; they must not change existing Format-4 keys.
Comparator tests check every mapping and explicit difficult pairs. Runtime casing,
culture, normalization and a future Unicode table cannot change persisted keys.
Unlisted scalars remain unchanged. No NFC/NFD equivalence is introduced. Unpaired
surrogates, traversal, backslashes, device names, trailing-dot/space aliases and
other prohibited Windows logical names are rejected.

Current key = root UUID (16 RFC bytes) + folded path (UTF-16 **big-endian**) +
`00 00`. History key appends positive sequence (Int64 big-endian) and ordinal
(Int32 big-endian, 0..4095). Source key is the 16-byte root UUID. Compare keys by
unsigned lexicographic byte order. Keys preserve separator boundaries: `Foo`,
`Foo2`, and `Foo/Bar` are distinct; folder ranges use `FOO/`, not bare `FOO`.
Original path spelling remains in each version record. Different root UUIDs
remain independent. `é/É`, `σ/ς`, and Deseret upper/lower pairs match;
`ſ/S`, `ı/I`, `K/K`, and `ß/ẞ` do not. Prefix upper bounds increment the final
non-255 byte and truncate; exact-path history uses its terminated current key.


## Logical segment and frame headers

Segment header offsets:

| Offset | Field |
|---:|---|
| 0 | 8-byte ASCII `FBV2SEG` followed by NUL |
| 8 | Int32 schema 4 |
| 12 | Int32 header length 160 |
| 16 | repository UUID |
| 32 | segment UUID |
| 48 | epoch UUID |
| 64 | Int64 positive segment ordinal |
| 72 | previous segment UUID (empty at ordinal 1) |
| 88 | Int64 previous ordinal (zero at ordinal 1, otherwise ordinal - 1) |
| 96 | 32 reserved zero bytes |
| 128 | SHA-256 of bytes 0..127 |

Frame header offsets:

| Offset | Field |
|---:|---|
| 0 | 8-byte ASCII `FBV2FRM` followed by NUL |
| 8 | Int32 kind: semantic 1, page 2, root 3, seal 4, retired 5 |
| 12 | Int32 bounded body length |
| 16 | Int64 positive serial |
| 24 | repository UUID |
| 40 | segment UUID |
| 56 | Int64 own logical offset |
| 64 | SHA-256 over header bytes 0..63 followed by exact body |
| 96 | body |

Address field order: segment UUID, Int64 segment ordinal, Int64 offset, Int32
body length, Int32 kind, Int64 serial, SHA-256, Int64 physical BlockOffset. Addresses bind exact type, extent,
identity and content. Offsets cannot precede the segment header or exceed segment
limits; pointers cannot exceed the captured frontier/serial. Typed backward
pointers require decreasing serials. No filenames or arbitrary paths appear in
addresses. Required segment headers and accessed closed-segment seals validate;
unvisited frames/payloads do not get scanned.


## Semantic record bodies

Every semantic body starts with a kind byte and nullable previous **semantic** address. Previous pointers must be within the captured frontier and decrease in serial. The closed set is:

1. **VersionAdded**: UUID; Int64 original mutation sequence; Int32 original ordinal; Int64 StoredUtc UTC ticks; root UUID; display relative-path string; Int64 length; Int64 creation UTC ticks; Int64 mtime UTC ticks; Int32 supported attributes; eight raw xxHash64 bytes. Fields/validation unchanged.
2. **CurrentUpsert**: UInt16 canonical current-key length; key; required VersionAdded address with matching logical path. This changes current selection, not historical existence.
3. **CurrentRemoved**: UInt16 current-key length; key. Removes membership only.
4. **SourceRoot**: UUID; descriptive location string; protected byte 0/1; Int64 maximum file size; UInt16 exclusion count; sorted canonical excluded-directory strings. Fields/meaning unchanged.
5. **VersionRemoved**: UInt16 version-key K length; exact K; version/payload UUID. Details below.

Unknown kinds fail. Index leaf values still point to positive VersionAdded/source records; **no Versions leaf points to a removal**. Negative decisions live in the committed semantic chain and delete their matching positive version key from the tree at the same publication.

### Kind 5: exact historical disappearance

| Body order | Bytes |
| --- | --- |
| Kind | 1, value 5 |
| Previous | 1 null flag, or 1 flag plus 88-byte semantic address |
| K length | UInt16 little-endian; even, 32..4,126; checked before allocation |
| K | Root UUID + canonical UTF-16BE path + zero terminator + original Int64BE positive sequence + Int32BE ordinal 0..4,095 |
| Version UUID | 16 RFC/network bytes, nonempty |

Require canonical path/root/key structure, target sequence no greater than captured root sequence, exact frame hash/type/address/frontier and no trailing bytes. Linked body length is `108 + K.Length`; null-predecessor body length is `20 + K.Length`. Including the 96-byte frame header, a linked removal is 236..4,330 bytes. The original version sequence/ordinal are not the removal batch's sequence.

There is no policy, cutoff, removal timestamp, stored time, payload path, length, checksum, retry counter, operation ID or acknowledgement field. The durable meaning is **this exact historical version no longer survives**. The UUID also identifies its eventual payload cleanup. Repository context comes from the enclosing authority.

A normal writer creates the record only after validating an exact surviving addition at K with matching UUID and protecting every exact current selection. An absent K is skipped without a duplicate record; a present K with mismatching UUID fails. Multiple versions of one path may share a removal batch, but duplicate K requests may not. Each version counts toward the 4,096-request bound.

A compacted base may contain one self-contained removal with no old addition. That is a legal negative decision and pending cleanup anchor, not a dangling pointer. Only verified compaction carry-forward creates this form. Generated UUIDs are never intentionally reused; detectable UUID aliasing across unrelated keys is corruption. This retains the existing generated-UUID uniqueness assumption, not a permanent registry after completed history is compacted away.


Root body order: Int64 generation, Int64 mutation sequence, transaction UUID,
epoch UUID, Int64 previous generation, previous transaction UUID, previous root
SHA-256, then nullable pointers to journal head, current tree, version tree,
source-config tree, retirement list. Generation zero has sequence zero, previous
generation -1, empty previous UUID, zero previous hash, and all-null roots.
Later roots have predecessor generation = generation - 1 and distinct nonempty
transaction UUID. Predecessor identity/hash permits reconciliation without
retaining an unbounded root-address chain. Segment links and the semantic head
supply explicit reconstruction.

Page body: index kind byte (current 1, versions 2, sources 3), level byte,
UInt16 count, then entries `(UInt16 key length, key bytes, required address)`;
remaining bytes through 16 KiB are zero. Count is 1..128, level 0..15. Leaf values
point to semantic version/config records. Internal entries point to pages one
level lower and carry the child's inclusive maximum key. Keys strictly increase;
child maxima and nonoverlapping ranges validate when traversed. Serial descent
precludes self/cyclic references. Empty trees use null roots. Pages cannot span
segments. Updates append changed leaves/ancestors, reuse unchanged branches,
share ancestor rewrites within a batch and split on byte/count capacity. The implementation first finds the minimum number of contiguous capacity-valid pages for each rewritten
range, then balances adjacent groups from right to left before emitting immutable
frames. Both byte and entry-count occupancy constrain the cuts. Untouched child
ranges are reused; sparse updates do not rebuild the full tree. This prevents
full-page/tiny-tail split proliferation without a global fill-factor policy.
Existing fragmented pages remain readable; explicit compaction densely rebuilds them.


## Bootstrap, credentials and small encrypted objects

`repository.json` is bounded to 8,192 bytes. Its canonical property order is `product`, `formatVersion`, `bootstrapSchema`, `encryptionMode`, `repositoryId`, `createdUtc`, `recovery`, `windowsUserKey`. Product is `Fluff-History`, numeric format4/schema1; mode is exactly `none` or `aes256-gcm-hkdf-sha256-v1`. UUID and UTC creation time describe the repository, never a source/file. All fields except optional `windowsUserKey` must be present; duplicate/unknown properties fail. Parsing validates types, identities, UTC, mode/slot coherence and bounds before expensive work. Exact generated UTF-8 encoding is required (the final optional Windows field may be absent instead of null); arbitrary whitespace/escaping/reordering is rejected. `none` requires both slots null. No source paths, timestamps, filenames, checksums or file data belong in this plaintext bootstrap.

Encrypted mode requires `recovery` with `kdf:"pbkdf2-sha256"`, `iterations:600000`, `salt` (base64 32 bytes), `wrappedKey` (base64 96-byte envelope). Exactly this iteration count is accepted, before KDF/allocation. Password is 1..1024 UTF-16 characters encoded by .NET PBKDF2 as UTF-8 without normalization. PBKDF2-HMAC-SHA256 yields a 32-byte wrapping key. The **random 32-byte repository master** is wrapped, never derived from the password/SID/account/checksum. Optional Windows blob is bounded base64 user-scoped DPAPI (`CryptProtectData`, UI forbidden, bootstrap binding as entropy), never machine-scoped. Missing/null/corrupt optional blob does not affect password recovery. It provides ordinary signed-in-user convenience, including Microsoft-connected users; no new-machine/reinstall/consumer-account escrow guarantee.

`Binding` is SHA256 of the generated bootstrap JSON with both slots replaced by null. This binds product/format/schema/mode/UUID/creation time; slots are unlock mechanisms, never history authorities. The optional slot is excluded deliberately. Active repository sessions compare the immutable normalized bootstrap excluding both password and Windows wrappers before required authority/writer operations. Complete valid historical repository replay is not detected without external trusted freshness; rollback/DoS, unlocked-process compromise and size/count/access patterns are outside the threat model.

All encrypted small objects use:

| Offset | Bytes | Meaning |
| ---: | ---: | --- |
| 0 | 8 | `FHENC4\0\0` |
| 8 | 4 | Little-endian crypto schema1 |
| 12 | 4 | Little-endian plaintext/ciphertext length, exact total minus64 |
| 16 | 32 | Fresh cryptographic random key salt |
| 48 | 16 | AES-256-GCM tag |
| 64 | N | Ciphertext |

Key = HKDF-SHA256(master-or-wrapping-key, salt, 32 bytes, info). Info is UTF8 `FluffHistory/v4/m20/` + fixed purpose, followed by 32-byte Binding and context bytes. Nonce is twelve zero bytes, **one encryption per independently derived key**. AEAD associated data is the exact first48 envelope bytes; purpose/binding/context are additionally bound through key derivation. Purpose/context: `password-slot` with PBKDF salt; `state` with empty context for both authority/candidate; `prepared-root` with empty context (its exact frame is independently bound by encrypted state); `metadata-block` with complete encoded segment160+block160 headers; `reusable-descriptor` with empty context (inner exact path/UUID/state/provenance and generated name checks remain); `history-scratch`, `capacity-scratch` with UTF8 generated local path + colon + record-index. The implementation removes the former derived `decoded-cache` disk purpose; decoded blocks live only in bounded RAM. Decimal contexts are invariant ASCII.

Fresh 256-bit salts avoid counter rollback across crashes/retries. At 2^32 independent objects, birthday collision probability is below2^-193; this is a documented probabilistic subkey-uniqueness claim. Keys/passwords are never logged or serialized in plaintext. Mutable keys are zeroed on disposal/failure where practical; the repository owns its unlocked key context and implements IDisposable. Caller-owned managed password memory and runtime/native copies cannot promise perfect erasure. No secret static global.

`Create`/`Open` retain explicit unencrypted behavior. `CreateEncrypted(directory, recoveryPassword, useWindowsUserProtector)` requires recovery; `Discover` returns encryption/locked state without decrypting user metadata; locked instances reject reader/writer access. `OpenWithPassword` and `OpenWithWindowsUser` authenticate state and its root before returning an unlocked repository. There are no Storage prompts. Recovery copies repository files to a new machine, discovers the locked header, opens with password, and uses normal browse/restore APIs without any original Windows slot or workspace. Storage provides explicit `EnrollCurrentWindowsUser`: under retained exclusive writer ownership it validates current state/root, wraps the same master with current-user DPAPI, flushes/readbacks a canonical same-directory candidate and atomically replaces only repository.json using the existing local/network replacement primitive. It verifies exact installed bytes and Windows-authenticated state/root. Password open stays read-only; enrollment preserves the recovery slot, immutable binding, identity and all history files. Pre-replacement failures leave the prior bootstrap; replacement/confirmation failure throws OwnershipEnrollmentUncertainException and requires fresh explicit reopen/retry. Slots never decide history; state.json remains sole history authority. Existing encrypted repositories with missing/stale Windows slots remain valid. See [ownership and recovery](SECURITY-AND-RECOVERY.md) for account-recovery limits. Dispose active readers/writers before the repository.

The logical keys, semantic records, roots and indexes specified below are inherited by Format 4. Semantic kinds remain VersionAdded=1, CurrentUpsert=2, CurrentRemoved=3, SourceRoot=4 and VersionRemoved=5. A batch is not a historical run or restore point.

Logical metadata segments retain their 67,108,864-byte (64 MiB) maximum, 160-byte `FBV2SEG\0` header (Int32 schema **4** at byte 8), UUID/epoch/ordinal/predecessor binding and header SHA-256. Logical frames retain the 96-byte `FBV2FRM\0` header and 1,048,576-byte body limit. Pages retain 16,384-byte bodies, count/byte limits, tree kinds, ordering and child-maximum checks.

Required addresses now encode **88 bytes**: the inherited 80-byte sequence (network UUID, ordinal, **logical** offset, body length, kind, serial, SHA-256), followed by little-endian Int64 `BlockOffset`. Nullable addresses retain the 0/1 prefix. `BlockOffset` is the physical offset of the block containing the frame's first logical byte; it is never a decoded-byte offset. Canonical JSON root addresses add `blockOffset` after `sha256`. Parent frame hashes and canonical state checksums bind locators. Every decoded address receives the existing validation plus physical locator bounds. Page capacity is calculated using 88-byte addresses; no comparator or semantic kind changes.

Embedded addresses in semantic/page/root/retirement bodies use this extension consistently. Their meanings and surrounding field order remain unchanged, but each present address occupies eight additional bytes. For example, a linked VersionRemoved body is now `108 + K.Length` bytes; its null-predecessor form remains `20 + K.Length`.

## Physical segment file

`metadata/<segment-UUID-N>.seg` starts with the ordinary directly readable 160-byte schema-4 segment header. All subsequent live logical bytes are carried in independently encoded immutable blocks. There is no ZIP, multi-segment archive, sidecar directory, persistent reader cache or second catalog/manifest authority.

Each block decodes **1..262,144 bytes**. Blocks are usually full during dense output, but publication/readback boundaries flush partial blocks. Partial blocks are never reopened or recompressed. Frame bytes can cross blocks; one maximum-sized frame requires at most five blocks from the production writer (the reader enforces a six-block traversal ceiling). Block boundaries are not a fixed logical grid after partial publications; readers use validated locators, not division by block size.

Physical segment length is independently bounded at **268,435,456 bytes (256 MiB)**, including all envelopes and abandoned bytes. Writers enforce it before writing a block. The logical segment maximum and physical segment maximum are different bounds.

### Block header: 160 bytes

All integers are little-endian; UUIDs use network order. Field offsets are from the beginning of the block.

| Offset | Bytes | Meaning |
| ---: | ---: | --- |
| 0 | 8 | `FHV4BLK\0` |
| 8 | 4 | Schema, exactly 4 |
| 12 | 4 | Encoding ID: 0=stored, 1=raw RFC 1951 Deflate; encrypted mode2=stored+AEAD,3=Deflate+AEAD |
| 16 | 16 | Repository UUID |
| 32 | 16 | Segment UUID |
| 48 | 8 | First decoded logical offset |
| 56 | 4 | Decoded length, 1..262,144 |
| 60 | 4 | Physical encoded length: positive and <=decoded length; encrypted mode adds96 |
| 64 | 8 | Previous block's physical offset, or zero for the first block |
| 72 | 8 | This block's exact physical offset |
| 80 | 4 | Flags: 0=ordinary, 1=seal; all other values forbidden |
| 84 | 12 | Reserved zero bytes |
| 96 | 32 | SHA-256 of exact decoded bytes in mode none; encrypted mode requires all zero |
| 128 | 32 | SHA-256 over header bytes 0..127 |

The segment header binds epoch and ordinal; each block independently binds repository/segment identity. A header hash is verified before trusting lengths or allocating decoded storage. Unknown encoding IDs never trigger guessing or fallback.

Exactly `EncodedLength` bytes follow. Encoding 0 requires equal encoded/decoded lengths. Encoding 1 is an independent Deflate stream without ZIP/zlib/gzip wrappers. Writers use `CompressionLevel.Fastest`; if compression saves **at most 32 bytes**, they use encoding 0. Both encodings use the same container and validation contract.

In encrypted mode only IDs2/3 are accepted (and only0/1 in mode none). Select compression first by the same rule, then seal `decodedSHA256[32] || compressed-or-stored-bytes` using the above envelope and metadata-block context. EncodedLength therefore includes **96 bytes** beyond the compression-only encoded payload. Public decoded hash is zero, preventing plaintext digest exposure. Stored ID2 requires exact decodedLength+96; ID3 is bounded by that maximum. Header/trailer checks remain early bounded corruption checks, followed by AEAD and only then bounded decompression/decoded hash/frame validation. No committed block is re-encrypted in place.

### Block trailer: 48 bytes

The payload is followed immediately by `FHV4END\0` (8 bytes), this block's physical offset (Int64), and SHA-256 over its complete 160-byte header plus exact encoded payload (32 bytes). Physical block end is `BlockOffset + 208 + EncodedLength`. Padding, concatenated Deflate members and trailing encoded bytes are forbidden.

The decoder verifies header/trailer hashes and identity, exact input/output lengths, completed Deflate input and decoded SHA-256. Its input adapter withholds the last input byte until requested and throws if an unfinished inflater requests bytes beyond the declared input; this prevents tolerant EOF and input read-ahead from hiding truncation/trailing data. Neither a block checksum nor successful decompression replaces exact logical frame validation.

## Continuity, random reads and seals

The first ordinary block starts at physical offset 160 and logical offset 160, with predecessor zero. An ordinary successor starts exactly at its predecessor's physical end and logical end; the predecessor must be unsealed and have a smaller physical offset. Bounds, gaps, overlaps, ordering, self-location, reserved fields and impossible extents fail closed. Header checks are bounded and cached; no mapping scan is required.

For a requested frame, locate its first block, verify that its logical range contains the requested logical offset, and recover only the necessary blocks. Cross-block reads require exact logical continuity and the preceding physical locator. Then validate the exact frame's repository/segment/offset, kind, length, serial, expected SHA-256 and captured frontier. No point read decompresses from the beginning of a 64 MiB segment.

The physical last seal block has flag 1 and exactly 112 decoded bytes: the inherited logical Seal frame (kind 4, 16-byte body containing logical seal offset and previous serial). Its logical offset is the end of ordinary metadata; the seal is outside the ordinary reader's visible logical data extent. A sealed segment must end exactly at this block's trailer. Read the last 48 bytes to locate the seal, then validate the container and logical seal. Extra sealed-tail bytes are invalid.

An active segment can have an uncommitted or torn physical suffix. Captured authority still names immutable blocks in its committed prefix; the suffix is never adopted or truncated. A writer continues only if the confirmed root ends exactly at the final ordinary block's logical and physical ends. Otherwise it seals/quarantines the old segment and rotates. Only a seal block may have a physical gap after its validated predecessor: that gap contains opaque abandoned bytes, is outside ordinary logical data and is never parsed as history. A newly constructed quarantine seal starts logically at the confirmed frontier. An already installed valid seal remains unchanged; any abandoned frames it encloses remain unreachable and are never replayed or adopted. Older captured addresses remain unchanged. Failed installation/replacement still requires exact reconciliation before another mutation.

## Logical read-ahead and workspace

Grouped Current/Timeline/Retention network readers retain a **1 MiB logical read-ahead budget**. Format 4 translates that budget using the current block's observed encoded/decoded ratio into bounded physical prefetch, capped at 1 MiB + 208 bytes per handle. Encoded prefetch is not eager decoded read-ahead: only requested blocks are decompressed. Fetching unrequested physical suffix bytes does not authorize interpreting them or crossing a captured logical frontier. Application physical ranges are not SMB protocol/wire counters.

At most eight segment handles are retained. Non-workspace point/control readers have a 256 KiB decoded-byte budget per handle; grouped read-ahead has 1 MiB. The implementation uses the same byte-bounded LRU implementation for these ordinary readers; it can retain multiple smaller blocks within that bound. Thus retained ordinary-reader decoded bytes are structurally bounded at 2 MiB / 8 MiB respectively across eight handles, plus at most eight encoded prefetch windows and bounded headers/frame buffers. Workspace readers instead share the single RAM cache described below. Failure never installs a partial encoded window or decoded block. Existing operation disposal, cancellation, frame validation and captured scope/epoch contracts remain.

Writer/comparison/maintenance workspaces are disposable derived storage outside the repository. They cache only requested **64 KiB physical ranges**, once per segment/range in that workspace, in sequential local cache files with in-memory range maps. The implementation stores fully validated decoded blocks in one workspace-owned **32 MiB plaintext RAM LRU** shared across segment handles, in both modes; no `.decoded` file or secondary decoded-block encryption exists. The exact full segment binding/physical offset identifies each entry; each hit matches logical/encoded extents, predecessor, flags, encoding and decoded digest before copying frame bytes. Every requested frame still receives exact type/address/hash/frontier validation. A hit reuses already validated materialized data without reauthenticating source ciphertext; eviction reconstructs from the independent encoded range cache/source. Eviction/disposal zero owned decoded buffers where practical. The byte budget counts decoded arrays, not an exact CLR heap quota; maps have one entry per retained block. MaximumMaterializedBytes now limits physical disk range bytes only; decoded RAM has its separate bound and current/peak/hit/miss/eviction counters. Existing destination segments are not downloaded or decompressed wholesale to append a sparse mutation.

Local output for an existing segment consists of its 160-byte binding header plus **only its new physical suffix**; a virtual stream supplies cached predecessor ranges and prevents committed-prefix writes. Newly generated segments are complete local files. Cache files are never staged and contain no recovery authority.

## Publication and reconciliation

`repository.json` is minimal bootstrap; `writer.lock` remains empty and uses unchanged persistent native-handle ownership. In mode none, state/prepared controls remain canonical readable controls. Encrypted mode seals their complete canonical bodies using the purposes above. **state.json remains the sole authority pointer**. Key slots/workspaces/cache never decide history. Its exact logical authority/root values are compared after authenticated decoding, regardless of randomized envelope bytes.

The writer retains exact ownership and predecessor checks, constructs locally, stages only non-authoritative append extents, installs/flushes/verifies metadata, and then replaces the existing authority boundary. Appending durable metadata never modifies committed blocks. Destination append length/header and exact transferred suffix SHA-256 are verified; installed candidate validation decompresses and validates the intended root before replacement. Prepared root companions remain small root bodies; the candidate binds their exact logical frame and physical locator.

Half-written blocks cannot become reachable through confirmed authority. Exact intended authority means Published; exact predecessor means Unpublished; conflicting/unreadable/ownership-lost outcomes remain uncertain. A failed session aborts. Fresh reconciliation classifies the outcome and never completes a prepared intent speculatively. Retained network ownership query and Windows network `MoveFileExW` / local `ReplaceFileW` boundary remain intact, without blind retry or copy/delete fallback. Flush/process-crash evidence does not establish server/device power-loss durability.

## Maintenance, allocation and payloads

Rebuild and compaction emit Format-4 blocks directly through the same appender. They preserve exact semantic folding, Current protection, survivor/index agreement, negative anchors, epoch/generation rules and exact metadata/payload retirement. Rebuild requires intact semantic blocks: corruption of compressed bytes can invalidate every logical frame sharing that block. Logical replaceable-index corruption within otherwise valid containers remains rebuildable. Application can invoke gated runtime compaction; see [retention](RETENTION.md). Normal opening does not perform it.

Allocation estimation takes **no compression credit**. It retains pessimistic logical frame/page/rotation bounds, adds independently allocation-rounded 208-byte block envelopes for full/partial/seal blocks, and charges both staged and installed metadata plus controls/namespace allowances. Maintenance output is bounded in both logical and physical bytes. Actual caller-visible free-space feedback remains decisive; Ready eviction precedes authorized history removal. Forever/fixed-age never pressure-delete history; only UntilSaturation authorizes it. Failed copies/publications preserve Current.

Version payload layout remains `versions/<UUID-prefix>/<UUID-N>.bin`. In mode none it contains **raw original bytes**, with no crypto container. In encrypted mode it contains the chunked representation below. Logical metadata.Length and seed-zero xxHash64 always describe plaintext. Source copy remains one pass, hashing inline and encrypting during draft writes; immutable installation never overwrites. Encrypted callers use owned PayloadDraft; the legacy externally installed raw PrepareVersion API is rejected in encrypted mode. OpenVersionPayload exposes plaintext; direct restore retains owned temp/length/checksum installation and cleanup.

## Encrypted payload schema1

All integers little-endian except nonce counters; UUIDs network order. Header first80 bytes: `FHPAY4\0\0`(8), schema1(Int32), chunk size262144(Int32), repositoryUUID(16), versionUUID(16), random key salt(32). Next16 bytes authenticate the empty plaintext with nonce zero and associated data equal to these80 bytes. Payload key derives with purpose `payload`, salt from header, and context equal to header first48 bytes. Never reuse a payload salt/key to resume/rewrite a failed copy.

Each record is index(UInt64), plaintext count(Int32), flags(Int32), ciphertext[count], tag[16]. Index starts1 and increments, strictly less than UInt32.MaxValue. Nonce is four zero bytes followed by index as **UInt64 big-endian**. AAD is exact header80 plus record header16. Data flags0; every data chunk has262144 bytes except at most one short last chunk. The reader requires the exact expected remaining logical size per record. An obligatory final record flags1/count16 encrypts total plaintext length(Int64) and data-record count(UInt64), including zero-length files. Exact final count/length and physical EOF are required. No padding/trailing/extra members. A chunk is authenticated before releasing its plaintext; consumers read through authenticated final EOF before success. Missing/reordered/duplicated/truncated records, wrong key/identity and header/tag/cipher changes fail closed.

Physical length = `144 + logicalLength + 32*ceil(logicalLength/262144)`, checked before source reads, with logical maximum `(UInt32.MaxValue-2)*262144`. Buffering is one256KiB plaintext chunk plus bounded ciphertext/AAD; source and restore buffers remain128KiB. No payload compression or destination checksum reread is added to ordinary backup.

## Derived storage, reuse and Shell integration

Decoded workspace blocks reside only in bounded RAM and are zeroed on eviction/disposal where practical; there is no decoded disk cache. Physical range caches and local append output already hold ciphertext. History-fold and Engine capacity-order external sort records are separately sealed with index/path binding; ciphertext length/hash records remain bounded and budgets charge crypto overhead. No plaintext decoded cache/sort files persist. Cache corruption fails; no cache is required for recovery.

Reusable-payload logical descriptor schema1 stays unchanged inside explicit crypto envelope1. Ready/Bound/writing files are encrypted; bound filename and exact inner provenance checks stay active. Encrypted bucket names use HMAC-SHA256 with HKDF purpose `reuse-bucket`, Binding as salt, no context; plain mode retains SHA256. Candidate validation locally decrypts/authenticates and verifies plaintext checksum; adoption performs zero source/payload recopy. Exact authority/revocation/reconciliation semantics are inherited. Allocation adds overhead for blocks/controls/descriptors and physical payload length (including empty files), without savings credit. Ready eviction/Current protection/UntilSaturation authorization remain unchanged.

Windows Shell receives only selected verified plaintext staging, never encrypted physical paths. A unique local directory is created with an explicit non-inherited current-user-only ACL (Unix portable test equivalent0700); files inherit it. Authenticate/decrypt and verify exact logical length/xxHash64 before queueing. PreCopy rechecks the selected version and staging bytes while retaining a read-only handle that denies mutation/deletion; original conflict UI, destination names, timestamps/attributes and outcome reporting remain. Staging files/directories are removed exactly/best effort on completion/failure. Preview uses the same restricted-directory policy and existing verified restore/lease path. Unencrypted Shell keeps its direct raw path and zero content staging. SSD/temp remnants cannot be securely erased by this design; preview application files/locks can defer cleanup. No VFS/Shell crypto extension is added.

## Rebuild and semantic validity

Explicit rebuild never trusts the replaceable Versions index. It validates the committed semantic chain and uses bounded local external sorting to fold additions/removals by K and serial. It separately checks UUID bindings, and resolves latest current/configuration decisions.

| Events at K | Meaning |
| --- | --- |
| One addition only | Survives at its original exact address. |
| One addition followed by one same-UUID removal | Does not survive; removal remains cleanup evidence. |
| One removal only | Legal compacted pending-cleanup anchor; does not survive. |
| Duplicate additions/removals, conflicting UUIDs, removal before its addition, UUID reused at another K | Invalid; do not publish. |

Every latest selected current address must appear as a surviving committed addition at its exact path/UUID and agree with valid protection configuration. Obsolete upserts can reference subsequently removed historical additions; they do not protect those versions. A current selection erased by a negative record is corruption, not permission to drop membership.

Rebuild bulk-builds final positive Versions/Current/Sources trees, keeping the same semantic head, retirement chain, epoch, mutation sequence and version fields. It increments storage generation only. Removed versions must remain absent after any valid sequence of removals, rebuilds and compactions.

Maintenance defaults are 2,000,000 semantic records, 1,000,000 current-decision/configuration entries per map, and 1 GiB of new output metadata. New explicit bounds are 1 GiB live sorting scratch, 8 GiB cumulative sort writes, and 16 GiB materialized destination metadata. Newly appended local output is independently bounded by output limits. Sorting buffers at most 4,096 events / 4 MiB encoded keys **per sorter**, with eight-way online merge levels and at most eight input readers per merge. Runs validate per-record SHA-256, bounded lengths, sorted order, expected counts and exact EOF. Scratch is derived and discardable; its binary representation is not a durable repository codec. Cancellation/budget failure cannot publish partial reconstructed state.

## Compaction and payload-retirement anchors

Compaction derives the same validated survivor set from semantics and compares it exactly with existing Versions entries: keys, addresses, UUIDs, counts, no missing/extra/removed entries. Current/configuration agreement remains required. An index disagreement requires explicit rebuild, not implicit repair by compaction.

The new epoch contains identical surviving version fields, correctly remapped current selections and source configurations. It omits removed additions. For every negative identity whose payload remains present or whose absence is unsafe/unknown, it emits one self-contained kind-5 anchor. It drops that negative only when the old addition is also omitted and the exact generated payload name is conclusively absent. Access denial, unsafe parents, reparse targets and `File.Exists == false` are not absence proof. A successful delete can leave its tombstone until a later compaction.

No cleanup acknowledgement kind or extra root is needed. The null-terminated replacement base severs old dependencies. Only after publication can exact retired metadata cleanup remove old segments; pending payload identity remains recoverable even when every old segment is gone. Repeated compaction/rebuild preserves the same survivor set and unfinished obligations.

## Payload cleanup and captured readers

`CleanupRetiredPayloads(limits, continuation, token)` walks only committed semantic predecessors. Its transient continuation binds exact authority and a validated semantic position. Default bounds are 2,000,000 records and 4,096 payload attempts per page. Each page reports records scanned, delete/already-absent/deferred/failed counts, cancellation and at most 128 problems. No persistent cleanup session or directory sweep exists. Immediate removal cleanup uses its confirmed bounded record list rather than replaying history per version.

Before destruction, revalidate ownership, control/authority, committed identity, K absent and current not referencing that UUID at the path. Generate the UUID path internally, refuse unsafe repository/shard/target paths, and never change attributes/permissions to force removal. Unknown/unpublished orphans remain outside this mechanism. Ordinary sharing/access failures preserve the tombstone and report deferral/failure; unsafe/control errors fail closed. Cleanup cancellation preserves committed visibility and remaining evidence. Logical lengths/deletion acknowledgements are not actual reclaimed allocation.

Windows deletes the validated no-follow ordinary file through a DELETE-access handle and `FileDispositionInfo`; open `VersionPayload` read sharing prevents that access. Ordinary namespace guards and final no-follow/type checks do not claim adversarial directory-tree snapshot isolation. Flush/process-crash behavior still does not establish directory power-loss persistence or SMB guarantees. Other-platform implementation paths are outside supported product and release validation.

`OpenVersionPayload` rechecks exact survival against fresh valid authority before opening and after an I/O failure. A removed exact identity throws `PayloadVersionExpiredException`, including same-epoch generation changes; it never substitutes another version. A still-surviving missing payload remains corruption/I/O. Unreadable fresh authority proves no expiry. Reader objects alone are not leases for all payloads; already-open handles retain OS behavior. Metadata epoch expiry/retirement behavior is unchanged.

Policy orchestration is described in [retention](RETENTION.md); numeric development formats 2/3 remain unsupported.

## Retired metadata record

A Retired frame body is segment UUID, epoch UUID, Int64 ordinal, nullable previous
Retired address. Compaction repacks exact old committed segment identities and
pending retirements. Cleanup checks repository/epoch/UUID/ordinal, refuses unresolved
publication and deletes only those exact files. Unknown orphans and payloads are not
included. Windows sharing can defer deletion; malformed bindings fail closed.
Deleted identities remain in the retirement chain until later maintenance.

## OneDrive split-secret sidecar

`onedrive-account-recovery.json` is canonical UTF-8, at most 8192 bytes. Ordered
fields are `product`, `schema`, `provider`, `repositoryId`, `generationId`, `itemId`,
`fileName`, `eTag`, `cloudSecretSha256`, `protectedKey`. Product is Fluff-History,
schema 1, provider `onedrive-appfolder-v1`. Nonempty repository/generation UUIDs,
item ID (at most 512 printable ASCII characters excluding / \\ ? #), eTag (at most
1024 characters without controls), lowercase 64-character secret SHA-256 and exact
`recovery-<repository N>-<generation N>.json` filename are validated. ProtectedKey
is base64 of exactly 128 bytes. Unknown/duplicate/missing/noncanonical data fails.

Context is SHA256 of UTF-8
`FluffHistory/v4/m20.3/onedrive-appfolder-recovery/v1/<repository N>/<generation N>/<itemId>/`
concatenated with bootstrap Binding. A 64-byte packet (context hash plus master)
is sealed using the random cloud half, context as wrapper binding and purpose
`onedrive-appfolder-master-v1`. The remote object/hash, packet binding, current
state/root and local sidecar are verified before success. This does not amend
repository history authority. Legacy PasswordVault material is cleanup-only.

Decryption first writes a bounded non-authoritative local cleanup obligation in
`onedrive-cleanup-<operation UUID N>.dat`. Its CurrentUser DPAPI payload is canonical
JSON: product `Fluff-History`, schema1, operationId, the exact validated recovery
reference, sourceBootstrapSha256 and targetBootstrapSha256. The reference contains
repository/generation UUIDs, itemId, fileName, eTag and cloudSecretSha256. The record
contains no recovery half or repository master. Its fixed DPAPI entropy is
`FluffHistory/onedrive-exact-cleanup/v1`. It must be flushed and authenticated/read back
under retained conversion ownership before retiring the local recovery sidecar.
At most eight records can accumulate; they have no timed expiry. Explicit cloud retry
requires an ordinary locally unlocked repository, matching repository/plain-bootstrap
binding and no migration marker. It can run after re-encryption, but rejects any ticket
matching the currently active recovery generation or item. Verified exact content and
the original eTag must precede conditional deletion; only its success or already-absent
response permits writer-owned acknowledgement. An initial not-found read is ambiguous
across accounts and retains the record. Identical full references share one verified
deletion proof only within the same authenticated invocation and retained writer.
Normal opening, backups, re-encryption and Shell do not consume records. See
[security and recovery](SECURITY-AND-RECOVERY.md) for profile/relocation limitations.

The literal cryptographic domain strings in this document are frozen storage-format
bytes. They are not presentation labels and must not be renamed without a deliberate
format/interoperability change.

## Resumable encryption conversion

## Persistent fence and commit point

1. **Preparing** records validated source authority and prepared target wrappers.
   No payload/cache representation changes occur yet. Under writer ownership,
   resume can refresh a valid source authority advanced by an older client before
   fencing. Immutable source bootstrap properties must still match; Windows-only
   wrapper enrollment is allowed. The refreshed semantic capture is preserved.
2. Atomically replace `repository.json` with the exact unsupported-mode sentinel:
   ordinary plain bootstrap identity/schema, no key slots, encryption mode
   `maintenance-<operation UUID in lowercase N format>`. This is a maintenance
   fence, never an accepted ordinary codec. Older fresh and retained
   clients reject it after the writer lock is released on pause/process death.
   Updated clients show migration-required before parsing the sentinel.
3. **Payloads / Metadata** retain the fence. Stream surviving versions and replace
   each payload through one flushed, independently verified same-directory
   candidate. Resume classifies by distinct exact raw/encrypted physical lengths
   plus complete checksum/authentication, never magic or parse failure. Unknown
   bytes stop safely. Existing dense compaction emission builds target metadata
   with reserved target segment identities.
4. **Pivot** installs target `state.json` FIRST, while the sentinel remains. Exact
   predecessor/intended bytes are reconciled after replacement exceptions. Verify
   all target metadata, payloads and semantic equality again.
5. Persist authenticated **Ready**, then expose target `repository.json` LAST.
   That header replacement is the physical commit point. Keep the writer lock
   through marker retirement and confirm a fresh ordinary target open.

After the target header commit, an older client may legitimately write target-mode
history if the process dies before marker removal. Ready recovery authenticates
the saved completion proof, opens actual current target authority, and preserves
valid higher-generation/nondecreasing-sequence advancement. It never restores the
old captured state. It also works after a released client compacts and cleans the
original snapshot's metadata. Reported conversion counts/digest describe the
original conversion; `PostCommitAuthorityAdvanced` distinguishes subsequent work.
Before header commit, recovery requires the exact recorded target state and full
semantic equality. Foreign headers, forbidden precommit advancement and malformed
controls fail closed. Target Windows-slot repair before exposure recomputes the
authenticated Ready binding; after exposure only an authenticated normal enrollment
may change that wrapper.

## Exact control and candidate contract

`encryption-migration.json` is canonical UTF-8 JSON, at most **65,536 bytes**.
Ordered fields: `product`, `schema`, `operationId`, `repositoryId`,
`sourceEncrypted`, `targetEncrypted`, `targetEpoch`, `maximumSegments`,
`sourceBootstrap`, `targetBootstrap`, `sourceState`, `targetState`,
`semanticDigest`, `phase`, `sha256`, `versionCount`, `logicalBytes`,
`sourcePhysicalBytes`, `targetPhysicalBytes`, `completionProof`.
Product is Fluff-History, schema 1. Embedded controls are exact base64 bytes,
individually at most 8,192 decoded bytes. Phase is Preparing, Payloads, Metadata,
Pivot or Ready. SHA256 covers canonical bytes with `sha256` empty. This detects
accidental corruption; it is not authentication or another history authority.

Ready alone has a 96-byte decoded `completionProof`: the unchanged AES/HKDF
envelope with purpose `state` and distinct nonempty ASCII context
`FluffHistory/v4/m25.2/conversion-commit/<operation UUID N>`, sealing SHA256 of
the canonical Ready control with `sha256` empty and `completionProof` null. The
encrypted side's master authenticates it (source for decryption, target for
encryption). A rechecksummed plaintext marker cannot authorize completion. A
different Windows profile may therefore need the original conversion recovery
password to retire Ready even when the committed target is plain. No plaintext
master, password, path list or growing progress list is stored.

Target keys persist only through the existing password/current-user DPAPI wrappers.
New encrypted targets are checked through both wrappers before maintenance writes.
Source/target state and roots authenticate independently; saved state identity can
be checked without requiring its already-retired metadata files after commit.

Control candidates are `encryption-control-<random UUID N>.tmp`; pivot candidates
are `encryption-pivot-<random UUID N>.tmp`, bounded to the corresponding control
size, flushed and independently read back before same-directory replacement. A
live operation removes only its exact temporary name. Orphan candidates are never
ordinary authority and are not broadly swept.

Payload candidates are `<VersionId N>.bin.migration-<OperationId N>.tmp` beside
their exact payload. Ownership is the active control plus captured surviving
identity. Length, xxHash64, encrypted header/chunks/final authentication are
independently verified before replacement. Buffers are bounded and cleared where
practical. Already-open payload handles can finish; Windows sharing that prevents
replacement causes safe bounded pause, never forced deletion.

Target segment IDs derive from SHA256 of the explicit conversion-segment domain,
operation UUID, target epoch and ordinal. The assessed dense logical bound fixes
a maximum count, capped at **1,024**, before creation. Retry touches only exact
reserved names, rejects reparse/foreign complete headers, and rebuilds disposable
partial output. No arbitrary metadata enumeration authorizes deletion.

## Authority, capacity and cleanup

`state.json` remains the sole history authority. A framed SHA256 semantic digest
covers repository UUID/creation, logical sequence, source configurations, Current
identities and all surviving full versions. Physical addresses, generation and
encryption are excluded. VersionIds, paths, StoredUtc, Sequence/Ordinal, Current,
retention and file-only history remain unchanged. Representative restored versions
are independently hashed. Conversion creates no backup completion or logical
Timeline revision. Old captured crypto contexts expire after completed mode change.

Inventory is streamed. Admission includes per-file allocation rounding, aggregate
permanent growth, largest target payload candidate, conservative metadata output,
full scratch budget, bounded controls and normal 5% reserve. Space is rechecked
between bounded units. No history is deleted to fund conversion. Cancellation
checks occur during payload verification/copy and semantic enumeration; even after
header commit cancellation may pause cleanup, never roll committed data back.

Cache layout is validated before initial marker publication. Only validated exact
Ready/bound descriptors and already-declared retired payloads may be retired;
unknown/corrupt cache layouts stop before conversion starts. After semantic success,
Application retries ordinary exact retired-metadata cleanup. Deferred plaintext
metadata retirement is explicitly reported; there is no secure-erasure promise.
