"""
CAD-only BOM and purchasing tools: no engineering items needed.

Inventor stores its BOM in Vault every time an assembly is checked in. These
tools read that stored BOM through the SDK bridge on a read-only session (no
license seat, so they work while Inventor and Vault Explorer are open) and
turn it into the same table an Inventor structured all-levels export gives,
so the purchasing sheet treats it exactly like a manual export.
"""

import asyncio
import json
from pathlib import PurePath
from typing import Any, Dict, List, Optional, Tuple

import vault_slim as slim
from vault_write_tools import find_file


def _dump(obj: Any) -> str:
    return json.dumps(obj, ensure_ascii=False, default=str)


def effective_source(row: Dict[str, Any]) -> Tuple[str, bool]:
    """Buy / Make / Other for a CAD row, and whether it was inferred.

    The Source iProperty wins. Without it, Inventor's Purchased structure or a
    Content Center part means Buy; anything else is Make and gets flagged.
    """
    src = str(row.get("source") or "").strip()
    if src:
        return src, False
    if row.get("bom_structure") == "Purchased" or row.get("content_center"):
        return "Buy", True
    return "Make", True


def missing_source(rows: List[Dict[str, Any]]) -> List[str]:
    """Files with no Source iProperty that are not obviously bought hardware."""
    out = []
    for r in rows:
        if not r.get("source") and not r.get("content_center") and r.get("bom_structure") != "Purchased":
            name = str(r.get("file") or "")
            if name and name not in out:
                out.append(name)
    return out


def rows_to_inventor_table(rows: List[Dict[str, Any]]):
    """CAD rows as an Inventor structured-export DataFrame for bom_purchasing."""
    import pandas as pd

    records = []
    for r in rows:
        source, _ = effective_source(r)
        records.append({
            "Item": r.get("row"),
            "Part Number": r.get("part_number") or PurePath(str(r.get("file") or "")).stem,
            "Title": r.get("title"),
            "BOM Structure": source,
            "Unit QTY": r.get("units") or "Each",
            "QTY": r.get("qty"),
            "Stock Number": r.get("stock_number"),
            "Description": r.get("description"),
            "REV": r.get("revision"),
            "Material": r.get("material"),
            "File Name": r.get("file"),
        })
    return pd.DataFrame.from_records(records)


def register(mcp, api, resolved_vault) -> None:

    async def load(vault: str, file: str) -> Tuple[Optional[Dict[str, Any]], Optional[Dict[str, Any]]]:
        """Resolve the assembly and read its stored BOM -> (cad_bom view, error)."""
        ref = file.strip()
        fv, err = (None, "")
        if "." not in PurePath(ref).name and not ref.isdigit():
            fv, err = await find_file(api, vault, ref + ".iam")
        if fv is None:
            fv, err = await find_file(api, vault, ref)
        if fv is None:
            return None, {"error": True, "message": err}
        master_id = (fv.get("file") or {}).get("id")
        try:
            import vault_sdk
            data = await asyncio.to_thread(vault_sdk.get_cad_bom, master_id)
        except Exception as exc:  # noqa: BLE001
            return None, {"error": True, "message": f"{type(exc).__name__}: {exc}"}
        if not data.get("found"):
            return None, {
                "error": True,
                "message": f"{fv.get('name')} has no stored Inventor BOM. Open it in Inventor and check it in.",
            }
        view = slim.cad_bom(data)
        notes = []
        if view["assembly"].get("checked_out"):
            notes.append(
                f"{fv.get('name')} is checked out; this is the BOM from the last check-in "
                f"({view['assembly'].get('checked_in')}). Edits since then are not included."
            )
        gaps = missing_source(view["rows"])
        if gaps:
            notes.append("No Source iProperty (treated as Make): " + ", ".join(gaps))
        if notes:
            view["notes"] = notes
        return view, None

    @mcp.tool()
    async def vault_get_cad_bom(file: str, vault_id_param: str = "") -> str:
        """
        Multi-level BOM of an Inventor assembly straight from the CAD data, no
        engineering items needed. Reads the BOM Inventor stored in Vault at the
        last check-in: every level, per-parent quantities, and each part's
        Part Number, Description, Source (Buy/Make/Other), Vendor, Stock
        Number and Material. Reference parts are left out and phantoms are
        flattened, as in Inventor's structured BOM view.

        Works while Inventor and Vault Explorer are open (read-only session).

        Args:
            file: Assembly file name, e.g. "CD-001882.iam" or "CD-001882"
                (the .iam is assumed), or a file id.
            vault_id_param: Vault ID. Leave empty to use the vault from config.json.
        """
        view, err = await load(resolved_vault(vault_id_param), file)
        return _dump(err or view)

    @mcp.tool()
    async def vault_generate_purchasing_sheet_from_cad(
        file: str, output_dir: str = "", vault_id_param: str = ""
    ) -> str:
        """
        Purchasing sheet for an Inventor assembly from its CAD data alone: no
        engineering items, no manual BOM export. Reads the BOM Inventor stored
        at the last check-in, marks bought rows from the Source iProperty
        (Content Center hardware counts as Buy), fills vendor, cost and lead
        time from the Engineering Purchased Parts Microsoft List, and writes
        "<assembly>-PurchasingExport.xlsx".

        Check the assembly in first so Vault has the current BOM.

        Args:
            file: Assembly file name, e.g. "CD-001882.iam" or "CD-001882", or a file id.
            output_dir: Folder for the .xlsx. Defaults to the Desktop.
            vault_id_param: Vault ID. Leave empty to use the vault from config.json.
        """
        view, err = await load(resolved_vault(vault_id_param), file)
        if err:
            return _dump(err)
        import bom_purchasing

        assembly = PurePath(str(view["assembly"].get("file") or file)).stem
        table = rows_to_inventor_table(view["rows"])
        result = await asyncio.to_thread(
            bom_purchasing.generate_from_dataframe, table, assembly, output_dir
        )
        if view.get("notes"):
            result.setdefault("warnings", []).extend(view["notes"])
        result["bom_rows"] = view["row_count"]
        return _dump(result)
