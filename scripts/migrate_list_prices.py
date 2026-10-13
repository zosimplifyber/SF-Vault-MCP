"""Copy "Cost Per" from the purchasing Microsoft List into each part's
Inventor "Estimated Cost" (Design Tracking "Cost") through the SF Vault Bridge.

    py -3.14 scripts/migrate_list_prices.py --report out.csv            (default, read-only)
    py -3.14 scripts/migrate_list_prices.py --apply --only CD-001625    (one-part pilot)
    py -3.14 scripts/migrate_list_prices.py --apply --include-released --batch 20

Report mode reads the list and looks files up in Vault (read-only REST), then asks
the bridge for fix_data_card DRY RUNS to learn each part's current Estimated
Cost. It writes nothing to Vault. --apply writes through the bridge (Inventor
must be running and signed in to Vault). Released files are only touched with
--include-released, which sends reviseReleased to the bridge.
"""
from __future__ import annotations

import argparse
import asyncio
import csv
import math
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable, Iterable, Iterator, Sequence

PROJECT_ROOT = Path(__file__).resolve().parent.parent
for _p in (str(PROJECT_ROOT), str(PROJECT_ROOT / "scripts")):
    if _p not in sys.path:
        sys.path.insert(0, _p)

CONTENT_CENTER_CATEGORY = "part - content center"
CONTENT_CENTER_ROOT = "$/content center files"
EXTENSIONS = (".ipt", ".iam")

REPORT_COLUMNS = ["Number", "Vault path", "state", "category", "current Estimated Cost",
                  "new price", "action", "detail"]
RESULT_COLUMNS = ["Number", "file", "status", "version", "detail"]


# ----------------------------------------------------------------------------- pure rules

def parse_price(value: Any) -> float | None:
    """A positive finite number from a list cell ("$1,234.50" ok), else None."""
    if value is None or isinstance(value, bool):
        return None
    if isinstance(value, (int, float)):
        number = float(value)
    else:
        text = str(value).strip().replace("$", "").replace(",", "")
        try:
            number = float(text)
        except ValueError:
            return None
    return number if math.isfinite(number) and number > 0 else None


def format_price(price: float) -> str:
    """Invariant text with at least two decimals and no float noise: 4.5 -> "4.50"."""
    text = f"{price:.4f}".rstrip("0")
    whole, _, frac = text.partition(".")
    return f"{whole}.{frac.ljust(2, '0')}"


def same_number(a: Any, b: Any) -> bool:
    x, y = parse_number(a), parse_number(b)
    return x is not None and y is not None and abs(x - y) < 0.00005


def parse_number(value: Any) -> float | None:
    if value is None or isinstance(value, bool):
        return None
    try:
        number = float(str(value).strip().replace("$", "").replace(",", ""))
    except ValueError:
        return None
    return number if math.isfinite(number) else None


def chunks(items: Sequence[Any], size: int) -> Iterator[list[Any]]:
    if size < 1:
        raise ValueError("batch size must be at least 1")
    for i in range(0, len(items), size):
        yield list(items[i:i + size])


@dataclass
class PlanRow:
    number: str
    price: float | None
    path: str = ""
    name: str = ""
    state: str = ""
    category: str = ""
    current: str = ""
    action: str = ""
    detail: str = ""
    error: str = ""
    skipped: str = ""
    note: str = ""
    extra: dict[str, Any] = field(default_factory=dict)

    @property
    def target(self) -> str:
        """What goes to fix_data_card: the full $/ path when known, else the file name."""
        return self.path or self.name

    def new_price(self) -> str:
        return format_price(self.price) if self.price is not None else ""


def is_released(state: str) -> bool:
    return state.strip().lower() == "released"


def is_content_center(category: str, path: str) -> bool:
    return (category.strip().lower() == CONTENT_CENTER_CATEGORY
            or path.strip().lower().startswith(CONTENT_CENTER_ROOT))


