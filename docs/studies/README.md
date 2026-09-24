# Studies

The measurement and design documents the shipped behaviour rests on. They were written in a scratch
folder while the work was being done; they are kept here because several of them are the ONLY record
of why a rule fires, why a metric was deleted, or what a number was measured on. Everything here is
evidence for a decision that has already shipped - it is not a plan.

Each one states its own sample, its build stamp and its method. Where a document and the code
disagree, the code is what runs and the document is stale: say so in the document rather than beside
it.

| file | what it settles |
|---|---|
| `commitment-register.md` | every commitment made to the user, with its state |
| `decisions-1.md`, `decisions-2.md` | the decisions taken on output safety, sampling, metrics and counts |
| `checker-design.md` | the feasibility checker: every refuse / vacuity / rarity / combination rule, its tier and its evidence class |
| `constraint-atlas.md` | the per-type geometry the checker reasons about, validated against 36,829 real instances with 0 violations |
| `altar-quantity-findings.md` | whether a boss always gets its full set of altars. 5,000 uniformly drawn seeds; the source of rules D3, D4, D5, V5b and A1, and of `data\<stamp>\count-sample.bin` |
| `altar-quantity-data.md` | the data files that study wrote, and how its sample is reproduced from seven numbers |
| `coastline-verdict.md` | why `coastline_length` and `deepest_point` were deleted, and what replaced them |
| `goal-model.md` | the reference site's goal model, and what SeedLab does instead |
| `resolution-study.md` | what a coarse sampling grid does to each metric - the measurements behind every grid refusal |
| `performance-profile.md`, `pilot-tables.md`, `search-tables.md` | measured throughput and cost, with the contention each figure was taken under |
| `dumper-children-notes.md` | the prefab-child / room walk: what it reads, and what it deliberately does not touch |
| `third-party-tools.md` | SeedLab vs bobmitch.com vs valheim.tools: who agrees with whom and why, and the feature inventory taken off bobmitch (its seed finder, base planner and category taxonomy) |

**The count sample.** `altar-quantity-findings.md` is the study that produced
`data\1.0.15-59f53fb5\count-sample.bin` (5,000 seeds x 183 types). That file is what rules D4 and D5
measure against, and it was re-verified against the current build on 2026-09-23: three sampled seeds,
183 types each, 549 cells, 0 differing.

Credits: see the root README - created and tested by DoomMachine; code, tests and docs written by
Claude (Anthropic) in Claude Code.
