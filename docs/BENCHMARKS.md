# Benchmark behind the README

The README reports a browser pilot using GPT-6 Luna at medium reasoning effort.
Each route completed the same three datasets: add five amounts, create an exact HTML report, save it to a new test folder,
and reopen it. An independent checker validated the calculation, saved contents and completed operations.

| Route | Successful tasks | Mean model tokens per completed task | Reduction vs browser Computer Use |
| --- | --- | ---: | ---: |
| Browser Computer Use using its object tree | 3/3 | 349,990 | Baseline |
| k-lani-bot compact native UI gateway | 3/3 | 284,660 | 18.7% |
| Reusable recipe through the test application's MCP functions | 3/3 | 80,376 | 77.0% |

These are measured input plus output tokens, including cached input and repeated context.
Failed attempts are included when calculating tokens per successful task. Each of these three selected routes passed all three attempts.
All three sent zero images to the model, so this comparison is about interaction and context size, not screenshot compression.

## What the numbers support

The compact UI gateway reduces model context while still using the real node's native UI access.
The reusable recipe demonstrates the potential of a program-specific workflow: fewer model round trips for the same verified result.
Its application functions belong to the controlled browser workbench. The 77% figure does not measure a complete workflow
through automatically captured generic UI functions or prove the same savings for Outlook, Excel or every desktop application.

The browser environments differed: Computer Use used its own Chrome session, while the node used kiosk Chrome in a private
X11 session. The compact route was added later. This is a three-dataset pilot, not a broad performance guarantee.
Definitions and framework development are separate from routine use; their total model usage was not isolated.
Separate image-token and tool-schema-token breakdowns are unavailable. Tokens are not the same as billed dollars.

## Claude pilot

Claude models were run as Claude Code agents in a disposable Docker lab with a desktop text editor (Mousepad).
An independent checker read the saved file from the host. The agents had only the k-lani-bot MCP tools or only a
screenshot/click/key/type tool set. Input tokens include cached input and the agent's fixed base context.

| Model and effort | Route | Task | Correct | Mean input tokens incl. cache |
| --- | --- | --- | ---: | ---: |
| Sonnet 5.5 `high` | Prepared cartridge | Short note (T1) | 3/3 | 271,883 |
| Sonnet 5.5 `high` | Screenshots | Short note (T1) | 3/3 | 301,417 |
| Sonnet 5.5 `high` | Prepared cartridge | Longer document (T2) | 3/3 | 364,635 |
| Sonnet 5.5 `high` | Screenshots | Longer document (T2) | 3/3 | 539,595 |
| Opus 5.5 `medium` | Prepared cartridge | Short note (T1) | 1/1 | 255,446 |
| Haiku 5.5 `xhigh`, earlier node | Saved function | Short note (T1) | 3/3 | 482,514 |
| Haiku 5.5 `xhigh`, earlier node | Saved function | Longer document (T2) | 3/3 | 671,267 |
| Haiku 5.5 `xhigh`, earlier node | Screenshots | Longer document (T2) | 3/3 | 577,778 |

About 46,500 tokens per round are the agent client's own system prompt and tool descriptions, so the client dominates
these totals; all k-lani-bot MCP responses of one task together were about 2,200 to 3,400 tokens. The Opus series was
stopped after one run to save quota. Haiku ran on an earlier node build: it was correct in all 12 runs with a saved
function, the six shown and six earlier ones, but read more context than screenshots because the function
result did not yet prove the saved state and the model re-checked it. Those reports were improved before the Sonnet runs;
Haiku has not been re-measured since. Without saved functions, a first Haiku attempt at the mail-to-Tryton task failed
and exposed gateway defects that were fixed afterwards; it has not been repeated with Haiku.

The pilot shows that smaller models complete saved actions correctly; Haiku's token saving is not yet shown.
It used one application. It does not measure which model authors cartridges or cross-program actions best, nor how
smaller models handle a saved action that spans several programs. Authoring both with Opus 5.5 at medium effort or
higher and leaving routine calls of the saved actions to smaller models is a practical choice, not a ranking.
Raw sessions stay private.

## Automatic capture evidence

Verified writes and tab navigation created reusable functions during their first ordinary UI use.
A separate acceptance test replaced both the browser process and the MCP frontend, then called a saved function with new text.
It verified the new text and returned a 156-byte result without a UI tree or image.
This proves reuse for those tested functions. Full-task model-token measurements of their second and third runs are still outstanding.

## Data and reproduction

- [Measured comparison and per-attempt results](../benchmarks/compact-ui-20261007/measurements.json)
- [First-use measurements](../benchmarks/first-use-visual-20261007/measurements.json)
- [Automatic capture and restart acceptance](../benchmarks/first-use-visual-20261007/automatic-acceptance.json)
- [Browser comparison harness](../modules/efficiency-benchmark/browser-workbench/measure.mjs)
- [Native capture/reuse harness](../modules/desktop-ui-mcp/learning-regression.mjs)
- [Private GUI session runner](../scripts/private-gui-session.sh)

The comparison harness needs the executables and isolated model access listed in its
[example configuration](../modules/efficiency-benchmark/browser-workbench/measurement.config.example.json).
Run GUI measurements only through the private session runner. Raw sessions, credentials and personal data stay outside Git.

Regenerate the README chart from the committed measurements with Python and `matplotlib==3.10.7`:

```bash
python scripts/render-readme-benchmark.py
```

The chart generator reads the measurements directly; it does not embed a separately maintained copy of the results.

[Back to README](../README.md)
