# k-lani-bot – OrcaSlicer 2.4.2 Cartridge

Local macro bundle, cartridge version 1.0.0. Target program: official OrcaSlicer 2.4.2. No OrcaSlicer binaries, no Python and no Blender code are part of this package. It is not a publicly approved registry package.

## Workflows and limits

- `inspect-ui.json`: observes only the accessibility surface exposed by the selected process. It does not claim to know unknown printer, filament or process profiles.
- `import-stl-linux-en.json`: real, automatically passed STL import into a fresh Untitled project (Linux/X11/GTK, English UI). Switches to preparation with Ctrl+N, opens the file dialog, sets the path literally and verifies that the dialog closed and the actual project title. No statement about printability, valid print profiles or mesh safety.
- `import-stl-fixture.json`: executable import workflow for the **synthetic test double**. No production import; no approval for real file pickers.

The real GUI acceptance for this narrow workflow passed once with the final macro. An earlier worker abort remains documented as an open robustness finding; a comprehensive reliability/security approval is missing. A dedicated test profile restricts the container to network `none`, a read-only root file system, no Linux capabilities, `no-new-privileges`, 2 CPUs and a synthetic workspace. OrcaSlicer's native child processes need more than the generic 128 tasks: the real app is tested in the **development trial** with 512 tasks and 1536 MiB. This changes no production defaults. No Docker socket, host X11, device, key or home-directory access is granted.

Windows/macOS, German UI, further file-picker/desktop variants, profile editing, G-code, slice/export, 3MF/ZIP projects, plugins, cloud and printer control are not approved by this first evidence. A model parser is itself an attack surface; an STL file is not trustworthy because of its extension. Third-party models belong in the same isolated acceptance.

## Installation through existing MCP tools

The operator selects the node and the actual process; an application window title alone is not enough for the program build identity. The current Linux window list reports no reliably detected application version. The bundle therefore does not activate automatically based on the title.

1. Read and check the desired JSON file as a `MacroDefinition`. For `save_macro`, remove the fields `version` and `sha256`; keep `name`, `description`, `parameters`, `steps`, `postconditions`. The authorization principal needs `macro.write`; the import fixture also needs `ui`.
2. Send `save_macro` with a fresh UUIDv7 `request_id`. The node generates the canonical hash; the operator pins it. Do not accept a freely chosen SHA or author approval.
3. Run `run_macro` with `name`, exactly this `sha256`, a fresh UUIDv7 and `params: {"pid": actual_pid}`. For the import workflows also pass `model_path`; for the real app also `expected_project_title` (for the test cube: `*synthetic-cube - OrcaSlicer`). The real import cartridge requires a fresh Untitled project, closed first-run dialogs and an explicitly permitted STL path. These parameters are currently no STL/file-permission boundary enforced by the node; run only in an approved, isolated job profile.
4. After the run, check the actual outcomes/postconditions. Do not automatically repeat a mutation after an unknown result.

This explicit local macro installation does not replace the still missing registry/signature/revocation broker. A `ui` principal can currently run other raw UI tools; the effect profile in `cartridge.json` is a review declaration and no additional OS sandbox.

## Reproducible test

The official AppImage from the GitHub release is provided only in `.artifacts/orcaslicer`. Expected download hash:

`d12fb8c8eac1aecd2dfb6377acd48f994f8fa439ed5292fa532dd82880f029fd`

The binary is not committed. For the test setup see `scripts/test-orcaslicer.sh`. The GTK fixture is C# with native system libraries, no NuGet/Python dependency. The ASCII STL test model was generated from our own cube coordinates. Results are stored in `.artifacts/orcaslicer`; `secured` remains false.

Official sources: [OrcaSlicer 2.4.2 release](https://github.com/OrcaSlicer/OrcaSlicer/releases/tag/v2.4.2), [OrcaSlicer repository](https://github.com/OrcaSlicer/OrcaSlicer). The recipe labels are based on visible UI contracts, not on copied OrcaSlicer source code.

Final acceptance runs: `scripts/test-orcaslicer.sh fixture` and `scripts/test-orcaslicer.sh real-replay`. `real` opens an interactive, isolated diagnostic mode. The real import remains `probably_harmful`, test evidence `real_app_tested`, independent review approval false.