def classify(row: PlanRow) -> str:
    """The row's action once its file facts and current cost are known."""
    if row.price is None:
        return "no price"
    if not row.name:
        return "no file"
    if is_content_center(row.category, row.path):
        return "content center"
    if row.error:
        return "error"
    if row.skipped:
        return "skipped"
    if same_number(row.current, row.new_price()):
        return "same"
    if is_released(row.state):
        return "released-revise"
    return "update"


class VaultLookupError(RuntimeError):
    """A Vault search failed (not "no such file"). ``fatal`` = auth/session class."""

    def __init__(self, message: str, fatal: bool = False):
        super().__init__(message)
        self.fatal = fatal


class RunStopped(RuntimeError):
    """The run cannot continue (session lost); ``plan`` holds the rows seen so far."""

    def __init__(self, message: str, plan: list["PlanRow"]):
        super().__init__(message)
        self.plan = plan


AUTH_MARKERS = ("401", "403", "unauthorized", "forbidden", "not_signed_in", "sign in",
                "sign-in", "signed in", "session", "token", "expired", "credential")


def is_auth_failure(message: str) -> bool:
    low = message.lower()
    return any(marker in low for marker in AUTH_MARKERS)


class AmbiguousMatch(ValueError):
    """Files with the exact name exist in more than one Vault folder."""

    def __init__(self, name: str, folders: list[str]):
        super().__init__(f"{name} exists in several folders: {', '.join(folders)}")
        self.folders = folders


def _folder_of(record: dict[str, Any]) -> str:
    folder = record.get("folder") or {}
    return str(folder.get("fullName") or folder.get("path") or "").rstrip("/")


def pick_latest(records: Sequence[dict[str, Any]], wanted: str) -> dict[str, Any] | None:
    """The exact (case-insensitive) name match with the highest version number.

    Raises AmbiguousMatch when exact matches sit in more than one folder.
    """
    low = wanted.strip().lower()
    exact = [r for r in records if str(r.get("name", "")).strip().lower() == low]
    if not exact:
        return None
    folders = sorted({_folder_of(r) for r in exact}, key=str.lower)
    if len(folders) > 1:
        raise AmbiguousMatch(wanted, [f or "(unknown folder)" for f in folders])

    def version(r: dict[str, Any]) -> int:
        for key in ("version", "versionNumber"):
            try:
                return int(r.get(key))
            except (TypeError, ValueError):
                continue
        return -1
    return max(exact, key=version)  # max keeps the first of equals


def plan_rows(rows: Iterable[dict[str, Any]], lookup: Callable[[str], dict[str, Any] | None],
              only: Sequence[str] | None = None) -> list[PlanRow]:
    """List rows -> plan rows with the Vault facts filled in.

    ``rows`` are dicts with ``Number`` and ``Cost Per``. ``lookup`` returns
    ``{name, path, state, category}`` for the latest file named <Number>.ipt or
    .iam, or None. Rows without a positive price are kept (action "no price")
    and never looked up. Duplicate numbers keep the first row.
    """
    wanted = {n.strip().lower() for n in only} if only else None
    seen: set[str] = set()
    plan: list[PlanRow] = []
    for raw in rows:
        number = str(raw.get("Number") or "").strip()
        key = number.lower()
        if not number or key in seen or (wanted is not None and key not in wanted):
            continue
        seen.add(key)
        row = PlanRow(number=number, price=parse_price(raw.get("Cost Per")))
        if row.price is not None:
            try:
                found = lookup(number)
            except AmbiguousMatch as exc:
                row.action = "ambiguous"
                row.detail = f"found in more than one folder: {'; '.join(exc.folders)}"
                plan.append(row)
                continue
            except VaultLookupError as exc:
                row.action = "lookup error"
                row.detail = str(exc)
                plan.append(row)
                if exc.fatal:
                    raise RunStopped(f"Vault lookup stopped at {number}: {exc}", plan) from exc
                continue
            if found:
                row.name = str(found.get("name") or "")
                row.path = str(found.get("path") or "")
                row.state = str(found.get("state") or "")
                row.category = str(found.get("category") or "")
        row.action = classify(row)
        plan.append(row)
    return plan


# ----------------------------------------------------------------------------- bridge calls

