# Screenshot discovery → an authored MCP action → reuse

Read the [agent walkthrough](../../../docs/AGENT-AUTHORING.md) first. This example is a native cartridge function,
not a new MCP server or a client-side loop around previously exposed business functions.

`fragments/` contains the observed Tryton UI operations. `build.py` compiles them into the new function
`tryton_create_draft_sale`, with unique temporary capture names, input parameters and local result checks.
One call replaces separate new-order, add-line and save calls. The gateway executes the primitive steps on the node.

```bash
python3 examples/agent-authoring/mail-to-tryton/build.py \
  .artifacts/agent-example/programs/tryton/6.0.34-1 --lines 2
python3 -m unittest discover -s examples/agent-authoring/mail-to-tryton -p 'test_*.py'
```

The output directory must not exist. Installation/reselection and the exact invocation are in the walkthrough.
Retain other functions when adding this entry to an existing cartridge. Never replace a version check with a guessed build.

Qualification scope: Tryton 6.0.34-1, English UI, Ubuntu 24.04, GTK3/X11. An idle saved document/list is required.
The task creates drafts using existing customers/products. It does not confirm a sale or change mail folders.
The compiled line count is fixed, from one to three; quantities are canonical whole-number text and products distinct.
Compile during authoring, then reuse the same bytes with new order values. Different line counts or builds require
a separately checked definition revision. The native format currently has no variable-length loop parameter.

Verification reads the full text immediately after entry, the current saved header and each product/quantity pair,
and Tryton's save status. Snapshot-bound IDs are checked before another observation replaces their snapshot.
Errors retain partial progress; there is no automatic replay, rollback or duplicate-order protection in this example.
The [native acceptance](acceptance.json) checks two mail orders/four positions after restarting the gateway, using the
unchanged two-line definition and one outer MCP call per order (79 internal steps). The independent database oracle
confirmed both drafts. A missing parameter was rejected without changing the application. One- and three-line variants
have compiler tests only. No model-token comparison was run.
