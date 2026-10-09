"""
Compact views of Vault REST v2 responses for the MCP tools.

The raw API repeats every lifecycle definition, colour, URL and property
definition on every record, so a three-row BOM came back as ~120k characters.
These helpers keep the fields an engineer actually reads (number, title, state,
revision, quantity, ids needed for the next call) and drop the rest. Every
slimmed tool also takes ``raw=True`` for the untouched payload.
"""

from typing import Any, Dict, List, Optional

# Keys that never help a reader: links back into the API and UI colours.
_NOISE_KEYS = {"url", "stateColor", "categoryColor", "color"}


def _date(value: Any) -> str:
    """Vault dates are ISO strings; keep the day. Year-1 means 'never'."""
    s = str(value or "")
    return "" if not s or s.startswith("0001-") else s[:10]


def _compact(d: Dict[str, Any]) -> Dict[str, Any]:
    """Drop empty values so absent fields cost nothing."""
    return {k: v for k, v in d.items() if v not in (None, "", [], {}) and v is not False}


def strip_noise(record: Any) -> Any:
    """Generic fallback: remove URLs and colours, recursively."""
    if isinstance(record, dict):
        return {k: strip_noise(v) for k, v in record.items() if k not in _NOISE_KEYS}
    if isinstance(record, list):
        return [strip_noise(v) for v in record]
    return record


def properties(record: Dict[str, Any]) -> Dict[str, Any]:
    """Collapse Vault's property list into {display name: value}."""
    out: Dict[str, Any] = {}
    for p in record.get("properties") or []:
        if not isinstance(p, dict):
            continue
        name = (p.get("definition") or {}).get("displayName") or p.get("propertyDefinitionId")
        value = p.get("value")
        if name and value not in (None, ""):
            out[str(name)] = value
    return out


def _state(record: Dict[str, Any]) -> str:
    return record.get("state") or (record.get("lifecycleState") or {}).get("name") or ""


# ----------------------------------------------------------------------
# Entities
# ----------------------------------------------------------------------

def item_version(iv: Dict[str, Any], *, with_properties: bool = False) -> Dict[str, Any]:
    out = _compact({
        "number": iv.get("number") or iv.get("name"),
        "title": iv.get("title"),
        "description": iv.get("description"),
        "revision": iv.get("revision"),
        "state": _state(iv),
        "category": iv.get("category"),
        "version": iv.get("version"),
        "modified": _date(iv.get("lastModifiedDate")),
        "modified_by": iv.get("lastModifiedUserName"),
        "comment": iv.get("comment"),
        "obsolete": iv.get("isLatestObsolete"),
        "item_version_id": iv.get("id"),
        "item_id": (iv.get("item") or {}).get("id"),
    })
    if with_properties:
        out["properties"] = properties(iv)
    return out


def item(record: Dict[str, Any], *, with_properties: bool = False) -> Dict[str, Any]:
    """An Item record wraps its latest itemVersion."""
    iv = record.get("itemVersion") or {}
    out = item_version(iv, with_properties=with_properties)
    out.setdefault("item_id", record.get("id"))
    return out


def folder(f: Dict[str, Any]) -> Dict[str, Any]:
    return _compact({
        "type": "folder",
        "name": f.get("name"),
        "path": f.get("fullName"),
        "subfolders": f.get("subfolderCount"),
        "library": f.get("isLibrary"),
        "folder_id": f.get("id"),
    })


def file_version(
    fv: Dict[str, Any],
    *,
    folders: Optional[Dict[str, Any]] = None,
    with_properties: bool = False,
) -> Dict[str, Any]:
    folder_id = fv.get("parentFolderId")
    parent = fv.get("parent") or (folders or {}).get(str(folder_id)) or {}
    out = _compact({
        "type": "file",
        "name": fv.get("name"),
        "revision": fv.get("revision"),
        "state": _state(fv),
        "category": fv.get("category"),
        "version": fv.get("version"),
        "modified": _date(fv.get("lastModifiedDate")),
        "checked_out_by": (fv.get("checkoutUserName") or "?") if fv.get("isCheckedOut") else "",
        "size": fv.get("size"),
        "folder": parent.get("fullName"),
        "file_version_id": fv.get("id"),
        "file_id": (fv.get("file") or {}).get("id"),
        "folder_id": folder_id,
    })
    if with_properties:
        out["properties"] = properties(fv)
    return out


def change_order(co: Dict[str, Any], *, with_properties: bool = False) -> Dict[str, Any]:
    out = _compact({
        "number": co.get("number") or co.get("name"),
        "title": co.get("title"),
        "description": co.get("description"),
        "state": co.get("state"),
        "approve_by": _date(co.get("approveDeadline")),
        "closed": _date(co.get("closeDate")),
        "modified": _date(co.get("lastModifiedDate")),
        "attachments": co.get("numberOfAttachments") if str(co.get("numberOfAttachments") or "0") != "0" else None,
        "change_order_id": co.get("id"),
    })
    if with_properties:
        out["properties"] = properties(co)
    return out


def entity(record: Dict[str, Any], folders: Optional[Dict[str, Any]] = None) -> Dict[str, Any]:
    """Slim any record by its entityType; unknown types get the generic strip."""
    kind = record.get("entityType")
    if kind == "Folder":
        return folder(record)
    if kind == "FileVersion":
        return file_version(record, folders=folders)
    if kind == "File" and isinstance(record.get("fileVersion"), dict):
        return file_version(record["fileVersion"], folders=folders)
    if kind == "ItemVersion":
        return item_version(record)
    if kind == "Item":
        return item(record)
    if kind == "ChangeOrder":
        return change_order(record)
    return strip_noise(record)