FixFn = Callable[..., dict[str, Any]]


def bridge_fix(files: list[str], *, dry_run: bool, revise_released: bool = False,
               cost: str = "") -> dict[str, Any]:
    """fix_data_card through the bridge client (kept local so the flag is optional)."""
    import vault_bridge
    return vault_bridge.fix_data_card(files, design={"Cost": cost} if cost else None, part_number_from_file_name=False,
                                      dry_run=dry_run, revise_released=revise_released)


def fill_current_costs(plan: list[PlanRow], fix: FixFn, batch: int = 20) -> None:
    """Dry-run fix_data_card to learn each file's current Estimated Cost, then re-classify.

    One dry run per distinct price would be exact but slow; instead each file is
    asked to change to its own new price, so the preview reports ``from`` (the
    current value) or ``no_change`` (already equal). Files the bridge skips keep
    a blank current cost.
    """
    targets = [r for r in plan if r.action in ("update", "released-revise", "same")]
    by_price: dict[str, list[PlanRow]] = {}
    for r in targets:
        by_price.setdefault(r.new_price(), []).append(r)
    try:
        for price, rows in by_price.items():
            for group in chunks(rows, batch):
                released = [r for r in group if is_released(r.state)]
                live = [r for r in group if not is_released(r.state)]
                for sub, flag in ((live, False), (released, True)):
                    if not sub:
                        continue
                    try:
                        reply = fix([r.target for r in sub], dry_run=True, revise_released=flag, cost=price)
                    except Exception as exc:  # noqa: BLE001
                        if _is_signed_out(exc):
                            for r in sub:
                                r.error = f"not_signed_in: {exc}"
                            raise StopSignIn(str(exc)) from exc
                        for r in sub:
                            r.error = str(exc)
                        continue
                    _apply_preview(sub, reply)
    except StopSignIn as exc:
        for r in plan:
            r.action = classify(r)
            r.detail = r.error or r.skipped or r.note
        raise RunStopped(str(exc), plan) from exc
    for r in plan:
        r.action = classify(r)
        r.detail = r.error or r.skipped or r.note


def _apply_preview(rows: list[PlanRow], reply: dict[str, Any]) -> None:
    for res in reply.get("results") or []:
        mine = _match(rows, str(res.get("file") or ""))
        if mine is None:
            continue
        status = res.get("status")
        if status == "error":
            mine.error = f"{res.get('step') or 'error'}: {res.get('message') or ''}".strip()
            if res.get("step") == "sign_in":
                for r in rows:
                    if not r.error:
                        r.error = mine.error
                raise StopSignIn(mine.error)
        elif status == "skipped":
            mine.skipped = str(res.get("reason") or res.get("message") or "skipped by the bridge")
        elif status == "no_change":
            mine.current = mine.new_price()
        elif status == "preview":
            change = (res.get("changes") or {}).get("Cost") or {}
            mine.current = "" if change.get("from") is None else str(change.get("from"))
            notes = []
            if res.get("would_revise"):
                transitions = res.get("transitions")
                if isinstance(transitions, (list, tuple)):
                    transitions = " -> ".join(str(t) for t in transitions)
                notes.append("would revise" + (f" ({transitions})" if transitions else ""))
            if res.get("linked_to_item"):
                notes.append(f"linked to item {res.get('linked_to_item')}")
            mine.note = "; ".join(notes)


def _match(rows: list[PlanRow], file: str) -> PlanRow | None:
    low = file.strip().lower()
    for r in rows:
        if low in (r.path.strip().lower(), r.name.strip().lower()) or low.endswith("/" + r.name.lower()):
            return r
    return None


class StopSignIn(RuntimeError):
    """The bridge lost its Vault sign-in; the run stopped."""


def _is_signed_out(exc: Exception) -> bool:
    return type(exc).__name__ == "BridgeUnavailable" or getattr(exc, "code", "") == "not_signed_in"


def _may_have_landed(exc: Exception) -> bool:
    return getattr(exc, "code", "") in ("timeout", "connection_lost")


