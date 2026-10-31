# Knowledge packs

A knowledge pack describes one known cause of kernel pool growth: what it is, the evidence that shows it on a PC, what
helps, where to read more, and who found it. The packs live in [`knowledge/`](../knowledge), one JSON file per known
issue, and are built into PoolWatch. Anyone can add one with a pull request, or describe one with the
[known-issue form](https://github.com/nubbymong/poolwatch/issues/new?template=known_issue.yml) for someone else to
write.

When the evidence on a PC matches a pack, PoolWatch:

- shows a note in Advanced view, next to the pool tags the evidence is about: what PoolWatch measured, what is going on,
  what helps, what PoolWatch already does or can do about it, the links and the credits;
- gives the matching suggestion in the Simple view its reason, when the pack names one of PoolWatch's built-in actions
  as the workaround and PoolWatch is already suggesting that action.

A pack only explains. It holds plain text, pool tags, program names, numbers and links to read, never a command, a path
or anything PoolWatch would run, and it cannot make PoolWatch suggest or change anything: whether a suggestion appears
is decided by PoolWatch's own rules, the same with or without packs.

## The packs that ship

| Pack | What it is | What PoolWatch can do |
|---|---|---|
| `windows-token-leak` | Windows 11 keeps a security token (pool tag `Toke`) for almost every program that starts another | the token-leak guard |
| `ntfc-ai-desktop-apps` | NTFS file records (`NtFC`) pile up in the non-paged pool while the Codex or Claude desktop app runs | nothing: the note says what helped others |
| `codex-gpu-shader-cache` | one cause of the above: the Codex desktop app's GPU shader cache | a shortcut that starts the app without it |
| `refs-standby-metadata` | ReFS file records (`Reff`, `RefF`, `RLin`, `MSPi`, `MSeb`, `MSb+`) kept in the kernel pool by the file cache | the hourly cache clear |

## Adding a known issue

1. **Start from public evidence.** A pack needs a public report with measurements: which pool tag grows, how fast, on
   which Windows builds, with which programs running, and whether a cache clear frees it. A guess or a single
   unexplained reading is not enough; describe it with the
   [known-issue form](https://github.com/nubbymong/poolwatch/issues/new?template=known_issue.yml) instead.
2. **Copy a pack** to `knowledge/<id>.json`, where `<id>` is the new issue's id. Keep `"$schema": "./schema.json"` at
   the top: editors such as VS Code then check the file against [`knowledge/schema.json`](../knowledge/schema.json) as
   you type.
3. **Fill in the fields** below, in plain words a non-technical reader understands. Say only what the sources show, and
   say plainly when PoolWatch cannot fix the issue.
4. **Add tests** to `tests/PoolWatch.Tests/KnownIssueMatcherTests.cs`: evidence that names your issue, and close
   evidence that must not (another Windows build, slower growth, the other pool, the program not running). Add the id to
   the list in `KnowledgePackTests.TheShippedPacksAreBuiltInAndLoadCleanly`, and any new pool tag a `tagGrowth` rule
   names to the tag list in the same test.
5. **Build and test** as [CONTRIBUTING.md](../CONTRIBUTING.md) describes. The tests load every pack exactly as
   PoolWatch does, so a pack PoolWatch would refuse fails them, with the reason.
6. **Open a pull request** that links the sources and says how you checked the thresholds.

## The fields

| Field | Required | What it holds |
|---|---|---|
| `format` | yes | `1` |
| `id` | yes | lower-case letters and digits, words joined by hyphens, up to 64 characters; the file's name |
| `title` | yes | the issue's name, up to 60 characters |
| `explanation` | yes | what is going on, up to 600 characters |
| `workaround` | yes | what helps, up to 400 characters |
| `actions` | no | up to 3 built-in action ids (below) |
| `suggestion` | with `actions` | the sentence after the evidence on the action's suggestion, up to 160 characters |
| `evidence` | yes | 1 to 8 rules (below); the issue applies when any one of them holds |
| `links` | yes | 1 to 8 pages to read more on: `title` (up to 100 characters) and `url` |
| `credits` | no | up to 8 people: `name` (up to 40 characters), `what` (up to 200), and an optional `url` |

Texts are plain, on one line, with no spaces at the start or end and no control or invisible characters. A credit's
`what` reads on from the name: "documented the leak and its cause".

### Evidence rules

Every condition in a rule must hold. Each rule needs a measurement, `tagGrowth` or `clearFreed`, so an issue is never
named on a PC where PoolWatch measured nothing; the other conditions narrow it down.

| Condition | Holds when |
|---|---|
| `tagGrowth` | the listed pool tags (`tags`, case matters) together grew at least `minMBPerHour` MB an hour, in the `pool` given (`any`, `paged` or `nonPaged`; default `any`) |
| `clearFreed` | a cache clear in the last day freed at least `minMB` MB of the listed tags together; PoolWatch names the newest such clear |
| `running` | any one of the listed programs is running: by `image` (the file name, "ChatGPT.exe") or by `storePackage` (the start of its Microsoft Store package's name, "OpenAI.Codex"), never both |
| `windowsBuild` | the Windows build number is from `min` to `max`, both included (26100 is Windows 11 24H2); either may be left out |
| `fileSystems` | a fixed drive uses one of the listed file systems ("ReFS") |

`tagGrowth` and `clearFreed` each have a `name`: what the tags hold, in plain words, as written in the middle of a
sentence ("security tokens"). PoolWatch builds its sentences from it, such as "Security tokens grew 120 MB in the last
hour." Programs have a `name` too ("Codex desktop"). 1 MB is 1,048,576 bytes, as PoolWatch shows sizes.

Growth is measured from what PoolWatch already holds in memory: over the last hour, and only over at least 10 minutes.
PoolWatch's own cache clears are measured across, as its detector measures them: a clear's drop never counts as a
fall, its refill in the 2 minutes before it is measured never counts as growth, growth after it counts from the level
it left, and growth before it still counts. A `minMBPerHour` is never lowered by a clear. What a tag does inside a
clear's 2 minutes is the clear's, so for a tag the clear dropped its growth there is not counted: a tag growing just over
a pack's rate that the clears keep trimming can read under it. At the default 10-second sampling that is up to about 4%
over the rate with hourly clears and about 8% with clears every 30 minutes (or in an hour where a light clear is
followed by a full one); a longer sampling interval makes a clear's 2 minutes longer on the samples, so more (up to
about 20% at the 300-second maximum). While a clear is being measured, no growth is known.

### Built-in actions

| Id | What PoolWatch does | The suggestion it explains |
|---|---|---|
| `auto-clear` | clears the cache every hour and shows what each clear freed | "Clear the cache every hour" |
| `token-guard` | turns on its token-leak guard, which sets, each time PoolWatch starts, the Windows setting that stops the leak (Windows resets it at each sign-in) | "Protect this PC from the Windows token leak" |
| `chatgpt-shortcut` | creates a desktop shortcut that starts the Codex desktop app (its program is ChatGPT.exe) without its GPU shader disk cache | "Start Codex without its shader cache" (setup's Fixes step and Settings offer the shortcut; the Simple view shows no card for it) |

The note says what PoolWatch already does about the issue, or names the suggestion while PoolWatch offers it, and
otherwise says nothing of the action. The Windows setting the token-leak guard turns on stops its issue outright: while
that setting is on, set by the guard or by something else, the token-leak issue is not named, because growth of its tag
then has another cause. The guard being on is not enough, since Windows can refuse the setting. The hourly clear only
keeps its issue down, so the ReFS issue is still named while it is on.

A new kind of action is a code change with its own review, not a pack.

### Links

Links point at `https://github.com/`, `https://learn.microsoft.com/` or `https://support.microsoft.com/`, written
plainly (a lower-case site name, no user name or port) with letters, digits and `- . _ ~ : / ? # = & % +` only. Ask in
your pull request if a source lives elsewhere.

## How PoolWatch reads a pack

PoolWatch runs as administrator, so it reads each pack as untrusted, even though every pack is reviewed here:

- a pack is at most 32 KB, strict JSON (no comments, no trailing commas, no field given twice), nested at most 8 deep;
- every field is checked against the limits above; at most 100 known issues are kept, each id once;
- fields PoolWatch does not know are ignored, so a pack written for a newer version still loads in an older one. That
  holds inside evidence rules too, so a new kind of condition needs a new `format`: an older PoolWatch would drop the
  condition and match on the rest of the rule alone;
- a pack that fails a check is left out whole, with the reason in the log; the other packs are not affected.

The packs ship inside PoolWatch and change with its releases; PoolWatch does not download packs.
