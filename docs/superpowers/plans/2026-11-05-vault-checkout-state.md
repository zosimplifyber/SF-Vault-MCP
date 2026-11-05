# Vault check-out, check-in, undo, state change: plan

Spec: `docs/superpowers/specs/2026-11-05-vault-checkout-state-design.md`.

1. Core contracts and rules (`VaultFileOps.cs`): facts records, `IVaultFileOps`, per-operation blocker and warning rules, the leaving-Released flag. Tests first with a fake host.
2. Tools (`VaultFileOpsTools.cs`): four preview/apply pairs, plan store, hash, drift check, include_children ordering. Tests.
3. Add-in host (`VaultFileOpsHost.cs`) and wiring in `AddInServer`. Build against the real Vault and Inventor assemblies.
4. Docs: README tool table, VERSION 1.5.0, tool descriptions, bridge status tool count.
5. Release zip, backup and install 1.5.0 (Inventor closed), live checklist in the ledger.
