# Build your programmable hands, then use them

**You are the controlling AI and the author of your tools.** Use k-lani-bot's existing screenshot-assisted controls to
understand an unfamiliar operation. Turn what you learn into a parameterized, executable MCP function, verify it,
save it and use it yourself. Optimize the saved action when a real missing case is found.

The routine caller supplies business inputs and receives a checked result. The procedure handles its clicks, text
entry, waits, dialog changes and checks without asking a model to choose each step again. Aim for one call per complete
business operation. Count authoring and routine use separately; fewer calls are a design result, not measured token savings.

## Three things to keep separate

| Thing | Responsibility | Mail → Tryton example |
| --- | --- | --- |
| Node | Operates applications on its computer under its access policy | The private desktop running Thunderbird and Tryton |
| Application cartridge | Implements tested capabilities for a specific application variant/build | Read a Thunderbird message; create a Tryton draft |
| Saved action | Combines capabilities and passes their data to complete a task | Read an order, interpret it, create and verify the draft |

The agent writes on its host. The gateway exposes the resulting functions through MCP. A **local macro bundle** runs
its control flow in that gateway and sends operations to the registered node. The node does not need the agent's model
or credentials. Host and node can be different computers. Native status/consent indicators remain on the operated node.

Replacing Thunderbird should change the mail implementation and its binding, not the order-processing action. This
requires a common data contract: message identity, sender, subject and body; customer, order reference and product/quantity
pairs; then a verified draft identity and result. Treat these as **logical capability contracts**, not existing tool names.
Current callable names and schemas come from discovery. An adapter must genuinely satisfy the contract before substitution.

## The author/operator cycle

1. **Identify.** Probe the existing node and the actual application variant/build. Select its window by observed identity.
2. **Reuse.** Inspect the task branch of its cartridge. Call an already qualified action when it fits.
3. **Explore a missing capability.** Use the existing screenshot controls and semantic observations. Work through one
   authorized example. Discover required fields, keyboard behavior, dialogs, waits, failure cases and the saved result.
4. **Program it.** Record stable semantic selectors, input parameters, transitions and output checks in executable code
   or a supported recipe. Group the deterministic steps into a complete business operation.
5. **Qualify.** Run the procedure, read back its business result, and test different input values after a fresh connection.
   Test a refused/partial case too. Never promote “input dispatched” or “dialog closed” to full business success.
6. **Save and expose.** Install the checked definition in the appropriate cartridge or supported action store. Load its
   actual MCP schema. A file on disk alone is not proof that the gateway can execute it.
7. **Use your new capability.** Make the MCP call yourself. In routine work, use the saved actions; keep low-level UI
   results outside model context. A result verifying the complete requested outcome needs no redundant screenshot.
8. **Improve deliberately.** On an uncovered case, inspect the discrepancy, adapt the definition, qualify and save its
   revision. Resume only from known state; never blindly replay a partially completed write.

Do not recreate a function for every input or leave a successful discovery as a chat transcript. Do not move an
unchanged model reasoning loop inside a server and call that token savings. Free-form mail interpretation can still need
the controlling AI: read the necessary content once, produce typed order data, then execute its deterministic entry locally.

## Version changes

An unfamiliar build is a compatibility question, not permission to rename the old version field. Confirm the program
identity, probe the functions the task needs, and test their behavior in an authorized fixture. Revalidate unchanged
behavior; repair changed selectors/transitions/checks; derive a cartridge for a newly qualified application build.
Keep one active cartridge per variant/build, with its revision inside the manifest. Preserve the previously qualified
build. A native API may qualify a version range; UI/layout-dependent selectors may require an exact build.

Start from the `other_builds` report that `ui_info` adds when the new build has no cartridge but an older one does
([operations guide](AGENT-OPERATIONS.md)). It is a probe, not an approval: no function becomes callable from it.
`test_run_needed` means every stored target still resolves in the current window, so the function needs only a test run in
an authorized fixture. `repair_needed` names the first step whose target no longer resolves; explore only that step and the
ones after it, not the whole function. `unchecked` names a window that must be open first; open it and call `ui_info` again.
Read the per-step detail through its `evidence_ref` before exploring. The report never derives the new cartridge: author and
verify the procedure for the new build as above, then keep the previous build's cartridge unchanged.

Application replacement follows the same rule. Bind the saved action to a different qualified mail cartridge only
after its read result satisfies the same contract. Missing capabilities must be implemented or reported explicitly.

## An executable example: make a whole Tryton order one call

