# TODO — wit-bindgen-dotnet

Status snapshot and open work. Grounded in the unit suite + a new semantic-compile gate and
the e2e harness run (`tests/WitBindgen.E2E`, generated host bindings over the real wasmtime runtime).

Legend: **[confirmed]** = personally verified this session · **[reported]** = surfaced by the
e2e workflow's triage, spot-checkable but not each independently re-run.

## State

- Unit tests: **169 passed / 0 failed** [confirmed] (was 164; +5 semantic-compile-gate tests).
- e2e: **58 passed / 0 failed / 1 skipped** [confirmed] (was 45; +13 new round-trip cases).
  Skip = `EchoEntity` (host-gen nested-record bug, external).
- Guest-generator bugs #1–#5 below are **fixed and verified** (semantic gate + e2e runtime).
  #6 remains a deferred design item.

## Guest-generator bugs (this repo)

1. **Cast-vs-multiply `(T)*(ptr)`** — **DONE** [confirmed: gate + e2e].
   - Fixed all 6 emission sites (TODO originally listed 4; `list<enum>` lift actually flows
     through `WriteLiftListElement`, and the flags load was also affected):
     - `GuestImportWriter` `WriteMemoryLoad` enum + flags + variant-disc; `WriteLiftListElement` enum
       (also made width-correct: enum list stride is the discriminant size, not always 4).
     - `GuestExportWriter` `WriteMemoryLoad` enum; `WriteLiftListElement` enum; `WriteMemoryFree` variant-disc.
   - Verified: `echo-list-color` and `make-large-tagged` (string-payload variant return + post-return free)
     round-trip in e2e; `CodeGenerationTests` stale asserts updated to the `(T)(*(...))` form.

2. **`result<T,E>`** — **DONE** [confirmed: gate].
   - `WitTypeToCS` maps result → `WitBindgen.Runtime.WitResult<TOk, TErr>` (absent arm → `byte`
     placeholder). Added lift/lower + memory load/store/free in both writers, plus flat param
     paths. Covers `result<T,E>`, `result<T>`, `result<_,E>`, and bare `result`.
   - ponytail limitation: a result/variant param mixing float and same-width int arms is not
     bit-reinterpreted across the join (same gap on the lower side); no such type in examples.

3. **Variant params not lifted** — **DONE** [confirmed: gate].
   - Added `Variant` case to `GuestExportWriter.LiftParam` + `WriteLiftFromFlatParams` via a new
     `LiftVariantFromFlatParams` (mirrors the existing lower path).

4. **Primitive lists return garbage** — **DONE** [confirmed: e2e runtime].
   - Root cause: the export trampoline lifted blittable-primitive list params as a *zero-copy*
     `ReadOnlySpan<T>` over WASM memory, then freed that memory **before** the user impl read it
     (use-after-free). `list<string>` survived because it copies into a managed `string[]`.
   - Fix: moved the param string/list `Free` to **after** the user call (`WriteParamFree`).
   - Verified: `echo-list-u8/u32/f64` round-trip correctly in e2e (`[0,1,128,255]` → identical).

5. **`char` and `flags`** — **DONE** [confirmed: gate + e2e].
   - char (int core ↔ uint high-level) and flags (typed ↔ int) now cast on both param and return
     paths in both writers; flags returns no longer emit `return default`.
   - Verified: `echo-char`, `echo-permission` round-trip in e2e.

6. **Resources across the export boundary** — **DEFERRED** (larger design) [confirmed: code-read].
   - Confirmed: resources are only generated via `GuestImportWriter.WriteImportResource`
     (DllImport call-outs); there is **no** `WriteExportResource` path. A resource in an
     *exported* interface is therefore emitted as an *imported* resource, so it is not
     guest-owned / round-trippable.
   - Implementing guest-side resource *export* (rep table, `[export]…` dtor, method trampolines
     via `UnmanagedCallersOnly`) is a substantial new feature, not a point fix. Revisit as its
     own milestone. The interface import-direction claim should be re-verified separately.

## Host-generator bugs (external `wasmtime-dotnet` — report upstream, not fixable here)

- `EchoEntity` nested-record lowering: wasmtime "expected field `x`, got `c`" [confirmed].
- Tuple **returns**: generated code references undefined `result_0` (CS0103) [reported].
- `option<string>` param: emits `value.Value` on a `string?` (CS1061) [reported].
- `own<resource>` return: host generator crashes (NullReferenceException) [reported].

## Test-infrastructure

- **Semantic compile gate** — **DONE**. `tests/WitBindgen.Tests/SemanticCompileTests.cs` binds the
  generated trees against the real framework ref set + `WitBindgen.Runtime` and fails on any C#
  error (string-contains tests don't, which is how bug #1 — a *semantic* error — shipped green).
  Import-side is self-contained; export-side tests pass a small user-impl partial. The
  `WitBindgen.Tests` project now references `WitBindgen.Runtime`.
- **`examples/EcsHost`** — **EXCLUDED from the solution** (`WitBindgen.slnx`); files kept on disk.
  It does not compile against the bumped submodule (host generator moved to an interface-based
  `IQuery`/`ICommands` API). Port is a future task.
- e2e `EchoEntity` stays `[Fact(Skip=…)]` — re-enable when the host-gen nested-record bug is fixed.

## Open decisions — RESOLVED

- Committed the own/borrow work + e2e harness + the #1–#5 fixes + the semantic gate on `main`.
- EcsHost excluded from the solution (kept on disk) rather than ported or deleted.