def apply_updates(plan: list[PlanRow], fix: FixFn, *, batch: int = 20,
                  include_released: bool = False) -> list[dict[str, Any]]:
    """Write the new prices. Returns result rows; stops on the first not_signed_in."""
    todo = [r for r in plan if r.action == "update"
            or (include_released and r.action == "released-revise")]
    out: list[dict[str, Any]] = []
    by_price: dict[str, list[PlanRow]] = {}
    for r in todo:
        by_price.setdefault(r.new_price(), []).append(r)
    try:
        for price, rows in by_price.items():
            for group in chunks(rows, batch):
                try:
                    reply = fix([r.target for r in group], dry_run=False,
                                revise_released=include_released, cost=price)
                except Exception as exc:  # noqa: BLE001 - BridgeUnavailable / BridgeError
                    code = getattr(exc, "code", type(exc).__name__)
                    if type(exc).__name__ == "BridgeUnavailable" or code == "not_signed_in":
                        for r in group:
                            out.append(_result(r.number, r.target, "error", detail=f"not_signed_in: {exc}"))
                        raise StopSignIn(str(exc)) from exc
                    for r in group:
                        out.append(_result(r.number, r.target, "error", detail=str(exc)))
                    if _may_have_landed(exc):
                        raise StopSignIn(str(exc)) from exc  # do not queue more writes blind
                    continue
                signed_out = False
                revise_stop: tuple[str, str] | None = None
                for res in reply.get("results") or []:
                    mine = _match(group, str(res.get("file") or ""))
                    detail = res.get("reason") or res.get("message") or res.get("warning") or ""
                    if res.get("step") == "sign_in":
                        signed_out = True
                    if revise_stop is None and (res.get("step") == "re_release"
                                                or (include_released and res.get("status") == "error")):
                        revise_stop = (str(res.get("file") or ""), str(detail))
                    out.append(_result(mine.number if mine else "", str(res.get("file") or ""),
                                       str(res.get("status") or ""), res.get("version"), str(detail)))
                if signed_out:
                    raise StopSignIn("The bridge reported the Vault session dropped.")
                if revise_stop is not None:
                    # A failed re-release can strand the file in Work in Progress; the bridge's own stop lasts one
                    # request only, so end the whole run here and send nothing more.
                    print(f"STOPPED after a failed revise on {revise_stop[0]}: {revise_stop[1]}. "
                          "The remaining files were not attempted. Check that file in Vault before rerunning.",
                          file=sys.stderr)
                    raise StopSignIn(f"revise failed on {revise_stop[0]}")
    except StopSignIn:
        pass
    return out


def _result(number: str, file: str, status: str, version: Any = None, detail: str = "") -> dict[str, Any]:
    return {"Number": number, "file": file, "status": status,
            "version": "" if version is None else version, "detail": detail}


# ----------------------------------------------------------------------------- output

def write_report(path: str, plan: list[PlanRow]) -> None:
    _write_csv(path, REPORT_COLUMNS, [
        {"Number": r.number, "Vault path": r.path or r.name, "state": r.state,
         "category": r.category, "current Estimated Cost": r.current,
         "new price": r.new_price(), "action": r.action, "detail": r.detail}
        for r in plan])


def write_results(path: str, results: list[dict[str, Any]]) -> None:
    _write_csv(path, RESULT_COLUMNS, results)


def _write_csv(path: str, columns: list[str], rows: list[dict[str, Any]]) -> None:
    target = Path(path)
    target.parent.mkdir(parents=True, exist_ok=True)
    with target.open("w", newline="", encoding="utf-8-sig") as fh:
        writer = csv.DictWriter(fh, fieldnames=columns)
        writer.writeheader()
        writer.writerows(rows)


# ----------------------------------------------------------------------------- real sources

def load_list_rows() -> list[dict[str, Any]]:
    import purchasing_reference as pr
    cfg = pr.resolve_reference_config()
    df = pr.load_mslist_dataframe(cfg["mslist"], cfg.get("column_map"))
    return df.to_dict("records")