Use the [native MCP quickstart](GETTING-STARTED.md#connect-your-mcp-client) to connect the existing node and a reviewed
cartridge library. Before executing this example, use a disposable Tryton database or a separately verified application
backup. Prepare two existing customers/products and synthetic order mails in an authorized test environment. Compiler
tests need none of those applications and do not write to them.

The existing laboratory task reads order mails in Thunderbird and creates Tryton drafts with existing products.
The new example is in [`examples/agent-authoring/mail-to-tryton`](../examples/agent-authoring/mail-to-tryton/).
Its source fragments are real UI procedures for Tryton **6.0.34-1**, English locale, GTK3/X11 on Ubuntu 24.04.
They demonstrate how screenshot-guided discovery becomes native `find_elements`, `type_text`, focus/keyboard and
verification steps. They are not a screenshot detector or a promise of compatibility with every Tryton build.

Observed decisions encoded in the procedure:

- Open Sales by semantic focus and keyboard activation; generic activation of this Tryton menu can fail.
- Bind fresh targets on every run. Never save process IDs, native element IDs, screenshots or customer data as selectors.
- Verify full text replacement before moving on. Wait for the line dialog instead of guessing a delay.
- Use fresh, bounded field/row layouts for this exact build. Positional field mappings are a known limitation: a changed
  layout needs qualification, not selecting the first vaguely matching field.
- After saving, reread customer, reference and the product/quantity pair of each row. Check the save state too.

Compile the two-position procedure once from the repository root:

```bash
python3 examples/agent-authoring/mail-to-tryton/build.py \
  .artifacts/agent-example/programs/tryton/6.0.34-1 --lines 2
python3 -m unittest discover -s examples/agent-authoring/mail-to-tryton -p 'test_*.py'
```

The output is a native local macro bundle with one new function, `tryton_create_draft_sale`; no new MCP server or model
process is required. Review its recipe and pinned manifest. In the authorized host library, merge the new entrypoint,
recipe file and file pin into the existing Tryton cartridge; retain its other functions. If this is an empty test library,
the generated `programs/tryton/6.0.34-1` directory can be used directly. Never overwrite an unrelated active manifest.

Use the existing compact native gateway with that library (`--cartridges`); keep its existing node registry. Call
`ui_info` to identify the actual Tryton process, select the cartridge through `cartridge_select` when needed, then load
the schema with `cartridge_browse`. The deployed function must be visible before calling it. An already running selected
cartridge may need reselection or a fresh gateway connection after a reviewed update.

For the generated two-position schema, one `cartridge_call` request is:

```json
{
  "function": "tryton_create_draft_sale",
  "arguments": {
    "pid": 123,
    "customer": "Cafe Mond",
    "reference": "4712",
    "product_1": "K-100",
    "quantity_1": "5",
    "product_2": "Z-010",
    "quantity_2": "2"
  }
}
```

Here `123` is an illustrative PID; replace it with the observed process. Customer, reference, product and quantity values
come from the current mail, not from these example literals. The gateway performs the whole procedure and checks locally.
The controlling AI does not call “new”, “add line”, “add line”, “save” separately anymore.
The [native acceptance](../examples/agent-authoring/mail-to-tryton/acceptance.json) used the unchanged two-line definition
after a gateway restart: two current mail orders, four correct positions, one outer MCP call per order and 79 internal
steps per call. An independent database check confirmed the drafts. Missing input was refused before application input.
The one- and three-line variants have compiler tests only; no model-token savings were measured.
This is dated native evidence for the recorded pre-publication build. Its original source commit remains in the
maintainer's private development archive; it is not a claim that every subsequent release was rerun against Tryton.

**Current executable scope:** the native macro format has no variable-length loop input. `--lines 1`, `2` or `3` chooses
the signature during authoring; compile once for the task's shape and reuse with different values. Changing that count
changes the definition and needs qualification. Do not describe this example as a general array-based order API.
Quantities use canonical positive whole-number UI text; products must be distinct. Failed input remains a partial run,
without automatic rollback or idempotent retry. The application must have no unsaved draft or unexpected dialog at entry.
The example creates drafts only; it does not confirm sales, delete messages or move mail.

This completes one business operation in one outer MCP call. Mail reading and free-text interpretation remain separate
capabilities in the full action. Combining them is further authoring, not a reason to mislabel a shell call containing
several MCP calls as a single MCP call. Keep outer calls, internal node steps, success and measured model usage separate.

## Short instruction to give a smaller agent

> You are the controlling AI. k-lani-bot gives you programmable hands: build the missing application capabilities and
> then use them yourself through MCP. Identify the program and build. Reuse qualified cartridge functions; for a missing
> function, use the existing screenshot/UI controls to discover and verify the operation, write a parameterized procedure,
> save it in the appropriate cartridge and call it with fresh inputs. Compose application capabilities into saved actions
> so an application can be replaced through a compatible cartridge. During routine work use the saved actions only;
> investigate the UI again when a result or version change requires repair. Aim for one business-operation call, verify
> its complete result locally and report a compact outcome. Keep authoring cost separate. If the current route cannot
> save or load the needed procedure, name that precise gap; do not pretend another click loop is finished automation.

See [operations](AGENT-OPERATIONS.md) for route-specific schemas, [workflows](workflows.md) for the current generated-function
engine, and [compatibility](compatibility.md) for revalidation. These mechanisms have different contracts; discover them.
