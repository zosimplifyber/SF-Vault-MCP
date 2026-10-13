"""SFVaultTools: command-line entry for the SF Vault Bridge ribbon buttons.

Built to a windowed one-file exe (build_vault_tools_exe.bat) and run by the
Inventor add-in. A windowed exe has no stdout, so every result goes to the
file named by --result. Specs:
docs/superpowers/specs/2026-10-12-ribbon-purchasing-sheet-design.md
docs/superpowers/specs/2026-10-12-ribbon-design-review-package-design.md
docs/superpowers/specs/2026-10-13-ribbon-manufacturing-packages-design.md
"""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
from typing import Any

import bom_purchasing as bp


def write_result(path: str, result: dict[str, Any]) -> None:
    Path(path).parent.mkdir(parents=True, exist_ok=True)
    Path(path).write_text(json.dumps(result, ensure_ascii=False, default=str), encoding="utf-8")


def reference_note(source: str, signin_needed: bool) -> str:
    """One plain sentence on where the sheet's prices came from."""
    if source == "mslist":
        return "Prices from the purchasing Microsoft List."
    if source == "excel":
        if signin_needed:
            return ("Not signed in to the purchasing Microsoft List; used purchased items.xlsx. "
                    "Sign in once with the BOM Purchasing tool's Sign in button.")
        return "Prices from purchased items.xlsx."
    return "No purchasing list found; vendor and cost columns are blank."


def run_purchasing(bom: str, assembly: str, out_dir: str, name: str,
                   source: str | None = None,
                   sales_tax_rate: float = bp.DEFAULT_SALES_TAX_RATE) -> dict[str, Any]:
    res = bp.generate_from_file(bom, assembly, out_dir, source=source,
                                sales_tax_rate=sales_tax_rate)
    if res.get("error"):
        return {"ok": False, "error": str(res.get("message") or "The purchasing engine failed.")}
    src = Path(res["output_path"])
    dst = Path(out_dir) / name
    if src.resolve() != dst.resolve():
        os.replace(src, dst)
    source = str(res.get("reference_source") or "none")
    if source not in ("mslist", "excel"):
        source = "none"
    return {
        "ok": True,
        "reference": source,
        "reference_note": reference_note(source, bool(res.get("reference_signin_needed"))),
        "workbook": str(dst),
        "lines": int(len(bp.read_bom_file(bom))),
        "matched": int(res.get("matched_parts") or 0),
        "total_purchased": int(res.get("total_purchased_parts") or 0),
        "unmatched": [str(u) for u in (res.get("unmatched_parts") or [])],
        "warnings": [str(w) for w in (res.get("warnings") or [])],
    }


def _parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(prog="SFVaultTools")
    sub = p.add_subparsers(dest="command", required=True)
    pur = sub.add_parser("purchasing")
    pur.add_argument("--bom", required=True)
    pur.add_argument("--assembly", required=True)
    pur.add_argument("--out", required=True)
    pur.add_argument("--name", required=True)
    pur.add_argument("--result", required=True)
    pur.add_argument("--source", default=None)
    pur.add_argument("--sales-tax-rate", type=float, default=bp.DEFAULT_SALES_TAX_RATE)
    rev = sub.add_parser("review")
    rev.add_argument("--bom", required=True)
    rev.add_argument("--manifest", required=True)
    rev.add_argument("--out", required=True)
    rev.add_argument("--result", required=True)
    pkg = sub.add_parser("package")
    pkg.add_argument("--manifest", required=True)
    pkg.add_argument("--out", required=True)
    pkg.add_argument("--result", required=True)
    return p


def main(argv: list[str]) -> int:
    args = _parser().parse_args(argv)
    try:
        if args.command == "purchasing":
            result = run_purchasing(args.bom, args.assembly, args.out, args.name, args.source,
                                    args.sales_tax_rate)
        elif args.command == "package":
            import manufacturing_package
            result = manufacturing_package.run_package(args.manifest, args.out)
        else:
            import review_package
            result = review_package.run_review(args.bom, args.manifest, args.out)
    except Exception as exc:  # the add-in must always get a result file
        result = {"ok": False, "error": f"{type(exc).__name__}: {exc}"}
    write_result(args.result, result)
    return 0 if result.get("ok") else 1


if __name__ == "__main__":
    import sys
    sys.exit(main(sys.argv[1:]))