# ----------------------------------------------------------------------
# Collections
# ----------------------------------------------------------------------

def _records(data: Any) -> List[Dict[str, Any]]:
    if isinstance(data, list):
        return [r for r in data if isinstance(r, dict)]
    if isinstance(data, dict):
        for key in ("results", "items", "data", "value"):
            if isinstance(data.get(key), list):
                return [r for r in data[key] if isinstance(r, dict)]
    return []


def collection(data: Any, row=None) -> Dict[str, Any]:
    """Slim a paged list: total, whether more exist, and slim rows."""
    data = data if isinstance(data, dict) else {"results": data}
    folders = ((data.get("included") or {}).get("folder")) or {}
    rows = _records(data)
    page = data.get("pagination") or {}
    convert = row or (lambda r: entity(r, folders))
    out: Dict[str, Any] = {
        "total": page.get("totalResults", len(rows)),
        "returned": len(rows),
    }
    if page.get("nextUrl"):
        out["more"] = "More results exist; raise limit or narrow the query."
    out["results"] = [convert(r) for r in rows]
    return out


# ----------------------------------------------------------------------
# Bills of materials
# ----------------------------------------------------------------------

def _qty(link: Dict[str, Any]) -> Any:
    n = link.get("quantityNumber")
    if isinstance(n, float) and n.is_integer():
        return int(n)
    return n if n is not None else link.get("quantity")


def _position_key(link: Dict[str, Any]):
    pos = str(link.get("positionNumber") or "")
    return (0, int(pos)) if pos.isdigit() else (1, pos)


def item_bom(data: Dict[str, Any], root_item_version_id: str) -> Dict[str, Any]:
    """Turn the BOM payload into depth-first rows with dotted row numbers.

    The endpoint returns every item version (parent included) plus link rows
    keyed by parentItemId/childItemId; quantities live only on the links.
    Rows are emitted depth-first so the dotted ``row`` matches Inventor's
    structured "Item" column ("1", "1.2", ...), which the purchasing sheet
    parses into its hierarchy.
    """
    versions = {str(v.get("id")): v for v in data.get("itemVersions") or []}
    by_parent: Dict[str, List[Dict[str, Any]]] = {}
    for link in data.get("itemBomLinks") or []:
        by_parent.setdefault(str(link.get("parentItemId")), []).append(link)

    rows: List[Dict[str, Any]] = []

    def walk(parent_id: str, prefix: str, seen: set) -> None:
        links = sorted(by_parent.get(parent_id, []), key=_position_key)
        for i, link in enumerate(links, start=1):
            child_id = str(link.get("childItemId"))
            child = versions.get(child_id, {"id": child_id})
            row_no = f"{prefix}{i}"
            props = properties(child)
            rows.append(_compact({
                "row": row_no,
                "qty": _qty(link),
                "units": link.get("units"),
                **item_version(child),
                # Source (Buy/Make/Other) decides which rows the purchasing
                # sheet treats as bought; vendor fields save a lookup.
                "source": props.get("Source"),
                "vendor": props.get("Vendor"),
                "vendor_number": props.get("Vendor Number"),
                "material": props.get("Material"),
            }))
            if child_id not in seen:
                walk(child_id, row_no + ".", seen | {child_id})

    root = str(root_item_version_id)
    walk(root, "", {root})
    parent = versions.get(root)
    return {
        "assembly": item_version(parent) if parent else {"item_version_id": root},
        "row_count": len(rows),
        "rows": rows,
    }


def item_parents(data: Dict[str, Any], item_version_id: str) -> Dict[str, Any]:
    """Where-used: direct parents with the quantity used, plus higher assemblies."""
    versions = {str(v.get("id")): v for v in data.get("itemVersions") or []}
    target = str(item_version_id)
    direct, direct_ids = [], set()
    for link in data.get("itemBomLinks") or []:
        if str(link.get("childItemId")) == target:
            pid = str(link.get("parentItemId"))
            direct_ids.add(pid)
            direct.append({"qty": _qty(link), **item_version(versions.get(pid, {"id": pid}))})
    higher = [
        item_version(v) for vid, v in versions.items()
        if vid != target and vid not in direct_ids
    ]
    out: Dict[str, Any] = {"used_in": direct}
    if higher:
        out["higher_level_assemblies"] = higher
    return out


def file_uses(data: Dict[str, Any]) -> Dict[str, Any]:
    """CAD BOM: the parent file once, then each child with its association type."""
    folders = ((data.get("included") or {}).get("folder")) or {}
    rows = _records(data)
    parent = rows[0].get("parentFile") if rows else None
    return {
        "parent": file_version(parent, folders=folders) if parent else None,
        "children": [
            {"association": r.get("fileAssocType"),
             **file_version(r.get("childFile") or {}, folders=folders)}
            for r in rows
        ],
    }


def file_parents(data: Dict[str, Any]) -> Dict[str, Any]:
    """CAD where-used: each referencing file with its association type."""
    folders = ((data.get("included") or {}).get("folder")) or {}
    return {
        "used_in": [
            {"association": r.get("fileAssocType"),
             **file_version(r.get("parentFile") or {}, folders=folders)}
            for r in _records(data)
        ],
    }


def associated_files(data: Dict[str, Any]) -> Dict[str, Any]:
    folders = ((data.get("included") or {}).get("folder")) or {}
    return collection(data, row=lambda r: {
        "association": r.get("itemAssociationType"),
        **file_version(r.get("file") or {}, folders=folders),
    })