def make_vault_lookup() -> Callable[[str], dict[str, Any] | None]:
    """Read-only Vault REST lookup of the latest file named <Number>.ipt / .iam."""
    import check_file_properties as cfp
    from check_item_properties import load_json
    from vault_rest_api import VaultRestAPI

    async def _session():
        vault_cfg = load_json(cfp.CONFIG_PATH).get("vault") or {}
        api = VaultRestAPI(servername=vault_cfg["servername"])
        sign_in = await api.create_session(database=vault_cfg["database"],
                                           username=vault_cfg["username"],
                                           password=vault_cfg["password"])
        if sign_in["error"]:
            raise RuntimeError(f"Vault sign-in failed: {sign_in['data']}")
        vault_id = str((sign_in["data"].get("vaultInformation") or {}).get("id", "")
                       or sign_in["data"].get("vaultId", "") or "")
        return api, vault_id

    api, vault_id = asyncio.run(_session())

    def lookup(number: str) -> dict[str, Any] | None:
        for ext in EXTENSIONS:
            wanted = number + ext
            resp = asyncio.run(api.search_file_versions(
                vault_id=vault_id, query=wanted, prop_def_ids="all", limit=25))
            if resp["error"]:
                message = f"Vault search for {wanted} failed: {resp['data']}"
                raise VaultLookupError(message, fatal=is_auth_failure(message))
            payload = resp.get("data") or {}
            record = pick_latest(cfp._results(payload), wanted)
            if record is None:
                continue
            props = cfp.flatten_file_properties(record, cfp.extract_definition_index(payload))
            folder = record.get("folder") or {}
            base = str(folder.get("fullName") or folder.get("path") or "").rstrip("/")
            return {"name": record.get("name") or wanted,
                    "path": f"{base}/{record.get('name') or wanted}" if base.startswith("$") else "",
                    "state": props.get("State") or "",
                    "category": props.get("Category Name") or ""}
        return None

    return lookup


# ----------------------------------------------------------------------------- CLI

def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--report", metavar="OUT.csv", help="write the dry-run report (default mode)")
    p.add_argument("--apply", action="store_true", help="write the prices through the bridge")
    p.add_argument("--results", metavar="OUT.csv", default="migrate_list_prices_results.csv",
                   help="results CSV for --apply")
    p.add_argument("--include-released", action="store_true",
                   help="also revise Released files (sends reviseReleased)")
    p.add_argument("--only", nargs="+", metavar="NUMBER", help="only these part numbers")
    p.add_argument("--batch", type=int, default=20, help="files per bridge call (default 20)")
    return p


def main(argv: list[str] | None = None, *, list_loader: Callable[[], list[dict[str, Any]]] | None = None,
         lookup_factory: Callable[[], Callable[[str], dict[str, Any] | None]] | None = None,
         fix: FixFn | None = None) -> int:
    args = build_parser().parse_args(argv)
    if args.batch < 1:
        print("--batch must be at least 1", file=sys.stderr)
        return 2
    if args.apply and args.report:
        print("Use --report (dry run) or --apply, not both.", file=sys.stderr)
        return 2
    fixer = fix or bridge_fix
    rows = (list_loader or load_list_rows)()
    try:
        plan = plan_rows(rows, (lookup_factory or make_vault_lookup)(), args.only)
        fill_current_costs(plan, fixer, args.batch)
    except RunStopped as exc:
        out = args.report or "migrate_list_prices_report.csv"
        write_report(out, exc.plan)
        print(f"STOPPED: {exc}. Partial report: {out}. Nothing was written to Vault.", file=sys.stderr)
        return 1
    if not args.apply:
        out = args.report or "migrate_list_prices_report.csv"
        write_report(out, plan)
        print(f"Report: {out} ({len(plan)} rows)")
        return 0
    results = apply_updates(plan, fixer, batch=args.batch, include_released=args.include_released)
    write_results(args.results, results)
    errors = [r for r in results if r["status"] in ("error", "skipped")]
    print(f"Results: {args.results} ({len(results)} files, {len(errors)} errors)")
    return 1 if errors else 0


if __name__ == "__main__":
    raise SystemExit(main())
